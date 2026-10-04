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
		// other builtins run here (SimProgram.RunGate, keep the two in step): input counts, internal states (clock,
		// RAM65536) by canonical gate, the clock level of the step, merge winners (display colouring, applied by SimProgram)
		public int* inCount;
		public ulong* istate; public int* istateLen;
		public int clockHigh, forced;
		public int* mergeWinner, winnerChanged; public byte* winnerQueued; public int winnerChangedCount;
		// a whole batch of steps (RunBatch): Simulator.RunSimulationSteps' loop, IdleSteps and the head of Program.Step
		public int frame, period, maxSteps, done, phase, stepFirst, after, hasCuts, handback;
		public int noiseAcc, lastClockLevel, stepsRun;
		public long realSteps, gatesRun;
		public uint seedState;
		public int* clockGates; public int clockGateCount;
		public int* posOfCanon;
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
				else if (tk != Nand && tk != NandNot && tk != Nop && !Supported(c, k, tk)) return k;

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
				else if (tk != Nop)
				{
					if (RunOther(c, k, tk)) dirty[w] |= bit; // a merge conflict is re-drawn next step
				}
				bits = b == 63 ? 0 : dirty[w] & ~((bit << 1) - 1);
			}
			return -1;
		}

		const byte MergeT = 255;
		const uint M = 1 | (1 << 16); // PinState.SingleBitMask

		static bool Supported(StepCtx* c, int k, byte tk)
		{
			switch (tk)
			{
				case MergeT:
				case (byte)ChipType.TriStateBuffer:
				case (byte)ChipType.Vcc:
				case (byte)ChipType.Gnd:
				case (byte)ChipType.Split_4To1Bit:
				case (byte)ChipType.Merge_1To4Bit:
				case (byte)ChipType.Merge_1To8Bit:
				case (byte)ChipType.Merge_4To8Bit:
				case (byte)ChipType.Split_8To4Bit:
				case (byte)ChipType.Split_8To1Bit:
					return true;
				case (byte)ChipType.Clock:
					return c->istate[c->canon[k]] != 0;
				case (byte)ChipType.Ram65536:
					return c->istate[c->canon[k]] != 0 && c->istateLen[c->canon[k]] >= 16384;
				default:
					return false;
			}
		}

		// SimProgram.In: a logic input, floating bits read as noise (the gate joins the noise list)
		static uint In(StepCtx* c, int slot, int k)
		{
			uint s = c->st[slot];
			if ((s & 0xFFFF0000u) == 0 || c->quiet[slot] != 0) return s;
			Arm(c, k);
			return Noisy(c, s);
		}

		// SimProgram.RunGate for the builtins above (keep the two in step); returns "run again next step"
		static bool RunOther(StepCtx* c, int k, byte tk)
		{
			uint* st = c->st;
			int g = c->canon[k];
			int* ins = c->inSlots + c->inStart[g];
			int* outs = c->outSlots + c->outStart[g];
			switch (tk)
			{
				case MergeT:
					return RunMerge(c, k, g);
				case (byte)ChipType.TriStateBuffer:
				{
					uint en = In(c, c->in1[k], k);
					Write(c, c->out0[k], (en & 1) == 1 ? st[c->in0[k]] : 0xFFFF0000u);
					return false;
				}
				case (byte)ChipType.Clock:
				{
					uint* cs = (uint*)c->istate[g];
					bool high = c->istateLen[g] >= 2 && cs[0] != 0 && c->forced < 0 ? cs[1] != 0 : c->clockHigh != 0;
					Write(c, c->out0[k], high ? 1u : 0u);
					return false;
				}
				case (byte)ChipType.Vcc: Write(c, c->out0[k], 1); return false;
				case (byte)ChipType.Gnd: Write(c, c->out0[k], 0); return false;
				case (byte)ChipType.Split_4To1Bit:
				{
					uint v = st[c->in0[k]];
					Write(c, outs[0], (v >> 3) & M);
					Write(c, outs[1], (v >> 2) & M);
					Write(c, outs[2], (v >> 1) & M);
					Write(c, outs[3], v & M);
					return false;
				}
				case (byte)ChipType.Merge_1To4Bit:
					Write(c, c->out0[k], (st[ins[3]] & M) | (st[ins[2]] & M) << 1 | (st[ins[1]] & M) << 2 | (st[ins[0]] & M) << 3);
					return false;
				case (byte)ChipType.Merge_1To8Bit:
				{
					uint r = 0;
					for (int b = 0; b < 8; b++) r |= (st[ins[7 - b]] & M) << b;
					Write(c, c->out0[k], r);
					return false;
				}
				case (byte)ChipType.Merge_4To8Bit:
				{
					uint a = st[c->in1[k]], b = st[c->in0[k]]; // PinState.Set8BitFrom4BitSources(ref r, in1, in0)
					uint bits = ((a & 0xFFFF) | ((b & 0xFFFF) << 4)) & 0xFFFF;
					uint tri = ((a >> 16) & 0xF) | (((b >> 16) & 0xF) << 4);
					Write(c, c->out0[k], bits | (tri << 16));
					return false;
				}
				case (byte)ChipType.Split_8To4Bit:
				{
					uint v = st[c->in0[k]]; // PinState.Set4BitFrom8BitSource: first output = high nibble, second = low
					uint hi = ((v & 0xF0) >> 4) | ((((v >> 16) & 0xF0) >> 4) << 16);
					uint lo = (v & 0xF) | (((v >> 16) & 0xF) << 16);
					Write(c, outs[0], hi);
					Write(c, outs[1], lo);
					return false;
				}
				case (byte)ChipType.Split_8To1Bit:
				{
					uint v = st[c->in0[k]];
					for (int b = 0; b < 8; b++) Write(c, outs[b], (v >> (7 - b)) & M);
					return false;
				}
				case (byte)ChipType.Ram65536:
				{
					// Ram65536.Run (the inputs read in the same order, for the same noise draws)
					uint dIn = In(c, ins[0], k), adrHigh = In(c, ins[1], k), adrLow = In(c, ins[2], k), we = In(c, ins[3], k), oe = In(c, ins[4], k), cs = In(c, ins[5], k);
					uint o = 0xFFu << 16;
					if ((cs & 1) == 1)
					{
						uint* mem = (uint*)c->istate[g];
						int a = (int)(((adrHigh & 0xFF) << 8) | (adrLow & 0xFF));
						int wd = (a >> 2) & 16383, sh = (a & 3) * 8;
						if ((we & 1) == 1) mem[wd] = (mem[wd] & ~(0xFFu << sh)) | ((dIn & 0xFF) << sh);
						if ((oe & 1) == 1) o = (mem[wd] >> sh) & 0xFF;
					}
					Write(c, c->out0[k], o);
					return false;
				}
			}
			return false;
		}

		// SimProgram.Merge (keep the two in step): per bit, a driven source beats a floating one, driven sources in
		// conflict are drawn at random, floating sources on a driven bit get the net's value written back
		static bool RunMerge(StepCtx* c, int k, int g)
		{
			uint* st = c->st;
			int* ins = c->inSlots + c->inStart[g];
			int count = c->inCount[g];
			uint drivenAny = 0, orBits = 0, andBits = 0xFFFF;
			for (int j = 0; j < count; j++)
			{
				uint s = st[ins[j]];
				uint bits = s & 0xFFFF, tri = s >> 16, drv = ~tri & 0xFFFF;
				orBits |= bits & drv;
				andBits &= bits | tri;
				drivenAny |= drv;
			}
			uint conflict = (orBits ^ andBits) & drivenAny;
			uint chosen = conflict != 0 && RandomBool(c) ? orBits : andBits;
			uint resTri = ~drivenAny & 0xFFFF;
			uint resBits = chosen & drivenAny;
			Write(c, c->out0[k], resBits | (resTri << 16));
			if (drivenAny != 0)
			{
				int winner = -1;
				for (int j = 0; j < count; j++)
				{
					int slot = ins[j];
					uint s = st[slot];
					uint tri = s >> 16;
					uint take = tri & drivenAny;
					if (take != 0) Write(c, slot, ((s & 0xFFFF & ~take) | (resBits & take)) | (tri << 16));
					else if (winner < 0 && (~tri & 0xFFFF) != 0) winner = j;
				}
				if (winner >= 0 && c->mergeWinner[g] != winner)
				{
					c->mergeWinner[g] = winner;
					if (c->winnerQueued[g] == 0) { c->winnerQueued[g] = 1; c->winnerChanged[c->winnerChangedCount++] = g; }
				}
			}
			return conflict != 0;
		}

		static uint PcgNext(StepCtx* c)
		{
			uint s = *c->pcg * 747796405 + 2891336453;
			*c->pcg = s;
			uint result = ((s >> (int)((s >> 28) + 4)) ^ s) * 277803737;
			return (result >> 22) ^ result;
		}
		static bool RandomBool(StepCtx* c) => PcgNext(c) < uint.MaxValue / 2; // Simulator.RandomBool
		static int RandomIndex(StepCtx* c, int n) => (int)(PcgNext(c) % (uint)n); // Simulator.RandomIndex

		// Simulator.NextStepSeed (keep the two in step)
		static uint NextStepSeed(StepCtx* c)
		{
			uint x = c->seedState += 0x9E3779B9u;
			x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
			return x;
		}

		public const int BatchDone = 0, BatchHandback = 1, BatchReschedule = 2;

		// Simulator.RunSimulationSteps' batched loop, entirely here: idle spans skipped (SimProgram.IdleSteps), real steps
		// run (the head of SimProgram.Step: noise re-draws, clock level; then the gates). Returns BatchHandback with
		// c->handback = a gate the caller runs (its dirty bit already cleared) before calling again, BatchReschedule when
		// the step needs SimProgram.Reschedule first (then call again), BatchDone when c->done reached c->maxSteps.
		// What the managed loop did once per batch (root inputs, ROM edits, keyboard version) is done by the caller.
		[BurstCompile(CompileSynchronously = true)]
		public static int RunBatch(StepCtx* c)
		{
			while (true)
			{
				if (c->phase == 0)
				{
					if (c->stepFirst == 0)
					{
						int idle = IdleSpan(c);
						if (idle > 0)
						{
							c->noiseAcc += idle * *c->noiseCount;
							c->frame = WrapFrame(c->frame + idle, c->period);
							c->done += idle;
						}
						if (c->done >= c->maxSteps) return BatchDone;
					}
					c->stepFirst = 0;
					// a real step: Simulator.RunSimulationSteps
					c->realSteps++;
					*c->pcg = NextStepSeed(c);
					bool reorder = c->frame % 100 == 0;
					c->frame = WrapFrame(c->frame + 1, c->period);
					c->phase = 2;
					if (reorder && c->hasCuts != 0) return BatchReschedule;
				}
				if (c->phase == 2)
				{
					// the head of SimProgram.Step
					c->stepsRun++;
					c->noiseAcc += *c->noiseCount;
					while (c->noiseAcc >= 1024 && *c->noiseCount > 0) // SimProgram.NoisePeriod
					{
						c->noiseAcc -= 1024;
						SetDirty(c, c->posOfCanon[c->noiseList[RandomIndex(c, *c->noiseCount)]]);
					}
					bool clockHigh = c->forced >= 0 ? c->forced == 1 : c->period != 0 && ((c->frame / c->period) & 1) == 0;
					int level = clockHigh ? 1 : 0;
					c->clockHigh = level;
					if (level != c->lastClockLevel)
					{
						c->lastClockLevel = level;
						for (int i = 0; i < c->clockGateCount; i++) SetDirty(c, c->posOfCanon[c->clockGates[i]]);
					}
					c->after = -1;
					c->phase = 1;
				}
				int ran = 0;
				int kh = Run(c, c->after, &ran);
				c->gatesRun += ran;
				if (kh >= 0)
				{
					c->dirty[kh >> 6] &= ~(1UL << (kh & 63));
					c->handback = kh;
					c->after = kh;
					return BatchHandback;
				}
				c->phase = 0;
				c->done++;
			}
		}

		static void SetDirty(StepCtx* c, int pos)
		{
			int w = pos >> 6;
			c->dirty[w] |= 1UL << (pos & 63);
			c->top[w >> 6] |= 1UL << (w & 63);
		}

		// Simulator.WrapFrame
		static int WrapFrame(int frame, int period)
		{
			if (frame < 1_000_000_000) return frame;
			int cycle = 200 * math.max(1, period);
			return frame - cycle * (frame / cycle - 1000);
		}

		// SimProgram.IdleSteps without what the caller checks once per batch (root inputs, ROM edits, keyboard)
		static int IdleSpan(StepCtx* c)
		{
			int left = c->maxSteps - c->done;
			if (left <= 0) return 0;
			for (int t = 0; t < c->topWords; t++)
				if (c->top[t] != 0)
				{
					int end = math.min(c->words, (t + 1) << 6);
					for (int w = t << 6; w < end; w++) if (c->dirty[w] != 0) return 0;
					c->top[t] = 0;
				}
			int span = left;
			if (c->forced < 0 && AnyClockRunning(c))
			{
				if (c->period <= 0) return 0;
				span = math.min(span, c->period - c->frame % c->period - 1);
			}
			return span < 0 ? 0 : span;
		}

		// SimProgram.AnyClockRunning
		static bool AnyClockRunning(StepCtx* c)
		{
			for (int i = 0; i < c->clockGateCount; i++)
			{
				int g = c->clockGates[i];
				if (c->istate[g] == 0 || c->istateLen[g] < 2 || ((uint*)c->istate[g])[0] == 0) return true;
			}
			return false;
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
