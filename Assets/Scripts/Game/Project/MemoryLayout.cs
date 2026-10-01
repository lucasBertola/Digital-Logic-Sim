using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DLS.Description;
using DLS.SaveSystem;
using DLS.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DLS.Game
{
	// ---------------------------------------------------------------------------------------------------------------
	// Memory editor model: how the memory CELLS of a chip (cross-coupled NAND pairs — latches, flip-flops) group into
	// WORDS the user can edit. The grouping ("rules") comes from Claude, reading the chip's structure (MemoryLayoutClaude),
	// and is VERIFIED by simulation before it is used (Verify): values are written into the cells the rules designate
	// and read back through the chip's own pins. Builtin ROM / RAM chips are banks by themselves (no rule needed).
	//
	// Rules (per chip TYPE, so a RAM made of 256 identical words costs one line per level):
	//   word  : children = the components holding bit 0, bit 1, ... (least significant first); every cell inside a
	//           component belongs to that bit (a master/slave flip-flop has two). "A+B" joins components; raw NAND
	//           gates may be listed ("NAND#3+NAND#4").
	//   array : children = sub-components holding successive blocks of words, in the order of the address value that
	//           selects them: address = childIndex * wordsPerChild + address inside the child.
	//   read  : per bank type, how to read word `a` through its own pins (input pin = expression of a; unlisted
	//           inputs 0; clock held low) and which output pins carry the word, least significant first.
	// ---------------------------------------------------------------------------------------------------------------

	public class MemoryRules
	{
		public List<MemoryBankRef> banks = new();
		public List<MemoryTypeRule> types = new();
		public List<MemoryReadRule> reads = new();
	}

	public class MemoryBankRef
	{
		public string name;
		public string[] path = Array.Empty<string>();
	}

	public class MemoryTypeRule
	{
		public string type;
		public string kind; // "word" | "array"
		public string[] children = Array.Empty<string>();
	}

	public class MemoryReadRule
	{
		public string type;
		public List<MemoryPinValue> inputs = new();
		public string[] output = Array.Empty<string>();
	}

	public class MemoryPinValue
	{
		public string pin;
		public string value;
	}

	// One memory cell: a cross-coupled NAND pair. Value v is stored as A = v ^ Pol, B = !A.
	public class MemCell
	{
		public SimChip A, B;
		public int Pol;
		public string HolderType;
		public string WordType; // the chip type whose "word" rule this cell belongs to
		public int IndexInHolder;

		public int Read() => (int)(A.OutputPins[0].State & 1) ^ Pol;

		public void Write(int v, SimProgram prog)
		{
			uint a = (uint)((v ^ Pol) & 1);
			A.OutputPins[0].State = a;
			B.OutputPins[0].State = a ^ 1;
			if (prog != null) { prog.NotifyPinWritten(A.OutputPins[0]); prog.NotifyPinWritten(B.OutputPins[0]); }
		}
	}

	// One editable memory: a list of words.
	public class MemoryBank
	{
		public string Name;
		public int Bits;
		public List<List<List<MemCell>>> Words; // word -> bit -> cells (latch banks)
		public SimChip Builtin;                 // builtin ROM / RAM bank
		public int BuiltinWords;
		// Several memories on the SAME address (parallel chips, e.g. 3 RAM256 holding the 3 bytes of a 24-bit
		// instruction) are ONE memory (user, 2026-10-02: "a memory is N addresses of words, full stop"): its word is
		// the parts side by side, the first part in the low bits.
		public List<MemoryBank> Parts;

		public int WordCount => Parts != null ? Parts[0].WordCount : Builtin != null ? BuiltinWords : Words.Count;

		public uint Read(int w)
		{
			if (Parts != null)
			{
				uint whole = 0;
				int shift = 0;
				foreach (MemoryBank part in Parts) { whole |= part.Read(w) << shift; shift += part.Bits; }
				return whole;
			}
			if (Builtin != null) return Builtin.InternalState[w] & (uint)((1L << Bits) - 1);
			uint v = 0;
			List<List<MemCell>> word = Words[w];
			for (int b = 0; b < word.Count; b++) if (word[b].Count > 0 && word[b][word[b].Count - 1].Read() == 1) v |= 1u << b;
			return v;
		}

		public void Write(int w, uint value, SimProgram prog)
		{
			if (Parts != null)
			{
				int shift = 0;
				foreach (MemoryBank part in Parts) { part.Write(w, (uint)((value >> shift) & ((1UL << part.Bits) - 1)), prog); shift += part.Bits; }
				return;
			}
			if (Builtin != null)
			{
				Builtin.InternalState[w] = value & (uint)((1L << Bits) - 1);
				Builtin.InternalStateEdited = true; // the gate re-runs (ROM), the RAM output follows at its next run
				return;
			}
			List<List<MemCell>> word = Words[w];
			for (int b = 0; b < word.Count; b++)
				foreach (MemCell c in word[b]) c.Write((int)((value >> b) & 1), prog);
		}
	}

	public static class MemoryLayout
	{
		// ---------------------------------------------------------------- cells

		// Cross-coupled NAND pairs in a sub-tree (children walked in ID order, so the order is stable).
		public static List<MemCell> FindCells(SimChip chip)
		{
			var cells = new List<MemCell>();
			Collect(chip, cells);
			return cells;
		}

		static void Collect(SimChip chip, List<MemCell> cells)
		{
			if (chip == null || chip.IsBuiltin) return;
			SimChip[] children = chip.SubChips.Where(c => c != null).OrderBy(c => c.ID).ToArray();
			CollectPairs(children, cells);
			foreach (SimChip c in children) if (!c.IsBuiltin) Collect(c, cells);
		}

		// pairs among the given sibling NANDs
		static void CollectPairs(IList<SimChip> siblings, List<MemCell> cells)
		{
			var nands = siblings.Where(c => c.IsBuiltin && c.ChipType == ChipType.Nand && c.OutputPins.Length > 0).ToList();
			var used = new HashSet<SimChip>();
			foreach (SimChip x in nands)
			{
				if (used.Contains(x)) continue;
				foreach (SimChip y in nands)
				{
					if (y == x || used.Contains(y) || y.ID < x.ID) continue;
					if (Feeds(x, y) && Feeds(y, x))
					{
						cells.Add(new MemCell { A = x, B = y });
						used.Add(x); used.Add(y);
						break;
					}
				}
			}
		}

		static bool Feeds(SimChip from, SimChip to)
		{
			foreach (SimPin t in from.OutputPins[0].ConnectedTargetPins) if (t.parentChip == to) return true;
			return false;
		}

		static bool IsBuiltinBank(ChipType t) => t is ChipType.Rom_256x16 or ChipType.dev_Ram_8Bit;

		// Builtin ROM / RAM anywhere under the chip (component-name paths for display)
		public static List<MemoryBank> BuiltinBanks(SimChip chip, ChipDescription desc, ChipLibrary lib, string prefix = "")
		{
			var banks = new List<MemoryBank>();
			if (chip == null || desc == null) return banks;
			Dictionary<int, string> names = CircuitExporter.BuildComponentNames(desc);
			foreach (SubChipDescription sd in desc.SubChips.OrderBy(s => s.ID))
			{
				(bool ok, SimChip c) = chip.TryGetSubChipFromID(sd.ID);
				if (!ok) continue;
				string label = string.IsNullOrEmpty(sd.Label) ? names[sd.ID] : sd.Label;
				string path = prefix.Length == 0 ? label : prefix + " / " + label;
				if (c.IsBuiltin)
				{
					if (c.ChipType == ChipType.Rom_256x16) banks.Add(new MemoryBank { Name = path, Bits = 16, Builtin = c, BuiltinWords = 256 });
					else if (c.ChipType == ChipType.dev_Ram_8Bit) banks.Add(new MemoryBank { Name = path, Bits = 8, Builtin = c, BuiltinWords = 256 });
				}
				else if (lib.GetChipDescriptionForSim(sd.Name) is { } cd) banks.AddRange(BuiltinBanks(c, cd, lib, path));
			}
			return banks;
		}

		// ---------------------------------------------------------------- resolution

		static MemoryTypeRule RuleFor(MemoryRules rules, string type) => rules.types.FirstOrDefault(t => ChipDescription.NameMatch(t.type, type));
		public static MemoryReadRule ReadFor(MemoryRules rules, string type) => rules.reads.FirstOrDefault(t => ChipDescription.NameMatch(t.type, type));

		// component name (as in the export, "RAM64#2") or label -> sub-chip description
		static SubChipDescription? Component(ChipDescription desc, string name)
		{
			Dictionary<int, string> names = CircuitExporter.BuildComponentNames(desc);
			string n = name.Trim();
			foreach (SubChipDescription sd in desc.SubChips)
				if (string.Equals(names[sd.ID], n, StringComparison.OrdinalIgnoreCase)) return sd;
			foreach (SubChipDescription sd in desc.SubChips)
				if (!string.IsNullOrEmpty(sd.Label) && string.Equals(sd.Label, n, StringComparison.OrdinalIgnoreCase)) return sd;
			return null;
		}

		// Words of a bank chip, per its rules. Returns null + error on anything inconsistent.
		public static List<List<List<MemCell>>> ResolveWords(SimChip sim, ChipDescription desc, MemoryRules rules, ChipLibrary lib, Dictionary<string, int[]> polarity, out string error)
		{
			var words = new List<List<List<MemCell>>>();
			error = Resolve(sim, desc, rules, lib, polarity, words, 0);
			if (error != null) return null;
			if (words.Count == 0) { error = $"no word found in {desc.Name}"; return null; }
			int bits = words[0].Count;
			if (words.Any(w => w.Count != bits)) { error = $"the words of {desc.Name} do not all have the same number of bits"; return null; }
			return words;
		}

		static string Resolve(SimChip sim, ChipDescription desc, MemoryRules rules, ChipLibrary lib, Dictionary<string, int[]> polarity, List<List<List<MemCell>>> words, int depth)
		{
			if (depth > 16) return "rule recursion too deep";
			MemoryTypeRule rule = RuleFor(rules, desc.Name);
			if (rule == null) return $"no rule for chip type \"{desc.Name}\"";
			if (rule.children == null || rule.children.Length == 0) return $"rule for \"{desc.Name}\" lists no component";
			bool isWord = string.Equals(rule.kind, "word", StringComparison.OrdinalIgnoreCase);
			bool isArray = string.Equals(rule.kind, "array", StringComparison.OrdinalIgnoreCase);
			if (!isWord && !isArray) return $"rule for \"{desc.Name}\": unknown kind \"{rule.kind}\"";

			if (isWord)
			{
				var word = new List<List<MemCell>>();
				for (int bit = 0; bit < rule.children.Length; bit++)
				{
					var cells = new List<MemCell>();
					string holderType = null;
					var rawNands = new List<SimChip>();
					foreach (string part in rule.children[bit].Split('+'))
					{
						SubChipDescription? sdq = Component(desc, part);
						if (sdq == null) return $"\"{desc.Name}\" has no component \"{part.Trim()}\"";
						SubChipDescription sd = sdq.Value;
						(bool ok, SimChip c) = sim.TryGetSubChipFromID(sd.ID);
						if (!ok) return $"component \"{part.Trim()}\" missing in the simulation";
						if (c.IsBuiltin) { rawNands.Add(c); holderType ??= desc.Name + "/" + sd.Name; }
						else { cells.AddRange(FindCells(c)); holderType ??= sd.Name; }
					}
					if (rawNands.Count > 0) CollectPairs(rawNands, cells);
					if (cells.Count == 0) return $"bit {bit} of \"{desc.Name}\" (\"{rule.children[bit]}\") holds no memory cell";
					for (int j = 0; j < cells.Count; j++)
					{
						cells[j].HolderType = holderType;
						cells[j].WordType = desc.Name;
						cells[j].IndexInHolder = j;
						cells[j].Pol = polarity != null && polarity.TryGetValue(holderType, out int[] p) && j < p.Length ? p[j] : 0;
					}
					word.Add(cells);
				}
				words.Add(word);
				return null;
			}

			int before = words.Count, perChild = -1;
			foreach (string childName in rule.children)
			{
				SubChipDescription? sdq = Component(desc, childName);
				if (sdq == null) return $"\"{desc.Name}\" has no component \"{childName.Trim()}\"";
				SubChipDescription sd = sdq.Value;
				(bool ok, SimChip c) = sim.TryGetSubChipFromID(sd.ID);
				if (!ok || c.IsBuiltin) return $"component \"{childName.Trim()}\" of \"{desc.Name}\" is not a custom chip";
				ChipDescription cd = lib.GetChipDescriptionForSim(sd.Name);
				if (cd == null) return $"chip \"{sd.Name}\" not found";
				int start = words.Count;
				string e = Resolve(c, cd, rules, lib, polarity, words, depth + 1);
				if (e != null) return e;
				int n = words.Count - start;
				if (perChild < 0) perChild = n;
				else if (n != perChild) return $"the components of \"{desc.Name}\" do not hold the same number of words";
			}
			return words.Count > before ? null : $"\"{desc.Name}\" holds no word";
		}

		// Banks of a placed chip (latch banks per rules, builtin ROM/RAM found by walking)
		public static List<MemoryBank> Banks(SimChip chip, ChipDescription desc, MemoryRules rules, Dictionary<string, int[]> polarity, ChipLibrary lib, out string error)
		{
			error = null;
			var banks = new List<MemoryBank>();
			var where = new List<(string parentPath, ChipDescription parent, SubChipDescription? inst, ChipDescription type)>();
			if (rules != null)
			{
				foreach (MemoryBankRef b in rules.banks)
				{
					SimChip s = chip;
					ChipDescription d = desc;
					string label = null;
					ChipDescription parentDesc = desc;
					SubChipDescription? inst = null;
					var parentPath = new List<string>();
					foreach (string step in b.path ?? Array.Empty<string>())
					{
						SubChipDescription? sdq = Component(d, step);
						if (sdq == null) { error = $"bank \"{b.name}\": \"{d.Name}\" has no component \"{step}\""; return null; }
						SubChipDescription sd = sdq.Value;
						if (inst != null) parentPath.Add(inst.Value.ID.ToString());
						parentDesc = d;
						inst = sd;
						(bool ok, SimChip c) = s.TryGetSubChipFromID(sd.ID);
						if (!ok) { error = $"bank \"{b.name}\": component \"{step}\" missing in the simulation"; return null; }
						s = c;
						d = lib.GetChipDescriptionForSim(sd.Name);
						label = string.IsNullOrEmpty(sd.Label) ? null : sd.Label;
						if (d == null) { error = $"bank \"{b.name}\": chip \"{sd.Name}\" not found"; return null; }
					}
					var words = ResolveWords(s, d, rules, lib, polarity, out string e);
					if (words == null) { error = $"bank \"{b.name}\": {e}"; return null; }
					banks.Add(new MemoryBank { Name = !string.IsNullOrEmpty(b.name) ? b.name : label ?? d.Name, Bits = words[0].Count, Words = words });
					where.Add((string.Join("/", parentPath), parentDesc, inst, d));
				}
				banks = MergeParallel(banks, where, rules, desc);
			}
			banks.AddRange(BuiltinBanks(chip, desc, lib));
			return banks;
		}

		// Memories whose address pins are all fed by the same sources, inside the same chip, are one memory with a
		// wider word (parallel RAMs). Address pins = the inputs whose read procedure depends on the address `a`.
		// Several words each, same word count, 32 bits at most together. Parts in natural name order (ram_prog0,
		// ram_prog1, ram_prog2): the first is the low byte. Registers (one word) are never merged.
		static List<MemoryBank> MergeParallel(List<MemoryBank> banks, List<(string parentPath, ChipDescription parent, SubChipDescription? inst, ChipDescription type)> where, MemoryRules rules, ChipDescription analysed)
		{
			string Key(int i)
			{
				var (parentPath, parent, inst, type) = where[i];
				if (inst == null || banks[i].WordCount < 2) return null;
				MemoryReadRule read = rules.reads.FirstOrDefault(r => string.Equals(r.type, type.Name, StringComparison.OrdinalIgnoreCase));
				if (read == null) return null;
				var parts = new List<string>();
				foreach (MemoryPinValue pv in read.inputs.Where(x => UsesAddress(x.value)).OrderBy(x => x.pin, StringComparer.Ordinal))
				{
					PinDescription? pin = (type.InputPins ?? Array.Empty<PinDescription>()).Cast<PinDescription?>().FirstOrDefault(x => x.Value.Name == pv.pin);
					if (pin == null) return null;
					var sources = (parent.Wires ?? Array.Empty<WireDescription>())
						.Where(w => w.TargetPinAddress.PinOwnerID == inst.Value.ID && w.TargetPinAddress.PinID == pin.Value.ID)
						.Select(w => $"{w.SourcePinAddress.PinOwnerID}.{w.SourcePinAddress.PinID}").OrderBy(x => x, StringComparer.Ordinal).ToList();
					if (sources.Count == 0) return null; // an unconnected address: nothing to prove they are parallel
					parts.Add(pv.pin + "=" + string.Join(",", sources));
				}
				return parts.Count == 0 ? null : $"{parentPath}|{banks[i].WordCount}|{string.Join(";", parts)}";
			}

			var keys = Enumerable.Range(0, banks.Count).Select(Key).ToList();
			var result = new List<MemoryBank>();
			var done = new HashSet<int>();
			for (int i = 0; i < banks.Count; i++)
			{
				if (done.Contains(i)) continue;
				var group = keys[i] == null ? new List<int> { i } : Enumerable.Range(0, banks.Count).Where(j => keys[j] == keys[i]).ToList();
				foreach (int j in group) done.Add(j);
				if (group.Count < 2 || group.Sum(j => banks[j].Bits) > 32) { foreach (int j in group) result.Add(banks[j]); continue; }
				var parts = group.Select(j => banks[j]).OrderBy(b => NaturalKey(b.Name), StringComparer.Ordinal).ToList();
				var (parentPath, parent, _, _) = where[group[0]];
				string name = string.IsNullOrEmpty(parentPath) ? analysed.Name : (parent?.Name ?? analysed.Name);
				result.Add(new MemoryBank { Name = name, Bits = parts.Sum(b => b.Bits), Parts = parts });
			}
			return result;
		}

		static bool UsesAddress(string expr) => !string.IsNullOrEmpty(expr) && System.Text.RegularExpressions.Regex.IsMatch(expr, @"(?<![A-Za-z0-9_])a(?![A-Za-z0-9_])");

		// "ram_prog10" sorts after "ram_prog2": digits padded
		static string NaturalKey(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));

		// The chip types of the rules' banks, walked from the analysed chip
		public static List<string> BankTypes(ChipDescription desc, MemoryRules rules, ChipLibrary lib)
		{
			var types = new List<string>();
			foreach (MemoryBankRef b in rules.banks)
			{
				ChipDescription d = desc;
				foreach (string step in b.path ?? Array.Empty<string>())
				{
					SubChipDescription? sdq = d == null ? null : Component(d, step);
					d = sdq == null ? null : lib.GetChipDescriptionForSim(sdq.Value.Name);
				}
				if (d != null && !types.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) types.Add(d.Name);
			}
			return types;
		}

		// ---------------------------------------------------------------- verification

		const int SettleTicks = 12;

		// what Verify fixed by itself (bit order), for display / logs; null = not collected
		[ThreadStatic] public static List<string> Corrections;

		// Verifies the rules of one bank chip type on an isolated copy, learning the cell polarities. Null = OK.
		public static string Verify(string typeName, MemoryRules rules, ChipLibrary lib, Dictionary<string, int[]> polarity, int seed = 7)
		{
			ChipDescription desc = lib.GetChipDescriptionForSim(typeName);
			if (desc == null) return $"chip \"{typeName}\" not found";
			MemoryReadRule read = ReadFor(rules, typeName);
			if (read == null) return $"no read procedure for \"{typeName}\"";
			if (read.output == null || read.output.Length == 0) return $"read procedure of \"{typeName}\" lists no output pin";

			int savedFrame = Simulator.simulationFrame, savedPeriod = Simulator.stepsPerClockTransition;
			try
			{
				Simulator.ResetForTests(seed);
				Simulator.stepsPerClockTransition = 250;
				Simulator.forcedClockState = 0;
				SimChip root = CircuitTester.BuildIsolatedSim(desc, lib);
				SimChip target = CircuitTester.TargetOf(root);
				var audio = new SimAudio();
				void Tick(int n) { for (int i = 0; i < n; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio); }

				// output pins, least significant first
				var outPins = new List<(SimPin pin, int bits)>();
				int width = 0;
				foreach (string name in read.output)
				{
					int j = Array.FindIndex(desc.OutputPins, p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
					if (j < 0) return $"\"{typeName}\" has no output pin \"{name}\"";
					int bits = (int)desc.OutputPins[j].BitCount;
					outPins.Add((target.OutputPins[j], bits));
					width += bits;
				}
				var inIdx = new List<(int index, string expr)>();
				foreach (MemoryPinValue pv in read.inputs ?? new List<MemoryPinValue>())
				{
					int j = Array.FindIndex(desc.InputPins, p => string.Equals(p.Name, (pv.pin ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
					if (j < 0) return $"\"{typeName}\" has no input pin \"{pv.pin}\"";
					if (!Expr.TryEval(pv.value, 0, out _, out string ee)) return $"input \"{pv.pin}\": {ee}";
					inIdx.Add((j, pv.value));
				}

				void Address(int a)
				{
					for (int i = 0; i < root.InputPins.Length; i++) root.InputPins[i].State = 0;
					foreach ((int index, string expr) in inIdx)
					{
						Expr.TryEval(expr, a, out long v, out _);
						int bits = (int)desc.InputPins[index].BitCount;
						root.InputPins[index].State = (uint)(v & ((1L << bits) - 1));
					}
				}
				long ReadOut()
				{
					long v = 0;
					int shift = 0;
					foreach ((SimPin pin, int bits) in outPins)
					{
						uint st = pin.State;
						if ((st >> 16 & ((1u << bits) - 1)) != 0) return -1; // floating
						v |= (long)(st & ((1u << bits) - 1)) << shift;
						shift += bits;
					}
					return v;
				}

				Address(0);
				Tick(SettleTicks);
				var words = ResolveWords(target, desc, rules, lib, null, out string err);
				if (words == null) return err;
				if (words[0].Count != width) return $"a word of \"{typeName}\" has {words[0].Count} bits but the read pins carry {width}";

				SimProgram prog = root.Program;

				// ---- bit order: which output bit does each bit's cells drive? (Claude often reverses it) ----
				// Word 0's bits are written one at a time; the output bit that follows is that bit's true position. A
				// consistent permutation other than the identity reorders the word rule's children, and the rule is
				// used corrected (the caller's rules object is updated, so the cache keeps the correction).
				{
					int n = words[0].Count;
					var perm = new int[n];
					bool ok = true;
					for (int i = 0; i < n && ok; i++)
					{
						List<MemCell> cells = words[0][i];
						int found = -1;
						for (int combo = 0; combo < (1 << Math.Min(cells.Count, 6)) && found < 0; combo++)
						{
							for (int j = 0; j < cells.Count; j++) cells[j].Pol = (combo >> j) & 1;
							foreach (MemCell c in cells) c.Write(1, prog);
							Address(0); Tick(SettleTicks);
							long r1 = ReadOut();
							foreach (MemCell c in cells) c.Write(0, prog);
							Address(0); Tick(SettleTicks);
							long r0 = ReadOut();
							if (r1 < 0 || r0 < 0) continue;
							long diff = r1 ^ r0;
							if (diff != 0 && (diff & (diff - 1)) == 0 && (r1 & diff) != 0 && cells.All(c => c.Read() == 0))
								found = (int)Math.Round(Math.Log(diff, 2));
						}
						if (found < 0) ok = false;
						else perm[i] = found;
					}
					if (ok && perm.Distinct().Count() == n && perm.Where((t, i) => t != i).Any())
					{
						string wordType = words[0][0][0].WordType;
						MemoryTypeRule wr = RuleFor(rules, wordType);
						if (wr != null && wr.children.Length == n)
						{
							var reordered = new string[n];
							for (int i = 0; i < n; i++) reordered[perm[i]] = wr.children[i];
							wr.children = reordered;
							Corrections?.Add($"bit order of \"{wordType}\" corrected by simulation");
							words = ResolveWords(target, desc, rules, lib, null, out err);
							if (words == null) return err;
						}
					}
				}

				// ---- learn the polarity of each holder type ----
				var learnt = new Dictionary<string, int[]>();
				for (int w = 0; w < words.Count; w++)
					for (int b = 0; b < words[w].Count; b++)
					{
						List<MemCell> cells = words[w][b];
						string holder = cells[0].HolderType;
						if (learnt.ContainsKey(holder)) continue;
						int k = cells.Count;
						if (k > 6) return $"\"{holder}\" holds {k} cells for one bit: too many to verify";
						int found = -1;
						for (int combo = 0; combo < (1 << k) && found < 0; combo++)
						{
							for (int j = 0; j < k; j++) cells[j].Pol = (combo >> j) & 1;
							bool ok = true;
							foreach (int v in new[] { 1, 0, 1 })
							{
								foreach (MemCell c in cells) c.Write(v, prog);
								Address(w);
								Tick(SettleTicks);
								long r = ReadOut();
								if (r < 0 || ((r >> b) & 1) != v || cells.Any(c => c.Read() != v)) { ok = false; break; }
							}
							if (ok) found = combo;
						}
						if (found < 0) return $"bit {b} of word {w} of \"{typeName}\" (cells of \"{holder}\") cannot be written and read back: the rule does not match the chip";
						learnt[holder] = Enumerable.Range(0, k).Select(j => (found >> j) & 1).ToArray();
					}
				// apply the learnt polarities
				foreach (var word in words) foreach (var bit in word) foreach (MemCell c in bit) c.Pol = learnt[c.HolderType][c.IndexInHolder];

				// ---- write distinct values in sampled words, read them all back through the pins ----
				var rnd = new Random(seed);
				var sample = new SortedSet<int> { 0, words.Count - 1 };
				if (words.Count <= 32) for (int w = 0; w < words.Count; w++) sample.Add(w);
				else while (sample.Count < 20) sample.Add(rnd.Next(words.Count));
				long mask = (1L << width) - 1;
				var expected = new Dictionary<int, long>();
				foreach (int w in sample)
				{
					long v;
					do v = rnd.Next() & mask; while (expected.ContainsValue(v) && expected.Count < mask);
					expected[w] = v;
					var bank = new MemoryBank { Bits = width, Words = words };
					bank.Write(w, (uint)v, prog);
				}
				Tick(2);
				foreach (int w in sample)
				{
					Address(w);
					Tick(SettleTicks);
					long r = ReadOut();
					if (r != expected[w])
						return $"\"{typeName}\": word {w} written as {expected[w]} reads back as {(r < 0 ? "floating" : r.ToString())} through the pins: the rule does not match the chip";
				}

				foreach (var kv in learnt) polarity[kv.Key] = kv.Value;
				return null;
			}
			catch (Exception e) { return "verification failed: " + e.Message; }
			finally
			{
				Simulator.forcedClockState = -1;
				Simulator.ClearTestSeed();
				Simulator.simulationFrame = savedFrame;
				Simulator.stepsPerClockTransition = savedPeriod;
				Simulator.needsOrderPass = true;
			}
		}

		// ---------------------------------------------------------------- cache (per project)

		public class CacheEntry
		{
			public string hash;
			public MemoryRules rules;
			public Dictionary<string, int[]> polarity = new();
		}

		public static string CachePath(string projectName) => Path.Combine(SavePaths.GetProjectPath(projectName), "MemoryLayouts.json");

		public static Dictionary<string, CacheEntry> LoadCache(string projectName)
		{
			try
			{
				string p = CachePath(projectName);
				if (File.Exists(p)) return CacheFromJson(File.ReadAllText(p));
			}
			catch (Exception) { /* a broken cache is only a cache */ }
			return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
		}

		public static void SaveCache(string projectName, Dictionary<string, CacheEntry> cache)
		{
			try { File.WriteAllText(CachePath(projectName), CacheToJson(cache)); }
			catch (Exception) { /* ignore */ }
		}

		// Structure of a chip and every custom chip below it (names, sub-chips, wires, pins — not positions, not the
		// memory state): a cached layout is reused only while this is unchanged.
		public static string StructureHash(ChipDescription desc, ChipLibrary lib)
		{
			var sb = new StringBuilder();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			void Visit(ChipDescription d)
			{
				if (d == null || !seen.Add(d.Name)) return;
				sb.Append("C:").Append(d.Name).Append('|');
				foreach (PinDescription p in d.InputPins ?? Array.Empty<PinDescription>()) sb.Append("i").Append(p.ID).Append(':').Append(p.Name).Append(':').Append((int)p.BitCount).Append(',');
				foreach (PinDescription p in d.OutputPins ?? Array.Empty<PinDescription>()) sb.Append("o").Append(p.ID).Append(':').Append(p.Name).Append(':').Append((int)p.BitCount).Append(',');
				foreach (SubChipDescription s in (d.SubChips ?? Array.Empty<SubChipDescription>()).OrderBy(s => s.ID)) sb.Append("s").Append(s.ID).Append(':').Append(s.Name).Append(':').Append(s.Label).Append(',');
				foreach (WireDescription w in d.Wires ?? Array.Empty<WireDescription>())
					sb.Append("w").Append(w.SourcePinAddress.PinOwnerID).Append('.').Append(w.SourcePinAddress.PinID).Append('>').Append(w.TargetPinAddress.PinOwnerID).Append('.').Append(w.TargetPinAddress.PinID).Append(',');
				sb.Append('\n');
				foreach (SubChipDescription s in d.SubChips ?? Array.Empty<SubChipDescription>())
				{
					ChipDescription c = lib.GetChipDescriptionForSim(s.Name);
					if (c != null && c.ChipType == ChipType.Custom) Visit(c);
				}
			}
			Visit(desc);
			using SHA1 sha = SHA1.Create();
			return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
		}

		// ---- JSON, by hand: the player build strips the constructors reflection-based deserialisation needs
		// ("Unable to find a constructor to use for type DLS.Game.MemoryRules"), the editor does not ----

		static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? null : (string)t;
		static string[] Strs(JToken t) => t is JArray a ? a.Select(x => (string)x).ToArray() : Array.Empty<string>();

		public static MemoryRules RulesFromJObject(JObject o)
		{
			var r = new MemoryRules();
			foreach (JToken b in o["banks"] as JArray ?? new JArray())
				r.banks.Add(new MemoryBankRef { name = Str(b["name"]), path = Strs(b["path"]) });
			foreach (JToken t in o["types"] as JArray ?? new JArray())
				r.types.Add(new MemoryTypeRule { type = Str(t["type"]), kind = Str(t["kind"]), children = Strs(t["children"]) });
			foreach (JToken t in o["reads"] as JArray ?? new JArray())
			{
				var rr = new MemoryReadRule { type = Str(t["type"]), output = Strs(t["output"]) };
				foreach (JToken pv in t["inputs"] as JArray ?? new JArray()) rr.inputs.Add(new MemoryPinValue { pin = Str(pv["pin"]), value = Str(pv["value"]) });
				r.reads.Add(rr);
			}
			return r;
		}

		public static JObject RulesToJObject(MemoryRules r) => new JObject
		{
			["banks"] = new JArray(r.banks.Select(b => new JObject { ["name"] = b.name, ["path"] = new JArray(b.path ?? Array.Empty<string>()) })),
			["types"] = new JArray(r.types.Select(t => new JObject { ["type"] = t.type, ["kind"] = t.kind, ["children"] = new JArray(t.children ?? Array.Empty<string>()) })),
			["reads"] = new JArray(r.reads.Select(t => new JObject
			{
				["type"] = t.type,
				["inputs"] = new JArray((t.inputs ?? new List<MemoryPinValue>()).Select(p => new JObject { ["pin"] = p.pin, ["value"] = p.value })),
				["output"] = new JArray(t.output ?? Array.Empty<string>())
			}))
		};

		public static string CacheToJson(Dictionary<string, CacheEntry> cache)
		{
			var root = new JObject();
			foreach (var kv in cache)
			{
				var pol = new JObject();
				foreach (var p in kv.Value.polarity ?? new Dictionary<string, int[]>()) pol[p.Key] = new JArray(p.Value);
				root[kv.Key] = new JObject { ["hash"] = kv.Value.hash, ["rules"] = kv.Value.rules == null ? null : RulesToJObject(kv.Value.rules), ["polarity"] = pol };
			}
			return root.ToString(Formatting.Indented);
		}

		public static Dictionary<string, CacheEntry> CacheFromJson(string json)
		{
			var cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
			JObject root = JObject.Parse(json);
			foreach (var prop in root.Properties())
			{
				if (prop.Value is not JObject e) continue;
				var entry = new CacheEntry { hash = Str(e["hash"]), rules = e["rules"] is JObject ro ? RulesFromJObject(ro) : null };
				if (e["polarity"] is JObject pol)
					foreach (var p in pol.Properties()) entry.polarity[p.Name] = p.Value is JArray a ? a.Select(x => (int)x).ToArray() : Array.Empty<int>();
				cache[prop.Name] = entry;
			}
			return cache;
		}

		// Rules for a chip whose TYPE rules are already known from another cached analysis (Registre B after
		// Registre A, a RAM256 inside another CPU...): the bank is the chip itself, the types and reads needed are
		// collected from the cache. Null when some needed rule is missing. Still verified by the caller.
		public static MemoryRules RulesFromCache(ChipDescription desc, Dictionary<string, CacheEntry> cache, ChipLibrary lib, out Dictionary<string, int[]> polarity)
		{
			polarity = new Dictionary<string, int[]>();
			var types = new Dictionary<string, MemoryTypeRule>(StringComparer.OrdinalIgnoreCase);
			var reads = new Dictionary<string, MemoryReadRule>(StringComparer.OrdinalIgnoreCase);
			foreach (CacheEntry e in cache.Values)
			{
				if (e?.rules == null) continue;
				foreach (MemoryTypeRule t in e.rules.types) if (t.type != null && !types.ContainsKey(t.type)) types[t.type] = t;
				foreach (MemoryReadRule r in e.rules.reads) if (r.type != null && !reads.ContainsKey(r.type)) reads[r.type] = r;
				foreach (var p in e.polarity ?? new Dictionary<string, int[]>()) if (!polarity.ContainsKey(p.Key)) polarity[p.Key] = p.Value;
			}
			if (!types.ContainsKey(desc.Name) || !reads.ContainsKey(desc.Name)) return null;
			var rules = new MemoryRules();
			rules.banks.Add(new MemoryBankRef { name = desc.Name, path = Array.Empty<string>() });
			rules.reads.Add(reads[desc.Name]);
			// the type and every type below it that has a rule
			var queue = new Queue<string>(new[] { desc.Name });
			var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			while (queue.Count > 0)
			{
				string tn = queue.Dequeue();
				if (!added.Add(tn) || !types.TryGetValue(tn, out MemoryTypeRule t)) continue;
				rules.types.Add(t);
				ChipDescription d = lib.GetChipDescriptionForSim(tn);
				if (d == null) continue;
				foreach (SubChipDescription s in d.SubChips ?? Array.Empty<SubChipDescription>()) queue.Enqueue(s.Name);
			}
			return rules;
		}

		// Parses Claude's structured answer
		public static MemoryRules ParseRules(string json, out string error)
		{
			error = null;
			try
			{
				if (string.IsNullOrWhiteSpace(json)) { error = "empty answer"; return null; }
				MemoryRules r = RulesFromJObject(JObject.Parse(json));
				r.banks ??= new List<MemoryBankRef>();
				r.types ??= new List<MemoryTypeRule>();
				r.reads ??= new List<MemoryReadRule>();
				foreach (MemoryBankRef b in r.banks) b.path ??= Array.Empty<string>();
				foreach (MemoryTypeRule t in r.types) t.children ??= Array.Empty<string>();
				foreach (MemoryReadRule t in r.reads) { t.inputs ??= new List<MemoryPinValue>(); t.output ??= Array.Empty<string>(); }
				return r;
			}
			catch (Exception e) { error = "unreadable answer: " + e.Message; return null; }
		}
	}

	// ---------------------------------------------------------------------------------------------------------------
	// Tiny integer expression evaluator for the read procedures: numbers (decimal, 0x hex, 0b binary), the variable
	// `a`, + - * / % & | ^ ~ << >> and parentheses, C precedence.
	// ---------------------------------------------------------------------------------------------------------------
	public static class Expr
	{
		public static bool TryEval(string text, long a, out long value, out string error)
		{
			value = 0;
			error = null;
			if (string.IsNullOrWhiteSpace(text)) { error = "empty expression"; return false; }
			var p = new Parser(text, a);
			try
			{
				value = p.Or();
				p.SkipWs();
				if (!p.End) { error = $"unexpected \"{text.Substring(p.Pos)}\" in \"{text}\""; return false; }
				return true;
			}
			catch (Exception e) { error = $"bad expression \"{text}\": {e.Message}"; return false; }
		}

		class Parser
		{
			readonly string s;
			readonly long a;
			public int Pos;
			public Parser(string s, long a) { this.s = s; this.a = a; }
			public bool End => Pos >= s.Length;
			public void SkipWs() { while (Pos < s.Length && char.IsWhiteSpace(s[Pos])) Pos++; }
			bool Eat(string op)
			{
				SkipWs();
				if (string.CompareOrdinal(s, Pos, op, 0, op.Length) == 0) { Pos += op.Length; return true; }
				return false;
			}
			public long Or() { long v = Xor(); while (true) { SkipWs(); if (Pos < s.Length && s[Pos] == '|') { Pos++; v |= Xor(); } else return v; } }
			long Xor() { long v = And(); while (Eat("^")) v ^= And(); return v; }
			long And() { long v = Shift(); while (true) { SkipWs(); if (Pos < s.Length && s[Pos] == '&') { Pos++; v &= Shift(); } else return v; } }
			long Shift()
			{
				long v = Add();
				while (true)
				{
					if (Eat("<<")) v <<= (int)Add();
					else if (Eat(">>")) v >>= (int)Add();
					else return v;
				}
			}
			long Add()
			{
				long v = Mul();
				while (true)
				{
					if (Eat("+")) v += Mul();
					else if (Eat("-")) v -= Mul();
					else return v;
				}
			}
			long Mul()
			{
				long v = Unary();
				while (true)
				{
					if (Eat("*")) v *= Unary();
					else if (Eat("/")) { long d = Unary(); if (d == 0) throw new Exception("division by zero"); v /= d; }
					else if (Eat("%")) { long d = Unary(); if (d == 0) throw new Exception("division by zero"); v %= d; }
					else return v;
				}
			}
			long Unary()
			{
				if (Eat("~")) return ~Unary();
				if (Eat("-")) return -Unary();
				if (Eat("+")) return Unary();
				return Atom();
			}
			long Atom()
			{
				SkipWs();
				if (Eat("("))
				{
					long v = Or();
					if (!Eat(")")) throw new Exception("missing )");
					return v;
				}
				if (Pos < s.Length && (s[Pos] == 'a' || s[Pos] == 'A') && (Pos + 1 >= s.Length || !char.IsLetterOrDigit(s[Pos + 1]))) { Pos++; return a; }
				int start = Pos;
				if (Pos + 1 < s.Length && s[Pos] == '0' && (s[Pos + 1] == 'x' || s[Pos + 1] == 'X'))
				{
					Pos += 2; int h = Pos;
					while (Pos < s.Length && Uri.IsHexDigit(s[Pos])) Pos++;
					if (Pos == h) throw new Exception("bad hex number");
					return Convert.ToInt64(s.Substring(h, Pos - h), 16);
				}
				if (Pos + 1 < s.Length && s[Pos] == '0' && (s[Pos + 1] == 'b' || s[Pos + 1] == 'B'))
				{
					Pos += 2; int h = Pos;
					while (Pos < s.Length && (s[Pos] == '0' || s[Pos] == '1')) Pos++;
					if (Pos == h) throw new Exception("bad binary number");
					return Convert.ToInt64(s.Substring(h, Pos - h), 2);
				}
				while (Pos < s.Length && char.IsDigit(s[Pos])) Pos++;
				if (Pos == start) throw new Exception($"unexpected \"{(Pos < s.Length ? s[Pos].ToString() : "end")}\"");
				return long.Parse(s.Substring(start, Pos - start));
			}
		}
	}
}
