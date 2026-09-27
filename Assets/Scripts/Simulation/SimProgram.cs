using System;
using System.Collections.Generic;
using DLS.Description;

namespace DLS.Simulation
{
	// The compiled, flat form of a SimChip tree — what a simulation step actually runs.
	//
	// The SimChip / SimPin tree stays the source of truth for STRUCTURE (it is what the editor addresses,
	// modifies at runtime and reads for display); this class turns it into arrays once, and again whenever the
	// structure changes:
	//   * every pin is resolved to a SLOT in one contiguous uint[] of pin states. A pin fed by exactly one
	//     source shares its source's slot (no copy: a signal crossing custom-chip boundaries costs nothing),
	//     a pin fed by several sources is a MERGE node with its own slot, a builtin output pin or a root input
	//     pin owns a slot, an unconnected pin gets a slot holding "floating, low" forever;
	//   * every builtin chip becomes a GATE reading and writing slots directly; custom chips vanish from the
	//     step, but not from the ORDER: gates are emitted by walking the tree exactly as the original stepper
	//     did — at every level the sub-chips run in the order they become ready (all their inputs produced by
	//     already-run siblings), and when none is ready (a feedback loop) one of the unready non-bus chips is
	//     picked at random and runs on last step's values. So the timing of latches, registers and buses is
	//     the same as before, and the random picks are re-drawn every 100 steps so race conditions vary.
	// SimPin.State reads through to the slot, so everything that looks at pins (UI sync, harness probes,
	// tests) sees the live values without any write-back.
	public sealed class SimProgram
	{
		const int MergeType = -1;
		static int compileStampCounter;

		struct Gate
		{
			public int type;              // ChipType, or MergeType
			public int in0, in1, out0;    // first slots (hot path)
			public int inStart, inCount;  // all input slots, in inSlots
			public int outStart, outCount;
			public int canon;             // index into the side arrays (internal state, merge info)
		}

		public uint[] states = Array.Empty<uint>();
		int slotCount;

		Gate[] canonical = Array.Empty<Gate>();  // gates in creation order
		Gate[] gates = Array.Empty<Gate>();      // gates in schedule order (copied from canonical)
		int[] inSlots = Array.Empty<int>(), outSlots = Array.Empty<int>();
		uint[][] internalState = Array.Empty<uint[]>();
		SimPin[] mergeTarget = Array.Empty<SimPin>();
		SimPin[][] mergeSources = Array.Empty<SimPin[]>();

		// ---- the tree, for scheduling (indices into `chips`, pre-order) ----
		SimChip[] chips = Array.Empty<SimChip>();
		int[] childStart = Array.Empty<int>(), childList = Array.Empty<int>();   // children of a custom chip
		int[] edgeStart = Array.Empty<int>(), edgeList = Array.Empty<int>();     // sibling consumers of a chip (one entry per wire)
		int[] indegree0 = Array.Empty<int>();                                    // wires from siblings into a chip
		int[] gateOfChip = Array.Empty<int>();                                   // canonical gate of a builtin chip, or -1
		int[] mergeBeforeStart = Array.Empty<int>(), mergeBeforeList = Array.Empty<int>(); // merge gates on a chip's input pins
		int[] mergeAfterStart = Array.Empty<int>(), mergeAfterList = Array.Empty<int>();   // merge gates on a chip's output pins
		bool hasCuts;

		public int GateCount => canonical.Length;
		public int SlotCount => slotCount;

		// ------------------------------------------------------------------ compile

		public static SimProgram Compile(SimChip root, SimProgram previous)
		{
			var prog = new SimProgram();
			var pins = new List<SimPin>(previous != null ? previous.slotCount : 1024);
			var chipList = new List<SimChip>();
			int stamp = System.Threading.Interlocked.Increment(ref compileStampCounter);

			void Walk(SimChip chip, SimChip parent)
			{
				chip.compileIndex = chipList.Count; chip.compileParent = parent;
				chipList.Add(chip);
				foreach (SimPin p in chip.InputPins) { p.compileStamp = stamp; p.compileIndex = pins.Count; pins.Add(p); }
				foreach (SimPin p in chip.OutputPins) { p.compileStamp = stamp; p.compileIndex = pins.Count; pins.Add(p); }
				foreach (SimChip s in chip.SubChips) Walk(s, chip);
			}
			Walk(root, null);
			int n = pins.Count, nc = chipList.Count;
			SimChip[] chips = chipList.ToArray();
			prog.chips = chips;

			// ---- sources of every pin (CSR) ----
			int[] nSrc = new int[n];
			foreach (SimPin p in pins)
				foreach (SimPin t in p.ConnectedTargetPins)
					if (t.compileStamp == stamp) nSrc[t.compileIndex]++;
			int[] srcStart = new int[n + 1];
			for (int i = 0; i < n; i++) srcStart[i + 1] = srcStart[i] + nSrc[i];
			int[] srcList = new int[srcStart[n]];
			int[] fill = new int[n];
			foreach (SimPin p in pins)
				foreach (SimPin t in p.ConnectedTargetPins)
					if (t.compileStamp == stamp) { int ti = t.compileIndex; srcList[srcStart[ti] + fill[ti]++] = p.compileIndex; }

			// ---- who owns a slot, who aliases whom ----
			bool[] isDriver = new bool[n];
			bool[] isConstant = new bool[n];
			int[] aliasOf = new int[n];
			for (int i = 0; i < n; i++) aliasOf[i] = -1;
			foreach (SimPin p in root.InputPins) isDriver[p.compileIndex] = true;
			foreach (SimChip b in chips)
			{
				if (!b.IsBuiltin) continue;
				if (ChipTypeHelper.IsBusOriginType(b.ChipType) && b.InputPins.Length > 0 && b.OutputPins.Length > 0)
					aliasOf[b.OutputPins[0].compileIndex] = b.InputPins[0].compileIndex; // a bus is a wire
				else
					foreach (SimPin p in b.OutputPins) isDriver[p.compileIndex] = true;
			}
			for (int i = 0; i < n; i++)
			{
				if (isDriver[i] || aliasOf[i] >= 0) continue;
				if (nSrc[i] >= 2) isDriver[i] = true;                 // merge node
				else if (nSrc[i] == 1) aliasOf[i] = srcList[srcStart[i]];
				else { isDriver[i] = true; isConstant[i] = true; }     // unconnected: floating, low, forever
			}

			int[] slotOf = new int[n];
			for (int i = 0; i < n; i++) slotOf[i] = -1;
			bool[] onPath = new bool[n];
			var path = new List<int>();
			int nextSlot = 0;
			for (int i = 0; i < n; i++)
			{
				if (slotOf[i] >= 0) continue;
				path.Clear();
				int cur = i;
				while (slotOf[cur] < 0 && !onPath[cur])
				{
					onPath[cur] = true; path.Add(cur);
					if (isDriver[cur]) { slotOf[cur] = nextSlot++; break; }
					cur = aliasOf[cur];
				}
				if (slotOf[cur] < 0) { isDriver[cur] = true; isConstant[cur] = true; slotOf[cur] = nextSlot++; } // a loop of plain wires
				int slot = slotOf[cur];
				foreach (int p in path) { slotOf[p] = slot; onPath[p] = false; }
			}
			prog.slotCount = nextSlot;

			// ---- state slots: keep every pin's current value (snapshot BEFORE rebinding, the array may be reused) ----
			uint[] old = new uint[n];
			for (int i = 0; i < n; i++) old[i] = pins[i].State;
			uint[] states = previous != null && previous.states.Length >= nextSlot ? previous.states : new uint[nextSlot + nextSlot / 4 + 64];
			for (int i = 0; i < n; i++)
				if (isDriver[i]) states[slotOf[i]] = isConstant[i] ? PinState.FloatingLow : old[i];
			prog.states = states;
			for (int i = 0; i < n; i++)
			{
				SimPin p = pins[i];
				p.stateIndex = slotOf[i];
				p.stateArray = states;
				// display colouring: where this pin's value comes from (a merge updates it while stepping)
				if (nSrc[i] == 1) { SimPin s = pins[srcList[srcStart[i]]]; p.latestSourceID = s.ID; p.latestSourceParentChipID = s.parentChip.ID; }
				else if (nSrc[i] == 0) { p.latestSourceID = -1; p.latestSourceParentChipID = -1; }
			}

			// ---- gates ----
			var gl = new List<Gate>(nc);
			var inList = new List<int>(nc * 3);
			var outList = new List<int>(nc);
			var stateList = new List<uint[]>(nc);
			var mTarget = new List<SimPin>();
			var mSources = new List<SimPin[]>();
			int[] gateOfChip = new int[nc];
			var mergeBefore = new List<int>[nc];
			var mergeAfter = new List<int>[nc];
			foreach (SimChip b in chips)
			{
				gateOfChip[b.compileIndex] = -1;
				if (!b.IsBuiltin) continue;
				if (ChipTypeHelper.IsBusType(b.ChipType)) continue; // origin = wire, terminus = nothing
				if (b.OutputPins.Length == 0 && b.ChipType != ChipType.Buzzer) continue; // pure displays: their input pins already carry the values
				Gate g = new() { type = (int)b.ChipType, canon = gl.Count, inStart = inList.Count, inCount = b.InputPins.Length, outStart = outList.Count, outCount = b.OutputPins.Length };
				foreach (SimPin p in b.InputPins) inList.Add(slotOf[p.compileIndex]);
				foreach (SimPin p in b.OutputPins) outList.Add(slotOf[p.compileIndex]);
				g.in0 = g.inCount > 0 ? inList[g.inStart] : 0;
				g.in1 = g.inCount > 1 ? inList[g.inStart + 1] : 0;
				g.out0 = g.outCount > 0 ? outList[g.outStart] : 0;
				gateOfChip[b.compileIndex] = gl.Count;
				gl.Add(g); stateList.Add(b.InternalState); mTarget.Add(null); mSources.Add(null);
			}
			for (int i = 0; i < n; i++)
			{
				if (nSrc[i] < 2 || !isDriver[i] || isConstant[i]) continue;
				SimPin target = pins[i];
				SimChip owner = target.parentChip;
				if (owner.IsBuiltin && !target.isInput) continue; // (a builtin output with sources: ignore them)
				Gate g = new() { type = MergeType, canon = gl.Count, inStart = inList.Count, inCount = nSrc[i], outStart = outList.Count, outCount = 1 };
				var srcs = new SimPin[nSrc[i]];
				for (int k = 0; k < nSrc[i]; k++) { int s = srcList[srcStart[i] + k]; inList.Add(slotOf[s]); srcs[k] = pins[s]; }
				outList.Add(slotOf[i]);
				g.in0 = inList[g.inStart]; g.in1 = inList[g.inStart + 1]; g.out0 = slotOf[i];
				// the merged value must be complete when its owner runs: right before it (input pin) or at its end (output pin)
				ref List<int> bucket = ref (target.isInput ? ref mergeBefore[owner.compileIndex] : ref mergeAfter[owner.compileIndex]);
				(bucket ??= new List<int>()).Add(gl.Count);
				gl.Add(g); stateList.Add(null); mTarget.Add(target); mSources.Add(srcs);
			}
			prog.canonical = gl.ToArray();
			prog.inSlots = inList.ToArray();
			prog.outSlots = outList.ToArray();
			prog.internalState = stateList.ToArray();
			prog.mergeTarget = mTarget.ToArray();
			prog.mergeSources = mSources.ToArray();
			prog.gateOfChip = gateOfChip;
			(prog.mergeBeforeStart, prog.mergeBeforeList) = Csr(mergeBefore);
			(prog.mergeAfterStart, prog.mergeAfterList) = Csr(mergeAfter);

			// ---- the tree for scheduling: children of every custom chip, and the wires between siblings ----
			int[] nChildren = new int[nc];
			foreach (SimChip c in chips) if (c.compileParent != null) nChildren[c.compileParent.compileIndex]++;
			int[] childStart = new int[nc + 1];
			for (int c = 0; c < nc; c++) childStart[c + 1] = childStart[c] + nChildren[c];
			int[] childList = new int[childStart[nc]];
			int[] cfill = new int[nc];
			foreach (SimChip c in chips) if (c.compileParent != null) { int p = c.compileParent.compileIndex; childList[childStart[p] + cfill[p]++] = c.compileIndex; }

			// a wire from an output pin of sibling S to an input pin of sibling T (same parent) is an edge S -> T
			int[] nEdges = new int[nc];
			int[] indeg = new int[nc];
			foreach (SimPin p in pins)
			{
				if (p.isInput || p.parentChip.compileParent == null) continue;
				SimChip s = p.parentChip;
				foreach (SimPin t in p.ConnectedTargetPins)
				{
					if (t.compileStamp != stamp || !t.isInput) continue;
					SimChip tc = t.parentChip;
					if (tc.compileParent != s.compileParent) continue;
					nEdges[s.compileIndex]++; indeg[tc.compileIndex]++;
				}
			}
			int[] edgeStart = new int[nc + 1];
			for (int c = 0; c < nc; c++) edgeStart[c + 1] = edgeStart[c] + nEdges[c];
			int[] edgeList = new int[edgeStart[nc]];
			int[] efill = new int[nc];
			foreach (SimPin p in pins)
			{
				if (p.isInput || p.parentChip.compileParent == null) continue;
				SimChip s = p.parentChip;
				foreach (SimPin t in p.ConnectedTargetPins)
				{
					if (t.compileStamp != stamp || !t.isInput) continue;
					SimChip tc = t.parentChip;
					if (tc.compileParent != s.compileParent) continue;
					edgeList[edgeStart[s.compileIndex] + efill[s.compileIndex]++] = tc.compileIndex;
				}
			}
			prog.childStart = childStart; prog.childList = childList;
			prog.edgeStart = edgeStart; prog.edgeList = edgeList;
			prog.indegree0 = indeg;

			prog.Schedule();
			return prog;
		}

		static (int[] start, int[] list) Csr(List<int>[] buckets)
		{
			int[] start = new int[buckets.Length + 1];
			for (int i = 0; i < buckets.Length; i++) start[i + 1] = start[i] + (buckets[i]?.Count ?? 0);
			int[] list = new int[start[buckets.Length]];
			for (int i = 0; i < buckets.Length; i++) buckets[i]?.CopyTo(list, start[i]);
			return (start, list);
		}

		// Emits the gates in the order the tree stepper visited the chips: per level, chips as they become
		// ready; when none is (feedback), a random unready chip — a non-bus one while any remains — runs with
		// last step's inputs. A merge node runs right before the chip that owns the pin.
		// scratch, indexed by chip (pos*) or by position within a level (remaining / nonBus, from childStart)
		int[] indeg, orderBuf, remaining, nonBus, posRem, posNonBus;
		readonly List<Queue<int>> queuePool = new();
		int nOrdered;

		void Schedule()
		{
			int nc = chips.Length, gc = canonical.Length;
			indeg ??= new int[nc];
			Array.Copy(indegree0, indeg, nc);
			orderBuf ??= new int[gc];
			remaining ??= new int[nc];
			nonBus ??= new int[nc];
			posRem ??= new int[nc];
			posNonBus ??= new int[nc];
			nOrdered = 0;
			hasCuts = false;
			Emit(0, 0);
			if (gates.Length != gc) gates = new Gate[gc];
			for (int k = 0; k < gc; k++) gates[k] = canonical[orderBuf[k]];
		}

		void Emit(int chip, int depth)
		{
			int cs = childStart[chip], count = childStart[chip + 1] - cs;
			if (count > 0)
			{
				if (queuePool.Count <= depth) queuePool.Add(new Queue<int>());
				Queue<int> ready = queuePool[depth];
				ready.Clear();
				// two O(1)-random-pick lists: every unfinished child, and the unfinished non-bus children
				int nRemaining = 0, nNonBus = 0;
				for (int k = 0; k < count; k++)
				{
					int c = childList[cs + k];
					posRem[c] = nRemaining; remaining[cs + nRemaining++] = c;
					if (ChipTypeHelper.IsBusOriginType(chips[c].ChipType)) posNonBus[c] = -1;
					else { posNonBus[c] = nNonBus; nonBus[cs + nNonBus++] = c; }
					if (indeg[c] == 0) ready.Enqueue(c);
				}
				while (nRemaining > 0)
				{
					int c;
					if (ready.Count > 0)
					{
						c = ready.Dequeue();
						if (posRem[c] < 0) continue; // already run as a forced pick
					}
					else
					{
						// feedback: one unready chip at random runs on last step's inputs — buses last, since
						// a bus must know all its inputs to display correctly
						hasCuts = true;
						c = nNonBus > 0 ? nonBus[cs + Simulator.RandomIndex(nNonBus)] : remaining[cs + Simulator.RandomIndex(nRemaining)];
					}
					// remove from both pick lists
					{
						int p = posRem[c], last = remaining[cs + nRemaining - 1];
						remaining[cs + p] = last; posRem[last] = p; nRemaining--; posRem[c] = -1;
						p = posNonBus[c];
						if (p >= 0) { last = nonBus[cs + nNonBus - 1]; nonBus[cs + p] = last; posNonBus[last] = p; nNonBus--; posNonBus[c] = -1; }
					}
					for (int m = mergeBeforeStart[c]; m < mergeBeforeStart[c + 1]; m++) orderBuf[nOrdered++] = mergeBeforeList[m];
					if (gateOfChip[c] >= 0) orderBuf[nOrdered++] = gateOfChip[c];
					else if (childStart[c + 1] > childStart[c] || mergeAfterStart[c + 1] > mergeAfterStart[c]) Emit(c, depth + 1);
					for (int e = edgeStart[c]; e < edgeStart[c + 1]; e++)
					{
						int t = edgeList[e];
						if (posRem[t] >= 0 && --indeg[t] == 0) ready.Enqueue(t);
					}
				}
			}
			for (int m = mergeAfterStart[chip]; m < mergeAfterStart[chip + 1]; m++) orderBuf[nOrdered++] = mergeAfterList[m];
		}

		// Every 100 steps: new random picks inside feedback loops (race conditions vary, as before)
		public void Reschedule()
		{
			if (hasCuts) Schedule();
		}

		// ------------------------------------------------------------------ step

		public void Step(SimAudio audio)
		{
			uint[] st = states;
			Gate[] gs = gates;
			int[] ins = inSlots, outs = outSlots;
			int forcedClock = Simulator.forcedClockState;
			bool clockHigh = forcedClock >= 0
				? forcedClock == 1
				: Simulator.stepsPerClockTransition != 0 && ((Simulator.simulationFrame / Simulator.stepsPerClockTransition) & 1) == 0;

			for (int k = 0; k < gs.Length; k++)
			{
				ref Gate g = ref gs[k];
				switch (g.type)
				{
					case (int)ChipType.Nand:
						st[g.out0] = (1 ^ (st[g.in0] & st[g.in1])) & 1;
						break;

					case MergeType:
						Merge(ref g, st, ins);
						break;

					case (int)ChipType.TriStateBuffer:
						// disabled: floats (noise). If something else drives its net, the merge node of that net
						// writes the net's value back into this slot right after (a floating output reads its net).
						if ((st[g.in1] & 1) == PinState.LogicHigh) st[g.out0] = st[g.in0];
						else st[g.out0] = Simulator.RandomBits16() | 0xFFFF0000u;
						break;

					case (int)ChipType.Clock:
						st[g.out0] = clockHigh ? PinState.LogicHigh : PinState.LogicLow;
						break;

					case (int)ChipType.Vcc: st[g.out0] = PinState.LogicHigh; break;
					case (int)ChipType.Gnd: st[g.out0] = PinState.LogicLow; break;

					case (int)ChipType.Split_4To1Bit:
					{
						uint v = st[g.in0];
						st[outs[g.outStart]] = (v >> 3) & PinState.SingleBitMask;
						st[outs[g.outStart + 1]] = (v >> 2) & PinState.SingleBitMask;
						st[outs[g.outStart + 2]] = (v >> 1) & PinState.SingleBitMask;
						st[outs[g.outStart + 3]] = v & PinState.SingleBitMask;
						break;
					}
					case (int)ChipType.Merge_1To4Bit:
					{
						int i = g.inStart;
						st[g.out0] = (st[ins[i + 3]] & PinState.SingleBitMask) | (st[ins[i + 2]] & PinState.SingleBitMask) << 1 | (st[ins[i + 1]] & PinState.SingleBitMask) << 2 | (st[ins[i]] & PinState.SingleBitMask) << 3;
						break;
					}
					case (int)ChipType.Merge_1To8Bit:
					{
						int i = g.inStart;
						uint r = 0;
						for (int b = 0; b < 8; b++) r |= (st[ins[i + 7 - b]] & PinState.SingleBitMask) << b;
						st[g.out0] = r;
						break;
					}
					case (int)ChipType.Merge_4To8Bit:
					{
						uint r = 0;
						PinState.Set8BitFrom4BitSources(ref r, st[g.in1], st[g.in0]);
						st[g.out0] = r;
						break;
					}
					case (int)ChipType.Split_8To4Bit:
					{
						uint v = st[g.in0], a = 0, b = 0;
						PinState.Set4BitFrom8BitSource(ref a, v, false);
						PinState.Set4BitFrom8BitSource(ref b, v, true);
						st[outs[g.outStart]] = a;
						st[outs[g.outStart + 1]] = b;
						break;
					}
					case (int)ChipType.Split_8To1Bit:
					{
						uint v = st[g.in0];
						for (int b = 0; b < 8; b++) st[outs[g.outStart + b]] = (v >> (7 - b)) & PinState.SingleBitMask;
						break;
					}

					case (int)ChipType.Key:
						st[g.out0] = SimKeyboardHelper.KeyIsHeld((char)internalState[g.canon][0]) ? PinState.LogicHigh : PinState.LogicLow;
						break;

					case (int)ChipType.Pulse:
					{
						uint[] mem = internalState[g.canon];
						uint input = st[g.in0];
						bool inputHigh = PinState.FirstBitHigh(input);
						uint remaining = mem[1];
						if (remaining == 0 && inputHigh && mem[2] == 0) { remaining = mem[0]; mem[1] = remaining; }
						uint o = PinState.LogicLow;
						if (remaining > 0) { mem[1]--; o = PinState.LogicHigh; }
						else if (PinState.GetTristateFlags(input) != 0) o = Simulator.RandomBits16() | 0xFFFF0000u;
						st[g.out0] = o;
						mem[2] = inputHigh ? 1u : 0;
						break;
					}

					case (int)ChipType.DisplayRGB:
					{
						uint[] mem = internalState[g.canon];
						int i = g.inStart;
						uint address = st[ins[i]], red = st[ins[i + 1]], green = st[ins[i + 2]], blue = st[ins[i + 3]];
						uint reset = st[ins[i + 4]], write = st[ins[i + 5]], refresh = st[ins[i + 6]], clock = st[ins[i + 7]];
						bool high = PinState.FirstBitHigh(clock);
						bool rising = high && mem[^1] == 0;
						mem[^1] = high ? 1u : 0;
						if (rising)
						{
							if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a + 256] = 0;
							else if (PinState.FirstBitHigh(write))
								mem[PinState.GetBitStates(address) + 256] = (uint)(PinState.GetBitStates(red) | (PinState.GetBitStates(green) << 4) | (PinState.GetBitStates(blue) << 8));
							if (PinState.FirstBitHigh(refresh)) for (int a = 0; a < 256; a++) mem[a] = mem[a + 256];
						}
						uint col = mem[PinState.GetBitStates(address)];
						st[outs[g.outStart]] = (col >> 0) & 0b1111;
						st[outs[g.outStart + 1]] = (col >> 4) & 0b1111;
						st[outs[g.outStart + 2]] = (col >> 8) & 0b1111;
						break;
					}

					case (int)ChipType.DisplayDot:
					{
						uint[] mem = internalState[g.canon];
						int i = g.inStart;
						uint address = st[ins[i]], pixel = st[ins[i + 1]], reset = st[ins[i + 2]], write = st[ins[i + 3]], refresh = st[ins[i + 4]], clock = st[ins[i + 5]];
						bool high = PinState.FirstBitHigh(clock);
						bool rising = high && mem[^1] == 0;
						mem[^1] = high ? 1u : 0;
						if (rising)
						{
							if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a + 256] = 0;
							else if (PinState.FirstBitHigh(write)) mem[PinState.GetBitStates(address) + 256] = PinState.GetBitStates(pixel);
							if (PinState.FirstBitHigh(refresh)) for (int a = 0; a < 256; a++) mem[a] = mem[a + 256];
						}
						st[g.out0] = (ushort)mem[PinState.GetBitStates(address)];
						break;
					}

					case (int)ChipType.dev_Ram_8Bit:
					{
						uint[] mem = internalState[g.canon];
						int i = g.inStart;
						uint address = st[ins[i]], data = st[ins[i + 1]], we = st[ins[i + 2]], reset = st[ins[i + 3]], clock = st[ins[i + 4]];
						bool high = PinState.FirstBitHigh(clock);
						bool rising = high && mem[^1] == 0;
						mem[^1] = high ? 1u : 0;
						if (rising)
						{
							if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a] = 0;
							else if (PinState.FirstBitHigh(we)) mem[PinState.GetBitStates(address)] = PinState.GetBitStates(data);
						}
						st[g.out0] = (ushort)mem[PinState.GetBitStates(address)];
						break;
					}

					case (int)ChipType.Rom_256x16:
					{
						uint data = internalState[g.canon][PinState.GetBitStates(st[g.in0])];
						st[outs[g.outStart]] = (data >> 8) & 0xFF;
						st[outs[g.outStart + 1]] = data & 0xFF;
						break;
					}

					case (int)ChipType.Buzzer:
						audio.RegisterNote(PinState.GetBitStates(st[g.in0]), PinState.GetBitStates(st[g.in1]));
						break;
				}
			}
		}

		// Several sources on one pin, merged PER BIT: a driven source always beats a floating one, driven
		// sources in conflict are resolved at random for the step, all-floating stays floating (carrying
		// the last source's noise). Floating sources on a driven bit then take the net's value (a disabled
		// 3-state buffer's output pin, and the wire from it, read the voltage of the line they sit on).
		void Merge(ref Gate g, uint[] st, int[] ins)
		{
			int start = g.inStart, count = g.inCount;
			uint drivenAny = 0, orBits = 0, andBits = 0xFFFF, lastBits = 0;
			for (int k = 0; k < count; k++)
			{
				uint s = st[ins[start + k]];
				uint bits = s & 0xFFFF, tri = s >> 16, drv = ~tri & 0xFFFF;
				orBits |= bits & drv;
				andBits &= bits | tri;
				drivenAny |= drv;
				lastBits = bits;
			}
			uint conflict = (orBits ^ andBits) & drivenAny;
			uint chosen = conflict != 0 && Simulator.RandomBool() ? orBits : andBits;
			uint resTri = ~drivenAny & 0xFFFF;
			uint resBits = (chosen & drivenAny) | (lastBits & resTri);
			st[g.out0] = resBits | (resTri << 16);

			if (drivenAny != 0)
			{
				SimPin winner = null;
				SimPin[] srcs = mergeSources[g.canon];
				for (int k = 0; k < count; k++)
				{
					int slot = ins[start + k];
					uint s = st[slot];
					uint tri = s >> 16;
					uint take = tri & drivenAny;
					if (take != 0) st[slot] = ((s & 0xFFFF & ~take) | (resBits & take)) | (tri << 16);
					else if (winner == null && (~tri & 0xFFFF) != 0) winner = srcs[k];
				}
				if (winner != null)
				{
					SimPin t = mergeTarget[g.canon];
					t.latestSourceID = winner.ID;
					t.latestSourceParentChipID = winner.parentChip.ID;
				}
			}
		}
	}
}
