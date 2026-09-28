using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEngine;

namespace DLS.Game
{
	// Self-test run INSIDE the built player: the stripped build behaves differently from the editor (a memory editor
	// bug existed only there, and the editor bench was green). Launch:
	//   DigitalLogicSim.exe -selftest [-selftest-claude]
	// It uses the "PC" project of the save folder, writes the report to <save folder>/selftest.txt and quits.
	//   - memory editor: known-good rules parsed, verified by simulation, CPU banks written / read back
	//   - memory state: captured, saved through the real serializer, reloaded, read back
	//   - cache JSON round trip
	//   - with -selftest-claude: the real Claude analysis of the PC chip, verified and cached (one or a few requests)
	public static class PlayerSelfTest
	{
		public static bool Active { get; private set; }
		static bool withClaude;
		static string claudeChipName = "Registre8";
		static readonly StringBuilder report = new();
		static int failures;
		static int phase;
		static ChipLibrary lib;
		static ChipDescription claudeChip;
		static int attempt;
		static string lastJson;
		static float started;

		public static void CheckArgs()
		{
			string[] args = Environment.GetCommandLineArgs();
			Active = args.Any(a => a.Equals("-selftest", StringComparison.OrdinalIgnoreCase));
			withClaude = args.Any(a => a.Equals("-selftest-claude", StringComparison.OrdinalIgnoreCase));
			int i = Array.FindIndex(args, a => a.Equals("-selftest-chip", StringComparison.OrdinalIgnoreCase));
			if (i >= 0 && i + 1 < args.Length) claudeChipName = args[i + 1];
		}

		static void Check(string name, Func<string> test)
		{
			string err;
			try { err = test(); }
			catch (Exception e) { err = "EXCEPTION " + e.GetType().Name + ": " + e.Message; }
			if (err == null) report.AppendLine("PASS  " + name);
			else { failures++; report.AppendLine("FAIL  " + name + ": " + err); }
		}

		public static void Update()
		{
			if (phase == 0)
			{
				phase = 1;
				report.AppendLine($"self-test, build {Main.BuildInfoString}, {DateTime.Now}");
				Check("load the PC project", () =>
				{
					Project p = Loader.LoadProject("PC");
					lib = p.chipLibrary;
					return lib.TryGetChipDescription("CPU", out _) ? null : "no CPU chip";
				});
				if (lib != null) SyncChecks();
				if (withClaude && lib != null && lib.TryGetChipDescription(claudeChipName, out claudeChip))
				{
					attempt = 1;
					started = Time.realtimeSinceStartup;
					MemoryLayoutClaude.Start(claudeChip, lib, "PC", null, null);
					phase = 2;
				}
				else phase = 3;
			}
			else if (phase == 2)
			{
				MemoryLayoutClaude.Poll();
				if (MemoryLayoutClaude.State == MemoryLayoutClaude.Status.Running) return;
				string err = null;
				if (MemoryLayoutClaude.State == MemoryLayoutClaude.Status.Failed) err = "request: " + MemoryLayoutClaude.Error;
				else
				{
					lastJson = MemoryLayoutClaude.ResultJson;
					MemoryLayoutClaude.Cancel();
					MemoryRules rules = MemoryLayout.ParseRules(lastJson, out err);
					var pol = new Dictionary<string, int[]>();
					if (rules != null)
						foreach (string t in MemoryLayout.BankTypes(claudeChip, rules, lib))
						{
							err = MemoryLayout.Verify(t, rules, lib, pol);
							if (err != null) break;
						}
					if (rules != null && err == null)
					{
						var cache = MemoryLayout.LoadCache("PC");
						cache[claudeChip.Name] = new MemoryLayout.CacheEntry { hash = MemoryLayout.StructureHash(claudeChip, lib), rules = rules, polarity = pol };
						MemoryLayout.SaveCache("PC", cache);
						bool cached = MemoryLayout.LoadCache("PC").ContainsKey(claudeChip.Name);
						if (!cached) failures++;
						report.AppendLine(cached
							? $"PASS  real Claude analysis of \"{claudeChip.Name}\" verified and cached (attempt {attempt}, {Time.realtimeSinceStartup - started:0} s)"
							: "FAIL  analysis verified but the cache was not written");
						report.AppendLine("      rules: " + lastJson);
						phase = 3;
						return;
					}
				}
				report.AppendLine($"      attempt {attempt} rejected: {err}");
				if (attempt >= 3 || err.StartsWith("request"))
				{
					failures++;
					report.AppendLine($"FAIL  real Claude analysis of \"{claudeChip.Name}\"");
					phase = 3;
					return;
				}
				attempt++;
				MemoryLayoutClaude.Start(claudeChip, lib, "PC", lastJson, err);
			}
			else if (phase == 3)
			{
				report.AppendLine(failures == 0 ? "===== SELF-TEST OK =====" : $"===== SELF-TEST: {failures} FAILED =====");
				try { File.WriteAllText(Path.Combine(SavePaths.AllData, "selftest.txt"), report.ToString()); } catch (Exception) { }
				phase = 4;
				Application.Quit(failures == 0 ? 0 : 1);
			}
		}

		// like the app: every input pin of the chip is driven (0 here), none floats
		static void Step(SimChip root, int n)
		{
			foreach (SimPin p in root.InputPins) if ((p.State >> 16) != 0) p.State = 0;
			for (int i = 0; i < n; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
		}

		static void SyncChecks()
		{
			lib.TryGetChipDescription("CPU", out ChipDescription cpu);
			MemoryRules rules = null;
			var pol = new Dictionary<string, int[]>();
			Check("memory editor: rules parsed (no reflection)", () =>
			{
				TextAsset t = Resources.Load<TextAsset>("SelfTestCpuRules");
				string json = t != null ? t.text : null;
				if (json == null) return "SelfTestCpuRules resource missing";
				rules = MemoryLayout.ParseRules(json, out string e);
				return e;
			});
			if (rules == null) return;
			foreach (string t in MemoryLayout.BankTypes(cpu, rules, lib))
			{
				string type = t;
				Check($"memory editor: {type} layout verified by simulation", () => MemoryLayout.Verify(type, rules, lib, pol));
			}
			SimChip root = null, target = null;
			List<MemoryBank> banks = null;
			Check("memory editor: CPU banks resolved", () =>
			{
				root = CircuitTester.BuildIsolatedSim(cpu, lib);
				target = CircuitTester.TargetOf(root);
				Step(root, 3);
				banks = MemoryLayout.Banks(target, cpu, rules, pol, lib, out string e);
				return banks == null ? e : banks.Count >= 5 ? null : $"only {banks.Count} banks";
			});
			if (banks == null) return;
			var values = new List<(MemoryBank b, int w, uint v)>();
			Check("memory editor: words written and read back", () =>
			{
				var rnd = new System.Random(3);
				foreach (MemoryBank b in banks)
					foreach (int w in new[] { 0, b.WordCount - 1, b.WordCount / 2 }.Distinct())
					{
						uint v = (uint)rnd.Next(1, (int)Math.Min(int.MaxValue, (1L << b.Bits) - 1));
						b.Write(w, v, root.Program);
						values.Add((b, w, v));
					}
				Step(root, 8);
				foreach (var (b, w, v) in values) if (b.Read(w) != v) return $"{b.Name}[{w}] = {b.Read(w)}, wrote {v}";
				return null;
			});
			Check("memory state: saved through the real serializer and restored", () =>
			{
				ChipDescription copy = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(cpu));
				copy.MemoryState = MemorySnapshot.Capture(target);
				ChipDescription back = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(copy));
				if (back.MemoryState == null) return "state lost by the serializer";
				SimChip r2 = CircuitTester.BuildIsolatedSim(back, lib);
				SimChip t2 = CircuitTester.TargetOf(r2);
				Step(r2, 8);
				var banks2 = MemoryLayout.Banks(t2, back, rules, pol, lib, out string e);
				if (banks2 == null) return e;
				foreach (var (b, w, v) in values)
				{
					MemoryBank b2 = banks2.First(x => x.Name == b.Name);
					if (b2.Read(w) != v) return $"{b.Name}[{w}] = {b2.Read(w)} after reload, expected {v}";
				}
				return null;
			});
			Check("memory editor: reversed register bits corrected by simulation", () =>
			{
				MemoryRules r = MemoryLayout.RulesFromJObject(MemoryLayout.RulesToJObject(rules));
				MemoryTypeRule reg = r.types.First(t => t.type == "Registre8");
				string[] right = reg.children.ToArray();
				reg.children = right.Reverse().ToArray();
				string e = MemoryLayout.Verify("Registre8", r, lib, new Dictionary<string, int[]>());
				if (e != null) return "not corrected: " + e;
				return reg.children.SequenceEqual(right) ? null : "corrected to the wrong order";
			});
			Check("memory editor: Registre8 reuses the CPU's analysis (no Claude call)", () =>
			{
				var cache = new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = new MemoryLayout.CacheEntry { hash = "h", rules = rules, polarity = pol } };
				lib.TryGetChipDescription("Registre8", out ChipDescription reg);
				MemoryRules r = MemoryLayout.RulesFromCache(reg, cache, lib, out var p2);
				return r == null ? "not found in the cache" : MemoryLayout.Verify("Registre8", r, lib, p2);
			});
			Check("memory editor: long messages wrapped", () => DLS.Graphics.MemoryEditMenu.Wrap(new string('w', 5) + " " + string.Join(" ", Enumerable.Repeat("word", 80)), 90).Split('\n').All(l => l.Length <= 90) ? null : "line too long");
			Check("memory editor: 8-bit field accepts FF / 255, refuses 100 / 256", () =>
				DLS.Graphics.MemoryEditMenu.FitsWord("FF", 8, 0) && DLS.Graphics.MemoryEditMenu.FitsWord("255", 8, 1) && !DLS.Graphics.MemoryEditMenu.FitsWord("100", 8, 0) && !DLS.Graphics.MemoryEditMenu.FitsWord("256", 8, 1) ? null : "wrong width check");
			Check("memory editor: decimal 3 shown as binary 00000011", () => DLS.Graphics.MemoryEditMenu.ConvertText("3", 8, 1, 2) == "00000011" ? null : "conversion wrong");
			Check("memory editor: a word changed by the circuit updates its field", () =>
			{
				var orig = banks.Select(b => Enumerable.Range(0, b.WordCount).Select(w => b.Read(w)).ToArray()).ToArray();
				var txt = banks.Select((b, i) => orig[i].Select(v => v.ToString()).ToArray()).ToArray();
				banks[0].Write(0, (orig[0][0] + 1) & 0xFF, root.Program);
				Step(root, 1);
				var ch = DLS.Graphics.MemoryEditMenu.Follow(banks, orig, txt, 1);
				return ch.Contains((0, 0)) && txt[0][0] == banks[0].Read(0).ToString() ? null : $"field says {txt[0][0]}, circuit {banks[0].Read(0)}";
			});
			Check("memory editor: cache JSON round trip", () =>
			{
				var c = new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = new MemoryLayout.CacheEntry { hash = "h", rules = rules, polarity = pol } };
				var back = MemoryLayout.CacheFromJson(MemoryLayout.CacheToJson(c));
				return back["CPU"].rules.banks.Count == rules.banks.Count && back["CPU"].polarity.Count == pol.Count ? null : "changed by the round trip";
			});
		}
	}
}
