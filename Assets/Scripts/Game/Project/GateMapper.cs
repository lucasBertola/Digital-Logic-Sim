using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DLS.Description;
using UnityEngine;

namespace DLS.Game
{
	// Rebuilds a combinational chip out of the logic packages the user actually owns (its hierarchy is
	// flattened to NAND on the way in, so it works on any brick that is combinational), and
	// minimises the NUMBER OF PACKAGES - a package that is started counts as a whole package, so the
	// objective is sum over gate types of ceil(gates / gates-per-package).
	//
	// This is classic cut-based technology mapping (Chen & Cong, "DAOmap", ICCAD 2004; the same cut
	// enumeration + area-flow scheme ABC's `map`/`if` use), with three adaptations:
	//
	//   * No free inverters. The library has no buffer and inversion costs a real gate, so the DP
	//     runs over SIGNALS (node, polarity) rather than nodes, and for each cut it tries all 2^k
	//     input polarity assignments - that is what lets NOR(~a,~b) be recognised as an AND, etc.
	//   * Degenerate gate use. All the gates here are symmetric, so an n-input gate can implement the
	//     same operator on fewer inputs by tying inputs together (AND3(a,b,b) = a AND b, NAND2(a,a) =
	//     NOT a). Matching is therefore "same operator, arity >= needed", and the emitted chip really
	//     does tie the spare pins, so what you see is what you would wire.
	//   * The ceiling objective. Package count is not a sum of per-gate costs, so no single weighting
	//     of the DP is right. A portfolio of weightings is evaluated in parallel, each producing a
	//     complete cover, and the cover with the lowest true package count wins.
	//
	// The emitted chip is read back gate by gate and compared to the original over the whole input
	// space before anything is written.
	public static class GateMapper
	{
		const double Inf = 1e18;
		static readonly int[] CutLimits = { 6, 8, 10, 12, 14, 18 };
		const int RandomWeightTrials = 160;
		const int SearchBudgetMs = 6000; // only big circuits ever reach it; small ones run every trial in a few hundred ms

		public class Result
		{
			public ChipDescription Chip;
			public string Error;
			public int OriginalNandCount;
			public int PackageCount;
			public int GateTotal;
			public int OriginalDepth;
			public int Depth;
			public List<(GateKind kind, int gates)> GateUsage = new();
			public List<(GatePackage package, int count)> Bill = new();
			public List<ChipDescription> BricksToCreate = new();
			public bool VerifiedExhaustively;
			public long VerifiedPatterns;
		}

		// ---------------------------------------------------------------- public API

		public static Result Map(string chipName, ChipLibrary library, string newName, bool[] selection)
		{
			Result result = new();

			// Hierarchical chips are flattened on the way in, so this works straight off a brick built
			// from your own sub-bricks - no need to run "Nand only" first.
			string error = NandMinimizer.ReadAsNandNetlist(chipName, library, out ChipDescription src, out Aig original, out int nandCount);
			if (error != null)
			{
				result.Error = error;
				return result;
			}

			result.OriginalNandCount = nandCount;

			List<(GateKind kind, int capacity)> buckets = GatePackages.Buckets(selection);
			if (buckets.Count == 0)
			{
				result.Error = "Aucun boitier selectionne.";
				return result;
			}

			AigLibrary.Build();

			// Candidate structures for the SAME function: the circuit as given, the NAND-minimised one,
			// and the one simplified through its don't-cares. They map differently, so all are tried.
			Aig nandMinimised = NandMinimizer.RunRewriting(original);
			List<Aig> pool = new() { original.Rebuild(), nandMinimised, AigDontCare.Simplify(original) };

			// Cuts only depend on the graph and the cut limit, so enumerate them once and let every
			// weighting reuse them (they used to be recomputed for each trial, which dominated the run).
			List<Aig> graphs = new();
			List<int> limits = new();
			foreach (Aig candidate in pool)
			{
				foreach (int limit in CutLimits)
				{
					graphs.Add(candidate);
					limits.Add(limit);
				}
			}

			List<AigCut>[][] cutSets = new List<AigCut>[graphs.Count][];
			Parallel.For(0, cutSets.Length, i => { cutSets[i] = AigCuts.Enumerate(graphs[i], limits[i]); });


			List<(double[] weights, bool[] forbidden)> weightings = BuildWeightings(buckets);

			Cover best = null;
			int bestSet = -1;
			double[] bestWeights = null;
			bool[] bestForbidden = null;

			object gate = new();

			// A wide portfolio, bounded by wall-clock rather than by a trial count: small circuits get
			// through every trial in a few ms, big ones stop once the budget is spent.
			System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
			int trials = cutSets.Length * weightings.Count;
			Parallel.For(0, trials, t =>
			{
				if (clock.ElapsedMilliseconds > SearchBudgetMs) return;

				int setIndex = t / weightings.Count;
				Aig graph = graphs[setIndex];
				(double[] weighting, bool[] forbidden) = weightings[t % weightings.Count];

				Cover cover = BuildCover(graph, buckets, weighting, cutSets[setIndex], null, forbidden);
				if (cover == null) return;
				cover = Refine(graph, buckets, weighting, cutSets[setIndex], cover, forbidden);
				Repack(cover, buckets);

				lock (gate)
				{
					if (best != null && !Better(cover, best)) return;
					best = cover;
					bestSet = setIndex;
					bestWeights = weighting;
					bestForbidden = forbidden;
				}
			});

			// Feedback round: once a cover exists we know which package types already have a paid-for
			// empty slot. One more gate there is FREE, one more anywhere else opens a whole chip. Feeding
			// that marginal cost back as the DP's weights steers the gate mix towards ceilings that come
			// out even - something no fixed weighting can express, since the DP's cost is linear and the
			// real one is a sum of ceilings.
			// Local search on the winning configuration: re-derive it keeping the runner-up cuts the DP
			// rejected, then swap them in one at a time and keep whatever lowers the real package count.
			if (best != null && bestSet >= 0)
			{
				Cover withOptions = BuildCover(graphs[bestSet], buckets, bestWeights, cutSets[bestSet], null, bestForbidden, true);
				if (withOptions != null)
				{
					withOptions = Refine(graphs[bestSet], buckets, bestWeights, cutSets[bestSet], withOptions, bestForbidden, true);
					Repack(withOptions, buckets);
					Cover searched = LocalSearch(withOptions, graphs[bestSet], buckets);
					if (searched != null && Better(searched, best)) best = searched;
				}
			}


			// Floor guarantee: when 2-input NANDs are on the list, the NAND-minimised netlist is itself a
			// valid cover, so the answer can never come out worse than what "Min nand" gives.
			Cover direct = NandOnlyCover(nandMinimised, buckets);
			if (direct != null) Repack(direct, buckets);
			if (direct != null && (best == null || Better(direct, best))) best = direct;

			if (best == null)
			{
				result.Error = "Les boitiers selectionnes ne suffisent pas a construire ce circuit.\n(il faut au moins de quoi inverser un signal)";
				return result;
			}

			// ---- split the cover into real packages, and resolve one brick per package type ----
			List<PackageInstance> instances = new();
			int[] instanceOfGate = new int[best.Gates.Count];
			int[] slotOfGate = new int[best.Gates.Count];
			Pack(best, buckets, selection, instances, instanceOfGate, slotOfGate);

			Dictionary<string, PackageBrick> resolved = new();
			foreach (PackageInstance instance in instances)
			{
				if (!resolved.TryGetValue(instance.Package.BrickName, out PackageBrick brick))
				{
					brick = ResolvePackageBrick(instance.Package, library, result.BricksToCreate);
					resolved[instance.Package.BrickName] = brick;
				}

				instance.Brick = brick;
			}

			ChipDescription chip = Emit(best, instances, instanceOfGate, slotOfGate, src, newName);
			result.Chip = chip;

			// ---- read the emitted chip back, slot by slot, and prove it is the same function ----
			Dictionary<string, PackageBrick> byName = new(ChipDescription.NameComparer);
			foreach (PackageBrick brick in resolved.Values) byName[brick.Name] = brick;

			string reError = ExtractGateNetlist(chip, byName, library, out Aig roundTrip);
			if (reError != null)
			{
				result.Error = "Verification impossible : " + reError;
				return result;
			}

			if (!AigEquivalence.Check(original, roundTrip, out bool exhaustive, out long patterns))
			{
				result.Error = "Verification ECHOUEE : le circuit produit ne donne pas le meme resultat.\nRien n'a ete cree.";
				return result;
			}

			result.VerifiedExhaustively = exhaustive;
			result.VerifiedPatterns = patterns;
			result.PackageCount = instances.Count; // the packages actually placed in the module
			result.GateTotal = best.Gates.Count;
			result.OriginalDepth = NandMinimizer.NetlistDepth(src, library);
			result.Depth = CoverDepth(best);

			for (int i = 0; i < buckets.Count; i++)
			{
				if (best.GatesPerBucket[i] > 0) result.GateUsage.Add((buckets[i].kind, best.GatesPerBucket[i]));
			}

			List<GatePackage> order = new();
			Dictionary<GatePackage, int> counts = new();
			foreach (PackageInstance instance in instances)
			{
				if (!counts.ContainsKey(instance.Package)) order.Add(instance.Package);
				counts.TryGetValue(instance.Package, out int n);
				counts[instance.Package] = n + 1;
			}

			foreach (GatePackage package in order) result.Bill.Add((package, counts[package]));

			return result;
		}

		public static string DescribeResult(string newName, Result r)
		{
			StringBuilder sb = new();
			sb.Append("Module \"").Append(newName).Append("\" cree.\n\n");
			// When every package holds a single gate, boxes and gates are the same number: say it once.
			bool unitOnly = r.Bill.Count > 0;
			foreach ((GatePackage package, int _) in r.Bill)
			{
				if (package.GatesPerPackage != 1) unitOnly = false;
			}

			if (unitOnly) sb.Append("PORTES : ").Append(r.GateTotal).Append('\n');
			else sb.Append("BOITIERS : ").Append(r.PackageCount).Append("   (").Append(r.GateTotal).Append(" portes)\n");

			foreach ((GatePackage package, int count) in r.Bill)
			{
				sb.Append('\n').Append(count).Append(" x  ").Append(package.Label);
			}

			sb.Append("\n\nRemplissage :");
			foreach ((GateKind kind, int gates) in r.GateUsage) sb.Append('\n').Append(kind.ShortName).Append(" : ").Append(gates);

			sb.Append("\n\nProfondeur (etages de portes) : ").Append(r.OriginalDepth).Append("  ->  ").Append(r.Depth);
			sb.Append("\n(depart : ").Append(r.OriginalNandCount).Append(" NAND)");
			sb.Append("\nEquivalence verifiee sur ").Append(r.VerifiedPatterns);
			sb.Append(r.VerifiedExhaustively ? " combinaisons (toutes)" : " combinaisons tirees au hasard");

			return sb.ToString();
		}

		// The package count is a sum of ceilings, so no single per-gate cost is right: a gate is worth
		// the fraction of a package it occupies, but whether one more gate is free (spare slot) or
		// costs a whole package depends on the rest of the cover. So the search tries a spread of
		// weightings and keeps the cover with the lowest real package count.
		static List<(double[] weights, bool[] forbidden)> BuildWeightings(List<(GateKind kind, int capacity)> buckets)
		{
			List<(double[], bool[])> weightings = new();

			double[] Base()
			{
				double[] w = new double[buckets.Count];
				for (int i = 0; i < w.Length; i++) w[i] = 1.0 / buckets[i].capacity;
				return w;
			}

			weightings.Add((Base(), null));

			// Targeted trials: push every gate out of one type (its last package may be nearly empty),
			// or make one type cheap so its started packages get filled up.
			for (int b = 0; b < buckets.Count; b++)
			{
				double[] penalise = Base();
				penalise[b] *= 6;
				weightings.Add((penalise, null));

				double[] favour = Base();
				favour[b] *= 0.3;
				weightings.Add((favour, null));
			}

			System.Random rng = new(20260913);
			for (int i = 0; i < RandomWeightTrials; i++)
			{
				double[] w = Base();
				for (int b = 0; b < w.Length; b++) w[b] *= 0.45 + 1.3 * rng.NextDouble();
				weightings.Add((w, null));
			}

			return weightings;
		}

		// ---------------------------------------------------------------- the cover

		class GateInstance
		{
			public int Bucket;
			public GateOp Op; // what it computes, so it can be re-sourced from another package type
			public int[] InputSignals; // AIG literals, one per USED input (spare pins get tied)
			public int OutputSignal;
		}

		class Cover
		{
			public Aig Graph;
			public Choice[] ChoiceOf; // how each signal is implemented, so a local search can alter it
			public List<Choice>[] Options; // the runners-up the DP considered for each signal
			public List<GateInstance> Gates = new();
			public Dictionary<int, int> GateOfSignal = new(); // signal literal -> index in Gates
			public int[] GatesPerBucket;
			public int PackageCount;
		}

		struct Choice
		{
			public GateOp Op;
			public int[] Leaves;
			public int Q; // bit i set = input i is taken complemented
			public int Bucket;
			public byte Kind; // 0 = none, 1 = free (constant / positive input), 2 = gate, 3 = constant
			public byte ConstValue;
		}

		// ---------------------------------------------------------------- matching

		static ushort OperatorTt(GateOp op, int m)
		{
			switch (op)
			{
				case GateOp.Not: return (ushort)~AigCuts.VarTt[0];
				case GateOp.And:
				case GateOp.Nand:
				{
					ushort t = 0xFFFF;
					for (int i = 0; i < m; i++) t &= AigCuts.VarTt[i];
					return op == GateOp.And ? t : (ushort)~t;
				}
				case GateOp.Or:
				case GateOp.Nor:
				{
					ushort t = 0;
					for (int i = 0; i < m; i++) t |= AigCuts.VarTt[i];
					return op == GateOp.Or ? t : (ushort)~t;
				}
				case GateOp.Xor:
				{
					ushort t = 0;
					for (int i = 0; i < m; i++) t ^= AigCuts.VarTt[i];
					return t;
				}
				default: return 0;
			}
		}

		// (operator, arity) -> truth table, precomputed: this lookup sits in the innermost loop of the
		// mapping DP and is hit millions of times.
		static readonly ushort[,] OperatorTable = BuildOperatorTable();
		static readonly GateOp[] MultiInputOps = { GateOp.Nand, GateOp.Nor, GateOp.And, GateOp.Or, GateOp.Xor };

		static ushort[,] BuildOperatorTable()
		{
			ushort[,] table = new ushort[6, 5];
			for (int op = 0; op < 6; op++)
			{
				for (int m = 1; m <= 4; m++) table[op, m] = OperatorTt((GateOp)op, m);
			}

			return table;
		}

		static bool TryClassify(ushort tt, int m, out GateOp op)
		{
			if (m == 1)
			{
				op = GateOp.Not;
				return tt == (ushort)~AigCuts.VarTt[0];
			}

			foreach (GateOp candidate in MultiInputOps)
			{
				if (tt == OperatorTable[(int)candidate, m])
				{
					op = candidate;
					return true;
				}
			}

			op = GateOp.And;
			return false;
		}

		// A symmetric n-input gate covers the same operator on fewer inputs by tying pins together;
		// NAND and NOR additionally give an inverter that way. XOR cannot be reduced.
		static bool CanImplement(GateKind bucket, GateOp op, int m)
		{
			if (op == GateOp.Not) return bucket.Op == GateOp.Not || (bucket.Op is GateOp.Nand or GateOp.Nor && bucket.Arity >= 2);
			if (op == GateOp.Xor) return bucket.Op == GateOp.Xor && bucket.Arity == m;
			return bucket.Op == op && bucket.Arity >= m;
		}

		// g(y) = f(y XOR q): the function the gate must compute when input i arrives complemented.
		static ushort NegateInputs(ushort tt, int q)
		{
			ushort r = 0;
			for (int index = 0; index < 16; index++)
			{
				if (((tt >> (index ^ q)) & 1) != 0) r |= (ushort)(1 << index);
			}

			return r;
		}

		static (int[] leaves, ushort tt) Reduce(int[] leaves, ushort tt)
		{
			int support = AigCuts.Support(tt, leaves.Length);
			int count = 0;
			for (int i = 0; i < leaves.Length; i++)
			{
				if (((support >> i) & 1) != 0) count++;
			}

			if (count == leaves.Length) return (leaves, tt);

			int[] reduced = new int[count];
			int j = 0;
			for (int i = 0; i < leaves.Length; i++)
			{
				if (((support >> i) & 1) != 0) reduced[j++] = leaves[i];
			}

			ushort compact = 0;
			for (int index = 0; index < 16; index++)
			{
				int source = 0;
				int slot = 0;
				for (int i = 0; i < leaves.Length; i++)
				{
					if (((support >> i) & 1) == 0) continue;
					if (((index >> slot) & 1) != 0) source |= 1 << i;
					slot++;
				}

				if (((tt >> source) & 1) != 0) compact |= (ushort)(1 << index);
			}

			return (reduced, compact);
		}

		// ---------------------------------------------------------------- mapping DP

		// Area flow spreads a shared sub-circuit's cost over its consumers, which is only an estimate.
		// Once a cover exists we know exactly which signals are already paid for, so re-running the DP
		// with those priced at zero (ABC calls this area recovery) lets gates reuse them for free.
		// Each round is evaluated on the true package count and kept only if it actually helps.
		static Cover Refine(Aig g, List<(GateKind kind, int capacity)> buckets, double[] weights, List<AigCut>[] cuts, Cover start, bool[] forbidden, bool keepOptions = false)
		{
			Cover best = start;

			for (int round = 0; round < 3; round++)
			{
				HashSet<int> alreadyPaid = new(best.GateOfSignal.Keys);
				Cover candidate = BuildCover(g, buckets, weights, cuts, alreadyPaid, forbidden, keepOptions);
				if (candidate == null) break;
				if (!Better(candidate, best)) break;
				best = candidate;
			}

			return best;
		}

		// The NAND-minimised netlist read straight as a cover: one NAND per AND node, plus one shared
		// NAND(x,x) inverter per signal that is also needed in the other polarity.
		static Cover NandOnlyCover(Aig g, List<(GateKind kind, int capacity)> buckets)
		{
			int nandBucket = -1;
			for (int b = 0; b < buckets.Count; b++)
			{
				if (buckets[b].kind.Op == GateOp.Nand && buckets[b].kind.Arity == 2) nandBucket = b;
			}

			if (nandBucket < 0) return null;

			Cover cover = new() { Graph = g, GatesPerBucket = new int[buckets.Count] };
			bool[] live = g.MarkLive();
			List<int> consumed = new();

			void AddGate(int outputSignal, int[] inputs)
			{
				if (cover.GateOfSignal.ContainsKey(outputSignal)) return;
				cover.GateOfSignal[outputSignal] = cover.Gates.Count;
				cover.Gates.Add(new GateInstance { Bucket = nandBucket, Op = inputs.Length == 1 ? GateOp.Not : GateOp.Nand, InputSignals = inputs, OutputSignal = outputSignal });
				cover.GatesPerBucket[nandBucket]++;
			}

			for (int node = g.NumPI + 1; node < g.NodeCount; node++)
			{
				if (!live[node]) continue;
				AddGate(Aig.Lit(node, true), new[] { g.F0[node], g.F1[node] });
				consumed.Add(g.F0[node]);
				consumed.Add(g.F1[node]);
			}

			foreach (int outLit in g.Outputs)
			{
				if (outLit >= 0) consumed.Add(outLit);
			}

			foreach (int literal in consumed)
			{
				int node = literal >> 1;
				if (node == 0) continue;
				bool complement = (literal & 1) != 0;
				bool needsInverter = node <= g.NumPI ? complement : !complement;
				if (needsInverter) AddGate(literal, new[] { Aig.Lit(node, !complement) });
			}

			int packages = 0;
			for (int b = 0; b < buckets.Count; b++)
			{
				if (cover.GatesPerBucket[b] == 0) continue;
				packages += (cover.GatesPerBucket[b] + buckets[b].capacity - 1) / buckets[b].capacity;
			}

			cover.PackageCount = packages;
			return cover;
		}

		// Keeps a handful of runner-up implementations per signal, cheapest first.
		static void Remember(List<Choice> options, Choice option)
		{
			const int MaxOptions = 8;
			foreach (Choice existing in options)
			{
				if (existing.Bucket == option.Bucket && existing.Q == option.Q && existing.Leaves == option.Leaves) return;
			}

			if (options.Count < MaxOptions) options.Add(option);
		}

		// Rebuilds the cover from a choice table, which is what lets the local search alter one signal
		// and see the effect on the whole thing.
		static Cover ExtractCover(Aig g, List<(GateKind kind, int capacity)> buckets, Choice[] choice)
		{
			Cover cover = new() { Graph = g, GatesPerBucket = new int[buckets.Count], ChoiceOf = choice };
			HashSet<int> done = new();
			Stack<int> pending = new();

			foreach (int outLit in g.Outputs)
			{
				if (outLit >= 0) pending.Push(outLit);
			}

			while (pending.Count > 0)
			{
				int signal = pending.Pop();
				if (!done.Add(signal)) continue;

				Choice c = choice[signal];
				if (c.Kind == 1 || c.Kind == 3) continue;
				if (c.Kind != 2) return null;

				int[] inputs = new int[c.Leaves.Length];
				for (int i = 0; i < c.Leaves.Length; i++)
				{
					inputs[i] = Aig.Lit(c.Leaves[i], ((c.Q >> i) & 1) != 0);
					pending.Push(inputs[i]);
				}

				cover.GateOfSignal[signal] = cover.Gates.Count;
				cover.Gates.Add(new GateInstance { Bucket = c.Bucket, Op = c.Op, InputSignals = inputs, OutputSignal = signal });
				cover.GatesPerBucket[c.Bucket]++;
			}

			cover.PackageCount = PackageCost(cover.GatesPerBucket, buckets);
			return cover;
		}

		// Local search on the mapping: repeatedly re-implement one signal with one of the runner-up
		// cuts the DP had rejected, and keep the change when the REAL cost (packages first, gates
		// second) goes down. The DP cannot do this itself: it minimises a linear sum, while a package
		// only ever costs something when it is opened. This is the classic local-search post-pass of
		// the technology-mapping literature, judged here on the discrete package count.
		static Cover LocalSearch(Cover start, Aig g, List<(GateKind kind, int capacity)> buckets)
		{
			if (start?.Options == null || start.ChoiceOf == null) return start;

			Choice[] choice = (Choice[])start.ChoiceOf.Clone();
			Cover best = ExtractCover(g, buckets, choice);
			if (best == null) return start;
			Repack(best, buckets);

			for (int sweep = 0; sweep < 8; sweep++)
			{
				bool improved = false;
				List<int> signals = new(best.GateOfSignal.Keys);

				foreach (int signal in signals)
				{
					List<Choice> options = start.Options[signal];
					if (options == null || options.Count < 2) continue;

					Choice current = choice[signal];
					foreach (Choice option in options)
					{
						if (option.Bucket == current.Bucket && option.Q == current.Q && option.Leaves == current.Leaves) continue;

						choice[signal] = option;
						Cover candidate = ExtractCover(g, buckets, choice);
						if (candidate == null) continue;
						Repack(candidate, buckets);

						if (!Better(candidate, best)) continue;
						best = candidate;
						current = option;
						improved = true;
					}

					choice[signal] = current;
				}

				if (!improved) break;
			}

			return best;
		}

		// Longest chain of gates from an input to an output. Slots of one package are independent
		// gates, so this is measured on the cover, not on the package sub-chips.
		static int CoverDepth(Cover cover)
		{
			Dictionary<int, int> level = new();
			HashSet<int> visiting = new();

			int SignalLevel(int signal)
			{
				int node = signal >> 1;
				if (node == 0) return 0;
				if (node <= cover.Graph.NumPI && (signal & 1) == 0) return 0;
				if (!cover.GateOfSignal.TryGetValue(signal, out int gateIndex)) return 0;
				if (level.TryGetValue(gateIndex, out int cached)) return cached;
				if (!visiting.Add(gateIndex)) return 0;

				int deepest = 0;
				foreach (int input in cover.Gates[gateIndex].InputSignals) deepest = Math.Max(deepest, SignalLevel(input));

				visiting.Remove(gateIndex);
				level[gateIndex] = deepest + 1;
				return deepest + 1;
			}

			int depth = 0;
			foreach (int outLit in cover.Graph.Outputs)
			{
				if (outLit >= 0) depth = Math.Max(depth, SignalLevel(outLit));
			}

			return depth;
		}

		// Bin-packing pass on the finished cover. A gate can very often be taken from a different
		// package type - an inverter is a NOR with its inputs tied, a 2-input AND fits in a 3-input AND -
		// and the mapping DP cannot see the point of doing so: its cost is linear per gate, while the
		// real cost is a ceiling per type. Emptying a type whose last package holds two or three gates
		// removes a whole chip, even though every individual move looks like a loss to the DP.
		// This is the "local search on the mapping" of the technology-mapping literature, specialised
		// to the discrete package cost. Nothing about the logic changes: the gates keep their operator,
		// their inputs and their output, only the package they are taken from differs.
		static void Repack(Cover cover, List<(GateKind kind, int capacity)> buckets)
		{
			if (cover.Gates.Count == 0) return;

			for (int sweep = 0; sweep < buckets.Count; sweep++)
			{
				int current = PackageCost(cover.GatesPerBucket, buckets);
				int bestSource = -1;
				int bestCost = current;
				int[] bestAssignment = null;

				for (int source = 0; source < buckets.Count; source++)
				{
					if (cover.GatesPerBucket[source] == 0) continue;

					int[] counts = (int[])cover.GatesPerBucket.Clone();
					int[] assignment = new int[cover.Gates.Count];
					for (int i = 0; i < assignment.Length; i++) assignment[i] = -1;

					bool feasible = true;
					for (int i = 0; i < cover.Gates.Count && feasible; i++)
					{
						GateInstance gate = cover.Gates[i];
						if (gate.Bucket != source) continue;

						int arity = gate.InputSignals.Length;
						int destination = -1;
						int spare = -1;

						for (int b = 0; b < buckets.Count; b++)
						{
							if (b == source || !CanImplement(buckets[b].kind, gate.Op, arity)) continue;

							// prefer the type with the most room left, so no new package is opened
							int room = counts[b] % buckets[b].capacity == 0 ? 0 : buckets[b].capacity - counts[b] % buckets[b].capacity;
							if (room <= spare) continue;
							spare = room;
							destination = b;
						}

						if (destination < 0)
						{
							feasible = false;
							break;
						}

						assignment[i] = destination;
						counts[destination]++;
						counts[source]--;
					}

					if (!feasible) continue;

					int cost = PackageCost(counts, buckets);
					if (cost >= bestCost) continue;
					bestCost = cost;
					bestSource = source;
					bestAssignment = assignment;
				}

				if (bestSource < 0) return;

				for (int i = 0; i < cover.Gates.Count; i++)
				{
					if (bestAssignment[i] < 0) continue;
					cover.GatesPerBucket[cover.Gates[i].Bucket]--;
					cover.Gates[i].Bucket = bestAssignment[i];
					cover.GatesPerBucket[bestAssignment[i]]++;
				}

				cover.PackageCount = bestCost;
			}
		}

		static int PackageCost(int[] gatesPerBucket, List<(GateKind kind, int capacity)> buckets)
		{
			int packages = 0;
			for (int b = 0; b < buckets.Count; b++)
			{
				if (gatesPerBucket[b] > 0) packages += (gatesPerBucket[b] + buckets[b].capacity - 1) / buckets[b].capacity;
			}

			return packages;
		}

		static bool Better(Cover a, Cover b) =>
			a.PackageCount < b.PackageCount || (a.PackageCount == b.PackageCount && a.Gates.Count < b.Gates.Count);

		static Cover BuildCover(Aig g, List<(GateKind kind, int capacity)> buckets, double[] weights, List<AigCut>[] cuts, HashSet<int> alreadyPaid = null, bool[] forbidden = null, bool keepOptions = false)
		{
			List<Choice>[] alternatives = keepOptions ? new List<Choice>[g.NodeCount * 2] : null;
			int n = g.NodeCount;
			double[] flow = new double[n * 2];
			Choice[] choice = new Choice[n * 2];
			for (int i = 0; i < flow.Length; i++) flow[i] = Inf;

			int[] refs = g.FanoutCounts();

			// constants are free: they come straight from a GND / VCC source
			flow[Aig.C0] = 0;
			flow[Aig.C1] = 0;
			choice[Aig.C0] = new Choice { Kind = 1 };
			choice[Aig.C1] = new Choice { Kind = 1 };

			// The cheapest way to invert a signal: a dedicated inverter, or a NAND/NOR with its inputs
			// tied together. Needed on its own, because a library without a plain AND can only produce
			// one polarity of a node from its inputs and must invert to get the other.
			double notWeight = Inf;
			int notBucket = -1;
			for (int b = 0; b < buckets.Count; b++)
			{
				if (forbidden != null && forbidden[b]) continue;
				if (!CanImplement(buckets[b].kind, GateOp.Not, 1)) continue;
				if (weights[b] < notWeight)
				{
					notWeight = weights[b];
					notBucket = b;
				}
			}

			for (int node = 1; node <= g.NumPI; node++)
			{
				flow[Aig.Lit(node, false)] = 0;
				choice[Aig.Lit(node, false)] = new Choice { Kind = 1 };

				if (notBucket >= 0)
				{
					flow[Aig.Lit(node, true)] = notWeight / Math.Max(1, refs[node]);
					choice[Aig.Lit(node, true)] = new Choice { Op = GateOp.Not, Leaves = new[] { node }, Q = 0, Bucket = notBucket, Kind = 2 };
				}
			}

			for (int node = g.NumPI + 1; node < n; node++)
			{
				for (int phase = 0; phase < 2; phase++)
				{
					int signal = Aig.Lit(node, phase == 1);
					double best = Inf;
					Choice bestChoice = default;

					foreach (AigCut cut in cuts[node])
					{
						if (cut.Leaves.Length == 1 && cut.Leaves[0] == node) continue;

						(int[] leaves, ushort tt) = Reduce(cut.Leaves, cut.Tt);
						ushort target = phase == 0 ? tt : (ushort)~tt;
						int m = leaves.Length;

						if (m == 0)
						{
							if (best > 0)
							{
								best = 0;
								bestChoice = new Choice { Kind = 3, ConstValue = (byte)(target != 0 ? 1 : 0) };
							}

							continue;
						}

						for (int q = 0; q < 1 << m; q++)
						{
							ushort phased = NegateInputs(target, q);
							if (!TryClassify(phased, m, out GateOp op)) continue;

							double inputs = 0;
							bool feasible = true;
							for (int i = 0; i < m; i++)
							{
								int leafSignal = Aig.Lit(leaves[i], ((q >> i) & 1) != 0);
								double c = flow[leafSignal];
								if (c >= Inf)
								{
									feasible = false;
									break;
								}

								// already produced by the previous cover: reusing it costs nothing more
								if (alreadyPaid == null || !alreadyPaid.Contains(leafSignal)) inputs += c;
							}

							if (!feasible) continue;

							for (int b = 0; b < buckets.Count; b++)
							{
								if (forbidden != null && forbidden[b]) continue;
								if (!CanImplement(buckets[b].kind, op, m)) continue;

								Choice option = new() { Op = op, Leaves = leaves, Q = q, Bucket = b, Kind = 2 };
								if (keepOptions)
								{
									alternatives[signal] ??= new List<Choice>();
									Remember(alternatives[signal], option);
								}

								double cost = weights[b] + inputs;
								if (cost >= best) continue;
								best = cost;
								bestChoice = option;
							}
						}
					}

					if (bestChoice.Kind == 0) continue;
					flow[signal] = bestChoice.Kind == 2 ? best / Math.Max(1, refs[node]) : best;
					choice[signal] = bestChoice;
					if (keepOptions && alternatives[signal] != null) Remember(alternatives[signal], bestChoice);
				}

				// Either polarity can also be obtained by inverting the other one. With a NAND-only or
				// NOR-only palette this is the ONLY way to get the second polarity, so without it those
				// (universal) palettes would be reported as insufficient.
				if (notBucket < 0) continue;
				for (int round = 0; round < 2; round++)
				{
					for (int phase = 0; phase < 2; phase++)
					{
						int signal = Aig.Lit(node, phase == 1);
						int other = Aig.Lit(node, phase == 0);
						if (flow[other] >= Inf) continue;

						// only the inverter itself is amortised: the signal it inverts already was
						double cost = notWeight / Math.Max(1, refs[node]) + flow[other];
						if (cost >= flow[signal]) continue;

						flow[signal] = cost;
						choice[signal] = new Choice { Op = GateOp.Not, Leaves = new[] { node }, Q = phase == 0 ? 1 : 0, Bucket = notBucket, Kind = 2 };
					}
				}
			}

			// ---- turn the choices into an actual cover, sharing whatever is reachable twice ----
			Cover cover = new() { Graph = g, GatesPerBucket = new int[buckets.Count] };
			HashSet<int> done = new();
			Stack<int> pending = new();

			foreach (int outLit in g.Outputs)
			{
				if (outLit >= 0) pending.Push(outLit);
			}

			while (pending.Count > 0)
			{
				int signal = pending.Pop();
				if (!done.Add(signal)) continue;

				Choice c = choice[signal];
				if (c.Kind == 1 || c.Kind == 3) continue;
				if (c.Kind != 2) return null; // unreachable with this palette

				int[] inputs = new int[c.Leaves.Length];
				for (int i = 0; i < c.Leaves.Length; i++)
				{
					inputs[i] = Aig.Lit(c.Leaves[i], ((c.Q >> i) & 1) != 0);
					pending.Push(inputs[i]);
				}

				cover.GateOfSignal[signal] = cover.Gates.Count;
				cover.Gates.Add(new GateInstance { Bucket = c.Bucket, Op = c.Op, InputSignals = inputs, OutputSignal = signal });
				cover.GatesPerBucket[c.Bucket]++;
			}

			int packages = 0;
			for (int b = 0; b < buckets.Count; b++)
			{
				if (cover.GatesPerBucket[b] == 0) continue;
				packages += (cover.GatesPerBucket[b] + buckets[b].capacity - 1) / buckets[b].capacity;
			}

			cover.PackageCount = packages;
			cover.ChoiceOf = choice;
			cover.Options = alternatives;
			return cover;
		}

		// ---------------------------------------------------------------- package bricks

		// One DLS brick per PACKAGE, not per gate: a "4xAND2" brick has 8 input pins and 4 output pins
		// and holds four independent AND gates, exactly like the real chip. The produced module is
		// therefore made of the very packages that were ticked - what you see is the bag of chips you
		// would buy, and how many of them is the number the mapper minimised.
		class PackageBrick
		{
			public string Name;
			public GatePackage Package;
			public int[][] SlotInputPinIDs; // [slot][pin]
			public int[] SlotOutputPinIDs; // [slot]
			public ChipType BuiltinType = ChipType.Custom; // a lone 2-input NAND is the builtin one
		}

		class PackageInstance
		{
			public GatePackage Package;
			public PackageBrick Brick;
			public int SubChipID;
		}

		// Spreads the gates of each kind over real packages, following the same "fill the biggest, then
		// the smallest that still fits" rule the bill of materials reports. Leftover slots stay empty,
		// exactly like the unused gates of a real chip.
		static void Pack(Cover cover, List<(GateKind kind, int capacity)> buckets, bool[] selection,
			List<PackageInstance> instances, int[] instanceOfGate, int[] slotOfGate)
		{
			for (int b = 0; b < buckets.Count; b++)
			{
				List<int> gates = new();
				for (int i = 0; i < cover.Gates.Count; i++)
				{
					if (cover.Gates[i].Bucket == b) gates.Add(i);
				}

				if (gates.Count == 0) continue;
				int next = 0;

				foreach ((GatePackage package, int count) in GatePackages.BillOfMaterials(selection, buckets[b].kind, gates.Count))
				{
					for (int c = 0; c < count; c++)
					{
						int index = instances.Count;
						instances.Add(new PackageInstance { Package = package });

						for (int slot = 0; slot < package.GatesPerPackage && next < gates.Count; slot++)
						{
							instanceOfGate[gates[next]] = index;
							slotOfGate[gates[next]] = slot;
							next++;
						}
					}
				}
			}
		}

		static PackageBrick ResolvePackageBrick(GatePackage package, ChipLibrary library, List<ChipDescription> toCreate)
		{
			// A one-gate NAND package IS the builtin NAND: no point wrapping it in a brick of its own.
			if (package.GatesPerPackage == 1 && package.Kind.Op == GateOp.Nand && package.Kind.Arity == 2)
			{
				return new PackageBrick
				{
					Name = ChipTypeHelper.GetName(ChipType.Nand),
					Package = package,
					SlotInputPinIDs = new[] { new[] { NandMinimizer.NandInA, NandMinimizer.NandInB } },
					SlotOutputPinIDs = new[] { NandMinimizer.NandOut },
					BuiltinType = ChipType.Nand
				};
			}

			Aig reference = PackageReferenceAig(package);

			for (int attempt = 0; attempt < 10; attempt++)
			{
				string name = attempt == 0 ? package.BrickName : package.BrickName + "-" + (attempt + 1);

				if (library.TryGetChipDescription(name, out ChipDescription existing))
				{
					// reuse only a brick whose interface AND function already match, so an unrelated chip
					// that happens to carry the name is never silently wired in
					if (IsEquivalentPackage(existing, package, reference, library)) return FromDescription(existing, package);
					continue;
				}

				ChipDescription desc = BuildPackageBrick(package, name, reference);
				toCreate.Add(desc);
				return FromDescription(desc, package);
			}

			string unique = package.BrickName + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
			ChipDescription fallback = BuildPackageBrick(package, unique, reference);
			toCreate.Add(fallback);
			return FromDescription(fallback, package);
		}

		static PackageBrick FromDescription(ChipDescription desc, GatePackage package)
		{
			int slots = package.GatesPerPackage;
			int arity = package.Kind.Arity;

			PackageBrick brick = new()
			{
				Name = desc.Name,
				Package = package,
				SlotInputPinIDs = new int[slots][],
				SlotOutputPinIDs = new int[slots]
			};

			for (int s = 0; s < slots; s++)
			{
				brick.SlotInputPinIDs[s] = new int[arity];
				for (int i = 0; i < arity; i++) brick.SlotInputPinIDs[s][i] = desc.InputPins[s * arity + i].ID;
				brick.SlotOutputPinIDs[s] = desc.OutputPins[s].ID;
			}

			return brick;
		}

		static bool IsEquivalentPackage(ChipDescription desc, GatePackage package, Aig reference, ChipLibrary library)
		{
			if (desc.ChipType != ChipType.Custom) return false;
			if (desc.InputPins == null || desc.InputPins.Length != package.GatesPerPackage * package.Kind.Arity) return false;
			if (desc.OutputPins == null || desc.OutputPins.Length != package.GatesPerPackage) return false;
			if (NandMinimizer.Extract(desc, library, out Aig existing, out _) != null) return false;
			return AigEquivalence.Check(reference, existing, out _, out _);
		}

		// The package as a function: independent gates, slot s driving output s.
		static Aig PackageReferenceAig(GatePackage package)
		{
			int arity = package.Kind.Arity;
			Aig g = new(package.GatesPerPackage * arity);

			for (int s = 0; s < package.GatesPerPackage; s++)
			{
				int start = s * arity;
				g.Outputs.Add(Apply(g, package.Kind.Op, arity, i => g.PiLit(start + i)));
			}

			return g.Rebuild();
		}

		static int Apply(Aig g, GateOp op, int arity, Func<int, int> input)
		{
			switch (op)
			{
				case GateOp.Not: return Aig.Not(input(0));
				case GateOp.And:
				case GateOp.Nand:
				{
					int acc = input(0);
					for (int i = 1; i < arity; i++) acc = g.And(acc, input(i));
					return op == GateOp.And ? acc : Aig.Not(acc);
				}
				case GateOp.Or:
				case GateOp.Nor:
				{
					int acc = input(0);
					for (int i = 1; i < arity; i++) acc = g.Or(acc, input(i));
					return op == GateOp.Or ? acc : Aig.Not(acc);
				}
				case GateOp.Xor:
				{
					int acc = input(0);
					for (int i = 1; i < arity; i++) acc = g.Xor(acc, input(i));
					return acc;
				}
				default: return Aig.C0;
			}
		}

		// Pins are named like a datasheet: 1A/1B -> 1Y, 2A/2B -> 2Y, ...
		static ChipDescription BuildPackageBrick(GatePackage package, string name, Aig reference)
		{
			int slots = package.GatesPerPackage;
			int arity = package.Kind.Arity;

			string[] inNames = new string[slots * arity];
			string[] outNames = new string[slots];
			for (int s = 0; s < slots; s++)
			{
				for (int i = 0; i < arity; i++) inNames[s * arity + i] = $"{s + 1}{(char)('A' + i)}";
				outNames[s] = $"{s + 1}Y";
			}

			int nextID = 1;
			PinDescription[] inPins = ChipEmitHelper.MakePins(inNames, true, ref nextID);
			PinDescription[] outPins = ChipEmitHelper.MakePins(outNames, false, ref nextID);

			Aig minimal = NandMinimizer.RunRewriting(reference);
			return NandMinimizer.EmitNandChip(minimal, name, inPins, outPins, BrickColour(package.Kind.Op),
				NameDisplayLocation.Centre, Vector2.zero, out _, out _, out _);
		}

		static Color BrickColour(GateOp op) => op switch
		{
			GateOp.Not => new Color(0.72f, 0.45f, 0.20f),
			GateOp.And => new Color(0.25f, 0.45f, 0.72f),
			GateOp.Or => new Color(0.30f, 0.62f, 0.40f),
			GateOp.Nand => new Color(0.73f, 0.26f, 0.26f),
			GateOp.Nor => new Color(0.62f, 0.32f, 0.62f),
			_ => new Color(0.65f, 0.60f, 0.25f)
		};

		// ---------------------------------------------------------------- emitting the mapped chip

		static ChipDescription Emit(Cover cover, List<PackageInstance> instances, int[] instanceOfGate, int[] slotOfGate,
			ChipDescription src, string newName)
		{
			int nextID = 1;
			List<SubChipDescription> subChips = new();
			List<WireDescription> wires = new();

			PinDescription[] inputPins = ChipEmitHelper.CopyPins(src.InputPins, ref nextID);
			PinDescription[] outputPins = ChipEmitHelper.CopyPins(src.OutputPins, ref nextID);

			foreach (PackageInstance instance in instances)
			{
				instance.SubChipID = ChipEmitHelper.AddSubChip(subChips, ref nextID, instance.Brick.Name, instance.Brick.BuiltinType);
			}

			int vccID = -1;
			int gndID = -1;

			PinAddress SourceOf(int signal)
			{
				int node = signal >> 1;
				bool complement = (signal & 1) != 0;

				if (node == 0)
				{
					if (complement)
					{
						if (vccID < 0) vccID = ChipEmitHelper.AddSubChip(subChips, ref nextID, ChipTypeHelper.GetName(ChipType.Vcc), ChipType.Vcc);
						return new PinAddress(vccID, 0);
					}

					if (gndID < 0) gndID = ChipEmitHelper.AddSubChip(subChips, ref nextID, ChipTypeHelper.GetName(ChipType.Gnd), ChipType.Gnd);
					return new PinAddress(gndID, 0);
				}

				if (node <= cover.Graph.NumPI && !complement) return new PinAddress(inputPins[node - 1].ID, 0);

				int gateIndex = cover.GateOfSignal[signal];
				PackageInstance instance = instances[instanceOfGate[gateIndex]];
				return new PinAddress(instance.SubChipID, instance.Brick.SlotOutputPinIDs[slotOfGate[gateIndex]]);
			}

			for (int i = 0; i < cover.Gates.Count; i++)
			{
				GateInstance gate = cover.Gates[i];
				PackageInstance instance = instances[instanceOfGate[i]];
				int[] pins = instance.Brick.SlotInputPinIDs[slotOfGate[i]];
				int used = gate.InputSignals.Length;

				for (int pin = 0; pin < pins.Length; pin++)
				{
					// spare pins of a wider gate are tied to an input that is already there, which is
					// exactly how you would use a 3-input gate as a 2-input one on a real board
					int signal = gate.InputSignals[Math.Min(pin, used - 1)];
					wires.Add(ChipEmitHelper.Wire(SourceOf(signal), new PinAddress(instance.SubChipID, pins[pin])));
				}
			}

			for (int i = 0; i < cover.Graph.Outputs.Count && i < outputPins.Length; i++)
			{
				int lit = cover.Graph.Outputs[i];
				if (lit < 0) continue;
				wires.Add(ChipEmitHelper.Wire(SourceOf(lit), new PinAddress(outputPins[i].ID, 0)));
			}

			return ChipEmitHelper.Assemble(newName, src.Colour, src.NameLocation, src.Size, inputPins, outputPins, subChips, wires);
		}

		// ---------------------------------------------------------------- reading a mapped chip back

		// Rebuilds the AIG of the produced chip from its packages, slot by slot, so the emitted wiring
		// itself (which slot, which pin, tied spare pins, shared outputs) is what gets checked.
		static string ExtractGateNetlist(ChipDescription desc, Dictionary<string, PackageBrick> bricks, ChipLibrary library, out Aig aig)
		{
			aig = null;
			PinDescription[] inPins = desc.InputPins ?? Array.Empty<PinDescription>();
			PinDescription[] outPins = desc.OutputPins ?? Array.Empty<PinDescription>();

			Dictionary<int, PackageBrick> subBrick = new();
			Dictionary<int, ChipType> constants = new();

			foreach (SubChipDescription sub in desc.SubChips ?? Array.Empty<SubChipDescription>())
			{
				if (bricks.TryGetValue(sub.Name, out PackageBrick brick))
				{
					subBrick[sub.ID] = brick;
					continue;
				}

				ChipDescription subDesc = library.GetChipDescriptionForSim(sub.Name);
				ChipType type = subDesc?.ChipType ?? ChipType.Custom;
				if (type is ChipType.Vcc or ChipType.Gnd) constants[sub.ID] = type;
				else return $"Composant inattendu dans le resultat : {sub.Name}.";
			}

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
			Dictionary<(int, int), int> slotLit = new();
			HashSet<(int, int)> inProgress = new();
			string failure = null;

			int SourceLit(PinAddress src)
			{
				if (piIndex.TryGetValue(src.PinOwnerID, out int pi)) return g.PiLit(pi);
				if (constants.TryGetValue(src.PinOwnerID, out ChipType type)) return type == ChipType.Vcc ? Aig.C1 : Aig.C0;

				if (subBrick.TryGetValue(src.PinOwnerID, out PackageBrick brick))
				{
					int slot = Array.IndexOf(brick.SlotOutputPinIDs, src.PinID);
					if (slot >= 0) return SlotLit(src.PinOwnerID, slot);
					failure ??= "Fil partant d'une broche inconnue.";
					return Aig.C0;
				}

				failure ??= "Fil relie a un element inconnu.";
				return Aig.C0;
			}

			int SinkLit(int ownerID, int pinID) => driver.TryGetValue((ownerID, pinID), out PinAddress src) ? SourceLit(src) : Aig.C0;

			int SlotLit(int subChipID, int slot)
			{
				if (slotLit.TryGetValue((subChipID, slot), out int cached)) return cached;
				if (!inProgress.Add((subChipID, slot)))
				{
					failure ??= "Boucle dans le circuit produit.";
					return Aig.C0;
				}

				PackageBrick brick = subBrick[subChipID];
				int[] pins = brick.SlotInputPinIDs[slot];
				int[] literals = new int[pins.Length];
				for (int i = 0; i < pins.Length; i++) literals[i] = SinkLit(subChipID, pins[i]);

				int lit = Apply(g, brick.Package.Kind.Op, literals.Length, i => literals[i]);

				inProgress.Remove((subChipID, slot));
				slotLit[(subChipID, slot)] = lit;
				return lit;
			}

			foreach (PinDescription p in outPins)
			{
				int lit = driver.TryGetValue((p.ID, 0), out PinAddress src) ? SourceLit(src) : -1;
				if (failure != null) return failure;
				g.Outputs.Add(lit);
			}

			aig = g;
			return null;
		}
	}
}
