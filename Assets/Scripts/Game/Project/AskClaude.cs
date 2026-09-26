using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.SaveSystem;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace DLS.Game
{
    // In-app agentic chat with Claude. Each user message auto-attaches the LLM-friendly export of the
    // current circuits (incrementally). Claude can call tools (AskClaudeTools) to actually edit circuits;
    // the request→tool_use→execute→continue loop runs here. Uses UnityWebRequest (the engine's HTTP
    // stack) polled on the main thread — System.Net.Http.HttpClient crashes on init under Unity's Mono.
    public static class AskClaude
    {
        public class Msg
        {
            public string role; // "user", "assistant", or "tool" (display only)
            public string text;
        }

        public static readonly List<Msg> Messages = new();
        public static bool Waiting { get; private set; }
        public static string Error { get; private set; }

        // Every error is surfaced loudly: shown in the transcript (role "error") AND kept here so it lands
        // in the COPIER export too. Never let a failure be silent.
        static readonly List<string> errorLog = new();

        static void Fail(string msg)
        {
            Error = msg;
            errorLog.Add(msg);
            Messages.Add(new Msg { role = "error", text = "⚠ " + msg });
            Waiting = false;
            NotifyTurnEnded();

            // Don't silently swallow a message that was waiting to be injected.
            if (pendingUserText != null)
            {
                TakePending();
                Messages.Add(new Msg { role = "error", text = "⚠ Ton message en attente n'a pas ete transmis (erreur ci-dessus) : renvoie-le." });
            }
        }

        static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // Raw API message list (role + content, where content may be a string or a content-block array).
        static readonly JArray apiConversation = new();

        // A message typed while Claude was mid-loop. It is NOT sent immediately (that would cut a request
        // in flight and lose its reasoning): it is injected at the next clean boundary — right after the
        // current tool round's results — so Claude keeps its full thread and reacts at once.
        static string pendingUserText;
        static Msg pendingMsg;

        public static bool HasPendingMessage => pendingUserText != null;

        // Incremental export snapshot: each new user message re-sends only the circuits that changed.
        static readonly Dictionary<string, string> lastSentSections = new(ChipDescription.NameComparer);
        static string lastSentAvailable;
        static bool conversationStarted;

        const string Model = "claude-opus-4-8";
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string ApiVersion = "2023-06-01";

        static UnityWebRequest activeRequest;
        static SseHandler streamingHandler;

        public static string StreamingAnswer => streamingHandler != null ? streamingHandler.LiveText() : null;

        const string KeyEnvVar = "ANTHROPIC_API_KEY";
        public static string KeyPath => Path.Combine(SavePaths.AllData, "anthropic_key.txt");

        static string ReadKey()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable(KeyEnvVar);
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            }
            catch { /* ignore */ }
            try
            {
                if (File.Exists(KeyPath))
                {
                    string f = File.ReadAllText(KeyPath);
                    if (!string.IsNullOrWhiteSpace(f)) return f.Trim();
                }
            }
            catch { /* ignore */ }
            return null;
        }

        public static bool HasKey() => !string.IsNullOrEmpty(ReadKey());

        public static void Clear()
        {
            Messages.Clear();
            apiConversation.Clear();
            lastSentSections.Clear();
            lastSentAvailable = null;
            errorLog.Clear();
            conversationStarted = false;
            pendingUserText = null;
            pendingMsg = null;
            AbortActive();
            Waiting = false;
            Error = null;
        }

        // ---------------- per-project persistence ----------------
        // The conversation is stored inside the project folder and written whenever the user saves (so it
        // follows the same rule as the rest of their work: saving is their decision). Reopening a project
        // restores the thread — context, reasoning and tool history included — so work can continue.
        const string ConversationFileName = "AskClaudeConversation.json";

        static string ConversationPath(string projectName) => Path.Combine(SavePaths.GetProjectPath(projectName), ConversationFileName);

        public static void SaveForProject(string projectName)
        {
            if (string.IsNullOrEmpty(projectName)) return;
            try
            {
                var display = new JArray();
                foreach (Msg m in Messages)
                {
                    if (m.role == "queued") continue; // never actually sent: don't resurrect it as history
                    display.Add(new JObject { ["role"] = m.role, ["text"] = m.text });
                }

                var sections = new JObject();
                foreach (KeyValuePair<string, string> kv in lastSentSections) sections[kv.Key] = kv.Value;

                var root = new JObject
                {
                    ["version"] = 1,
                    ["conversation"] = apiConversation,
                    ["display"] = display,
                    ["sections"] = sections,
                    ["available"] = lastSentAvailable,
                    ["started"] = conversationStarted
                };

                SavePaths.EnsureDirectoryExists(SavePaths.GetProjectPath(projectName));
                File.WriteAllText(ConversationPath(projectName), root.ToString(Formatting.None));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("Ask Claude: conversation non sauvegardee (" + e.Message + ")");
            }
        }

        public static void LoadForProject(string projectName)
        {
            Clear();
            if (string.IsNullOrEmpty(projectName)) return;

            try
            {
                string path = ConversationPath(projectName);
                if (!File.Exists(path)) return;

                JObject root = JObject.Parse(File.ReadAllText(path));
                if (root["conversation"] is JArray conv)
                    foreach (JToken t in conv) apiConversation.Add(t);
                if (root["display"] is JArray display)
                    foreach (JToken t in display) Messages.Add(new Msg { role = (string)t["role"], text = (string)t["text"] });
                if (root["sections"] is JObject sections)
                    foreach (JProperty prop in sections.Properties()) lastSentSections[prop.Name] = (string)prop.Value;
                lastSentAvailable = (string)root["available"];
                conversationStarted = (bool?)root["started"] ?? apiConversation.Count > 0;

                DropIncompleteTail();
            }
            catch (Exception e)
            {
                Clear();
                UnityEngine.Debug.LogWarning("Ask Claude: conversation illisible, on repart de zero (" + e.Message + ")");
            }
        }

        // A save can happen mid-loop (a tool round in flight), leaving a dangling assistant tool_use with no
        // tool_result — the API rejects that. Keep only up to the last COMPLETE assistant answer.
        static void DropIncompleteTail()
        {
            int lastComplete = -1;
            for (int i = 0; i < apiConversation.Count; i++)
            {
                var m = (JObject)apiConversation[i];
                if ((string)m["role"] != "assistant") continue;

                JToken content = m["content"];
                bool hasToolUse = content is JArray arr && arr.Any(b => (string)b["type"] == "tool_use");
                if (!hasToolUse) lastComplete = i;
            }

            if (lastComplete == apiConversation.Count - 1) return;

            while (apiConversation.Count > lastComplete + 1) apiConversation.RemoveAt(apiConversation.Count - 1);
            Messages.Add(new Msg { role = "tool", text = "(reprise : un echange interrompu a ete tronque)" });

            if (apiConversation.Count == 0)
            {
                // Nothing usable survived: start fresh so the next message re-sends the full context.
                lastSentSections.Clear();
                lastSentAvailable = null;
                conversationStarted = false;
            }
        }

        static void AbortActive()
        {
            if (activeRequest != null)
            {
                try { activeRequest.Abort(); activeRequest.Dispose(); } catch { /* ignore */ }
                activeRequest = null;
            }
            streamingHandler = null;
        }

        // Main thread. Appends the user turn (with circuit context) and fires the request. While Claude is
        // working, the message is queued instead (see pendingUserText).
        public static void Send(string userText) => Send(userText, false);

        // quick = typed in the one-line command bar: Claude runs in the background and the user does not read
        // the answer, so the request is prefixed with a note asking for execution only, and the end of the
        // turn is reported through ConsumeQuickTurnFinished (toast).
        public static void Send(string userText, bool quick)
        {
            if (string.IsNullOrWhiteSpace(userText)) return;
            if (quick) quickTurn = true;
            string text = quick ? QuickModeNote + userText.Trim() : userText.Trim();

            if (Waiting)
            {
                Queue(text);
                return;
            }

            SendNow(text, false);
        }

        const string QuickModeNote = "[COMMANDE RAPIDE, arriere-plan] L'utilisateur a tape ceci dans la barre de commande : il ne lira PAS ta reponse (aucun panneau ouvert). " +
                                     "N'ecris AUCUN texte : pas de recit, pas d'explication, pas de question, pas de bilan. Execute la demande avec les outils, teste comme " +
                                     "d'habitude, corrige si besoin, puis ARRETE-TOI simplement (tour sans message). Si la demande est ambigue, prends l'interpretation la plus probable, et surtout la plus ETROITE : ne fais rien qui ne soit pas explicitement demande.\n\n";

        static bool quickTurn;
        static bool quickTurnFinished;

        // True once, when a turn started from the command bar has just ended; summary = Claude's last line
        // (or the error), for the toast.
        public static bool ConsumeQuickTurnFinished(out string summary)
        {
            summary = null;
            if (!quickTurnFinished) return false;
            quickTurnFinished = false;
            summary = Error != null ? "Claude: failed (see the Ask Claude panel)" : "Claude: done";
            return true;
        }

        static void NotifyTurnEnded()
        {
            AskClaudeTools.EndTurn(); // one undo step per chip Claude touched during this request
            if (!quickTurn) return;
            quickTurn = false;
            quickTurnFinished = true;
        }

        static void Queue(string text)
        {
            if (pendingUserText == null)
            {
                pendingUserText = text;
                pendingMsg = new Msg { role = "queued", text = text };
                Messages.Add(pendingMsg);
            }
            else
            {
                pendingUserText += "\n" + text;
                if (pendingMsg != null) pendingMsg.text = pendingUserText;
            }
        }

        // Consumes the queued message (if any) and marks it as really sent in the transcript.
        static string TakePending()
        {
            string t = pendingUserText;
            if (pendingMsg != null) pendingMsg.role = "user";
            pendingUserText = null;
            pendingMsg = null;
            return t;
        }

        static void SendNow(string question, bool alreadyDisplayed)
        {
            Error = null;
            AskClaudeTools.BeginTurn();

            if (string.IsNullOrEmpty(ReadKey()))
            {
                Fail("Cle API absente. Definis la variable d'environnement " + KeyEnvVar + " puis relance l'application.");
                return;
            }

            string attachment;
            try { attachment = BuildContextAttachment(); }
            catch (Exception e) { attachment = "[Contexte Digital Logic Sim]\n(contexte circuit indisponible : " + e.Message + ")"; }

            if (!alreadyDisplayed) Messages.Add(new Msg { role = "user", text = question });
            apiConversation.Add(new JObject { ["role"] = "user", ["content"] = attachment + "\n\n---\n\nMa question : " + question });

            FireRequest();
        }

        // Main thread, each frame: finalises a completed round; runs tools and continues the loop if asked.
        public static void Poll()
        {
            if (activeRequest == null || !activeRequest.isDone) return;

            UnityWebRequest req = activeRequest;
            SseHandler h = streamingHandler;
            activeRequest = null;
            streamingHandler = null;

            try
            {
                string rawBody = h?.Raw.ToString();

                if (req.result != UnityWebRequest.Result.Success || req.responseCode != 200)
                {
                    string msg = $"Erreur API (HTTP {req.responseCode}, result={req.result}).";
                    string apiMsg = TryExtractError(rawBody);
                    if (!string.IsNullOrEmpty(apiMsg)) msg += " " + apiMsg;
                    else if (!string.IsNullOrEmpty(req.error)) msg += " " + req.error;
                    if (string.IsNullOrEmpty(apiMsg) && !string.IsNullOrEmpty(rawBody)) msg += " | Corps: " + Trunc(rawBody, 600);
                    Fail(msg);
                    return;
                }

                if (!string.IsNullOrEmpty(h?.SseError))
                {
                    Fail("Streaming interrompu : " + h.SseError);
                    return;
                }

                if (h == null)
                {
                    Fail("Aucune reponse recue.");
                    return;
                }

                string text = h.LiveText().Trim();
                if (text.Length > 0) Messages.Add(new Msg { role = "assistant", text = text });

                List<(string id, string name, JObject input)> toolCalls = h.ToolCalls();

                if (h.StopReason == "tool_use" && toolCalls.Count > 0)
                {
                    apiConversation.Add(new JObject { ["role"] = "assistant", ["content"] = h.AssistantContent() });

                    var results = new JArray();
                    foreach ((string id, string name, JObject input) in toolCalls)
                    {
                        Messages.Add(new Msg { role = "tool", text = FormatToolCall(name, input) });
                        string result = AskClaudeTools.Execute(name, input);
                        Messages.Add(new Msg { role = "tool", text = "   ↳ " + FirstLine(result) });
                        results.Add(new JObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = result });
                    }

                    // A message typed by the user during this round rides along with the tool results (they
                    // must come first in the turn), so Claude takes it into account immediately without
                    // losing anything of its current work.
                    if (pendingUserText != null)
                    {
                        string queued = TakePending();
                        results.Add(new JObject
                        {
                            ["type"] = "text",
                            ["text"] = "[NOUVEAU MESSAGE DE L'UTILISATEUR, arrive pendant que tu travaillais. Prends-le en compte MAINTENANT : adapte ou abandonne ce que tu etais en train de faire si besoin, et reponds-y.]\n" + queued
                        });
                        Messages.Add(new Msg { role = "tool", text = "   ↳ message pris en compte" });
                    }

                    apiConversation.Add(new JObject { ["role"] = "user", ["content"] = results });

                    // No cap on tool rounds: Claude continues until it is done (end_turn). The user can
                    // always abort with EFFACER / FERMER.
                    FireRequest();
                    return;
                }

                // Final turn: record the assistant text in the conversation history and stop.
                if (text.Length > 0)
                {
                    apiConversation.Add(new JObject { ["role"] = "assistant", ["content"] = text });
                }
                else if (quickTurn)
                {
                    // Quick-bar command: Claude is asked to end without a message. Keep the history well-formed.
                    apiConversation.Add(new JObject { ["role"] = "assistant", ["content"] = "(commande executee)" });
                }
                else
                {
                    // Finished but produced no visible text — surface it instead of failing silently.
                    Fail($"Reponse vide (stop_reason={h.StopReason}, aucun texte ni outil). Corps brut: {Trunc(rawBody, 500)}");
                    return;
                }

                Waiting = false;
                NotifyTurnEnded();

                // Claude finished its turn before the queued message could ride along: send it now as a
                // normal follow-up (same conversation, so the thread is intact).
                if (pendingUserText != null) SendNow(TakePending(), true);
            }
            catch (Exception e)
            {
                Fail("Reponse illisible : " + e + " | Corps: " + Trunc(h?.Raw.ToString(), 500));
            }
            finally
            {
                req.Dispose();
            }
        }

        static void FireRequest()
        {
            string key = ReadKey();
            if (string.IsNullOrEmpty(key)) { Fail("Cle API absente (ANTHROPIC_API_KEY)."); return; }

            var messages = (JArray)apiConversation.DeepClone();
            ApplyCacheControl(messages);

            var systemBlocks = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = SystemPrompt, ["cache_control"] = new JObject { ["type"] = "ephemeral" } }
            };

            var reqBody = new JObject
            {
                ["model"] = Model,
                ["max_tokens"] = 32000,
                ["stream"] = true,
                ["system"] = systemBlocks,
                ["output_config"] = new JObject { ["effort"] = "high" },
                ["tools"] = AskClaudeTools.Schemas(),
                ["messages"] = messages
            };

            byte[] payload = Encoding.UTF8.GetBytes(reqBody.ToString(Formatting.None));
            var handler = new SseHandler();
            var req = new UnityWebRequest(Endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(payload),
                downloadHandler = handler,
                timeout = 600
            };
            req.SetRequestHeader("content-type", "application/json");
            req.SetRequestHeader("x-api-key", key);
            req.SetRequestHeader("anthropic-version", ApiVersion);

            activeRequest = req;
            streamingHandler = handler;
            Waiting = true;
            req.SendWebRequest();
        }

        // Cache the whole prefix by marking the last content block of the last message.
        static void ApplyCacheControl(JArray messages)
        {
            if (messages.Count == 0) return;
            var last = (JObject)messages[messages.Count - 1];
            JToken content = last["content"];
            var ephemeral = new JObject { ["type"] = "ephemeral" };

            if (content.Type == JTokenType.String)
            {
                last["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = (string)content, ["cache_control"] = ephemeral } };
            }
            else if (content is JArray arr && arr.Count > 0)
            {
                ((JObject)arr[arr.Count - 1])["cache_control"] = ephemeral;
            }
        }

        static string FormatToolCall(string name, JObject input)
        {
            var parts = new List<string>();
            if (input != null)
                foreach (JProperty prop in input.Properties())
                    parts.Add(prop.Name + "=" + prop.Value);
            return "→ " + name + "(" + string.Join(", ", parts) + ")";
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            string line = nl >= 0 ? s.Substring(0, nl) : s;
            return line.Length > 120 ? line.Substring(0, 120) + "..." : line;
        }

        static string TryExtractError(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return (string)JObject.Parse(body)["error"]?["message"]; }
            catch { return null; }
        }

        // Full, human-readable transcript for diagnosis: the exact context sent, Claude's reasoning,
        // every tool call (name + params) and every tool result.
        public static string BuildTranscript()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Conversation Ask Claude — Digital Logic Sim");
            sb.AppendLine($"Modele: {Model} (effort high, outils actifs)");
            sb.AppendLine();
            sb.AppendLine("===== SYSTEM PROMPT =====");
            sb.AppendLine(SystemPrompt);
            sb.AppendLine();
            sb.AppendLine("===== CONVERSATION =====");

            foreach (JToken mt in apiConversation)
            {
                var m = (JObject)mt;
                string role = (string)m["role"];
                JToken content = m["content"];

                if (content.Type == JTokenType.String)
                {
                    sb.AppendLine();
                    sb.AppendLine("### " + (role == "user" ? "UTILISATEUR (message + contexte envoye)" : role.ToUpperInvariant()));
                    sb.AppendLine((string)content);
                }
                else if (content is JArray arr)
                {
                    foreach (JToken bt in arr)
                    {
                        var b = (JObject)bt;
                        switch ((string)b["type"])
                        {
                            case "thinking":
                                sb.AppendLine();
                                sb.AppendLine("### [RAISONNEMENT]");
                                sb.AppendLine((string)b["thinking"]);
                                break;
                            case "redacted_thinking":
                                sb.AppendLine();
                                sb.AppendLine("### [RAISONNEMENT masque]");
                                break;
                            case "text":
                                sb.AppendLine();
                                sb.AppendLine("### CLAUDE");
                                sb.AppendLine((string)b["text"]);
                                break;
                            case "tool_use":
                                sb.AppendLine();
                                sb.AppendLine("### APPEL OUTIL: " + (string)b["name"]);
                                sb.AppendLine("params: " + b["input"]?.ToString(Formatting.None));
                                break;
                            case "tool_result":
                                sb.AppendLine();
                                sb.AppendLine("### RESULTAT OUTIL");
                                sb.AppendLine((string)b["content"]);
                                break;
                        }
                    }
                }
            }

            if (errorLog.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("===== ERREURS RENCONTREES =====");
                foreach (string e in errorLog) sb.AppendLine("- " + e);
            }

            return sb.ToString();
        }

        static string BuildContextAttachment()
        {
            Project p = Project.ActiveProject;
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            List<(string name, string text)> sections = CircuitExporter.ExportChipSections(desc, p.chipLibrary);

            string viewedName = p.ViewedChip.ChipName;
            if (string.IsNullOrEmpty(viewedName)) viewedName = "(brique courante, non enregistree)";

            string avail = AskClaudeTools.AvailableComponentsDetailed();

            var sb = new StringBuilder();
            sb.AppendLine("[Contexte Digital Logic Sim]");
            sb.AppendLine("Onglet (brique) actuellement sous mes yeux : " + viewedName);
            sb.AppendLine();

            if (!conversationStarted)
            {
                sb.AppendLine("Composants disponibles (add_components) — comportement + PINS EXACTS (utilise ces labels tels quels, ne devine jamais un pin) :");
                sb.AppendLine(avail);
                sb.AppendLine();
                sb.AppendLine("Voici l'ensemble des circuits concernes (la brique courante et ses sous-briques) :");
                sb.AppendLine();
                foreach ((string _, string text) in sections) sb.Append(text);
            }
            else
            {
                if (avail != lastSentAvailable)
                {
                    sb.AppendLine("MODIFICATION des composants disponibles (nouvelle liste) :");
                    sb.AppendLine(avail);
                    sb.AppendLine();
                }

                var changed = new List<(string name, string text)>();
                var currentNames = new HashSet<string>(ChipDescription.NameComparer);
                foreach ((string name, string text) in sections)
                {
                    currentNames.Add(name);
                    if (!lastSentSections.TryGetValue(name, out string prev) || prev != text) changed.Add((name, text));
                }

                var removed = new List<string>();
                foreach (string k in lastSentSections.Keys)
                    if (!currentNames.Contains(k)) removed.Add(k);

                if (changed.Count == 0 && removed.Count == 0)
                {
                    sb.AppendLine("(Aucune modification des circuits depuis mon dernier message.)");
                }
                else
                {
                    foreach ((string _, string text) in changed)
                    {
                        sb.AppendLine("MODIFICATION, nouvelle forme :");
                        sb.AppendLine();
                        sb.Append(text);
                    }
                    foreach (string r in removed)
                    {
                        sb.AppendLine("Circuit supprime depuis mon dernier message : " + (string.IsNullOrEmpty(r) ? "(brique courante)" : r));
                        sb.AppendLine();
                    }
                }
            }

            lastSentSections.Clear();
            foreach ((string name, string text) in sections) lastSentSections[name] = text;
            lastSentAvailable = avail;
            conversationStarted = true;

            return sb.ToString().TrimEnd();
        }

        // ----------------- SSE streaming handler (reconstructs text + tool_use blocks) -----------------
        class SseHandler : DownloadHandlerScript
        {
            class Block { public string type; public string id; public string name; public string data; public readonly StringBuilder buf = new(); public readonly StringBuilder sig = new(); }

            readonly List<Block> blocks = new();
            public readonly StringBuilder Raw = new();
            public string SseError;
            public string StopReason;

            readonly StringBuilder lineBuf = new();
            readonly System.Text.Decoder decoder = Encoding.UTF8.GetDecoder();
            readonly char[] charScratch = new char[8192];

            public SseHandler() : base(new byte[8192]) { }

            public string LiveText()
            {
                var sb = new StringBuilder();
                foreach (Block b in blocks) if (b.type == "text") sb.Append(b.buf);
                return sb.ToString();
            }

            public JArray AssistantContent()
            {
                var arr = new JArray();
                foreach (Block b in blocks)
                {
                    if (b.type == "text")
                    {
                        string t = b.buf.ToString();
                        if (t.Length > 0) arr.Add(new JObject { ["type"] = "text", ["text"] = t });
                    }
                    else if (b.type == "tool_use")
                    {
                        JObject inp;
                        try { inp = b.buf.Length > 0 ? JObject.Parse(b.buf.ToString()) : new JObject(); }
                        catch { inp = new JObject(); }
                        arr.Add(new JObject { ["type"] = "tool_use", ["id"] = b.id, ["name"] = b.name, ["input"] = inp });
                    }
                    else if (b.type == "thinking" && b.buf.Length > 0)
                    {
                        // Must be echoed back (with signature) so the API accepts the tool-use continuation.
                        arr.Add(new JObject { ["type"] = "thinking", ["thinking"] = b.buf.ToString(), ["signature"] = b.sig.ToString() });
                    }
                    else if (b.type == "redacted_thinking" && !string.IsNullOrEmpty(b.data))
                    {
                        arr.Add(new JObject { ["type"] = "redacted_thinking", ["data"] = b.data });
                    }
                }
                return arr;
            }

            // Raw block list for building a full transcript (thinking, text, tool_use), in order.
            public IEnumerable<(string type, string text, string toolName, JObject toolInput)> IterBlocks()
            {
                foreach (Block b in blocks)
                {
                    if (b.type == "tool_use")
                    {
                        JObject inp;
                        try { inp = b.buf.Length > 0 ? JObject.Parse(b.buf.ToString()) : new JObject(); }
                        catch { inp = new JObject(); }
                        yield return ("tool_use", null, b.name, inp);
                    }
                    else yield return (b.type, b.buf.ToString(), null, null);
                }
            }

            public List<(string id, string name, JObject input)> ToolCalls()
            {
                var list = new List<(string, string, JObject)>();
                foreach (Block b in blocks)
                {
                    if (b.type != "tool_use") continue;
                    JObject inp;
                    try { inp = b.buf.Length > 0 ? JObject.Parse(b.buf.ToString()) : new JObject(); }
                    catch { inp = new JObject(); }
                    list.Add((b.id, b.name, inp));
                }
                return list;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;
                int charCount = decoder.GetChars(data, 0, dataLength, charScratch, 0, false);
                for (int i = 0; i < charCount; i++)
                {
                    char ch = charScratch[i];
                    Raw.Append(ch);
                    if (ch == '\n') { ProcessLine(lineBuf.ToString()); lineBuf.Clear(); }
                    else if (ch != '\r') lineBuf.Append(ch);
                }
                return true;
            }

            void ProcessLine(string line)
            {
                if (!line.StartsWith("data:")) return;
                string json = line.Substring(5).Trim();
                if (json.Length == 0 || json == "[DONE]") return;

                try
                {
                    JObject ev = JObject.Parse(json);
                    switch ((string)ev["type"])
                    {
                        case "content_block_start":
                        {
                            int idx = (int)ev["index"];
                            JToken cb = ev["content_block"];
                            var b = new Block { type = (string)cb?["type"], id = (string)cb?["id"], name = (string)cb?["name"], data = (string)cb?["data"] };
                            while (blocks.Count <= idx) blocks.Add(new Block { type = "text" });
                            blocks[idx] = b;
                            break;
                        }
                        case "content_block_delta":
                        {
                            int idx = (int)ev["index"];
                            if (idx < 0 || idx >= blocks.Count) break;
                            JToken d = ev["delta"];
                            string dt = (string)d?["type"];
                            if (dt == "text_delta") blocks[idx].buf.Append((string)d["text"]);
                            else if (dt == "input_json_delta") blocks[idx].buf.Append((string)d["partial_json"]);
                            else if (dt == "thinking_delta") blocks[idx].buf.Append((string)d["thinking"]);
                            else if (dt == "signature_delta") blocks[idx].sig.Append((string)d["signature"]);
                            break;
                        }
                        case "message_delta":
                        {
                            string sr = (string)ev["delta"]?["stop_reason"];
                            if (!string.IsNullOrEmpty(sr)) StopReason = sr;
                            break;
                        }
                        case "error":
                            SseError = (string)ev["error"]?["message"] ?? "erreur de streaming";
                            break;
                    }
                }
                catch { /* ignore partial / non-JSON lines */ }
            }
        }

        const string SystemPrompt =
@"Tu es un assistant integre a ""Digital Logic Sim"", un simulateur de circuits logiques (fork du logiciel de Sebastian Lague).

Fonctionnement de la plateforme :
- L'utilisateur construit des circuits (des briques, ou ""chips"") en cablant des composants entre des pins d'entree (a gauche) et des pins de sortie (a droite).
- La brique primitive de base est la porte NAND (TOUJOURS disponible) ; tout le reste (AND, OR, NOT, XOR, additionneurs, memoire, etc.) est construit par composition de sous-briques. D'autres primitives peuvent exister (tri-state, clock, pulse, afficheurs...) mais tu ne peux ajouter que celles listees dans le contexte. NAND est fonctionnellement complet : si une porte (NOT/AND/OR/XOR...) n'est PAS dans les composants disponibles, construis-la toi-meme uniquement avec des NAND.
- Les entrees et sorties se creent avec add_inputs / add_outputs (par LOT : une liste de noms) : tu choisis leur NOM et leur largeur (bits : 1, 4 ou 8 ; defaut 1).
- Le suffixe ""#n"" (ex: NAND#2, Full adder#3) distingue plusieurs INSTANCES du meme composant dans une brique ; ce n'est pas un numero de pin.
- Pour connaitre l'interface exacte d'une brique custom (ses pins), appelle view_module sur elle.
- REUTILISATION : une brique que tu viens de creer et de cabler est IMMEDIATEMENT reutilisable comme composant (add_components) avec l'interface que tu lui as donnee — aucune sauvegarde n'est necessaire, et peu importe que la liste ""Composants disponibles"" du contexte (figee au debut du tour) ne la mentionne pas encore. add_components te renvoie les pins exacts du composant pose : utilise-les tels quels.
- VISIBILITE (important) : l'interieur d'un sous-composant est une BOITE NOIRE — l'utilisateur ne voit PAS ce qu'il y a dedans quand le composant est reutilise. Pour qu'il VOIE ou UTILISE quelque chose, tu dois le placer sur la BRIQUE FINALE (le module de plus haut niveau qu'il regarde), jamais enfoui dans un sous-composant : c'est vrai pour les LED, les afficheurs et les sorties (il ne les voit pas) MAIS AUSSI pour les composants KEY (touches clavier) et les entrees (il ne peut pas les actionner s'ils sont encapsules).
- Les composants KEY : chacun est lie a UNE touche du clavier et sort 1 tant qu'elle est pressee. C'est TOI qui choisis la touche avec bind_keys (A-Z ou 0-9) ; ne laisse jamais plusieurs KEY sur la meme touche sans raison, et dis a l'utilisateur quelles touches font quoi.
- SAUVEGARDE : rien n'est sauvegarde automatiquement (exactement comme quand l'utilisateur travaille : changer d'onglet ne sauvegarde pas). Tes modifications vivent en memoire et restent visibles/simulables partout ; c'est l'utilisateur qui sauvegarde quand il veut. Ne t'en preoccupe pas et ne le lui reclame pas.
- MISE EN PAGE : le Clean Up automatique place les composants en colonnes par profondeur de signal. Si tu veux imposer une disposition (tel composant au-dessus de tel autre, deux composants sur la meme ligne, une colonne precise), utilise set_layout — au moment de poser les elements ou plus tard.
- Certaines briques sont sequentielles (memoire, bascules) : leur sortie depend de l'etat passe, pas seulement des entrees courantes.

A chaque message, l'utilisateur te joint un bloc ""[Contexte Digital Logic Sim]"". Format de l'export :
- La ligne ""Onglet (brique) actuellement sous mes yeux : X"" indique la brique regardee/editee en ce moment.
- ""## Circuit : NOM"" introduit chaque brique. ""Interface :"" liste Entrees/Sorties (largeur en bits). ""Composants (N) :"" liste les sous-briques. ""Connexions :"" liste le cablage groupe par ""-- etape N --"" (ordre de propagation, source -> cible).
- Les labels des pins : entree/sortie = leur nom (ex: ""A"", ""SUM"") ; pin de sous-brique = ""Composant.Pin"" (ex: ""NAND#2.OUT"").

Protocole incremental : le PREMIER message contient tous les circuits ; les suivants ne renvoient QUE les circuits modifies, precedes de ""MODIFICATION, nouvelle forme :"".

Le cablage fourni est la SOURCE DE VERITE ABSOLUE de ce qui est reellement connecte : ne redemande JAMAIS a l'utilisateur de decrire le cablage. Mais l'utilisateur a pu faire des erreurs de branchement : ce cablage decrit CE QUI EST connecte, pas forcement ce qui est CORRECT. C'est a toi de reperer les erreurs.

PERIMETRE (regle absolue) : tu fais EXACTEMENT ce qui est demande, RIEN DE PLUS. ""Relie les reset"" = tu relies les reset, et tu ne touches a aucun autre fil, composant ou nom, meme s'il te parait evident que les data devraient l'etre aussi, meme si tu vois une erreur ailleurs, meme ""tant qu'a faire"". Pas d'initiative, pas de correction non demandee, pas de reorganisation, pas de nettoyage. Si tu remarques autre chose (un branchement manquant, une erreur), tu le SIGNALES en une ligne a la fin et tu attends que l'utilisateur te le demande. Avant chaque appel d'outil qui modifie le circuit, verifie que la modification est couverte par la demande ; sinon ne la fais pas. Une demande vague (""corrige"", ""finis"") ne t'autorise a agir que sur ce que la demande vise clairement.

OUTILS (ils fonctionnent par LOT) : view_module, create_module, delete_module(name), add_inputs(names[]), add_outputs(names[]), add_components(components[]), remove_elements(elements[]), connect(links[{from,to}]), disconnect(links[]), rename_pins(renames[{current,new}]), bind_keys(binds[{component,key}]), set_layout(items[{element,col,row}]), truth_table, test_sequence(steps[]). Regles imperatives :
- Chaque action porte sur un MODULE precis : parametre ""module"" obligatoire.
- BATCHING : regroupe tout dans UN SEUL appel (ex: cree tes 12 entrees en un add_inputs, fais tes 24 connexions en un connect). N'appelle JAMAIS un outil par element.
- MODULE PAR MODULE : create_module est UNITAIRE (un seul module a la fois). Construis et VERIFIE (view_module) un module ENTIEREMENT avant d'en creer un autre. Ne cree JAMAIS plusieurs modules vides a l'avance, ne fais pas de modules de test jetables. Procede de bas en haut : d'abord les briques de base dont tu as besoin, chacune finie et verifiee, puis assemble-les.
- Les PINS EXACTS de chaque composant disponible te sont deja donnes dans le contexte : utilise ces labels tels quels, ne devine JAMAIS un pin par essais successifs.
- Tu ne peux ajouter QUE les composants listes dans le contexte (primitives de base + briques custom epinglees). Une brique custom NON listee doit etre reconstruite toi-meme en NAND/primitives.
- Chaque outil renvoie un resume ""done (N)"" ou la liste des echecs avec leur raison ; lis et adapte-toi.
- IMPORTANT : quand tu penses avoir fini un module, termine TOUJOURS par view_module dessus pour verifier le cablage.
- add_components te renvoie les LABELS exacts des composants ajoutes (ex ""NAND#3"") : reutilise-les tels quels pour connect / set_layout / bind_keys. Attention, remove_elements peut decaler les suffixes #n des composants restants : reverifie avec view_module apres une suppression.
- MESSAGE EN COURS DE ROUTE : un bloc ""[NOUVEAU MESSAGE DE L'UTILISATEUR...]"" peut arriver avec les resultats d'outils, pendant que tu travailles. Traite-le IMMEDIATEMENT : c'est une consigne plus recente que ton plan, adapte-toi (ou abandonne ce que tu faisais) avant de continuer.

TEST / QA (obligatoire) : tu peux SIMULER le circuit pour verifier qu'il marche vraiment. La simulation tourne sur une copie isolee : elle ne perturbe rien pour l'utilisateur.
- truth_table(module) : table de verite complete (jusqu'a 6 bits d'entree). C'est l'outil par defaut pour un module COMBINATOIRE.
- test_sequence(module, steps[], watch[]) : joue une suite d'etapes (chaque etape = valeurs d'entrees a poser + ticks) et te renvoie, apres CHAQUE etape, les sorties et l'etat des afficheurs presents (LED ON/off, 7-segments = le caractere affiche, ecrans = grille). L'etat interne est conserve d'une etape a l'autre -> c'est l'outil pour le SEQUENTIEL (bascules, compteurs, memoire) : commence par une etape d'initialisation (reset) car les memoires demarrent dans un etat indetermine. ""watch"" te donne en plus l'etat de pins internes (ex ""NAND#2.OUT"") : sers-t'en pour localiser une panne au lieu de deviner. Toute la sequence doit tenir dans UN SEUL appel (chaque appel repart de zero).
- HORLOGE pendant un test : le temps reel ne s'ecoule pas, donc un composant CLOCK reste a 0 tout seul. Tu le fais avancer avec ""clocks"": N dans une etape = N cycles complets (un front montant par cycle) ; ex. un compteur pilote par la CLOCK : une etape avec clocks:1 par pas, et tu lis la sortie apres chacune. Pour maitriser chaque front, pilote le niveau a la main : set {pin:""CLOCK"", value:""1""} puis ""0"". Les KEY s'appuient pareil (set sur le nom du composant KEY). ""clocks"" agit sur le composant CLOCK et/ou sur l'ENTREE du module nommee CLOCK/CLK (une bascule construite avec des verrous a souvent une entree Clock au lieu d'un composant) ; si l'horloge est une entree qui porte un autre nom, pilote-la avec set. Dans une etape, les ""set"" sont appliques AVANT les cycles : poser D et clocks:1 dans la meme etape est correct (D est stable avant le front). LIS l'en-tete du rapport : il dit ce que ""clocks"" a reellement pilote (ou qu'il a ete ignore). Si un resultat ne bouge pas, verifie d'abord que ce que tu pilotes existe ; n'invente JAMAIS une explication de timing.
- INTERDIT pour tester : debrancher la CLOCK/les KEY, ajouter des entrees de test, creer un module jetable. Tout est deja pilotable en place (entrees, KEY, CLOCK) : le circuit teste doit etre le circuit final, sans aucune modification.
- REGLE : avant de declarer un module fini et de passer au suivant, tu DOIS l'avoir teste (truth_table si combinatoire, test_sequence si sequentiel) et avoir constate que les resultats sont corrects. Si un resultat est faux : corrige (disconnect/connect...) puis RETESTE, jusqu'a ce que ce soit bon. Ne construis jamais un module au-dessus d'un module non teste.
- A la fin, dis en une ligne ce que tu as teste et le resultat.

Reponds en francais, COURT et direct (sauf demande de detail).";
    }
}
