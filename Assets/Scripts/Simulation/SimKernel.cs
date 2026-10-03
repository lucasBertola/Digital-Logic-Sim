using Unity.Burst;
using Unity.Mathematics;

namespace DLS.Simulation
{
	// What the Burst part of SimProgram.Step reads and writes (pinned arrays of the program).
	public unsafe struct StepCtx
	{
		public uint* st;
		public byte* type;
		public int* in0, in1, out0, out1;
		public ulong* dirty, top;
		public int words, topWords, gc;
		public int* consStart, consPos, consOther, consOut;
		public byte* quiet, armed;
	}

	// The hot part of a step compiled by Burst (branch burst-experiment): it runs the pending NAND / NAND+inverter /
	// no-op gates in schedule order exactly like the managed loop, and stops at the first gate it cannot run (any
	// other builtin, a gate in the noise list, a NAND reading a floating line that is not quiet) with that gate's
	// dirty bit still set; SimProgram runs it, then calls again from the position after it. Same order, same marks,
	// same values: the managed loop and this one are interchangeable (bench "burst stepper = managed stepper").
	[BurstCompile]
	public static unsafe class SimKernel
	{
		public const byte Nand = 1, NandNot = 254, Nop = 253;

		// Runs the pending gates after position `after` (-1: from the start). Returns the position of the first gate
		// left to the caller, or -1 when the step is finished. `ran` counts the gates run here.
		[BurstCompile(CompileSynchronously = true)]
		public static int Run(StepCtx* c, int after, int* ran)
		{
			ulong* top = c->top;
			int startTw = after < 0 ? 0 : (after >> 12);
			for (int tw = startTw; tw < c->topWords; tw++)
			{
				ulong tbits = top[tw];
				if (after >= 0 && tw == startTw)
				{
					int wa = after >> 6, ba = after & 63, tba = wa & 63;
					ulong rest = ba == 63 ? 0 : c->dirty[wa] & ~((2UL << ba) - 1);
					int r = RunWord(c, wa, rest, ran);
					if (r >= 0) return r;
					if (c->dirty[wa] == 0) top[tw] &= ~(1UL << tba);
					tbits = tba == 63 ? 0 : top[tw] & ~((2UL << tba) - 1);
				}
				while (tbits != 0)
				{
					int tb = math.tzcnt(tbits);
					int w = (tw << 6) + tb;
					if (w >= c->words) break;
					int r = RunWord(c, w, c->dirty[w], ran);
					if (r >= 0) return r;
					if (c->dirty[w] == 0) top[tw] &= ~(1UL << tb);
					tbits = tb == 63 ? 0 : top[tw] & ~((2UL << tb) - 1);
				}
			}
			return -1;
		}

		// the gates of one dirty word from `bits` on; >= 0: a gate for the caller, -1: word done
		static int RunWord(StepCtx* c, int w, ulong bits, int* ran)
		{
			uint* st = c->st;
			ulong* dirty = c->dirty;
			while (bits != 0)
			{
				int b = math.tzcnt(bits);
				ulong bit = 1UL << b;
				int k = (w << 6) + b;
				if (k >= c->gc) { dirty[w] &= ~bit; return -1; }
				byte tk = c->type[k];
				if (c->armed[k] != 0) return k;
				if (tk == Nand || tk == NandNot)
				{
					int ia = c->in0[k], ic = c->in1[k];
					uint a = st[ia], cv = st[ic];
					if (((a | cv) & 0x10000) != 0)
					{
						if ((a & 0x10000) != 0 && c->quiet[ia] == 0) return k;
						if ((cv & 0x10000) != 0 && c->quiet[ic] == 0) return k;
					}
					dirty[w] &= ~bit;
					(*ran)++;
					uint v = (1 ^ (a & cv)) & 1;
					int o = c->out0[k];
					if (tk == Nand) { if (st[o] != v) { st[o] = v; Mark(c, o); } }
					else
					{
						st[o] = v;
						int o2 = c->out1[k];
						uint nv = (1 ^ v) & 1;
						if (st[o2] != nv) { st[o2] = nv; Mark(c, o2); }
					}
				}
				else if (tk == Nop) { dirty[w] &= ~bit; (*ran)++; }
				else return k;
				bits = b == 63 ? 0 : dirty[w] & ~((bit << 1) - 1);
			}
			return -1;
		}

		// SimProgram.MarkConsumers, NAND wake-up filter included
		static void Mark(StepCtx* c, int slot)
		{
			uint* st = c->st;
			for (int j = c->consStart[slot], e = c->consStart[slot + 1]; j < e; j++)
			{
				int o = c->consOther[j];
				if (o >= 0 && (st[o] & 0x10001u) == 0 && st[c->consOut[j]] == 1) continue;
				int p = c->consPos[j], w = p >> 6;
				c->dirty[w] |= 1UL << (p & 63);
				c->top[w >> 6] |= 1UL << (w & 63);
			}
		}
	}
}
