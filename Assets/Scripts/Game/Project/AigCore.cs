using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DLS.Game
{
	// And-Inverter Graph: the working representation shared by the NAND minimiser and the gate
	// (74xx package) mapper. A literal is node * 2 + complement bit; node 0 is the constant node
	// and nodes 1..NumPI are the primary inputs.
	public class Aig
	{
		public const int C0 = 0;
		public const int C1 = 1;

		public readonly List<int> F0 = new();
		public readonly List<int> F1 = new();
		public readonly int NumPI;
		public readonly List<int> Outputs = new(); // one literal per output pin, -1 when left unconnected
		readonly Dictionary<long, int> hash = new();

		public Aig(int numPI)
		{
			NumPI = numPI;
			for (int i = 0; i <= numPI; i++)
			{
				F0.Add(-1);
				F1.Add(-1);
			}
		}

		public int NodeCount => F0.Count;
		public static int Lit(int node, bool complement) => (node << 1) | (complement ? 1 : 0);
		public static int NodeOf(int lit) => lit >> 1;
		public static int Not(int lit) => lit ^ 1;
		public int PiLit(int index) => Lit(index + 1, false);

		static long Key(int a, int b) => ((long)a << 32) | (uint)b;

		// Structural hashing + the trivial identities: everything built through this is already
		// free of duplicated and constant-foldable logic.
		public int And(int a, int b)
		{
			if (a == C0 || b == C0) return C0;
			if (a == C1) return b;
			if (b == C1) return a;
			if (a == b) return a;
			if ((a ^ b) == 1) return C0;
			if (a > b) (a, b) = (b, a);

			if (hash.TryGetValue(Key(a, b), out int existing)) return Lit(existing, false);

			F0.Add(a);
			F1.Add(b);
			int index = F0.Count - 1;
			hash[Key(a, b)] = index;
			return Lit(index, false);
		}

		public int Or(int a, int b) => Not(And(Not(a), Not(b)));
		public int Xor(int a, int b) => Or(And(a, Not(b)), And(Not(a), b));

		// Undo the nodes created after `count`. A rejected candidate must leave nothing behind:
		// dead nodes would make every later cost evaluation slower and slower.
		public void Truncate(int count)
		{
			while (F0.Count > count)
			{
				int last = F0.Count - 1;
				hash.Remove(Key(F0[last], F1[last]));
				F0.RemoveAt(last);
				F1.RemoveAt(last);
			}
		}

		public bool[] MarkLive()
		{
			bool[] live = new bool[NodeCount];
			Stack<int> stack = new();

			foreach (int outLit in Outputs)
			{
				if (outLit < 0) continue;
				int node = outLit >> 1;
				if (node > NumPI && !live[node])
				{
					live[node] = true;
					stack.Push(node);
				}
			}

			while (stack.Count > 0)
			{
				int node = stack.Pop();
				for (int side = 0; side < 2; side++)
				{
					int child = (side == 0 ? F0[node] : F1[node]) >> 1;
					if (child > NumPI && !live[child])
					{
						live[child] = true;
						stack.Push(child);
					}
				}
			}

			return live;
		}

		// How many times each node is used as a fanin of a live node (plus once per output),
		// which is what area-flow uses to amortise a shared sub-circuit over its consumers.
		public int[] FanoutCounts()
		{
			bool[] live = MarkLive();
			int[] refs = new int[NodeCount];

			for (int node = NumPI + 1; node < NodeCount; node++)
			{
				if (!live[node]) continue;
				refs[F0[node] >> 1]++;
				refs[F1[node] >> 1]++;
			}

			foreach (int outLit in Outputs)
			{
				if (outLit >= 0) refs[outLit >> 1]++;
			}

			return refs;
		}

		// Rebuild compactly (dead nodes dropped, indices topological), optionally applying a
		// substitution table produced by a rewriting pass.
		public Aig Rebuild(List<int> subst = null)
		{
			Aig result = new(NumPI);
			int[] map = new int[NodeCount];
			for (int i = 0; i < map.Length; i++) map[i] = -1;

			int Resolve(int lit) => subst == null ? lit : ResolveSubst(subst, lit);

			int Mapped(int lit)
			{
				int r = Resolve(lit);
				int node = r >> 1;
				if (node <= NumPI) return Lit(node, (r & 1) != 0);
				return map[node] ^ (r & 1);
			}

			List<(int node, bool expanded)> stack = new();

			void Visit(int lit)
			{
				int root = Resolve(lit) >> 1;
				if (root <= NumPI || map[root] >= 0) return;
				stack.Add((root, false));

				while (stack.Count > 0)
				{
					(int node, bool expanded) = stack[^1];
					if (map[node] >= 0)
					{
						stack.RemoveAt(stack.Count - 1);
						continue;
					}

					if (!expanded)
					{
						stack[^1] = (node, true);
						for (int side = 0; side < 2; side++)
						{
							int child = Resolve(side == 0 ? F0[node] : F1[node]) >> 1;
							if (child > NumPI && map[child] < 0) stack.Add((child, false));
						}
					}
					else
					{
						stack.RemoveAt(stack.Count - 1);
						map[node] = result.And(Mapped(F0[node]), Mapped(F1[node]));
					}
				}
			}

			foreach (int outLit in Outputs)
			{
				if (outLit >= 0) Visit(outLit);
			}

			foreach (int outLit in Outputs) result.Outputs.Add(outLit < 0 ? -1 : Mapped(outLit));

			return result;
		}

		public static int ResolveSubst(List<int> subst, int lit)
		{
			int cur = lit;
			for (int guard = 0; guard < 1 << 20; guard++)
			{
				int node = cur >> 1;
				if (node >= subst.Count) return cur;
				int s = subst[node];
				if ((s >> 1) == node) return cur;
				cur = s ^ (cur & 1);
			}

			return cur;
		}
	}

	// ---------------------------------------------------------------- cuts

	public readonly struct AigCut
	{
		public readonly int[] Leaves; // node indices, ascending
		public readonly ushort Tt; // function of the cut root over the leaves

		public AigCut(int[] leaves, ushort tt)
		{
			Leaves = leaves;
			Tt = tt;
		}
	}

	// K-feasible cut enumeration (K = 4), the front end of every cut-based algorithm here:
	// DAG-aware rewriting and technology mapping both work on these.
	public static class AigCuts
	{
		public static readonly ushort[] VarTt = { 0xAAAA, 0xCCCC, 0xF0F0, 0xFF00 };

		public static List<AigCut>[] Enumerate(Aig g, int cutLimit)
		{
			List<AigCut>[] cuts = new List<AigCut>[g.NodeCount];

			for (int node = 0; node <= g.NumPI && node < g.NodeCount; node++)
			{
				cuts[node] = new List<AigCut> { new(new[] { node }, VarTt[0]) };
			}

			for (int node = g.NumPI + 1; node < g.NodeCount; node++)
			{
				List<AigCut> merged = new();
				int lit0 = g.F0[node];
				int lit1 = g.F1[node];
				List<AigCut> a = cuts[Aig.NodeOf(lit0)];
				List<AigCut> b = cuts[Aig.NodeOf(lit1)];

				foreach (AigCut ca in a)
				{
					foreach (AigCut cb in b)
					{
						int[] leaves = Union(ca.Leaves, cb.Leaves);
						if (leaves == null) continue;

						ushort ta = Expand(ca.Tt, ca.Leaves, leaves);
						ushort tb = Expand(cb.Tt, cb.Leaves, leaves);
						if ((lit0 & 1) != 0) ta = (ushort)~ta;
						if ((lit1 & 1) != 0) tb = (ushort)~tb;

						Add(merged, new AigCut(leaves, (ushort)(ta & tb)), cutLimit);
					}
				}

				merged.Insert(0, new AigCut(new[] { node }, VarTt[0]));
				cuts[node] = merged;
			}

			return cuts;
		}

		static void Add(List<AigCut> list, AigCut cut, int cutLimit)
		{
			foreach (AigCut existing in list)
			{
				if (SameLeaves(existing.Leaves, cut.Leaves)) return;
			}

			if (list.Count < cutLimit)
			{
				list.Add(cut);
				return;
			}

			// Keep the cuts with the fewest leaves: those are the ones that match small cells.
			int worst = 0;
			for (int i = 1; i < list.Count; i++)
			{
				if (list[i].Leaves.Length > list[worst].Leaves.Length) worst = i;
			}

			if (list[worst].Leaves.Length > cut.Leaves.Length) list[worst] = cut;
		}

		public static bool SameLeaves(int[] a, int[] b)
		{
			if (a.Length != b.Length) return false;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i]) return false;
			}

			return true;
		}

		public static int[] Union(int[] a, int[] b)
		{
			Span<int> tmp = stackalloc int[8];
			int n = 0;
			int i = 0;
			int j = 0;

			while (i < a.Length || j < b.Length)
			{
				if (n >= 5) return null;
				int v;
				if (i < a.Length && (j >= b.Length || a[i] < b[j])) v = a[i++];
				else if (j < b.Length && (i >= a.Length || b[j] < a[i])) v = b[j++];
				else
				{
					v = a[i];
					i++;
					j++;
				}

				tmp[n++] = v;
			}

			if (n > 4) return null;

			int[] result = new int[n];
			for (int k = 0; k < n; k++) result[k] = tmp[k];
			return result;
		}

		// Rewrites a truth table expressed over `from` into the variable slots of `to` (a superset).
		public static ushort Expand(ushort tt, int[] from, int[] to)
		{
			if (SameLeaves(from, to)) return tt;

			Span<int> slot = stackalloc int[4];
			for (int i = 0; i < from.Length; i++)
			{
				for (int j = 0; j < to.Length; j++)
				{
					if (to[j] == from[i])
					{
						slot[i] = j;
						break;
					}
				}
			}

			ushort result = 0;
			for (int m = 0; m < 16; m++)
			{
				int source = 0;
				for (int i = 0; i < from.Length; i++)
				{
					if (((m >> slot[i]) & 1) != 0) source |= 1 << i;
				}

				if (((tt >> source) & 1) != 0) result |= (ushort)(1 << m);
			}

			return result;
		}

		// The set of variables the function actually depends on.
		public static int Support(ushort tt, int leafCount)
		{
			int support = 0;
			for (int i = 0; i < leafCount; i++)
			{
				// depends on variable i if flipping it changes the function anywhere
				for (int m = 0; m < 16; m++)
				{
					if (((tt >> m) & 1) != ((tt >> (m ^ (1 << i))) & 1))
					{
						support |= 1 << i;
						break;
					}
				}
			}

			return support;
		}
	}

	// ---------------------------------------------------------------- minimal-structure library

	// For every 4-input function, the cheapest AIG structure found: tt = A AND B, where A and B are
	// the truth tables of strictly cheaper functions. Built once by a cost-ordered breadth-first
	// enumeration - the same idea as ABC's precomputed rewriting library, except the table is indexed
	// by the truth table itself, so no NPN canonisation is needed.
	// Immutable once built, hence safe to share between threads.
	public static class AigLibrary
	{
		const int MaxCost = 6;
		static ushort[] libA;
		static ushort[] libB;
		static bool[] libHas;
		static readonly object buildLock = new();

		public static void Build()
		{
			if (libHas != null) return;

			lock (buildLock)
			{
				if (libHas != null) return;

				ushort[] a = new ushort[65536];
				ushort[] b = new ushort[65536];
				bool[] has = new bool[65536];
				byte[] cost = new byte[65536];
				for (int i = 0; i < 65536; i++) cost[i] = 255;

				List<ushort>[] levels = new List<ushort>[MaxCost + 1];
				for (int i = 0; i <= MaxCost; i++) levels[i] = new List<ushort>();

				void Seed(ushort tt)
				{
					cost[tt] = 0;
					cost[(ushort)~tt] = 0;
					levels[0].Add(tt);
				}

				Seed(0);
				foreach (ushort v in AigCuts.VarTt) Seed(v);

				for (int c = 1; c <= MaxCost; c++)
				{
					for (int ca = 0; ca <= c - 1; ca++)
					{
						int cb = c - 1 - ca;
						if (cb < ca) break;

						foreach (ushort f in levels[ca])
						{
							foreach (ushort h in levels[cb])
							{
								for (int polarity = 0; polarity < 4; polarity++)
								{
									ushort x = (polarity & 1) == 0 ? f : (ushort)~f;
									ushort y = (polarity & 2) == 0 ? h : (ushort)~h;
									ushort tt = (ushort)(x & y);
									if (cost[tt] <= c) continue;

									cost[tt] = (byte)c;
									a[tt] = x;
									b[tt] = y;
									has[tt] = true;
									levels[c].Add(tt);

									ushort inverse = (ushort)~tt;
									if (cost[inverse] > c) cost[inverse] = (byte)c;
								}
							}
						}
					}
				}

				libA = a;
				libB = b;
				libHas = has; // published last: readers test this one
			}
		}

		// Builds the structure for `tt` into the graph, using `leaves` for the four variables.
		// Returns -1 when the function is not in the library. Unused variables may safely be padded
		// with any literal: the stored identity holds for every assignment of them.
		public static int BuildInto(Aig g, ushort tt, int[] leaves)
		{
			Build();
			return Recurse(tt, 0);

			int Recurse(ushort f, int depth)
			{
				if (depth > 32) return -1;
				if (f == 0) return Aig.C0;
				if (f == 0xFFFF) return Aig.C1;

				for (int i = 0; i < 4; i++)
				{
					if (f == AigCuts.VarTt[i]) return leaves[i];
					if (f == (ushort)~AigCuts.VarTt[i]) return Aig.Not(leaves[i]);
				}

				bool complement = false;
				ushort key = f;
				if (!libHas[key])
				{
					key = (ushort)~f;
					complement = true;
					if (!libHas[key]) return -1;
				}

				int x = Recurse(libA[key], depth + 1);
				if (x < 0) return -1;
				int y = Recurse(libB[key], depth + 1);
				if (y < 0) return -1;

				int result = g.And(x, y);
				return complement ? Aig.Not(result) : result;
			}
		}
	}

	// ---------------------------------------------------------------- equivalence checking

	// Bit-parallel simulation of two AIGs over the input space, used to prove that a generated
	// circuit computes exactly the same thing as the one it was derived from.
	public static class AigEquivalence
	{
		public const int MaxExhaustiveInputs = 20;
		const int BlockWords = 256;

		static readonly ulong[] BasePatterns =
		{
			0xAAAAAAAAAAAAAAAAul, 0xCCCCCCCCCCCCCCCCul, 0xF0F0F0F0F0F0F0F0ul,
			0xFF00FF00FF00FF00ul, 0xFFFF0000FFFF0000ul, 0xFFFFFFFF00000000ul
		};

		public static bool Check(Aig a, Aig b, out bool exhaustive, out long patterns)
		{
			exhaustive = a.NumPI <= MaxExhaustiveInputs;
			patterns = 0;

			if (a.NumPI != b.NumPI || a.Outputs.Count != b.Outputs.Count) return false;

			for (int i = 0; i < a.Outputs.Count; i++)
			{
				if (a.Outputs[i] < 0 != b.Outputs[i] < 0) return false;
			}

			int n = a.NumPI;
			bool exhaustiveLocal = exhaustive; // `out` parameters cannot be captured by the parallel body
			long totalWords = exhaustiveLocal ? Math.Max(1L, 1L << Math.Max(0, n - 6)) : 4096;
			int blockCount = (int)((totalWords + BlockWords - 1) / BlockWords);
			patterns = exhaustiveLocal ? 1L << n : totalWords * 64;

			bool mismatch = false;

			// Blocks of input patterns are independent, so spread them over the cores.
			Parallel.For(0, blockCount, (block, state) =>
			{
				if (mismatch)
				{
					state.Stop();
					return;
				}

				long start = (long)block * BlockWords;
				int words = (int)Math.Min(BlockWords, totalWords - start);

				ulong[][] piValues = new ulong[n][];
				for (int i = 0; i < n; i++) piValues[i] = new ulong[words];
				FillInputs(piValues, n, start, words, exhaustiveLocal, new Random(0x5EED + block));

				ulong[][] valuesA = NewBuffer(a.NodeCount, words);
				ulong[][] valuesB = NewBuffer(b.NodeCount, words);
				Simulate(a, piValues, valuesA, words);
				Simulate(b, piValues, valuesB, words);

				for (int o = 0; o < a.Outputs.Count; o++)
				{
					if (a.Outputs[o] < 0) continue;
					for (int w = 0; w < words; w++)
					{
						if (Value(valuesA, a.Outputs[o], w) != Value(valuesB, b.Outputs[o], w))
						{
							mismatch = true;
							state.Stop();
							return;
						}
					}
				}
			});

			return !mismatch;
		}

		static ulong[][] NewBuffer(int nodeCount, int words)
		{
			ulong[][] buffer = new ulong[nodeCount][];
			for (int i = 0; i < nodeCount; i++) buffer[i] = new ulong[words];
			return buffer;
		}

		static void FillInputs(ulong[][] piValues, int n, long startWord, int words, bool exhaustive, Random rng)
		{
			for (int i = 0; i < n; i++)
			{
				ulong[] dst = piValues[i];

				if (!exhaustive)
				{
					for (int w = 0; w < words; w++) dst[w] = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
					continue;
				}

				if (i < 6)
				{
					ulong pattern = BasePatterns[i];
					for (int w = 0; w < words; w++) dst[w] = pattern;
				}
				else
				{
					// Bits 6 and above of the pattern index are constant inside one 64-bit word.
					for (int w = 0; w < words; w++) dst[w] = (((startWord + w) >> (i - 6)) & 1) != 0 ? ulong.MaxValue : 0ul;
				}
			}
		}

		static void Simulate(Aig g, ulong[][] piValues, ulong[][] values, int words)
		{
			for (int i = 0; i < g.NumPI; i++) Array.Copy(piValues[i], values[i + 1], words);

			for (int node = g.NumPI + 1; node < g.NodeCount; node++)
			{
				ulong[] dst = values[node];
				int lit0 = g.F0[node];
				int lit1 = g.F1[node];
				for (int w = 0; w < words; w++) dst[w] = Value(values, lit0, w) & Value(values, lit1, w);
			}
		}

		static ulong Value(ulong[][] values, int lit, int word)
		{
			int node = lit >> 1;
			ulong v = node == 0 ? 0ul : values[node][word];
			return (lit & 1) != 0 ? ~v : v;
		}
	}
}
