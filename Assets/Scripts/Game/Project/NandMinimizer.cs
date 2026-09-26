using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DLS.Description;
using UnityEngine;

namespace DLS.Game
{
	// Minimises the NAND count of a purely combinational NAND-only chip.
	//
	// Method (the standard one in logic synthesis, as implemented in Berkeley's ABC):
	//   Mishchenko, Chatterjee, Brayton - "DAG-Aware AIG Rewriting: A Fresh Look at Combinational
	//   Logic Synthesis", DAC 2006; refined in Riener, Mishchenko, Soeken - "Exact DAG-Aware
	//   Rewriting", DATE 2020.
	//
	//   1. The NAND netlist is read into an AIG: a NAND gate is exactly one 2-input AND node whose
	//      output is consumed complemented, so #NAND == #AND nodes.
	//   2. The AIG is built through structural hashing + constant folding, which already collapses
	//      the duplicated logic that hand-built (and above all flattened) circuits are full of.
	//   3. DAG-aware rewriting: for every node, 4-input cuts are enumerated, the cut function is
	//      looked up in the precomputed table of minimal AIG structures, and the sub-graph is
	//      replaced when that makes the circuit smaller.
	//
	// IMPORTANT - what is being minimised: a NAND computes ~(a&b), so an AND node whose output is
	// also needed uncomplemented costs one extra inverter gate (NAND(x,x)), shared between all its
	// consumers. Driving the search on the AIG node count alone therefore makes circuits WORSE
	// (a 4-NAND XOR "optimises" to 3 nodes + 3 inverters = 6 gates). Every replacement here is
	// judged on the real mapped NAND count instead, so the result can never be worse than the input.
	//
	// Several parameterisations run in parallel (no single cut limit wins on every circuit) and the
	// cheapest result is kept. Every step preserves the function by construction, and on top of that
	// the emitted chip is read back and compared to the original over the whole input space before
	// anything is written to disk.
	public static class NandMinimizer
	{
		static readonly int[] CutLimitPortfolio = { 6, 8, 10, 12, 14, 16, 20 };

		public const int NandInA = 0;
		public const int NandInB = 1;
		public const int NandOut = 2;

		// ---------------------------------------------------------------- public API

		public static bool CanOptimize(string chipName, ChipLibrary library) =>
			ReadAsNandNetlist(chipName, library, out _, out _, out int nand) == null && nand > 0;

		// Reads a chip as a NAND netlist. A chip built from your own bricks is hierarchical, so it is
		// flattened first - in memory only, nothing is written and no intermediate brick is created.
		// Returns null on success, otherwise why the chip cannot be handled.
		public static string ReadAsNandNetlist(string chipName, ChipLibrary library, out ChipDescription netlist, out Aig aig, out int nandCount)
		{
			netlist = null;
			aig = null;
			nandCount = 0;

			ChipDescription source = library.GetChipDescriptionForSim(chipName);
			if (source == null || source.ChipType != ChipType.Custom) return "Brique introuvable.";

			string error = Extract(source, library, out aig, out nandCount);
			if (error == null)
			{
				netlist = source;
				return null;
			}

			ChipDescription flattened = NandFlattener.Flatten(chipName, library, source.Name, out _);
			if (flattened == null) return error;

			string flatError = Extract(flattened, library, out aig, out nandCount);
			if (flatError != null) return flatError;

			netlist = flattened;
			return null;
		}

		// ---------------------------------------------------------------- reading a chip into an AIG

		// Returns null when the chip qualifies, otherwise the reason why it does not.
		public static string Extract(ChipDescription desc, ChipLibrary library, out Aig aig, out int nandCount)
		{
			aig = null;
			nandCount = 0;

			PinDescription[] inPins = desc.InputPins ?? Array.Empty<PinDescription>();
			PinDescription[] outPins = desc.OutputPins ?? Array.Empty<PinDescription>();

			foreach (PinDescription p in inPins)
			{
				if (p.BitCount != PinBitCount.Bit1) return "Les entrees doivent etre en 1 bit.";
			}

			foreach (PinDescription p in outPins)
			{
				if (p.BitCount != PinBitCount.Bit1) return "Les sorties doivent etre en 1 bit.";
			}

			Dictionary<int, ChipType> subTypes = new();
			foreach (SubChipDescription sub in desc.SubChips ?? Array.Empty<SubChipDescription>())
			{
				ChipDescription subDesc = library.GetChipDescriptionForSim(sub.Name);
				if (subDesc == null) return $"Sous-brique introuvable : {sub.Name}.";
				if (subDesc.ChipType is not (ChipType.Nand or ChipType.Vcc or ChipType.Gnd)) return "La brique ne contient pas que des NAND.";
				subTypes[sub.ID] = subDesc.ChipType;
				if (subDesc.ChipType == ChipType.Nand) nandCount++;
			}

			// One driver per sink: two wires into the same pin would make the result order-dependent.
			Dictionary<(int, int), PinAddress> driver = new();
			foreach (WireDescription wire in desc.Wires ?? Array.Empty<WireDescription>())
			{
				(int, int) key = (wire.TargetPinAddress.PinOwnerID, wire.TargetPinAddress.PinID);
				if (driver.ContainsKey(key)) return "Une entree recoit plusieurs fils.";
				driver[key] = wire.SourcePinAddress;
			}

			Dictionary<int, int> piIndex = new();
			for (int i = 0; i < inPins.Length; i++) piIndex[inPins[i].ID] = i;

			Aig g = new(inPins.Length);
			Dictionary<int, int> gateLit = new();
			HashSet<int> inProgress = new();
			string failure = null;

			int SourceLit(PinAddress src)
			{
				if (piIndex.TryGetValue(src.PinOwnerID, out int pi)) return g.PiLit(pi);

				if (subTypes.TryGetValue(src.PinOwnerID, out ChipType type))
				{
					if (type == ChipType.Vcc) return Aig.C1;
					if (type == ChipType.Gnd) return Aig.C0;
					return Aig.Not(GateLit(src.PinOwnerID)); // a NAND outputs the complement of its AND node
				}

				failure ??= "Fil relie a un element inconnu.";
				return Aig.C0;
			}

			// An unconnected input pin reads LOW in the simulator (disconnected bits are 0).
			int SinkLit(int ownerID, int pinID) => driver.TryGetValue((ownerID, pinID), out PinAddress src) ? SourceLit(src) : Aig.C0;

			int GateLit(int subChipID)
			{
				if (gateLit.TryGetValue(subChipID, out int cached)) return cached;
				if (!inProgress.Add(subChipID))
				{
					failure ??= "Le circuit contient une boucle : il n'est pas combinatoire.";
					return Aig.C0;
				}

				int a = SinkLit(subChipID, NandInA);
				int b = SinkLit(subChipID, NandInB);
				int lit = g.And(a, b);

				inProgress.Remove(subChipID);
				gateLit[subChipID] = lit;
				return lit;
			}

			foreach (PinDescription p in outPins)
			{
				int lit = driver.TryGetValue((p.ID, 0), out PinAddress src) ? SourceLit(src) : -1;
				if (failure != null) return failure;
				g.Outputs.Add(lit);
			}

			// Gates that drive nothing still have to be loop-free before we call this combinational.
			foreach (KeyValuePair<int, ChipType> entry in subTypes)
			{
				if (entry.Value == ChipType.Nand) GateLit(entry.Key);
				if (failure != null) return failure;
			}

			aig = g;
			return null;
		}

		// ---------------------------------------------------------------- logic depth

		// Longest chain of gates from an input to an output, counting each sub-chip as one level.
		// Works on a netlist of single-gate sub-chips (NAND) plus VCC/GND, which is what the NAND
		// tools produce. Nothing here optimises depth - it is measured so it can be reported.
		public static int NetlistDepth(ChipDescription desc, ChipLibrary library)
		{
			Dictionary<(int, int), PinAddress> driver = new();
			foreach (WireDescription wire in desc.Wires ?? Array.Empty<WireDescription>())
			{
				driver[(wire.TargetPinAddress.PinOwnerID, wire.TargetPinAddress.PinID)] = wire.SourcePinAddress;
			}

			Dictionary<int, SubChipDescription> subs = new();
			Dictionary<int, ChipDescription> subDescs = new();
			foreach (SubChipDescription sub in desc.SubChips ?? Array.Empty<SubChipDescription>())
			{
				subs[sub.ID] = sub;
				subDescs[sub.ID] = library.GetChipDescriptionForSim(sub.Name);
			}

			HashSet<int> piIDs = new();
			foreach (PinDescription p in desc.InputPins ?? Array.Empty<PinDescription>()) piIDs.Add(p.ID);

			Dictionary<int, int> level = new();
			HashSet<int> visiting = new();

			int SourceLevel(PinAddress src)
			{
				if (piIDs.Contains(src.PinOwnerID)) return 0;
				return subs.ContainsKey(src.PinOwnerID) ? Level(src.PinOwnerID) : 0;
			}

			int Level(int subChipID)
			{
				if (level.TryGetValue(subChipID, out int cached)) return cached;
				if (!visiting.Add(subChipID)) return 0; // loop guard; these netlists are combinational

				ChipDescription subDesc = subDescs[subChipID];
				int deepest = 0;
				bool isSource = subDesc == null || subDesc.ChipType is ChipType.Vcc or ChipType.Gnd;

				if (!isSource && subDesc.InputPins != null)
				{
					foreach (PinDescription pin in subDesc.InputPins)
					{
						if (driver.TryGetValue((subChipID, pin.ID), out PinAddress src)) deepest = Math.Max(deepest, SourceLevel(src));
					}
				}

				int result = isSource ? 0 : deepest + 1;
				visiting.Remove(subChipID);
				level[subChipID] = result;
				return result;
			}

			int depth = 0;
			foreach (PinDescription p in desc.OutputPins ?? Array.Empty<PinDescription>())
			{
				if (driver.TryGetValue((p.ID, 0), out PinAddress src)) depth = Math.Max(depth, SourceLevel(src));
			}

			return depth;
		}

		// ---------------------------------------------------------------- the objective: mapped NAND count

		// live AND nodes + one shared inverter per signal that is also needed in the other polarity.
		public static int MappedCost(Aig g, List<int> subst)
		{
			int[] mark = new int[g.NodeCount];
			int[] invMark = new int[g.NodeCount];
			int[] stack = new int[g.NodeCount + 4];
			int top = 0;
			int gates = 0;

			int Resolve(int lit) => subst == null ? lit : Aig.ResolveSubst(subst, lit);

			void Consume(int resolvedLit)
			{
				int node = resolvedLit >> 1;
				bool complement = (resolvedLit & 1) != 0;
				if (node == 0) return; // constants come from VCC/GND, which are not NAND gates

				// A gate outputs ~node, so it is the UNcomplemented use that needs an inverter.
				bool needsInverter = node <= g.NumPI ? complement : !complement;
				if (!needsInverter || invMark[node] == 1) return;
				invMark[node] = 1;
				gates++;
			}

			foreach (int outLit in g.Outputs)
			{
				if (outLit < 0) continue;
				int resolved = Resolve(outLit);
				Consume(resolved);
				int node = resolved >> 1;
				if (node > g.NumPI && mark[node] == 0)
				{
					mark[node] = 1;
					stack[top++] = node;
					gates++;
				}
			}

			while (top > 0)
			{
				int node = stack[--top];
				for (int side = 0; side < 2; side++)
				{
					int resolved = Resolve(side == 0 ? g.F0[node] : g.F1[node]);
					Consume(resolved);
					int child = resolved >> 1;
					if (child > g.NumPI && mark[child] == 0)
					{
						mark[child] = 1;
						if (top >= stack.Length) Array.Resize(ref stack, stack.Length * 2);
						stack[top++] = child;
						gates++;
					}
				}
			}

			return gates;
		}

		// ---------------------------------------------------------------- rewriting

		// Structural rewriting and don't-care simplification open room for each other: removing a node
		// changes what is observable, and re-expressing logic creates new candidates to merge. So the
		// two alternate until neither finds anything.
		public static Aig RunRewriting(Aig aig)
		{
			AigLibrary.Build(); // once, on this thread, before anything runs in parallel

			Aig best = aig.Rebuild();
			int bestCost = MappedCost(best, null);

			for (int round = 0; round < 3; round++)
			{
				Aig rewritten = RewritePortfolio(best);
				Aig simplified = AigDontCare.Simplify(rewritten);
				Aig again = ReferenceEquals(simplified, rewritten) ? rewritten : RewritePortfolio(simplified);

				int cost = Math.Min(MappedCost(rewritten, null), Math.Min(MappedCost(simplified, null), MappedCost(again, null)));
				Aig candidate = MappedCost(again, null) == cost ? again : MappedCost(simplified, null) == cost ? simplified : rewritten;

				if (cost >= bestCost) break;
				best = candidate;
				bestCost = cost;
			}

			return best;
		}

		static Aig RewritePortfolio(Aig aig)
		{
			Aig strashed = aig.Rebuild();
			Aig[] candidates = new Aig[CutLimitPortfolio.Length];

			// No single cut limit is best on every circuit, so run the whole portfolio at once -
			// the runs are independent, so this is free wall-clock time on any multi-core machine.
			try
			{
				Parallel.For(0, CutLimitPortfolio.Length, i => { candidates[i] = OptimiseWith(strashed, CutLimitPortfolio[i]); });
			}
			catch (AggregateException)
			{
				for (int i = 0; i < CutLimitPortfolio.Length; i++) candidates[i] ??= OptimiseWith(strashed, CutLimitPortfolio[i]);
			}

			Aig best = strashed;
			int bestCost = MappedCost(strashed, null);

			foreach (Aig candidate in candidates)
			{
				if (candidate == null) continue;
				int cost = MappedCost(candidate, null);
				if (cost < bestCost)
				{
					best = candidate;
					bestCost = cost;
				}
			}

			return best;
		}

		static Aig OptimiseWith(Aig strashed, int cutLimit)
		{
			Aig best = strashed.Rebuild();
			int bestCost = MappedCost(best, null);

			for (int pass = 0; pass < 16; pass++)
			{
				Aig candidate = RewritePass(best, cutLimit);
				int cost = MappedCost(candidate, null);
				if (cost >= bestCost) break;
				best = candidate;
				bestCost = cost;
			}

			return best;
		}

		static Aig RewritePass(Aig g, int cutLimit)
		{
			int originalNodeCount = g.NodeCount;
			List<int> subst = new(originalNodeCount);
			for (int i = 0; i < originalNodeCount; i++) subst.Add(Aig.Lit(i, false));

			List<AigCut>[] cuts = AigCuts.Enumerate(g, cutLimit);
			int currentCost = MappedCost(g, subst);
			int[] leafLits = new int[4];

			for (int node = g.NumPI + 1; node < originalNodeCount; node++)
			{
				List<AigCut> nodeCuts = cuts[node];
				if (nodeCuts == null) continue;

				foreach (AigCut cut in nodeCuts)
				{
					if (cut.Leaves.Length < 2) continue;

					int mark = g.NodeCount;
					for (int i = 0; i < 4; i++) leafLits[i] = i < cut.Leaves.Length ? Aig.ResolveSubst(subst, Aig.Lit(cut.Leaves[i], false)) : Aig.C0;

					int replacement = AigLibrary.BuildInto(g, cut.Tt, leafLits);
					while (subst.Count < g.NodeCount) subst.Add(Aig.Lit(subst.Count, false));

					int previous = subst[node];
					bool accepted = false;

					if (replacement >= 0 && replacement != previous)
					{
						subst[node] = replacement;
						int cost = MappedCost(g, subst);
						if (cost < currentCost)
						{
							currentCost = cost;
							accepted = true;
						}
						else subst[node] = previous;
					}

					if (!accepted)
					{
						g.Truncate(mark);
						if (subst.Count > g.NodeCount) subst.RemoveRange(g.NodeCount, subst.Count - g.NodeCount);
					}
				}
			}

			return g.Rebuild(subst);
		}

		// ---------------------------------------------------------------- AIG -> NAND chip

		public static ChipDescription EmitNandChip(Aig g, string newName, PinDescription[] templateIn, PinDescription[] templateOut,
			Color colour, NameDisplayLocation nameLocation, Vector2 minSize,
			out int gateCount, out int inverterCount, out bool usesConstants)
		{
			string nandName = ChipTypeHelper.GetName(ChipType.Nand);
			string vccName = ChipTypeHelper.GetName(ChipType.Vcc);
			string gndName = ChipTypeHelper.GetName(ChipType.Gnd);

			int nextID = 1;
			List<SubChipDescription> subChips = new();
			List<WireDescription> wires = new();

			PinDescription[] inputPins = ChipEmitHelper.CopyPins(templateIn, ref nextID);
			PinDescription[] outputPins = ChipEmitHelper.CopyPins(templateOut, ref nextID);

			// Live AND nodes, in topological order (indices are already topological after Rebuild).
			bool[] live = g.MarkLive();
			List<int> nodes = new();
			for (int node = g.NumPI + 1; node < g.NodeCount; node++)
			{
				if (live[node]) nodes.Add(node);
			}

			Dictionary<int, int> gateID = new();
			foreach (int node in nodes) gateID[node] = NewGate(ChipType.Nand);

			Dictionary<int, int> piInverter = new();
			Dictionary<int, int> nodeInverter = new();
			int vccID = -1;
			int gndID = -1;
			int inverters = 0;
			bool constants = false;

			PinAddress SourceOf(int lit)
			{
				int node = lit >> 1;
				bool complement = (lit & 1) != 0;

				if (node == 0)
				{
					constants = true;
					if (complement)
					{
						if (vccID < 0) vccID = NewGate(ChipType.Vcc);
						return new PinAddress(vccID, 0);
					}

					if (gndID < 0) gndID = NewGate(ChipType.Gnd);
					return new PinAddress(gndID, 0);
				}

				if (node <= g.NumPI)
				{
					PinAddress direct = new(inputPins[node - 1].ID, 0);
					if (!complement) return direct;
					if (!piInverter.TryGetValue(node, out int inv))
					{
						inv = NewInverter(direct);
						piInverter[node] = inv;
					}

					return new PinAddress(inv, NandOut);
				}

				// The gate of an AND node outputs its complement, which is the common case.
				PinAddress gate = new(gateID[node], NandOut);
				if (complement) return gate;

				if (!nodeInverter.TryGetValue(node, out int inverter))
				{
					inverter = NewInverter(gate);
					nodeInverter[node] = inverter;
				}

				return new PinAddress(inverter, NandOut);
			}

			foreach (int node in nodes)
			{
				wires.Add(ChipEmitHelper.Wire(SourceOf(g.F0[node]), new PinAddress(gateID[node], NandInA)));
				wires.Add(ChipEmitHelper.Wire(SourceOf(g.F1[node]), new PinAddress(gateID[node], NandInB)));
			}

			for (int i = 0; i < g.Outputs.Count && i < outputPins.Length; i++)
			{
				int lit = g.Outputs[i];
				if (lit < 0) continue;
				wires.Add(ChipEmitHelper.Wire(SourceOf(lit), new PinAddress(outputPins[i].ID, 0)));
			}

			inverterCount = inverters;
			usesConstants = constants;
			gateCount = nodes.Count + inverters;

			return ChipEmitHelper.Assemble(newName, colour, nameLocation, minSize, inputPins, outputPins, subChips, wires);

			int NewGate(ChipType type)
			{
				string name = type == ChipType.Nand ? nandName : type == ChipType.Vcc ? vccName : gndName;
				return ChipEmitHelper.AddSubChip(subChips, ref nextID, name, type);
			}

			int NewInverter(PinAddress source)
			{
				int id = NewGate(ChipType.Nand);
				inverters++;
				wires.Add(ChipEmitHelper.Wire(source, new PinAddress(id, NandInA)));
				wires.Add(ChipEmitHelper.Wire(source, new PinAddress(id, NandInB)));
				return id;
			}
		}
	}
}
