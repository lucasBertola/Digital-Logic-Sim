using DLS.Description;
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
		// RUN FAST models (SimProgram.BuildModelTable)
		public int* canon, inStart, outStart, inSlots, outSlots, modelOff, modelData;
		public ulong* modelPtrs;
		public int hasModels;
		// noise: the per-step random stream and the noise list (SimProgram.ArmNoise / DisarmNoise, Simulator.RandomBits16)
		public uint* pcg;
		public int* noiseList, noisePos, noiseCount;
		public uint pcgV; public int noiseCountV; // what pcg / noiseCount point to (the context lives in unmanaged memory)
	}

	// The hot part of a step compiled by Burst: it runs the pending NAND / NAND+inverter / no-op gates and the RUN FAST
	// models (tables, register / counter / RAM templates) in schedule order exactly like the managed loop — floating
	// inputs read as noise with the same random draws, the noise list kept the same way — and stops at the first gate
	// it cannot run (any other builtin) with that gate's dirty bit still set; SimProgram runs it, then calls again from
	// the position after it. Same order, same marks, same values: the managed loop and this one are interchangeable
	// (bench "burst stepper = managed stepper").
	[BurstCompile]
	public static unsafe class SimKernel
	{
		public const byte Nand = 1, NandNot = 254, Nop = 253, Model = (byte)ChipType.FastModel;

		// true when this class really runs Burst-compiled code (the [BurstDiscard] call is removed by Burst)
		[BurstCompile(CompileSynchronously = true)]
		public static int CompiledByBurst() { int managed = 0; MarkManaged(ref managed); return managed == 0 ? 1 : 0; }
		[BurstDiscard] static void MarkManaged(ref int m) => m = 1;

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
			uint* vin = stackalloc uint[MaxModelInputs];
			while (bits != 0)
			{
				int b = math.tzcnt(bits);
				ulong bit = 1UL << b;
				int k = (w << 6) + b;
				if (k >= c->gc) { dirty[w] &= ~bit; return -1; }
				byte tk = c->type[k];
				int g = -1, off = -1;
				if (tk == Model)
				{
					if (c->hasModels == 0) return k;
					g = c->canon[k]; off = c->modelOff[g];
					if (off < 0 || c->modelData[off + 2] > MaxModelInputs) return k;
				}
				else if (tk != Nand && tk != NandNot && tk != Nop) return k;

				dirty[w] &= ~bit;
				(*ran)++;
				if (c->armed[k] != 0) Disarm(c, k); // re-armed below if it still reads a floating line

				if (tk == Nand || tk == NandNot)
				{
					int ia = c->in0[k], ic = c->in1[k];
					uint a = st[ia], cv = st[ic];
					if (((a | cv) & 0x10000) != 0)
					{
						bool na = (a & 0x10000) != 0 && c->quiet[ia] == 0, ncc = (cv & 0x10000) != 0 && c->quiet[ic] == 0;
						if (na) a = Noisy(c, a);
						if (ncc) cv = Noisy(c, cv);
						if (na || ncc) Arm(c, k);
					}
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
				else if (tk == Model)
				{
					int* md = c->modelData + off;
					int nIn = md[2], i0 = c->inStart[g];
					for (int j = 0; j < nIn; j++)
					{
						int s = c->inSlots[i0 + j];
						uint v = st[s];
						if ((v & 0xFFFF0000u) != 0 && c->quiet[s] == 0) { Arm(c, k); v = Noisy(c, v); } // SimProgram.In
						vin[j] = v;
					}
					bool rerun = md[0] == 1 ? RunLut(c, md, g, vin) : RunSeq(c, md, g, vin);
					if (rerun) dirty[w] |= bit; // runs again next step (the bit is behind the scan)
				}
				bits = b == 63 ? 0 : dirty[w] & ~((bit << 1) - 1);
			}
			return -1;
		}

		const int MaxModelInputs = 64;

		// Simulator.RandomBits16 on the stream the caller handed over
		static uint Random16(StepCtx* c)
		{
			uint s = *c->pcg * 747796405 + 2891336453;
			*c->pcg = s;
			uint result = ((s >> (int)((s >> 28) + 4)) ^ s) * 277803737;
			result = (result >> 22) ^ result;
			return result & 0xFFFF;
		}

		// SimProgram.Noisy: floating bits become random bits (tri flags kept)
		static uint Noisy(StepCtx* c, uint s)
		{
			uint tri = s >> 16;
			return tri == 0 ? s : (s & 0xFFFF & ~tri) | (Random16(c) & tri) | (tri << 16);
		}

		// SimProgram.ArmNoise / DisarmNoise
		static void Arm(StepCtx* c, int pos)
		{
			int g = c->canon[pos];
			c->armed[pos] = 1;
			if (c->noisePos[g] >= 0) return;
			c->noisePos[g] = *c->noiseCount; c->noiseList[(*c->noiseCount)++] = g;
		}

		static void Disarm(StepCtx* c, int pos)
		{
			int g = c->canon[pos];
			c->armed[pos] = 0;
			int p = c->noisePos[g];
			if (p < 0) return;
			int last = c->noiseList[*c->noiseCount - 1];
			c->noiseList[p] = last; c->noisePos[last] = p; (*c->noiseCount)--; c->noisePos[g] = -1;
		}

		// LutModel.Run on the slots: index = input bits concatenated, first pin in the low bits
		static bool RunLut(StepCtx* c, int* md, int g, uint* vin)
		{
			int nIn = md[2], nOut = md[3], o0 = c->outStart[g];
			int* bits = md + 4;
			int index = 0, shift = 0;
			for (int j = 0; j < nIn; j++)
			{
				index |= (int)(vin[j] & ((1u << bits[j]) - 1)) << shift;
				shift += bits[j];
			}
			uint* table = (uint*)c->modelPtrs[md[1]] + index * nOut;
			for (int o = 0; o < nOut; o++) Write(c, c->outSlots[o0 + o], table[o]);
			return false;
		}

		// SeqModel.Run on the slots (keep the two in step); returns RerunNextStep
		static bool RunSeq(StepCtx* c, int* md, int g, uint* vin)
		{
			int nOut = md[3], o0 = c->outStart[g];
			int* p = md + 4;
			int kind = p[0], D = p[1], Clk = p[2], Load = p[3], Reset = p[4], Oe = p[5], Cs = p[6], We = p[7], Q = p[8], QN = p[9], flags = p[10], resetMode = p[11];
			uint mask = (uint)p[12];
			int nAddr = p[13];
			uint floating = mask << 16;
			uint* state = (uint*)c->modelPtrs[md[1]];
			uint* r = (uint*)c->modelPtrs[md[1] + 1];
			bool rising = (flags & 1) != 0, loadHigh = (flags & 2) != 0, resetHigh = (flags & 4) != 0, oeHigh = (flags & 8) != 0, csHigh = (flags & 16) != 0, weHigh = (flags & 32) != 0;
			if (kind == 2) // RAM
			{
				uint a = 0; int shift = 0;
				for (int j = 0; j < nAddr; j++) { int pin = p[14 + 2 * j], b = p[15 + 2 * j]; a |= (vin[pin] & (uint)((1 << b) - 1)) << shift; shift += b; }
				bool sel = Cs < 0 || Active(vin, Cs, csHigh);
				if (sel && Active(vin, We, weHigh)) state[a] = vin[D] & mask;
				uint q = sel && (Oe < 0 || Active(vin, Oe, oeHigh)) ? state[a] : floating;
				for (int o = 0; o < nOut; o++) Write(c, c->outSlots[o0 + o], o == Q ? q : 0);
				return false;
			}
			if (r[3] != 0) { r[2] = state[0]; r[3] = 0; }
			uint clk = vin[Clk] & 1;
			bool edge = rising ? r[0] == 0 && clk == 1 : r[0] == 1 && clk == 0;
			bool beforeEdgeLevel = rising ? clk == 0 : clk == 1;
			r[0] = clk;
			if (Reset >= 0 && resetMode == 2 && Active(vin, Reset, resetHigh)) state[0] = 0;
			else if (edge)
			{
				if (Reset >= 0 && resetMode == 1 && r[5] != 0) state[0] = 0;
				else if (kind == 0) { if (r[4] != 0) state[0] = r[1]; }
				else state[0] = r[4] != 0 ? r[1] : (state[0] + 1) & mask;
			}
			if (beforeEdgeLevel)
			{
				r[1] = vin[D] & mask;
				r[4] = Load < 0 || Active(vin, Load, loadHigh) ? 1u : 0u;
				r[5] = Reset >= 0 && Active(vin, Reset, resetHigh) ? 1u : 0u;
			}
			bool rerun = false;
			if (state[0] != r[2]) { r[3] = 1; rerun = true; }
			bool on = Oe < 0 || Active(vin, Oe, oeHigh);
			for (int o = 0; o < nOut; o++)
			{
				uint v = o == Q ? (on ? r[2] : floating) : (o == QN && QN >= 0) ? (on ? ~r[2] & mask : floating) : 0;
				Write(c, c->outSlots[o0 + o], v);
			}
			return rerun;
		}

		static bool Active(uint* vin, int pin, bool high) => pin >= 0 && ((vin[pin] & 1) == 1) == high;

		static void Write(StepCtx* c, int slot, uint v)
		{
			if (c->st[slot] != v) { c->st[slot] = v; Mark(c, slot); }
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
