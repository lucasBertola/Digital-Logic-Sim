using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.SaveSystem;
using DLS.Simulation;

namespace DLS.Game
{
	// RUN FAST: the simulation tree of a chip where every sub-module that has a behavioural model is a single gate
	// (a ChipType.FastModel leaf), the others being built as usual and searched for models one level down.
	//  - combinational module of at most MaxLutBits input bits -> a truth table computed by the real simulator
	//    (exact by construction, floating outputs included) and re-checked in another order;
	//  - sequential module -> a template model (FastTemplates) if one is verified against the gates;
	//  - a module holding a clock, a key, a display, a buzzer, a ROM, a builtin RAM or a pulse is never modelled
	//    (its inside has a life of its own or is visible): it is built and its parts are tried instead.
	// Decisions are cached per chip by its structure hash (FastCache).
	public static class FastBuilder
	{
		public const int MaxLutBits = 16;

		public class Instance
		{
			public int[] Path;          // sub-chip IDs from the root to the modelled module
			public SimChip Leaf;
			public ChipDescription Desc;
		}

		public static SimChip Build(ChipDescription root, ChipLibrary lib, FastCache cache, List<Instance> instances)
		{
			var hashes = new Dictionary<string, string>(ChipDescription.NameComparer); // one structure hash per chip type and build
			return Node(root, -1, null, new List<int>(), true, lib, cache, instances, hashes);
		}

		static SimChip Node(ChipDescription d, int id, uint[] internalData, List<int> path, bool isRoot, ChipLibrary lib, FastCache cache, List<Instance> instances, Dictionary<string, string> hashes)
		{
			if (d == null) return new SimChip();
			if (d.ChipType != ChipType.Custom) return Simulator.BuildSimChip(d, lib, id, internalData);
			if (!isRoot)
			{
				if (!hashes.TryGetValue(d.Name, out string hash)) hashes[d.Name] = hash = MemoryLayout.StructureHash(d, lib);
				FastModel proto = Decide(d, lib, cache, hash);
				if (proto != null)
				{
					var leaf = new SimChip(d, id, proto.Clone());
					instances.Add(new Instance { Path = path.Append(id).ToArray(), Leaf = leaf, Desc = d });
					return leaf;
				}
			}
			var childPath = isRoot ? path : new List<int>(path) { id };
			SubChipDescription[] subs = d.SubChips ?? Array.Empty<SubChipDescription>();
			var children = new SimChip[subs.Length];
			for (int i = 0; i < subs.Length; i++)
				children[i] = Node(lib.GetChipDescriptionForSim(subs[i].Name), subs[i].ID, subs[i].InternalData, childPath, false, lib, cache, instances, hashes);
			var chip = new SimChip(d, id, internalData, children);
			foreach (WireDescription w in d.Wires ?? Array.Empty<WireDescription>()) chip.AddConnection(w.SourcePinAddress, w.TargetPinAddress);
			return chip;
		}

		static readonly HashSet<ChipType> LifeOfItsOwn = new()
		{
			ChipType.Clock, ChipType.Key, ChipType.Pulse, ChipType.Buzzer, ChipType.Rom_256x16, ChipType.dev_Ram_8Bit, ChipType.Ram65536,
			ChipType.SevenSegmentDisplay, ChipType.DisplayRGB, ChipType.DisplayDot, ChipType.DisplayLED, ChipType.LcdDem122032, ChipType.LcdSt7920
		};

		public static bool Modellable(ChipDescription d, ChipLibrary lib, HashSet<string> visiting = null)
		{
			visiting ??= new HashSet<string>(ChipDescription.NameComparer);
			if (!visiting.Add(d.Name)) return true;
			foreach (SubChipDescription s in d.SubChips ?? Array.Empty<SubChipDescription>())
			{
				ChipDescription c = lib.GetChipDescriptionForSim(s.Name);
				if (c == null) return false;
				if (LifeOfItsOwn.Contains(c.ChipType)) return false;
				if (c.ChipType == ChipType.Custom && !Modellable(c, lib, visiting)) return false;
			}
			return true;
		}

		// The model for a module, or null (built from its parts instead)
		public static FastModel Decide(ChipDescription d, ChipLibrary lib, FastCache cache, string hash = null)
		{
			hash ??= MemoryLayout.StructureHash(d, lib);
			if (cache.TryGet(d.Name, hash, d, out FastModel known, out bool none)) return none ? null : known;
			FastModel model = null;
			try
			{
				if (Modellable(d, lib))
				{
					if (!CircuitExporter.IsSequential(d, lib)) model = BuildLut(d, lib);
					else model = FastTemplates.Find(d, lib);
				}
			}
			catch (Exception) { model = null; }
			cache.Put(d.Name, hash, model);
			return model;
		}

		// ---- truth tables ----

		public static int InputBits(ChipDescription d) => (d.InputPins ?? Array.Empty<PinDescription>()).Sum(p => (int)p.BitCount);

		public static LutModel BuildLut(ChipDescription d, ChipLibrary lib)
		{
			int[] bits = d.InputPins.Select(p => (int)p.BitCount).ToArray();
			int n = bits.Sum(), outs = d.OutputPins.Length;
			if (n > MaxLutBits || outs == 0) return null;
			int combos = 1 << n;
			var table = new uint[combos * outs];
			var probe = new Evaluator(d, lib);
			try
			{
				for (int idx = 0; idx < combos; idx++)
				{
					if (!probe.Eval(idx, bits, table, idx * outs)) return null; // never settles: not truly combinational
				}
				// re-check in a shuffled order on a fresh copy: a hidden state would answer differently
				var check = new Evaluator(d, lib);
				var rnd = new Random(n * 7919 + outs);
				var got = new uint[outs];
				for (int s = 0; s < Math.Min(512, combos); s++)
				{
					int idx = rnd.Next(combos);
					if (!check.Eval(idx, bits, got, 0)) return null;
					for (int o = 0; o < outs; o++) if (got[o] != table[idx * outs + o]) return null;
				}
			}
			finally { probe.Dispose(); }
			return new LutModel(bits, outs, table);
		}

		// The real simulator on an isolated copy: set the inputs, step until the outputs hold still
		sealed class Evaluator : IDisposable
		{
			readonly SimChip root, target;
			readonly SimAudio audio = new();

			public Evaluator(ChipDescription d, ChipLibrary lib)
			{
				Simulator.ResetForTests(MemoryLayout.StructureHash(d, lib).GetHashCode());
				root = CircuitTester.BuildIsolatedSim(d, lib);
				target = CircuitTester.TargetOf(root);
			}

			public bool Eval(int idx, int[] bits, uint[] into, int at)
			{
				int shift = 0;
				for (int j = 0; j < bits.Length; j++)
				{
					root.InputPins[j].State = PinState.Make((ushort)((idx >> shift) & ((1 << bits[j]) - 1)), 0);
					shift += bits[j];
				}
				int outs = target.OutputPins.Length, stable = 0;
				var last = new uint[outs];
				for (int step = 0; step < 96; step++)
				{
					Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
					bool same = step > 0;
					for (int o = 0; o < outs; o++)
					{
						uint v = target.OutputPins[o].State;
						if (v != last[o]) same = false;
						last[o] = v;
					}
					stable = same ? stable + 1 : 0;
					if (stable >= 3)
					{
						Array.Copy(last, 0, into, at, outs);
						return true;
					}
				}
				return false;
			}

			public void Dispose() => Simulator.ClearTestSeed();
		}
	}

	// Per-chip decisions (model or none), keyed by structure hash. Persistence: FastCacheFile.
	public class FastCache
	{
		readonly Dictionary<string, (string hash, FastModel model, FastTemplates.Spec spec)> entries = new(ChipDescription.NameComparer);
		public int Count => entries.Count;
		public bool Changed; // something new to save
		public IEnumerable<(string name, string hash, FastModel model, FastTemplates.Spec spec)> Entries
		{
			get { lock (entries) return entries.Select(kv => (kv.Key, kv.Value.hash, kv.Value.model, kv.Value.spec)).ToList(); }
		}

		public bool TryGet(string name, string hash, ChipDescription d, out FastModel model, out bool none)
		{
			model = null; none = false;
			lock (entries)
			{
				if (!entries.TryGetValue(name, out var e) || e.hash != hash) return false;
				if (e.model == null && e.spec != null && d != null) entries[name] = e = (e.hash, new SeqModel(e.spec, d), e.spec);
				model = e.model;
				none = e.model == null;
				return true;
			}
		}

		public void Put(string name, string hash, FastModel model)
		{
			lock (entries) { entries[name] = (hash, model, (model as SeqModel)?.Spec); Changed = true; }
		}

		public void PutSpec(string name, string hash, FastTemplates.Spec spec)
		{
			lock (entries) entries[name] = (hash, null, spec);
		}
	}
}
