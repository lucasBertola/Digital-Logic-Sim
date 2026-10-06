using System;
using System.Text;
using DLS.Description;
using DLS.SaveSystem;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace DLS.Game
{
	// Asks Claude how the memory cells of a chip group into words (MemoryLayout rules), from the chip's full export.
	// One request at a time, polled from the main thread (UnityWebRequest — HttpClient breaks under Unity's Mono).
	// The answer is only a proposal: MemoryLayout.Verify checks it by simulation, and a failed check is sent back
	// to Claude for another attempt (MemoryEditMenu drives the attempts).
	public static class MemoryLayoutClaude
	{
		const string Endpoint = "https://api.anthropic.com/v1/messages";
		const string Model = "claude-opus-5";

		public enum Status { Idle, Running, Done, Failed }

		static UnityWebRequest request;
		public static Status State { get; private set; } = Status.Idle;
		public static string ResultJson { get; private set; }
		public static string Error { get; private set; }

		public const string SystemPrompt =
@"You analyse digital circuits built in a logic simulator from NAND gates and a few builtin chips. Memory cells are
cross-coupled NAND pairs (latches); a D flip-flop holds two (master and slave). The user wants to edit the memory of
a chip WORD by WORD. Describe how its cells group into words, using ONLY component names exactly as they appear in
the export (e.g. ""RAM64#2"", ""VerrouD 2#8"") or a component's label (e.g. ""Registre A"", ""BD0"").

banks: every memory the user would edit as one list of words — a register, a counter, an addressed RAM. path = the
component names from the analysed chip down to the chip that IS the memory ([] if the analysed chip itself is the
memory). Do not list builtin ROM / RAM chips (handled automatically). Do not list a chip without memory cells.

types: one rule per chip type needed to reach every cell of every bank:
- kind ""word"": children = the components holding bit 0, bit 1, ... of the word, LEAST significant first; all the
  cells inside a listed component belong to that bit. Join components with ""+"" when a bit's cells are spread over
  several; raw NAND gates may be listed (""NAND#3+NAND#4"") when a bit is a latch drawn directly in the chip.
  Find the bit order from the wiring to the chip's data pins (for split/merge chips the LAST pin is the least
  significant bit).
- kind ""array"": children = the sub-components holding successive blocks of words, listed in the order of the
  address value that selects them (follow the decoder outputs: D0 selects block 0...). Word address =
  childIndex * wordsPerChild + address inside the child: the chip decodes the HIGH address bits.

reads: for each BANK chip type, how to read word a through its OWN pins: inputs = pin / value, value being an
expression of a (e.g. ""a"", ""(a>>1)&1"", ""1""); inputs not listed are held at 0; the clock is held low. output =
the output pin names carrying the word, least significant first. Enable whatever output / chip-select / read pins
the chip needs so the word appears on its outputs.

Your rules are checked by simulation (values written into the cells you designate must read back through the pins
you name). If a previous attempt was rejected, you are told why: fix exactly that.";

		static JObject Schema()
		{
			JObject Str() => new JObject { ["type"] = "string" };
			JObject Arr(JObject items) => new JObject { ["type"] = "array", ["items"] = items };
			JObject Obj(JObject props, params string[] required) => new JObject
			{
				["type"] = "object",
				["properties"] = props,
				["required"] = new JArray(required),
				["additionalProperties"] = false
			};
			return Obj(new JObject
			{
				["banks"] = Arr(Obj(new JObject { ["name"] = Str(), ["path"] = Arr(Str()) }, "name", "path")),
				["types"] = Arr(Obj(new JObject
				{
					["type"] = Str(),
					["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("word", "array") },
					["children"] = Arr(Str())
				}, "type", "kind", "children")),
				["reads"] = Arr(Obj(new JObject
				{
					["type"] = Str(),
					["inputs"] = Arr(Obj(new JObject { ["pin"] = Str(), ["value"] = Str() }, "pin", "value")),
					["output"] = Arr(Str())
				}, "type", "inputs", "output"))
			}, "banks", "types", "reads");
		}

		// previousJson / previousError: the rejected attempt, for a retry
		public static void Start(ChipDescription chip, ChipLibrary lib, string projectName, string previousJson, string previousError)
		{
			Cancel();
			ResultJson = null;
			Error = null;
			string key = AskClaude.ApiKey;
			if (string.IsNullOrEmpty(key)) { State = Status.Failed; Error = "No Anthropic API key (set ANTHROPIC_API_KEY)."; return; }

			string export = CircuitExporter.ExportChipWithDeps(chip, lib, projectName);
			var messages = new JArray
			{
				new JObject { ["role"] = "user", ["content"] = $"Chip to analyse: \"{chip.Name}\".\n\n{export}" }
			};
			if (!string.IsNullOrEmpty(previousJson))
			{
				messages.Add(new JObject { ["role"] = "assistant", ["content"] = previousJson });
				messages.Add(new JObject { ["role"] = "user", ["content"] = "The simulation rejected these rules: " + previousError + "\nGive corrected rules." });
			}

			var body = new JObject
			{
				["model"] = Model,
				["max_tokens"] = 16000,
				["system"] = SystemPrompt,
				["messages"] = messages,
				["output_config"] = new JObject { ["effort"] = "high", ["format"] = new JObject { ["type"] = "json_schema", ["schema"] = Schema() } },
				["fallbacks"] = "default"
			};
			byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
			request = new UnityWebRequest(Endpoint, "POST")
			{
				uploadHandler = new UploadHandlerRaw(payload),
				downloadHandler = new DownloadHandlerBuffer(),
				timeout = 600
			};
			request.SetRequestHeader("content-type", "application/json");
			request.SetRequestHeader("x-api-key", key);
			request.SetRequestHeader("anthropic-version", "2023-06-01");
			request.SetRequestHeader("anthropic-beta", "server-side-fallback-2026-07-01");
			State = Status.Running;
			request.SendWebRequest();
		}

		public static void Poll()
		{
			if (State != Status.Running || request == null || !request.isDone) return;
			try
			{
				string text = request.downloadHandler?.text ?? "";
				if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
				{
					State = Status.Failed;
					Error = $"Claude request failed ({request.responseCode}): {Short(text)}";
					AskClaude.NotifyKeyRejected(request.responseCode);
					return;
				}
				JObject resp = JObject.Parse(text);
				if ((string)resp["stop_reason"] == "refusal") { State = Status.Failed; Error = "Claude declined the request."; return; }
				var sb = new StringBuilder();
				foreach (JToken block in resp["content"] ?? new JArray())
					if ((string)block["type"] == "text") sb.Append((string)block["text"]);
				ResultJson = sb.ToString();
				State = string.IsNullOrWhiteSpace(ResultJson) ? Status.Failed : Status.Done;
				if (State == Status.Failed) Error = "Claude returned no rules.";
			}
			catch (Exception e) { State = Status.Failed; Error = "Unreadable answer from Claude: " + e.Message; }
			finally { request.Dispose(); request = null; }
		}

		public static void Cancel()
		{
			if (request != null) { try { request.Abort(); request.Dispose(); } catch (Exception) { } request = null; }
			State = Status.Idle;
		}

		static string Short(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;
	}
}
