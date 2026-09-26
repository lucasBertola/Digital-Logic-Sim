using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DLS.Game
{
	// Don't-care based simplification (the idea behind ABC's `mfs` / `resub`).
	//
	// Structural rewriting only ever replaces a sub-circuit by one computing the SAME function. But a
	// node does not have to keep its function: it only has to keep the function of the chip's OUTPUTS.
	// For every input pattern where flipping a node changes no output, that node's value is free -
	// those are its observability don't-cares. A node can then be replaced by a constant, an input, or
	// another node that merely agrees with it on the patterns that are actually observable.
	//
	// Tools normally approximate this with SAT over a local window. Circuits here are small enough to
	// do it EXACTLY: the care set is obtained by simulating the whole circuit twice, once with the node
	// flipped, and XOR-ing the outputs. So every replacement is provably output-preserving, not merely
	// plausible - which matters, because nothing may change the result.
	public static class AigDontCare
	{
		public const int MaxInputs = 20;

		// Cost grows as nodes x nodes x words, and memory as nodes x words. A flat input limit made this
		// pass vanish on anything past 16 inputs even when the circuit had shrunk enough to afford it,
		// so the two real costs are budgeted directly instead.
		const long MemoryBudgetWords = 4_000_000; // ~32 MB per buffer
		const long WorkBudgetWords = 1_500_000_000;

		public static bool Applicable(Aig g)
		{
			if (g.NumPI is <= 0 or > MaxInputs) return false;

			long words = Math.Max(1, 1L << Math.Max(0, g.NumPI - 6));
			long nodes = g.NodeCount;
			return nodes * words <= MemoryBudgetWords && nodes * nodes * words <= WorkBudgetWords;
		}

		public static Aig Simplify(Aig g, int rounds = 4)
		{
			if (!Applicable(g)) return g;

			Aig best = g.Rebuild();
			for (int round = 0; round < rounds; round++)
			{
				Aig candidate = Pass(best);
				if (candidate.NodeCount >= best.NodeCount) break;
				best = candidate;
			}

			return best;
		}

		static Aig Pass(Aig g)
		{
			int n = g.NodeCount;
			int words = Math.Max(1, 1 << Math.Max(0, g.NumPI - 6));

			ulong[][] values = New(n, words);
			ulong[][] flipped = New(n, words);
			ulong[] care = new ulong[words];
			ulong[] normalOut = new ulong[words];

			List<int> subst = new(n);
			for (int i = 0; i < n; i++) subst.Add(Aig.Lit(i, false));

			FillInputs(values, g.NumPI, words);
			FillInputs(flipped, g.NumPI, words);
			Simulate(g, values, subst, words);

			bool[] live = g.MarkLive();

			for (int node = g.NumPI + 1; node < n; node++)
			{
				if (!live[node]) continue;

				ComputeCare(g, values, flipped, subst, node, words, care, normalOut);

				// Nothing observable: the node can be anything at all.
				bool anyCare = false;
				for (int w = 0; w < words; w++)
				{
					if (care[w] != 0)
					{
						anyCare = true;
						break;
					}
				}

				int replacement = anyCare ? FindAgreeingSignal(g, values, subst, node, words, care) : Aig.C0;
				if (replacement < 0 || replacement == Aig.Lit(node, false)) continue;

				subst[node] = replacement;
				Simulate(g, values, subst, words); // later nodes must see the new values
			}

			return g.Rebuild(subst);
		}

		// A signal that agrees with `node` everywhere it is observable can stand in for it. Constants
		// and inputs come first: replacing by one of those kills the node's whole cone.
		static int FindAgreeingSignal(Aig g, ulong[][] values, List<int> subst, int node, int words, ulong[] care)
		{
			if (Agrees(values, node, Aig.C0, words, care)) return Aig.C0;
			if (Agrees(values, node, Aig.C1, words, care)) return Aig.C1;

			int best = -1;
			object gate = new();

			// Scanning the candidates is the other half of the cost; the lowest matching index is kept
			// so the outcome does not depend on which thread got there first.
			Parallel.For(1, node, candidate =>
			{
				if (Volatile.Read(ref best) >= 0 && candidate > Volatile.Read(ref best)) return;
				if (Aig.NodeOf(Aig.ResolveSubst(subst, Aig.Lit(candidate, false))) != candidate) return;

				for (int phase = 0; phase < 2; phase++)
				{
					int signal = Aig.Lit(candidate, phase == 1);
					if (!Agrees(values, node, signal, words, care)) continue;

					lock (gate)
					{
						if (best < 0 || candidate < best >> 1) best = signal;
					}

					return;
				}
			});

			return best;
		}

		static bool Agrees(ulong[][] values, int node, int signal, int words, ulong[] care)
		{
			ulong[] target = values[node];
			for (int w = 0; w < words; w++)
			{
				if (((target[w] ^ Value(values, signal, w)) & care[w]) != 0) return false;
			}

			return true;
		}

		// care = the patterns for which flipping `node` changes at least one output.
		static void ComputeCare(Aig g, ulong[][] values, ulong[][] flipped, List<int> subst, int node, int words, ulong[] care, ulong[] scratch)
		{
			Array.Clear(care, 0, words);

			ForEachChunk(words, (start, end) =>
			{
				int length = end - start;
				for (int i = 0; i <= node; i++) Array.Copy(values[i], start, flipped[i], start, length);
				for (int w = start; w < end; w++) flipped[node][w] = ~values[node][w];

				for (int i = node + 1; i < g.NodeCount; i++) Evaluate(g, flipped, subst, i, start, end);

				foreach (int outLit in g.Outputs)
				{
					if (outLit < 0) continue;
					int resolved = Aig.ResolveSubst(subst, outLit);
					for (int w = start; w < end; w++) care[w] |= Value(values, resolved, w) ^ Value(flipped, resolved, w);
				}
			});
		}

		// Input patterns are independent of each other, so the word range is split across cores. On a
		// 19-input circuit that is 8192 words per node, which is where all the time goes.
		const int ChunkWords = 256;

		static void ForEachChunk(int words, Action<int, int> body)
		{
			int chunks = (words + ChunkWords - 1) / ChunkWords;
			if (chunks <= 2)
			{
				body(0, words);
				return;
			}

			Parallel.For(0, chunks, c =>
			{
				int start = c * ChunkWords;
				body(start, Math.Min(start + ChunkWords, words));
			});
		}

		static void Simulate(Aig g, ulong[][] values, List<int> subst, int words)
		{
			ForEachChunk(words, (start, end) =>
			{
				for (int node = g.NumPI + 1; node < g.NodeCount; node++) Evaluate(g, values, subst, node, start, end);
			});
		}

		static void Evaluate(Aig g, ulong[][] values, List<int> subst, int node, int start, int end)
		{
			int a = Aig.ResolveSubst(subst, g.F0[node]);
			int b = Aig.ResolveSubst(subst, g.F1[node]);
			ulong[] dst = values[node];
			for (int w = start; w < end; w++) dst[w] = Value(values, a, w) & Value(values, b, w);
		}

		static ulong Value(ulong[][] values, int lit, int word)
		{
			int node = lit >> 1;
			ulong v = node == 0 ? 0ul : values[node][word];
			return (lit & 1) != 0 ? ~v : v;
		}

		static readonly ulong[] BasePatterns =
		{
			0xAAAAAAAAAAAAAAAAul, 0xCCCCCCCCCCCCCCCCul, 0xF0F0F0F0F0F0F0F0ul,
			0xFF00FF00FF00FF00ul, 0xFFFF0000FFFF0000ul, 0xFFFFFFFF00000000ul
		};

		static void FillInputs(ulong[][] values, int numPI, int words)
		{
			for (int i = 0; i < numPI; i++)
			{
				ulong[] dst = values[i + 1];
				if (i < 6)
				{
					for (int w = 0; w < words; w++) dst[w] = BasePatterns[i];
				}
				else
				{
					for (int w = 0; w < words; w++) dst[w] = ((w >> (i - 6)) & 1) != 0 ? ulong.MaxValue : 0ul;
				}
			}
		}

		static ulong[][] New(int nodeCount, int words)
		{
			ulong[][] buffer = new ulong[nodeCount][];
			for (int i = 0; i < nodeCount; i++) buffer[i] = new ulong[words];
			return buffer;
		}
	}
}
