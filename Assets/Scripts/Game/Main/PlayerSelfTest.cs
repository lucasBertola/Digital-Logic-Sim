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
	// It uses a FROZEN copy of the bench fixture (TestData/Bench/PC), copied by runPlayerSelfTest.bat into the save
	// folder as "_SelfTest_PC" and deleted afterwards: the user's own projects change while they work (CPU was renamed
	// CPU_2 and the self-test failed on it) and are never touched. It writes the report to <save folder>/selftest.txt and quits.
	//   - memory editor: known-good rules parsed, verified by simulation, CPU banks written / read back
	//   - memory state: captured, saved through the real serializer, reloaded, read back
	//   - cache JSON round trip
	//   - with -selftest-claude: the real Claude analysis of the PC chip, verified and cached (one or a few requests)
	public static class PlayerSelfTest
	{
		const string ProjectName = "_SelfTest_PC";
		public static bool Active { get; private set; }
		static bool withClaude;
		static string claudeChipName = "Registre8";
		static readonly StringBuilder report = new();
		static int failures;
		static int phase;
		static ChipLibrary lib;
		static Project loadedProject;
		static ChipDescription claudeChip;
		static int attempt;
		static string lastJson;
		static float started;

		// steps/s of the fixture CPU (saved inputs, builtin clock, 70 steps per transition), batched like the sim
		// thread, managed loop vs Burst kernel, alternated 3 times for 0.7 s each
		static string BurstCpuRate()
		{
			lib.TryGetChipDescription("CPU", out ChipDescription c);
			var best = new double[2];
			Simulator.stepsPerClockTransition = 70;
			for (int round = 0; round < 6; round++)
			{
				int mode = round & 1;
				SimProgram.UseBurst = mode == 1;
				try
				{
					SimChip root = Simulator.BuildSimChip(c, lib);
					for (int i = 0; i < root.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)c.InputPins[i].InputState, 0);
					var audio = new SimAudio();
					Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, 20000);
					var sw = System.Diagnostics.Stopwatch.StartNew();
					long steps = 0;
					while (sw.ElapsedMilliseconds < 700) steps += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, 256);
					best[mode] = Math.Max(best[mode], steps / sw.Elapsed.TotalSeconds);
				}
				finally { SimProgram.UseBurst = true; }
			}
			return $"managed {best[0]:0} steps/s, Burst {best[1]:0} steps/s (x{best[1] / best[0]:0.00}){(Unity.Burst.BurstCompiler.IsEnabled ? "" : " [Burst disabled]")}";
		}

		// write + read cycles per second (one step per pin change), the native RAM65536 vs the fixture's RAM256
		static string RamSpeed()
		{
			double Rate(string chip, int addrPins)
			{
				if (!lib.TryGetChipDescription(chip, out ChipDescription d)) return 0;
				SimChip r = CircuitTester.BuildIsolatedSim(d, lib);
				var names = d.InputPins.Select(p => p.Name).ToList();
				int Pin(string n) => names.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
				int din = Pin("D_in"), we = Pin("We"), oe = Pin(chip == "RAM65536" ? "Oe" : "Oe"), cs = Pin("Cs");
				int adr = chip == "RAM65536" ? Pin("Adr_low") : Pin("Adresses");
				if (din < 0 || we < 0 || oe < 0 || cs < 0 || adr < 0) return 0;
				void Set(int pin, uint v) => r.InputPins[pin].State = PinState.Make((ushort)v, 0);
				var audio = new SimAudio(); // one for the whole run (the Step helper makes one per step, which costs more than the RAM)
				void St(int n) { for (int i = 0; i < n; i++) Simulator.RunSimulationStep(r, Array.Empty<DevPinInstance>(), audio); }
				for (int i = 0; i < d.InputPins.Length; i++) Set(i, 0);
				Set(cs, 1);
				St(10);
				var sw = System.Diagnostics.Stopwatch.StartNew();
				int cycles = 0;
				while (sw.ElapsedMilliseconds < 600)
				{
					uint a = (uint)(cycles * 7) & 0xFF;
					Set(adr, a); Set(din, a ^ 0x5A); St(4); Set(we, 1); St(4); Set(we, 0); Set(oe, 1); St(4); Set(oe, 0); St(4);
					cycles++;
				}
				return cycles / sw.Elapsed.TotalSeconds;
			}
			double native = Rate("RAM65536", 16), latch = Rate("RAM256", 8);
			return $"{native:0} vs {latch:0} write+read cycles/s (16 steps each){(latch > 0 ? $", x{native / latch:0}" : "")}";
		}

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
					Project p = Loader.LoadProject(ProjectName);
					lib = p.chipLibrary;
					loadedProject = p;
					return lib.TryGetChipDescription("CPU", out _) ? null : "no CPU chip";
				});
				if (lib != null) SyncChecks();
				if (withClaude && lib != null && lib.TryGetChipDescription(claudeChipName, out claudeChip))
				{
					attempt = 1;
					started = Time.realtimeSinceStartup;
					MemoryLayoutClaude.Start(claudeChip, lib, ProjectName, null, null);
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
						var cache = MemoryLayout.LoadCache(ProjectName);
						cache[claudeChip.Name] = new MemoryLayout.CacheEntry { hash = MemoryLayout.StructureHash(claudeChip, lib), rules = rules, polarity = pol };
						MemoryLayout.SaveCache(ProjectName, cache);
						bool cached = MemoryLayout.LoadCache(ProjectName).ContainsKey(claudeChip.Name);
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
				MemoryLayoutClaude.Start(claudeChip, lib, ProjectName, lastJson, err);
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
			Check("create chip: ALU8 with half its components extracted computes the same", () =>
			{
				lib.TryGetChipDescription("ALU8", out ChipDescription alu);
				var rnd = new System.Random(4);
				int[] ids = alu.SubChips.Select(s => s.ID).OrderBy(_ => rnd.Next()).Take(alu.SubChips.Length / 2).ToArray();
				ChipExtractor.Result r = ChipExtractor.Extract(alu, ids, "SELFTEST HALF ALU", lib, out string e);
				if (r == null) return e;
				lib.NotifyChipSaved(r.NewChip); // this self-test's own library, nothing is written
				SimChip ra = CircuitTester.BuildIsolatedSim(alu, lib), rb = CircuitTester.BuildIsolatedSim(r.NewParent, lib);
				SimChip ta = CircuitTester.TargetOf(ra), tb = CircuitTester.TargetOf(rb);
				for (int t = 0; t < 100; t++)
				{
					for (int i = 0; i < alu.InputPins.Length; i++)
					{
						uint v = (uint)rnd.Next(1 << (int)alu.InputPins[i].BitCount);
						ra.InputPins[i].State = PinState.Make((ushort)v, 0);
						rb.InputPins[i].State = PinState.Make((ushort)v, 0);
					}
					Step(ra, 12); Step(rb, 12);
					for (int o = 0; o < ta.OutputPins.Length; o++)
						if ((ta.OutputPins[o].State & 0xFF) != (tb.OutputPins[o].State & 0xFF)) return $"vector {t}: output {alu.OutputPins[o].Name} differs";
				}
				return null;
			});
			Check("clock stopped (TURN OFF) in the CPU holds its level while the sim runs", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c0);
				ChipDescription c = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(c0));
				int i = Array.FindIndex(c.SubChips, s => s.Name == "CLOCK");
				if (i < 0) return "no CLOCK in the CPU";
				c.SubChips[i].InternalData = new uint[] { 1, 1 };
				SimChip r = CircuitTester.BuildIsolatedSim(c, lib);
				SimChip clk = CircuitTester.TargetOf(r).SubChips.First(x => x.ChipType == ChipType.Clock);
				Simulator.stepsPerClockTransition = 70;
				for (int k = 0; k < 10; k++) { Step(r, 50); if ((clk.OutputPins[0].State & 1) != 1) return $"stopped clock went low after {(k + 1) * 50} steps"; }
				clk.UpdateInternalState(new uint[] { 1, 0 });
				Step(r, 1);
				return (clk.OutputPins[0].State & 1) == 0 ? null : "a click did not bring it low";
			});
			Check("Ask Claude panel: a selected answer is copied as written", () =>
			{
				string answer = "Le registre A charge la valeur du bus au front montant.\nPuis le bus passe a l'ALU.";
				var lines = DLS.Graphics.AskClaudeMenu.BuildLines(new List<AskClaude.Msg> { new AskClaude.Msg { role = "assistant", text = answer } }, 20);
				int first = lines.FindIndex(l => l.text.Contains("Le registre")), last = lines.FindLastIndex(l => l.text.Trim().Length > 0);
				string got = DLS.Graphics.AskClaudeMenu.SelectedText(lines, (first, 0), (last, lines[last].text.Length));
				return got == answer ? null : "copied as: " + got;
			});
			Check("memory editor: 24-bit word shown with a separator every 8 bits, pasted lines parsed", () =>
			{
				string g = DLS.Graphics.MemoryEditMenu.ConvertText("ABCDEF", 24, 0, 2);
				if (g != "10101011 11001101 11101111") return "grouped as " + g;
				var v = DLS.Graphics.MemoryEditMenu.ParsePasted("AB\r\nCD\nEF\n", 8, 0, out string err);
				return err == null && v.Count == 3 && v[2] == 0xEF ? null : "paste parsed wrong";
			});
			Check("memory editor: the user's program pasted with // comments (binary, editor in hex)", () =>
			{
				string program = "01010000 00000000 00000101//range 5 dans A\r\n01001000 00000000 00000011//ranger 3 dans B\r\n01000100 00000000 00001010//Setter l'adresse MAR a 10\r\n10000010 00000000 00000000// additioner et mettre dans la RAM(10)\r\n00110000 00000000 00000000//mettre la ram dans A\r\n";
				var v = DLS.Graphics.MemoryEditMenu.ParsePasted(program, 24, 0, out string err);
				return err == null && v.Count == 5 && v[0] == 0x500005 && v[4] == 0x300000 ? null : $"{v.Count} words, {err}";
			});
			Check("an unconnected input through a MERGE 1->4 is a driven 0 (the user's ALU4 shifters)", () =>
			{
				ChipDescription merge = lib.GetChipDescription(ChipTypeHelper.GetName(ChipType.Merge_1To4Bit));
				int next = 1;
				PinDescription[] ins = ChipEmitHelper.MakePins(new[] { "C_in", "A3", "A2", "A1" }, true, ref next);
				PinDescription[] outs = ChipEmitHelper.MakePins(new[] { "OUT" }, false, ref next);
				outs[0].BitCount = PinBitCount.Bit4;
				var subs = new List<SubChipDescription>();
				int m = ChipEmitHelper.AddSubChip(subs, ref next, merge.Name, ChipType.Merge_1To4Bit);
				var wires = new List<WireDescription>();
				for (int i = 0; i < 4; i++) wires.Add(ChipEmitHelper.Wire(new PinAddress(ins[i].ID, 0), new PinAddress(m, merge.InputPins[i].ID)));
				wires.Add(ChipEmitHelper.Wire(new PinAddress(m, merge.OutputPins[0].ID), new PinAddress(outs[0].ID, 0)));
				ChipDescription shifter = ChipEmitHelper.Assemble("SELFTEST SHIFT", Color.gray, NameDisplayLocation.Centre, Vector2.zero, ins, outs, subs, wires);
				lib.NotifyChipSaved(shifter); // this self-test's own library, nothing is written
				// a parent that leaves C_in unconnected and drives A3..A1
				next = 1;
				PinDescription[] pIns = ChipEmitHelper.MakePins(new[] { "A3", "A2", "A1" }, true, ref next);
				PinDescription[] pOuts = ChipEmitHelper.MakePins(new[] { "OUT" }, false, ref next);
				pOuts[0].BitCount = PinBitCount.Bit4;
				var pSubs = new List<SubChipDescription>();
				int s = ChipEmitHelper.AddSubChip(pSubs, ref next, shifter.Name, ChipType.Custom);
				var pWires = new List<WireDescription>();
				for (int i = 0; i < 3; i++) pWires.Add(ChipEmitHelper.Wire(new PinAddress(pIns[i].ID, 0), new PinAddress(s, ins[i + 1].ID)));
				pWires.Add(ChipEmitHelper.Wire(new PinAddress(s, outs[0].ID), new PinAddress(pOuts[0].ID, 0)));
				ChipDescription parent = ChipEmitHelper.Assemble("SELFTEST PARENT", Color.gray, NameDisplayLocation.Centre, Vector2.zero, pIns, pOuts, pSubs, pWires);
				SimChip r = CircuitTester.BuildIsolatedSim(parent, lib);
				for (int i = 0; i < 3; i++) r.InputPins[i].State = PinState.Make(1, 0);
				for (int k = 0; k < 2000; k++) Simulator.RunSimulationStep(r, Array.Empty<DevPinInstance>(), new SimAudio());
				uint st = CircuitTester.TargetOf(r).OutputPins[0].State;
				return (st >> 16) == 0 ? null : $"OUT floats (state {st:X8})";
			});
			Check("a chip saved with a new interface: the chips using it lose the removed pin's wires, keep a re-created one's", () =>
			{
				lib.TryGetChipDescription("FullA", out ChipDescription fa);
				lib.TryGetChipDescription("Add4", out ChipDescription add4);
				ChipDescription faNew = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(fa));
				int cin = fa.InputPins.First(x => x.Name == "C_in").ID, a0 = fa.InputPins.First(x => x.Name == "A0").ID;
				faNew.InputPins = faNew.InputPins.Where(x => x.Name != "C_in").Select(x => { if (x.Name == "A0") x.ID = 777; return x; }).ToArray();
				ChipDescription r = ChipInterfaceUpdate.Apply(add4, "FullA", fa, faNew, out bool changed);
				var faIDs = new HashSet<int>(add4.SubChips.Where(s => s.Name == "FullA").Select(s => s.ID));
				int To(ChipDescription d, int pin) => d.Wires.Count(w => faIDs.Contains(w.TargetPinAddress.PinOwnerID) && w.TargetPinAddress.PinID == pin);
				if (!changed || To(r, cin) != 0) return "wires to the removed C_in kept";
				return To(r, 777) == To(add4, a0) && To(add4, a0) > 0 ? null : "the re-created A0 lost its wires";
			});
			Check("LCD DEM122032A: in the DISPLAY collection of an existing project, driven through its pins", () =>
			{
				if (!loadedProject.description.ChipCollections.Any(col => col.Chips.Any(ch => ch == "LCD DEM122032A"))) return "not added to the collections of an existing project";
				ChipDescription lcd = lib.GetChipDescription("LCD DEM122032A");
				SimChip r = CircuitTester.BuildIsolatedSim(lcd, lib);
				SimChip t = CircuitTester.TargetOf(r);
				void Set(int pin, uint v) => r.InputPins[pin].State = PinState.Make((ushort)v, 0);
				void Send(int a0, uint v) { Set(2, 0); Set(1, (uint)a0); Set(0, v); Step(r, 2); Set(3, 1); Step(r, 2); Set(3, 0); Step(r, 2); }
				for (int i = 0; i < 6; i++) Set(i, 0);
				Step(r, 2);
				Send(0, 0xAF); Send(0, 0xB8); Send(0, 4); Send(1, 0x01); // display on, page 0, column 4, top pixel
				if (!LcdDem122032.Pixel(t.InternalState, 4, 0)) return "pixel (4, 0) not lit after the commands";
				string grid = CircuitTester.ReadLcdGrid(t);
				return grid.Split('\n').Length == 16 ? null : "the text rendering for Claude is not 16 lines";
			});
			Check("LCD ST7920 128x64: in the DISPLAY collection of an existing project, text and graphics through its pins, ROM fonts present", () =>
			{
				if (!loadedProject.description.ChipCollections.Any(col => col.Chips.Any(ch => ch == "LCD ST7920 128x64"))) return "not added to the collections of an existing project";
				ChipDescription lcd = lib.GetChipDescription("LCD ST7920 128x64");
				SimChip r = CircuitTester.BuildIsolatedSim(lcd, lib);
				SimChip t = CircuitTester.TargetOf(r);
				void Set(int pin, uint v) => r.InputPins[pin].State = PinState.Make((ushort)v, 0);
				void Send(int rs, uint v) { Set(2, 0); Set(1, (uint)rs); Set(0, v); Step(r, 2); Set(3, 1); Step(r, 2); Set(3, 0); Step(r, 2); }
				for (int i = 0; i < 6; i++) Set(i, 0);
				Set(4, 1); Set(5, 1); // parallel, not in reset
				Step(r, 2);
				foreach (uint cmd in new uint[] { 0x30, 0x0C, 0x01, 0x80 }) Send(0, cmd);
				Send(1, 'A'); Send(1, 'B'); Send(1, 0xD6); Send(1, 0xD0); // "AB" then GB2312 D6D0
				for (int y = 0; y < 16; y++)
					for (int x = 0; x < 8; x++)
						if (LcdSt7920.Pixel(t.InternalState, x, y) != (((St7920Font.Half['A' * 16 + y] >> (7 - x)) & 1) != 0)) return $"'A' drawn wrong at ({x}, {y})";
				int lit = 0;
				for (int y = 0; y < 16; y++) for (int x = 16; x < 32; x++) if (LcdSt7920.Pixel(t.InternalState, x, y)) lit++;
				if (lit < 20) return "the GB2312 16x16 ROM is missing in the build";
				string grid = CircuitTester.ReadSt7920Grid(t);
				return grid.Split('\n').Length == 33 && grid.Contains("\"AB") ? null : "the text rendering for Claude is wrong";
			});
			Check("RAM65536: in the MEMORY collection of an existing project, written and read through its pins", () =>
			{
				if (!loadedProject.description.ChipCollections.Any(col => col.Chips.Any(ch => ch == "RAM65536"))) return "not added to the collections of an existing project";
				SimChip r = CircuitTester.BuildIsolatedSim(lib.GetChipDescription("RAM65536"), lib);
				void Set(int pin, uint v) => r.InputPins[pin].State = PinState.Make((ushort)v, 0);
				for (int i = 0; i < 6; i++) Set(i, 0);
				Set(5, 1); // Cs
				foreach ((uint a, uint v) in new[] { (0x1234u, 0x5Au), (0xFFFFu, 0x77u), (0x0000u, 0x11u) })
				{
					Set(1, a >> 8); Set(2, a & 0xFF); Set(0, v); Step(r, 1); Set(3, 1); Step(r, 1); Set(3, 0); Step(r, 1);
				}
				Set(4, 1); // Oe
				foreach ((uint a, uint v) in new[] { (0x1234u, 0x5Au), (0xFFFFu, 0x77u), (0x0000u, 0x11u) })
				{
					Set(1, a >> 8); Set(2, a & 0xFF); Step(r, 1);
					uint got = r.OutputPins[0].State;
					if ((got & 0xFF) != v || (got >> 16) != 0) return $"read {got:X} at {a:X4}, wrote {v:X2}";
				}
				return null;
			});
			report.AppendLine("      RAM65536 vs the RAM256 built from latches: " + RamSpeed());
			Check("RUN FAST: the CPU's fast tree gives the gates' outputs, cycle after cycle; the cache survives its file", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c);
				var cache = new FastCache();
				uint[] ins = c.InputPins.Select(x => x.InputState).ToArray();
				string diff = FastVerify.Compare(c, lib, cache, 70, 200, ins);
				if (diff != null) return "gates and fast mode differ at " + diff;
				var back = new FastCache();
				FastCacheFile.FromJson(FastCacheFile.ToJson(cache), back);
				var inst = new List<FastBuilder.Instance>();
				FastBuilder.Build(c, lib, back, inst);
				return inst.Count > 0 && back.Entries.Count() == cache.Entries.Count() ? null : $"cache round trip: {back.Entries.Count()} of {cache.Entries.Count()} entries, {inst.Count} models";
			});
			Check("automatic RUN FAST: an empty cache is not ready, Prepare makes the CPU ready from a cache file alone; the cache file is shipped but not hashed", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c);
				var cache = new FastCache();
				if (FastBuilder.CachedModels(c, lib, cache) != -1) return "an empty cache counts as ready";
				FastBuilder.Prepare(c, lib, cache);
				var back = new FastCache();
				FastCacheFile.FromJson(FastCacheFile.ToJson(cache), back);
				int n = FastBuilder.CachedModels(c, lib, back);
				if (n <= 0) return $"after Prepare and a file round trip: {n} models ready";
				back.Changed = false;
				var inst = new List<FastBuilder.Instance>();
				FastBuilder.Build(c, lib, back, inst);
				if (back.Changed || inst.Count != n) return $"Build after Prepare: computed {back.Changed}, {inst.Count} models for {n} announced";
				if (!BundledProjects.IsContentFile(BundledProjects.FastCacheFileName)) return "the cache file is not shipped";
				return null;
			});
			Check("Claude without a key: the key popup instead of the action, CANCEL does nothing, SAVE keeps the key and runs it", () =>
			{
				var menuBefore = DLS.Graphics.UIDrawer.ActiveMenu;
				string tmp = Path.Combine(Application.temporaryCachePath, "selftest_key.txt");
				AskClaude.KeyPathOverrideForTests = tmp;
				AskClaude.IgnoreEnvForTests = true;
				try
				{
					if (File.Exists(tmp)) File.Delete(tmp);
					bool ran = false;
					DLS.Graphics.ApiKeyPopup.Require(() => ran = true);
					if (ran || DLS.Graphics.UIDrawer.ActiveMenu != DLS.Graphics.UIDrawer.MenuType.ApiKey) return "no popup without a key";
					DLS.Graphics.ApiKeyPopup.Cancel();
					if (ran || File.Exists(tmp)) return "CANCEL did something";
					DLS.Graphics.ApiKeyPopup.Require(() => ran = true);
					DLS.Graphics.ApiKeyPopup.Confirm("sk-ant-selftest");
					return ran && AskClaude.ApiKey == "sk-ant-selftest" ? null : "SAVE did not keep the key / run the action";
				}
				finally
				{
					AskClaude.IgnoreEnvForTests = false;
					AskClaude.KeyPathOverrideForTests = null;
					DLS.Graphics.UIDrawer.SetActiveMenu(menuBefore);
					try { File.Delete(tmp); } catch { }
				}
			});
			report.AppendLine("      burst experiment: " + BurstBench.Run());
			foreach (bool fastTree in new[] { false, true })
			Check(fastTree ? "burst stepper = managed stepper in the built player (CPU RUN FAST tree: models in the kernel, every slot, 1500 steps)" : "burst stepper = managed stepper in the built player (CPU, every slot, 1500 steps)", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c);
				var traces = new List<uint[]>[2];
				for (int pass = 0; pass < 2; pass++)
				{
					SimProgram.UseBurst = pass == 1;
					try
					{
						SimChip root = fastTree ? FastBuilder.Build(c, lib, new FastCache(), new List<FastBuilder.Instance>()) : Simulator.BuildSimChip(c, lib);
						Simulator.ResetForTests(77);
						var rnd = new System.Random(5);
						traces[pass] = new List<uint[]>();
						for (int s = 0; s < 1500; s++)
						{
							if (s % 6 == 0) foreach (SimPin p in root.InputPins) if (rnd.Next(2) == 0) p.State = PinState.Make((ushort)rnd.Next(256), 0);
							Simulator.forcedClockState = (s / 9) & 1;
							Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
							traces[pass].Add((uint[])root.Program.states.Clone());
						}
					}
					finally { SimProgram.UseBurst = true; Simulator.forcedClockState = -1; Simulator.ClearTestSeed(); }
				}
				for (int s = 0; s < 1500; s++) if (!traces[0][s].SequenceEqual(traces[1][s])) return "states differ at step " + s;
				return SimKernel.CompiledByBurst() == 1 ? null : "the kernel is not compiled by Burst in the player";
			});
			report.AppendLine("      burst on the CPU, batched like the sim thread: " + BurstCpuRate());
			Check("RECORD CLOCK STEPS NEEDED: the batched Burst loop records what the reference loop records (CPU, builtin clock)", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c);
				var res = new (int, long, long)[2];
				for (int pass = 0; pass < 2; pass++)
				{
					try
					{
						SimChip root = Simulator.BuildSimChip(c, lib);
						Simulator.ResetForTests(43);
						Simulator.stepsPerClockTransition = 70;
						foreach (SimPin pin in root.InputPins) pin.State = 0;
						var audioS = new SimAudio();
						Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audioS);
						root.Program.ResetSettle(); root.Program.RecordSettle = true;
						for (int bt = 0; bt < 300; bt++)
						{
							int doneS = 0;
							while (doneS < 300) doneS += pass == 0 ? Simulator.RunSimulationStepsReference(root, Array.Empty<DevPinInstance>(), audioS, 300 - doneS) : Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audioS, 300 - doneS);
						}
						res[pass] = (root.Program.SettleMax, root.Program.SettleEdges, root.Program.SettleUnsettled);
					}
					finally { Simulator.ClearTestSeed(); }
				}
				return res[0].Item2 > 0 && res[0] == res[1] ? null : $"reference {res[0]}, batched {res[1]} (max, edges, not settled)";
			});
			// 2026-10-04: the whole batched loop runs in the Burst kernel (SimKernel.RunBatch), compiled ahead of time here
			foreach (bool fastTree in new[] { false, true })
			Check(fastTree ? "batched loop in the Burst kernel = reference loop in the built player (CPU RUN FAST tree, builtin clock, every slot after every batch)" : "batched loop in the Burst kernel = reference loop in the built player (CPU, builtin clock, every slot after every batch)", () =>
			{
				lib.TryGetChipDescription("CPU", out ChipDescription c);
				var traces = new List<uint[]>[2];
				for (int pass = 0; pass < 2; pass++)
				{
					try
					{
						SimChip root = fastTree ? FastBuilder.Build(c, lib, new FastCache(), new List<FastBuilder.Instance>()) : Simulator.BuildSimChip(c, lib);
						Simulator.ResetForTests(31);
						Simulator.stepsPerClockTransition = 70;
						var rndK = new System.Random(8);
						var audioK = new SimAudio();
						traces[pass] = new List<uint[]>();
						for (int bt = 0; bt < 300; bt++)
						{
							if (bt % 5 == 0) foreach (SimPin p in root.InputPins) if (rndK.Next(2) == 0) p.State = PinState.Make((ushort)rndK.Next(256), 0);
							int nSteps = 2 + rndK.Next(300);
							int doneK = 0;
							while (doneK < nSteps) doneK += pass == 0 ? Simulator.RunSimulationStepsReference(root, Array.Empty<DevPinInstance>(), audioK, nSteps - doneK) : Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audioK, nSteps - doneK);
							traces[pass].Add((uint[])root.Program.states.Clone());
						}
						if (pass == 1 && !root.Program.CanRunBatchKernel) return "the batch kernel was not used";
					}
					finally { Simulator.ClearTestSeed(); }
				}
				for (int bt = 0; bt < traces[0].Count; bt++) if (!traces[0][bt].SequenceEqual(traces[1][bt])) return "states differ after batch " + bt;
				return null;
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
