using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Mathematics;

namespace DLS.Simulation
{
	// EXPERIMENT (branch burst-experiment): the shape of SimProgram's hot loop — NAND gates run in schedule order from
	// dirty bits, a changed output wakes its consumers — compiled by Burst vs run by Mono, same code.
	public static unsafe class PropagateMono
	{
		static int[] deBruijn;
		static readonly int[] DeBruijnInit = { 0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4, 62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5, 63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11, 46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6 };

		public static int Step(uint* states, int* in0, int* in1, int* outSlot, ulong* dirty, int dirtyWords, int* consStart, int* consList)
		{
			int[] DeBruijn = deBruijn ??= new[] { 0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4, 62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5, 63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11, 46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6 };
			int ran = 0;
			for (int w = 0; w < dirtyWords; w++)
			{
				while (dirty[w] != 0)
				{
					ulong bits = dirty[w];
					int b = DeBruijn[(int)(((bits & (ulong)-(long)bits) * 0x03f79d71b4cb0a89UL) >> 58)];
					dirty[w] = bits & (bits - 1);
					int g = (w << 6) | b;
					uint v = ~(states[in0[g]] & states[in1[g]]) & 1u;
					ran++;
					int o = outSlot[g];
					if (states[o] != v)
					{
						states[o] = v;
						for (int c = consStart[o]; c < consStart[o + 1]; c++) { int k = consList[c]; dirty[k >> 6] |= 1UL << (k & 63); }
					}
				}
			}
			return ran;
		}
	}

	[BurstCompile]
	public static unsafe class PropagateBurst
	{
		[BurstCompile(CompileSynchronously = true)]
		public static int Step(uint* states, int* in0, int* in1, int* outSlot, ulong* dirty, int dirtyWords, int* consStart, int* consList)
		{
			int ran = 0;
			for (int w = 0; w < dirtyWords; w++)
			{
				while (dirty[w] != 0)
				{
					ulong bits = dirty[w];
					int b = math.tzcnt(bits);
					dirty[w] = bits & (bits - 1);
					int g = (w << 6) | b;
					uint v = ~(states[in0[g]] & states[in1[g]]) & 1u;
					ran++;
					int o = outSlot[g];
					if (states[o] != v)
					{
						states[o] = v;
						for (int c = consStart[o]; c < consStart[o + 1]; c++) { int k = consList[c]; dirty[k >> 6] |= 1UL << (k & 63); }
					}
				}
			}
			return ran;
		}
	}

	public static unsafe class BurstBench
	{
		// Mono vs Burst on a random feedback-free NAND network of 44 000 gates, ~35 gate runs per step
		public static string Run(int steps = 300000)
		{
			const int inputs = 64, gates = 44000;
			int slots = inputs + gates;
			var rnd = new System.Random(1);
			int[] in0 = new int[gates], in1 = new int[gates], outSlot = new int[gates];
			for (int g = 0; g < gates; g++)
			{
				int Pick() => g < 50 ? rnd.Next(inputs) : inputs + Math.Max(0, g - 1 - rnd.Next(40));
				in0[g] = Pick(); in1[g] = Pick(); outSlot[g] = inputs + g;
			}
			int[] count = new int[slots + 1];
			for (int g = 0; g < gates; g++) { count[in0[g]]++; count[in1[g]]++; }
			int[] consStart = new int[slots + 1];
			for (int s2 = 0; s2 < slots; s2++) consStart[s2 + 1] = consStart[s2] + count[s2];
			int[] fill = (int[])consStart.Clone();
			int[] consList = new int[consStart[slots]];
			for (int g = 0; g < gates; g++) { consList[fill[in0[g]]++] = g; consList[fill[in1[g]]++] = g; }
			int dirtyWords = (gates + 63) / 64;
			string result = "";
			foreach (string mode in new[] { "Mono", "Burst", "Mono", "Burst" })
			{
				uint[] states = new uint[slots];
				ulong[] dirty = new ulong[dirtyWords];
				for (int g = 0; g < gates; g++) dirty[g >> 6] |= 1UL << (g & 63);
				var r2 = new System.Random(7);
				long runs = 0;
				var sw = Stopwatch.StartNew();
				fixed (uint* st = states) fixed (int* a = in0) fixed (int* b = in1) fixed (int* o = outSlot) fixed (ulong* d = dirty) fixed (int* cs = consStart) fixed (int* cl = consList)
				{
					for (int step = 0; step < steps; step++)
					{
						if ((step & 7) == 0)
						{
							int i = r2.Next(inputs);
							st[i] ^= 1;
							for (int c = cs[i]; c < cs[i + 1]; c++) { int k = cl[c]; d[k >> 6] |= 1UL << (k & 63); }
						}
						runs += mode == "Mono" ? PropagateMono.Step(st, a, b, o, d, dirtyWords, cs, cl) : PropagateBurst.Step(st, a, b, o, d, dirtyWords, cs, cl);
					}
				}
				result += $"{mode} {sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, runs):0.0} ns/gate  ";
			}
			return result + (BurstCompiler.IsEnabled ? "(Burst enabled)" : "(Burst DISABLED)");
		}
	}
}
