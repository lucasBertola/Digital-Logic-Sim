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
	//     pin owns a slot, an unconnected pin gets a slot holding "floating, low" forever. Slots are numbered
	//     in the order the step first touches them, so a step streams through memory;
	//   * every builtin chip becomes a GATE reading and writing slots directly (hot fields in struct-of-arrays
	//     form, in schedule order); custom chips vanish from the step, but not from the ORDER: gates are
	//     emitted by walking the tree exactly as the original stepper did — at every level the sub-chips run
	//     in the order they become ready (all their inputs produced by already-run siblings), and when none is
	//     ready (a feedback loop) one of the unready non-bus chips is picked at random and runs on last step's
	//     values. So the timing of latches, registers and buses is the same as before, and the random picks
	//     are re-drawn every 100 steps so race conditions vary;
	//   * a step only runs the gates whose inputs changed (a dirty bit per gate, set by whoever writes one of
	//     its input slots to a different value, cleared when it runs — in schedule order, so a change made by
	//     an earlier gate is seen in the same step, and one made by a later gate — feedback — on the next).
	//     Gates with a life of their own (clock, key, buzzer, a merge with a conflict to re-draw, a pulse
	//     counting down) re-arm themselves; a floating output (disabled 3-state buffer, floating pulse) joins
	//     the noise list and is re-drawn now and then (NoisePeriod).
	// SimPin.State reads through to the slot, so everything that looks at pins (UI sync, harness probes,
	// tests) sees the live values without any write-back.
	public sealed class SimProgram
	{
		const byte MergeType = 255;
		// A NAND whose only consumer is an inverter (NAND with both inputs on it) runs the inverter in the same
		// go: one gate run and one marking instead of two — the user's AND / OR / NOR chips all end that way.
		// Exact: the inverter always ran right after, in the same step. The inverter gate becomes a no-op.
		const byte NandNotType = 254, NopType = 253;
		static int compileStampCounter;

		public uint[] states = Array.Empty<uint>();
		int slotCount;

		// ---- gates, canonical order (cold fields) ----
		int gateCount;
		byte[] cType = Array.Empty<byte>();
		int[] cInStart = Array.Empty<int>(), cInCount = Array.Empty<int>(), cOutStart = Array.Empty<int>(), cOutCount = Array.Empty<int>();
		int[] inSlots = Array.Empty<int>(), outSlots = Array.Empty<int>();
		uint[][] internalState = Array.Empty<uint[]>();
		SimChip[] chipOfGate = Array.Empty<SimChip>();
		SimPin[] mergeTarget = Array.Empty<SimPin>();
		SimPin[][] mergeSources = Array.Empty<SimPin[]>();
		int[] romGates = Array.Empty<int>();
		int[] clockGates = Array.Empty<int>(), keyGates = Array.Empty<int>();
		int lastClockLevel = -1;     // -1: unknown, the clock gates must run
		int lastKeyVersion = -1;
		public bool HasBuzzer;       // a buzzer registers its note every step: such a circuit never idles
		// input pins of the root, as the caller passed them last time (their SimPins are looked up once)
		public object InputPinsArray; public SimPin[] InputSimPins = Array.Empty<SimPin>();

		// ---- gates, schedule order (hot fields) ----
		byte[] sType = Array.Empty<byte>();
		int[] sIn0 = Array.Empty<int>(), sIn1 = Array.Empty<int>(), sOut0 = Array.Empty<int>(), sCanon = Array.Empty<int>();
		int[] sOut1 = Array.Empty<int>();       // NandNot: the fused inverter's output slot
		int[] cOut1 = Array.Empty<int>();       // per canonical gate, -1 unless NandNot
		int[] posOfCanon = Array.Empty<int>();
		ulong[] dirty = Array.Empty<ulong>();
		ulong[] dirtyTop = Array.Empty<ulong>(); // bit w set when dirty[w] may be non-zero (cleared lazily by the step loop)

		// ---- who reads a slot (canonical gate ids), to mark on change ----
		int[] slotConsStart = Array.Empty<int>(), slotConsList = Array.Empty<int>();
		int[] slotConsPos = Array.Empty<int>(); // the same consumers as schedule positions (hot path), kept in sync with posOfCanon
		// NAND wake-up filter: per consumer entry, the OTHER input slot of a NAND consumer and its output slot (-1 otherwise)
		int[] slotConsOther = Array.Empty<int>(), slotConsOut = Array.Empty<int>();

		// ---- root inputs: written from outside the step, change-detected against a shadow copy ----
		// ---- noise ----
		// A floating (high-impedance) line carries no value: its slot holds tri flags and bits 0, and costs
		// nothing while nobody reads it (a RAM made of 2 048 disabled buffers is idle). Noise appears where
		// LOGIC reads a floating bit: the reading gate substitutes a random bit and joins the noise list, from
		// which it is re-run with probability 1/NoisePeriod per step (a few times a second — the display
		// only shows 60 frames a second, and draws floating pins as flicker itself). So whatever a floating
		// line feeds sees unpredictable values, as before, and an idle floating bus is free.
		// (user rule, 2026-09-27: "it may change randomly about once a second" — at a few thousand steps per
		// second, 1/1024 per step is a few times a second)
		public const int NoisePeriod = 1024;
		int[] noiseList = Array.Empty<int>(), noisePos = Array.Empty<int>(); // canonical gate ids (survive a reschedule)
		byte[] armedAt = Array.Empty<byte>(); // by schedule position: 1 when the gate is in the noise list (hot-path check)
		int noiseAccumulator;
		// slots nobody ever drives (unconnected pins): floating, but read as a quiet 0, never as noise
		bool[] quiet = Array.Empty<bool>();
		int noiseCount;

		int[] rootInputSlots = Array.Empty<int>();
		uint[] rootShadow = Array.Empty<uint>();

		// ---- the tree, for scheduling (indices into `chips`, pre-order) ----
		SimChip[] chips = Array.Empty<SimChip>();
		int[] childStart = Array.Empty<int>(), childList = Array.Empty<int>();   // children of a custom chip
		int[] edgeStart = Array.Empty<int>(), edgeList = Array.Empty<int>();     // sibling consumers of a chip (one entry per wire)
		int[] indegree0 = Array.Empty<int>();                                    // wires from siblings into a chip
		int[] gateOfChip = Array.Empty<int>();                                   // canonical gate of a builtin chip, or -1
		int[] mergeBeforeStart = Array.Empty<int>(), mergeBeforeList = Array.Empty<int>(); // merge gates on a chip's input pins
		int[] mergeAfterStart = Array.Empty<int>(), mergeAfterList = Array.Empty<int>();   // merge gates on a chip's output pins
		bool hasCuts;
		// per custom chip: the segment of the schedule its subtree occupies (fixed length), and whether a random
		// pick happens at its own level — only those chips are worth re-drawing
		int[] segStart = Array.Empty<int>(), segLen = Array.Empty<int>();
		bool[] levelHasCuts = Array.Empty<bool>();
		readonly List<int> cutChips = new();
		const int RedrawChipsPerReschedule = 16;
		const int MaxRedrawSegment = 2048; // gates

		public int GateCount => gateCount;
		public int GatesRunLastStep; // diagnostic: how many gates the last step actually ran
		public int NoiseCount => noiseCount;
		// diagnostic: the chips whose gate currently reads a floating line (and its parent chain)
		// diagnostic: the gates the LAST step ran (canonical ids), with their first output value
		public readonly List<int> LastStepGates = new();
		public bool TraceGates;
		public (SimChip chip, byte type, uint output) GateInfo(int g) => (chipOfGate[g], cType[g], cOutCount[g] > 0 ? states[outSlots[cOutStart[g]]] : 0);

		// diagnostic: duplicate gates (same type and input slots) and the runs they cost
		public (int gates, int duplicates, long duplicateRuns, long totalRuns) DuplicateGates()
		{
			var seen = new Dictionary<(byte, int, int), int>();
			int dup = 0; long dupRuns = 0, total = 0;
			for (int g = 0; g < gateCount; g++)
			{
				long runs = RunsByGate.Length == gateCount ? RunsByGate[g] : 0;
				total += runs;
				if (cType[g] != (byte)ChipType.Nand) continue;
				int a = inSlots[cInStart[g]], b = inSlots[cInStart[g] + 1];
				var key = (cType[g], Math.Min(a, b), Math.Max(a, b));
				if (seen.ContainsKey(key)) { dup++; dupRuns += runs; }
				else seen[key] = g;
			}
			return (gateCount, dup, dupRuns, total);
		}

		public IEnumerable<(SimChip chip, long runs)> GateRuns()
		{
			for (int g = 0; g < RunsByGate.Length; g++) if (RunsByGate[g] > 0) yield return (chipOfGate[g], RunsByGate[g]);
		}

		public IEnumerable<SimChip> ArmedGateChips()
		{
			for (int j = 0; j < noiseCount; j++) yield return chipOfGate[noiseList[j]];
		}
		public bool CollectStats;                       // diagnostic: count runs per gate type
		public readonly long[] RunsByType = new long[256];
		public long[] RunsByGate = Array.Empty<long>(); // diagnostic, by canonical gate (when CollectStats)
		public double RescheduleMs;                     // diagnostic: time spent re-drawing the schedule
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

			// snapshot every pin's current value BEFORE anything is rebound (the state array may be reused)
			uint[] old = new uint[n];
			for (int i = 0; i < n; i++) old[i] = pins[i].State;

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
				else { isDriver[i] = true; isConstant[i] = true; }     // unconnected: a quiet driven 0, forever
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

			// ---- gates (canonical order) ----
			var types = new List<byte>(nc);
			var inStart = new List<int>(nc); var inCount = new List<int>(nc);
			var outStart = new List<int>(nc); var outCount = new List<int>(nc);
			var inList = new List<int>(nc * 3);
			var outList = new List<int>(nc);
			var stateList = new List<uint[]>(nc);
			var chipList2 = new List<SimChip>(nc);
			var mTarget = new List<SimPin>();
			var mSources = new List<SimPin[]>();
			var roms = new List<int>();
			var clocks = new List<int>();
			var keys = new List<int>();
			int[] gateOfChip = new int[nc];
			var mergeBefore = new List<int>[nc];
			var mergeAfter = new List<int>[nc];
			foreach (SimChip b in chips)
			{
				gateOfChip[b.compileIndex] = -1;
				if (!b.IsBuiltin) continue;
				if (ChipTypeHelper.IsBusType(b.ChipType)) continue; // origin = wire, terminus = nothing
				if (b.OutputPins.Length == 0 && b.ChipType != ChipType.Buzzer) continue; // pure displays: their input pins already carry the values
				gateOfChip[b.compileIndex] = types.Count;
				if (b.ChipType is ChipType.Rom_256x16 or ChipType.dev_Ram_8Bit or ChipType.Clock) roms.Add(types.Count); // edited from outside: re-run (a clock: stopped / manual level)
				if (b.ChipType == ChipType.Clock) clocks.Add(types.Count);
				if (b.ChipType == ChipType.Key) keys.Add(types.Count);
				if (b.ChipType == ChipType.Buzzer) prog.HasBuzzer = true;
				types.Add((byte)b.ChipType);
				inStart.Add(inList.Count); inCount.Add(b.InputPins.Length);
				outStart.Add(outList.Count); outCount.Add(b.OutputPins.Length);
				foreach (SimPin p in b.InputPins) inList.Add(slotOf[p.compileIndex]);
				foreach (SimPin p in b.OutputPins) outList.Add(slotOf[p.compileIndex]);
				stateList.Add(b.InternalState); chipList2.Add(b); mTarget.Add(null); mSources.Add(null);
			}
			for (int i = 0; i < n; i++)
			{
				if (nSrc[i] < 2 || !isDriver[i] || isConstant[i]) continue;
				SimPin target = pins[i];
				SimChip owner = target.parentChip;
				if (owner.IsBuiltin && !target.isInput) continue; // (a builtin output with sources: ignore them)
				int g = types.Count;
				types.Add(MergeType);
				inStart.Add(inList.Count); inCount.Add(nSrc[i]);
				outStart.Add(outList.Count); outCount.Add(1);
				var srcs = new SimPin[nSrc[i]];
				for (int k = 0; k < nSrc[i]; k++) { int s = srcList[srcStart[i] + k]; inList.Add(slotOf[s]); srcs[k] = pins[s]; }
				outList.Add(slotOf[i]);
				stateList.Add(null); chipList2.Add(owner); mTarget.Add(target); mSources.Add(srcs);
				// the merged value must be complete when its owner runs: right before it (input pin) or at its end (output pin)
				ref List<int> bucket = ref (target.isInput ? ref mergeBefore[owner.compileIndex] : ref mergeAfter[owner.compileIndex]);
				(bucket ??= new List<int>()).Add(g);
			}
			int gc = types.Count;
			prog.gateCount = gc;
			prog.cType = types.ToArray();
			prog.cInStart = inStart.ToArray(); prog.cInCount = inCount.ToArray();
			prog.cOutStart = outStart.ToArray(); prog.cOutCount = outCount.ToArray();
			prog.inSlots = inList.ToArray();
			prog.outSlots = outList.ToArray();
			prog.internalState = stateList.ToArray();
			prog.chipOfGate = chipList2.ToArray();
			prog.mergeTarget = mTarget.ToArray();
			prog.mergeSources = mSources.ToArray();
			prog.romGates = roms.ToArray();
			prog.clockGates = clocks.ToArray();
			prog.keyGates = keys.ToArray();
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

			// ---- first schedule, then number the slots in the order the step touches them (memory locality) ----
			prog.Schedule();
			int[] renum = new int[nextSlot];
			for (int s = 0; s < nextSlot; s++) renum[s] = -1;
			int next = 0;
			for (int k = 0; k < gc; k++)
			{
				int g = prog.sCanon[k];
				for (int j = 0; j < prog.cInCount[g]; j++) { int s = prog.inSlots[prog.cInStart[g] + j]; if (renum[s] < 0) renum[s] = next++; }
				for (int j = 0; j < prog.cOutCount[g]; j++) { int s = prog.outSlots[prog.cOutStart[g] + j]; if (renum[s] < 0) renum[s] = next++; }
			}
			for (int s = 0; s < nextSlot; s++) if (renum[s] < 0) renum[s] = next++;
			for (int j = 0; j < prog.inSlots.Length; j++) prog.inSlots[j] = renum[prog.inSlots[j]];
			for (int j = 0; j < prog.outSlots.Length; j++) prog.outSlots[j] = renum[prog.outSlots[j]];
			for (int i = 0; i < n; i++) slotOf[i] = renum[slotOf[i]];
			prog.slotCount = nextSlot;
			prog.CopySchedule(0, gc); // hot arrays again, with the final slot numbers

			// ---- state slots ----
			uint[] states = previous != null && previous.states.Length >= nextSlot ? previous.states : new uint[nextSlot + nextSlot / 4 + 64];
			for (int i = 0; i < n; i++)
				// an undriven pin holds a DRIVEN 0: "floating low" was read as 0 by logic, but a merge / split / enabled
				// buffer copied its floating flag along (the user's ALU4: an unconnected C_in made a 4-bit OUT flicker)
				if (isDriver[i]) states[slotOf[i]] = isConstant[i] ? PinState.LogicLow : old[i];
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
			prog.rootInputSlots = new int[root.InputPins.Length];
			prog.rootShadow = new uint[root.InputPins.Length];
			for (int i = 0; i < root.InputPins.Length; i++) { int s = slotOf[root.InputPins[i].compileIndex]; prog.rootInputSlots[i] = s; prog.rootShadow[i] = states[s]; }

			// ---- consumers of every slot (canonical gate ids) ----
			prog.BuildConsumerLists();
			prog.quiet = new bool[nextSlot];
			for (int i = 0; i < n; i++) if (isConstant[i]) prog.quiet[slotOf[i]] = true;
			// (root inputs are NOT quiet: they are driven by the player; a floating output computed from them — a buffer
			// with its enable at 0 — must read as noise. The bench drives every input like the app does.)
			// a stateless gate fed only by never-driven lines produces a constant too (a SPLIT of an unconnected
			// 8-bit input, the RAM built on it...): its outputs are quiet as well, so nothing downstream reads noise
			for (int k = 0; k < gc; k++)
			{
				int g = prog.sCanon[k];
				byte t = prog.cType[g];
				bool stateless = t == MergeType || t == (byte)ChipType.Nand || t == NandNotType || t == (byte)ChipType.TriStateBuffer
					|| t == (byte)ChipType.Split_4To1Bit || t == (byte)ChipType.Split_8To1Bit || t == (byte)ChipType.Split_8To4Bit
					|| t == (byte)ChipType.Merge_1To4Bit || t == (byte)ChipType.Merge_1To8Bit || t == (byte)ChipType.Merge_4To8Bit;
				if (!stateless || prog.cInCount[g] == 0) continue;
				bool allQuiet = true;
				for (int j = 0; j < prog.cInCount[g] && allQuiet; j++) allQuiet = prog.quiet[prog.inSlots[prog.cInStart[g] + j]];
				if (allQuiet) for (int j = 0; j < prog.cOutCount[g]; j++) prog.quiet[prog.outSlots[prog.cOutStart[g] + j]] = true;
			}

			foreach (SimPin p in pins) p.quietArray = prog.quiet; // display: a never-driven pin shows 0, not flicker
			prog.FuseInverters();
			prog.EliminateCommonGates(pins);
			prog.BuildConsumerLists();

			prog.dirty = new ulong[(gc + 63) / 64];
			prog.dirtyTop = new ulong[(prog.dirty.Length + 63) / 64];
			prog.noiseList = new int[gc];
			prog.noisePos = new int[gc];
			prog.armedAt = new byte[gc];
			prog.ClearNoise();
			prog.MarkAllDirty();
			return prog;
		}

		// NAND + sole inverter consumer -> one NandNot gate (see NandNotType). The inverter must be scheduled after
		// the NAND, and stay so across reschedules: every level from their common ancestor down to each of them
		// must be free of random picks (a cut-free level always emits its children in the same order).
		void FuseInverters()
		{
			int gc = gateCount;
			cOut1 = new int[gc];
			for (int g = 0; g < gc; g++) cOut1[g] = -1;
			var ancestors = new HashSet<SimChip>();
			int fused = 0;
			for (int g = 0; g < gc; g++)
			{
				if (cType[g] != (byte)ChipType.Nand) continue;
				int s = outSlots[cOutStart[g]];
				// the slot's consumer entries: one per input pin, so the inverter shows up twice
				int cs = slotConsStart[s], n = slotConsStart[s + 1] - cs;
				if (n < 1 || n > 2) continue;
				int c = slotConsList[cs];
				if (n == 2 && slotConsList[cs + 1] != c) continue;
				if (c == g || cType[c] != (byte)ChipType.Nand) continue;
				if (inSlots[cInStart[c]] != s || inSlots[cInStart[c] + 1] != s) continue;
				if (posOfCanon[c] <= posOfCanon[g]) continue;
				// order stability
				SimChip x = chipOfGate[g], y = chipOfGate[c];
				ancestors.Clear();
				for (SimChip a = x.compileParent; a != null; a = a.compileParent) ancestors.Add(a);
				SimChip common = null;
				for (SimChip a = y.compileParent; a != null; a = a.compileParent) if (ancestors.Contains(a)) { common = a; break; }
				if (common == null) continue;
				bool stable = true;
				for (SimChip a = x.compileParent; stable && a != null; a = a.compileParent) { if (levelHasCuts[a.compileIndex]) stable = false; if (a == common) break; }
				for (SimChip a = y.compileParent; stable && a != null; a = a.compileParent) { if (levelHasCuts[a.compileIndex]) stable = false; if (a == common) break; }
				if (!stable) continue;
				cType[g] = NandNotType;
				cOut1[g] = outSlots[cOutStart[c]];
				cType[c] = NopType;
				fused++;
			}
			FusedInverters = fused;
			CopySchedule(0, gc); // types and sOut1 into the hot arrays
		}
		public int FusedInverters; // diagnostic

		// slot -> the gates reading it (canonical ids and schedule positions), plus the NAND wake-up filter data
		void BuildConsumerLists()
		{
			int gc = gateCount, ns = slotCount;
			int[] nCons = new int[ns];
			for (int g = 0; g < gc; g++)
				if (cType[g] != NopType) for (int j = 0; j < cInCount[g]; j++) nCons[inSlots[cInStart[g] + j]]++;
			int[] consStart = new int[ns + 1];
			for (int s = 0; s < ns; s++) consStart[s + 1] = consStart[s] + nCons[s];
			int[] consList = new int[consStart[ns]];
			int[] cf = new int[ns];
			for (int g = 0; g < gc; g++)
				if (cType[g] != NopType) for (int j = 0; j < cInCount[g]; j++) { int s = inSlots[cInStart[g] + j]; consList[consStart[s] + cf[s]++] = g; }
			slotConsStart = consStart;
			slotConsList = consList;
			slotConsOther = new int[consList.Length]; slotConsOut = new int[consList.Length];
			for (int s = 0; s < ns; s++)
				for (int j = consStart[s]; j < consStart[s + 1]; j++)
				{
					int g = consList[j];
					slotConsOther[j] = -1; slotConsOut[j] = -1;
					if (cType[g] != (byte)ChipType.Nand && cType[g] != NandNotType) continue;
					int a = inSlots[cInStart[g]], b = inSlots[cInStart[g] + 1];
					if (a == b) continue; // an inverter: both inputs are this slot
					slotConsOther[j] = a == s ? b : a;
					slotConsOut[j] = outSlots[cOutStart[g]];
				}
			slotConsPos = new int[consList.Length];
			for (int j = 0; j < consList.Length; j++) slotConsPos[j] = posOfCanon[consList[j]];
		}

		// Common gates: two NANDs (or NAND+inverter pairs) computing the same function of the same input slots
		// compute the same value at all times when their inputs come from outside their common ancestor's subtree
		// (so both read the same value, fresh or one step old, whatever the order) and their chains up to that
		// ancestor are free of random picks (so the first one always runs before the second one's consumers).
		// The second gate is dropped, its consumers read the first, and its pins are re-bound to the first's slots
		// so the display stays exact. The user's 64 RAM4 decoders all compute the same minterms of the same two
		// address bits: only the enable AND differs.
		void EliminateCommonGates(List<SimPin> pins)
		{
			int gc = gateCount, ns = slotCount;
			int[] producer = new int[ns];
			for (int s = 0; s < ns; s++) producer[s] = -1;
			for (int g = 0; g < gc; g++)
			{
				if (cType[g] == NopType) continue;
				for (int j = 0; j < cOutCount[g]; j++) producer[outSlots[cOutStart[g] + j]] = g;
				if (cOut1.Length == gc && cOut1[g] >= 0) producer[cOut1[g]] = g;
			}
			// several representatives per key: a gate that cannot be merged with the first one (their common ancestor
			// level has random picks — the user's register splitters vs the RAM's) may still merge with a later one
			var first = new Dictionary<(byte, int, int), List<int>>();
			var redirect = new Dictionary<int, int>(); // dead slot -> live slot
			var ancestors = new HashSet<SimChip>();
			int merged = 0;
			for (int k = 0; k < gc; k++)
			{
				int g = sCanon[k];
				byte t = cType[g];
				bool twoIn = t == (byte)ChipType.Nand || t == NandNotType;
				bool oneIn = t == (byte)ChipType.Split_8To1Bit || t == (byte)ChipType.Split_4To1Bit || t == (byte)ChipType.Split_8To4Bit; // pure routing: the user's 256 RAM words all split the same bus
				if (!twoIn && !oneIn) continue;
				int a = inSlots[cInStart[g]], b = twoIn ? inSlots[cInStart[g] + 1] : -1;
				if (redirect.TryGetValue(a, out int ra)) a = ra;
				if (twoIn && redirect.TryGetValue(b, out int rb)) b = rb;
				inSlots[cInStart[g]] = a; if (twoIn) inSlots[cInStart[g] + 1] = b; // consumers of dropped gates read the kept one
				var key = (t, Math.Min(a, b), Math.Max(a, b));
				if (!first.TryGetValue(key, out List<int> reps)) { first[key] = new List<int> { g }; continue; }
				int g1 = -1;
				foreach (int cand in reps) if (CanMergeInto(cand, g, a, b, producer, ancestors)) { g1 = cand; break; }
				if (g1 < 0) { reps.Add(g); continue; }
				// ---- drop g in favour of g1 (every output) ----
				for (int j = 0; j < cOutCount[g]; j++) redirect[outSlots[cOutStart[g] + j]] = outSlots[cOutStart[g1] + j];
				if (t == NandNotType) redirect[cOut1[g]] = cOut1[g1];
				MergedByType[t]++;
				cType[g] = NopType;
				if (cOut1.Length == gc) cOut1[g] = -1;
				merged++;
			}
			// (conditions as a function so several representatives can be tried)
			bool CanMergeInto(int g1, int g, int a, int b, int[] producer, HashSet<SimChip> ancestors)
			{
				SimChip x = chipOfGate[g1], y = chipOfGate[g];
				ancestors.Clear();
				for (SimChip c = x.compileParent; c != null; c = c.compileParent) ancestors.Add(c);
				SimChip common = null;
				for (SimChip c = y.compileParent; c != null; c = c.compileParent) if (ancestors.Contains(c)) { common = c; break; }
				if (common == null) return false;
				for (SimChip c = x.compileParent; c != null; c = c.compileParent) { if (levelHasCuts[c.compileIndex]) return false; if (c == common) break; }
				for (SimChip c = y.compileParent; c != null; c = c.compileParent) { if (levelHasCuts[c.compileIndex]) return false; if (c == common) break; }
				foreach (int s in new[] { a, b })
				{
					if (s < 0) continue;
					int p = producer[s];
					if (p < 0) continue; // root input or constant: the same for both
					bool inside = false;
					for (SimChip c = chipOfGate[p]; c != null; c = c.compileParent) if (c == common) { inside = true; break; }
					if (!inside) continue; // produced outside the common subtree: both read the same value, fresh or one step old
					// produced inside it: fine if it always runs before the first gate, i.e. it precedes it now and its
					// chain up to the common ancestor is free of random picks (its order relative to both is then fixed)
					if (posOfCanon[p] >= posOfCanon[g1]) return false;
					for (SimChip c = chipOfGate[p].compileParent; c != null; c = c.compileParent) { if (levelHasCuts[c.compileIndex]) return false; if (c == common) break; }
				}
				return true;
			}
			// remaining readers of dropped slots (gates processed before the drop, other gate types) and the pins
			for (int j = 0; j < inSlots.Length; j++) if (redirect.TryGetValue(inSlots[j], out int live)) inSlots[j] = live;
			foreach (SimPin p in pins) if (redirect.TryGetValue(p.stateIndex, out int live)) p.stateIndex = live;
			CommonGatesMerged = merged;
			CopySchedule(0, gc);
		}
		public int CommonGatesMerged; // diagnostic
		public readonly int[] MergedByType = new int[256];

		static (int[] start, int[] list) Csr(List<int>[] buckets)
		{
			int[] start = new int[buckets.Length + 1];
			for (int i = 0; i < buckets.Length; i++) start[i + 1] = start[i] + (buckets[i]?.Count ?? 0);
			int[] list = new int[start[buckets.Length]];
			for (int i = 0; i < buckets.Length; i++) buckets[i]?.CopyTo(list, start[i]);
			return (start, list);
		}

		void MarkAllDirty()
		{
			for (int w = 0; w < dirty.Length; w++) dirty[w] = ulong.MaxValue;
			int rem = gateCount & 63;
			if (rem != 0 && dirty.Length > 0) dirty[dirty.Length - 1] = (1UL << rem) - 1; // no padding bits
			for (int w = 0; w < dirty.Length; w++) dirtyTop[w >> 6] |= 1UL << (w & 63);
		}

		// ------------------------------------------------------------------ schedule

		// Emits the gates in the order the tree stepper visited the chips: per level, chips as they become
		// ready; when none is (feedback), a random unready chip — a non-bus one while any remains — runs with
		// last step's inputs. A merge node runs right before the chip that owns the pin.
		// scratch, indexed by chip (pos*) or by position within a level (remaining / nonBus, from childStart)
		int[] indeg, orderBuf, remaining, nonBus, posRem, posNonBus;
		readonly List<Queue<int>> queuePool = new();
		int nOrdered;

		void Schedule()
		{
			int nc = chips.Length, gc = gateCount;
			indeg ??= new int[nc];
			orderBuf ??= new int[gc];
			remaining ??= new int[nc];
			nonBus ??= new int[nc];
			posRem ??= new int[nc];
			posNonBus ??= new int[nc];
			segStart = new int[nc]; segLen = new int[nc]; levelHasCuts = new bool[nc];
			nOrdered = 0;
			hasCuts = false;
			Emit(0, 0);
			cutChips.Clear();
			for (int c = 0; c < nc; c++) if (levelHasCuts[c]) cutChips.Add(c);
			CopySchedule(0, gc);
		}

		void CopySchedule(int from, int count)
		{
			int gc = gateCount;
			if (sType.Length != gc)
			{
				sType = new byte[gc]; sIn0 = new int[gc]; sIn1 = new int[gc]; sOut0 = new int[gc]; sOut1 = new int[gc]; sCanon = new int[gc]; posOfCanon = new int[gc];
			}
			for (int k = from; k < from + count; k++)
			{
				int g = orderBuf[k];
				sCanon[k] = g; posOfCanon[g] = k;
				sType[k] = cType[g];
				sIn0[k] = cInCount[g] > 0 ? inSlots[cInStart[g]] : 0;
				sIn1[k] = cInCount[g] > 1 ? inSlots[cInStart[g] + 1] : 0;
				sOut0[k] = cOutCount[g] > 0 ? outSlots[cOutStart[g]] : 0;
				sOut1[k] = cOut1.Length == gateCount ? cOut1[g] : -1;
			}
		}

		void Emit(int chip, int depth)
		{
			int cs = childStart[chip], count = childStart[chip + 1] - cs;
			segStart[chip] = nOrdered;
			bool cutsHere = false;
			if (count > 0)
			{
				for (int k = 0; k < count; k++) { int c = childList[cs + k]; indeg[c] = indegree0[c]; }
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
						hasCuts = true; cutsHere = true;
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
			segLen[chip] = nOrdered - segStart[chip];
			levelHasCuts[chip] = cutsHere;
		}

		// Every 100 steps: new random picks inside some feedback loops (race conditions vary, as before).
		// A few chips whose level has a random pick are re-emitted IN PLACE (a subtree's segment has a fixed
		// length), so a CPU full of latches costs a few tiny re-emissions, not a walk of the whole tree.
		readonly List<int> pendingCanon = new();
		public void Reschedule()
		{
			if (!hasCuts || cutChips.Count == 0) return;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			int n = Math.Min(RedrawChipsPerReschedule, cutChips.Count);
			for (int i = 0; i < n; i++)
			{
				int chip = cutChips[Simulator.RandomIndex(cutChips.Count)];
				int start = segStart[chip], len = segLen[chip];
				// a level whose subtree is the whole CPU (the bus loop) costs milliseconds to re-emit and is not a
				// latch race: the small levels (latches, registers) are the ones worth re-drawing
				if (len > MaxRedrawSegment) continue;
				// the pending dirty gates of the segment keep pending at their new positions (noise list is by canonical id)
				pendingCanon.Clear();
				for (int k = start; k < start + len; k++)
				{
					int w = k >> 6; ulong bit = 1UL << (k & 63);
					if ((dirty[w] & bit) != 0) { pendingCanon.Add(sCanon[k]); dirty[w] &= ~bit; }
				}
				nOrdered = start;
				Emit(chip, 0);
				CopySchedule(start, len);
				// every consumer entry pointing at a gate of the segment must carry its new position: those entries
				// live in the lists of the slots these gates read
				for (int k = start; k < start + len; k++)
				{
					int g = sCanon[k];
					for (int j = 0; j < cInCount[g]; j++)
					{
						int s = inSlots[cInStart[g] + j];
						for (int e = slotConsStart[s]; e < slotConsStart[s + 1]; e++) slotConsPos[e] = posOfCanon[slotConsList[e]];
					}
				}
				for (int w = start >> 6; w <= (start + len - 1) >> 6 && w < dirty.Length; w++)
					if (dirty[w] == 0) dirtyTop[w >> 6] &= ~(1UL << (w & 63));
				foreach (int c in pendingCanon) SetDirty(posOfCanon[c]);
				for (int k = start; k < start + len; k++) armedAt[k] = (byte)(noisePos[sCanon[k]] >= 0 ? 1 : 0);
			}
			RescheduleMs += sw.Elapsed.TotalMilliseconds;
		}

		// ------------------------------------------------------------------ step

		// index of the lowest set bit (no BitOperations under this runtime)
		static readonly int[] lowestBitTable = BuildLowestBitTable();
		const ulong DeBruijn = 0x03f79d71b4ca8b09UL;

		static int[] BuildLowestBitTable()
		{
			var t = new int[64];
			for (int i = 0; i < 64; i++) t[(int)(((1UL << i) * DeBruijn) >> 58)] = i;
			return t;
		}

		// real steps run by this program, and "nothing is pending": the display waits for one of them after a
		// rebuild (the first steps are the circuit settling from its restored state, not values worth showing)
		public long StepsRun;
		public bool NothingPending
		{
			get
			{
				foreach (ulong w in dirty) if (w != 0) return false;
				return true;
			}
		}
		public bool SettledAfterBuild => StepsRun >= 64 || (StepsRun > 0 && NothingPending);

		public void Step(SimAudio audio) => Step(audio, Simulator.simulationFrame, Simulator.stepsPerClockTransition, Simulator.forcedClockState);

		// (frame, period, forced clock passed in: the batched loop keeps them in locals, a thread-static read costs)
		public unsafe void Step(SimAudio audio, int frame, int period, int forcedClock)
		{
			StepsRun++;
			int gc = gateCount;
			if (gc == 0) return;
			uint[] states = this.states;

			// external edits of an internal state (ROM contents): re-run that gate
			foreach (int g in romGates)
			{
				SimChip c = chipOfGate[g];
				if (c.InternalStateEdited) { c.InternalStateEdited = false; SetDirty(posOfCanon[g]); }
			}
			// root inputs were written from outside the step: mark what changed
			for (int i = 0; i < rootInputSlots.Length; i++)
			{
				int s = rootInputSlots[i];
				uint v = states[s];
				if (v != rootShadow[i]) { rootShadow[i] = v; MarkConsumers(s); }
			}

			// noise readers: on average noiseCount / NoisePeriod of them re-run this step, picked at random
			noiseAccumulator += noiseCount;
			while (noiseAccumulator >= NoisePeriod && noiseCount > 0)
			{
				noiseAccumulator -= NoisePeriod;
				SetDirty(posOfCanon[noiseList[Simulator.RandomIndex(noiseCount)]]);
			}

			bool clockHigh = forcedClock >= 0
				? forcedClock == 1
				: period != 0 && ((frame / period) & 1) == 0;
			int clockLevel = clockHigh ? 1 : 0;
			if (clockLevel != lastClockLevel) { lastClockLevel = clockLevel; foreach (int g in clockGates) SetDirty(posOfCanon[g]); }
			int keyVersion = SimKeyboardHelper.Version;
			if (keyVersion != lastKeyVersion) { lastKeyVersion = keyVersion; foreach (int g in keyGates) SetDirty(posOfCanon[g]); }

			int ran = 0;
			if (TraceGates) LastStepGates.Clear();
			// the kernel pays when it runs many NANDs per call; a tree of models (RUN FAST) hands almost every gate back,
			// and each call costs more than the gate: such a program runs managed, with a kernel step now and then to
			// re-measure. Both paths give the same result (bench "burst stepper = managed stepper").
			if (UseBurst && !CollectStats && !TraceGates && (!kernelNotWorth || (++kernelSampleTick & 255) == 0))
			{
				// NAND / NAND+inverter / no-op gates run by the Burst kernel, every other gate here, same order
				fixed (uint* st = states)
				fixed (byte* type = sType, arm = armedAt)
				fixed (int* in0 = sIn0, in1 = sIn1, out0 = sOut0, out1 = sOut1)
				fixed (ulong* dirtyBits = dirty, top = dirtyTop)
				fixed (int* cs = slotConsStart, cp = slotConsPos, co = slotConsOther, cu = slotConsOut)
				fixed (bool* q = quiet)
				{
					StepCtx ctx = new StepCtx
					{
						st = st, type = type, in0 = in0, in1 = in1, out0 = out0, out1 = out1, dirty = dirtyBits, top = top,
						words = dirty.Length, topWords = dirtyTop.Length, gc = gc, consStart = cs, consPos = cp, consOther = co,
						consOut = cu, quiet = (byte*)q, armed = arm
					};
					int kernelRan = 0, after = -1;
					while (true)
					{
						int k = SimKernel.Run(&ctx, after, &kernelRan);
						if (k < 0) break;
						dirtyBits[k >> 6] &= ~(1UL << (k & 63));
						ran++;
						RunOne(k, st, type, in0, in1, out0, clockHigh, audio);
						after = k;
						kernelBails++;
					}
					ran += kernelRan;
					kernelGates += kernelRan;
					if (++kernelSteps == 64)
					{
						kernelNotWorth = kernelGates < 2 * kernelBails;
						kernelSteps = 0; kernelGates = 0; kernelBails = 0;
					}
					KernelRunsLastStep = kernelRan;
				}
				GatesRunLastStep = ran;
				return;
			}
			fixed (uint* st = states)
			fixed (byte* type = sType)
			fixed (int* in0 = sIn0, in1 = sIn1, out0 = sOut0)
			fixed (ulong* dirtyBits = dirty)
			fixed (ulong* top = dirtyTop)
			{
				int words = dirty.Length, topWords = dirtyTop.Length;
				for (int tw = 0; tw < topWords; tw++)
				{
					ulong tbits = top[tw];
					while (tbits != 0)
					{
					int tb = lowestBitTable[(int)(((tbits & (~tbits + 1)) * DeBruijn) >> 58)];
					int w = (tw << 6) + tb;
					if (w >= words) { tbits = 0; break; }
					ulong bits = dirtyBits[w];
					while (bits != 0)
					{
						int b = lowestBitTable[(int)(((bits & (~bits + 1)) * DeBruijn) >> 58)];
						ulong bit = 1UL << b;
						dirtyBits[w] &= ~bit; // cleared before running: a gate may re-arm itself
						int k = (w << 6) + b;
						if (k >= gc) { bits = 0; break; }
						ran++;
						if (CollectStats) { RunsByType[type[k]]++; if (RunsByGate.Length != gateCount) RunsByGate = new long[gateCount]; RunsByGate[sCanon[k]]++; }
						if (TraceGates) LastStepGates.Add(sCanon[k]);
						RunOne(k, st, type, in0, in1, out0, clockHigh, audio);

						// gates later in this word may have been marked by what just ran: take them this step
						bits = dirtyBits[w] & (b == 63 ? 0 : ~((bit << 1) - 1));
					}
					if (dirtyBits[w] == 0) top[tw] &= ~(1UL << tb); // nothing left pending in this word (feedback marks keep it)
					tbits = top[tw] & (tb == 63 ? 0 : ~((2UL << tb) - 1));
					}
				}
			}
			GatesRunLastStep = ran;
		}

		// Burst experiment: the step's NAND gates run by SimKernel (false = the managed loop only, for comparison)
		public int KernelRunsLastStep; // gates of the last step run by the Burst kernel
		bool kernelNotWorth;
		public bool KernelSwitchedOff => kernelNotWorth;
		int kernelSampleTick, kernelSteps;
		long kernelGates, kernelBails;
		public static bool UseBurst { get => !managedOnly && BurstEnabled; set => managedOnly = !value; }
		public static volatile bool BurstEnabled = true; // every thread (probes: -noBurst)
		[ThreadStatic] static bool managedOnly; // per thread: a bench case compares both while others run

		// one gate (its dirty bit already cleared)
		unsafe void RunOne(int k, uint* st, byte* type, int* in0, int* in1, int* out0, bool clockHigh, SimAudio audio)
		{
			if (armedAt[k] != 0) DisarmNoise(k); // re-armed below if it still reads a floating bit

			byte tk = type[k];
			if (tk == (byte)ChipType.Nand || tk == NandNotType)
			{
				int ia = in0[k], ic = in1[k];
				uint a = st[ia], c = st[ic];
				if (((a | c) & 0x10000) != 0)
				{
					bool[] q = quiet;
					bool na = (a & 0x10000) != 0 && !q[ia], ncc = (c & 0x10000) != 0 && !q[ic];
					if (na) a = Noisy(a);
					if (ncc) c = Noisy(c);
					if (na || ncc) ArmNoise(k);
				}
				uint v = (1 ^ (a & c)) & 1;
				int o = out0[k];
				if (tk == (byte)ChipType.Nand) { if (st[o] != v) { st[o] = v; MarkConsumers(o); } }
				else
				{
					st[o] = v; // the NAND's own pin (its only reader is the fused inverter)
					int o2 = sOut1[k];
					uint nv = (1 ^ v) & 1;
					if (st[o2] != nv) { st[o2] = nv; MarkConsumers(o2); }
				}
			}
			else if (tk != NopType) RunGate(k, st, clockHigh, audio);
		}

		// A pin's state was written from outside the step (memory editor, sim thread parked): the gates reading it
		// run at the next step.
		public void NotifyPinWritten(SimPin p)
		{
			if (p.stateArray != states || p.stateIndex < 0 || p.stateIndex >= slotCount) return;
			MarkConsumers(p.stateIndex);
		}

		void SetDirty(int pos)
		{
			int w = pos >> 6;
			dirty[w] |= 1UL << (pos & 63);
			dirtyTop[w >> 6] |= 1UL << (w & 63);
		}

		// ---- idle time ----
		// How many steps from now can pass without anything running: no gate is pending, the root inputs did
		// not change, no ROM was edited, and no clock transition is due before then.
		// (Keyboard changes cannot be predicted; the caller keeps spans short and the next real step sees them.)
		public int IdleSteps(int maxSteps) => IdleSteps(maxSteps, Simulator.simulationFrame, Simulator.stepsPerClockTransition, Simulator.forcedClockState);

		public int IdleSteps(int maxSteps, int frame, int period, int forcedClock)
		{
			if (HasBuzzer || maxSteps <= 0) return 0;
			for (int t = 0; t < dirtyTop.Length; t++)
				if (dirtyTop[t] != 0)
				{
					// a summary bit may be stale (word emptied by a feedback-marked gate that ran later): check the words
					for (int w = t << 6; w < Math.Min(dirty.Length, (t + 1) << 6); w++) if (dirty[w] != 0) return 0;
					dirtyTop[t] = 0;
				}
			for (int i = 0; i < rootInputSlots.Length; i++) if (states[rootInputSlots[i]] != rootShadow[i]) return 0;
			foreach (int g in romGates) if (chipOfGate[g].InternalStateEdited) return 0;
			if (SimKeyboardHelper.Version != lastKeyVersion && keyGates.Length > 0) return 0;
			// (noise re-draws due during the span are not an event: their budget accrues and they are all applied at
			// the next real step — a re-draw moved by half a clock period is still "a few times a second")
			int span = maxSteps;
			if (forcedClock < 0 && AnyClockRunning())
			{
				if (period <= 0) return 0;
				// the level of a step is decided by its frame / period, so the clock changes on the step whose frame is
				// the next multiple of period: that step must be a real one, only the steps before it can be skipped
				int f = frame;
				int untilClock = period - f % period - 1;
				span = Math.Min(span, untilClock);
			}
			return span < 0 ? 0 : span;
		}

		// a clock stopped by the user (TURN OFF) never changes level by itself: it must not cut the idle spans
		bool AnyClockRunning()
		{
			foreach (int g in clockGates)
			{
				uint[] cs = internalState[g];
				if (cs.Length < 2 || cs[0] == 0) return true;
			}
			return false;
		}

		// The state after `steps` idle steps is the same as now; only the noise budget accrues.
		public void SkipIdleSteps(int steps)
		{
			noiseAccumulator += steps * noiseCount;
		}

		void ClearNoise()
		{
			for (int k = 0; k < noisePos.Length; k++) noisePos[k] = -1;
			Array.Clear(armedAt, 0, armedAt.Length);
			noiseCount = 0;
			noiseAccumulator = 0;
		}

		// gate at schedule position pos reads a floating line: re-run it now and then (see NoisePeriod)
		void ArmNoise(int pos)
		{
			int c = sCanon[pos];
			armedAt[pos] = 1;
			if (noisePos[c] >= 0) return;
			noisePos[c] = noiseCount; noiseList[noiseCount++] = c;
		}

		// the value a gate reads from a slot: floating bits become random bits (tri flags kept)
		static uint Noisy(uint s)
		{
			uint tri = s >> 16;
			return tri == 0 ? s : (s & 0xFFFF & ~tri) | (Simulator.RandomBits16() & tri) | (tri << 16);
		}

		void DisarmNoise(int pos)
		{
			int c = sCanon[pos];
			armedAt[pos] = 0;
			int p = noisePos[c];
			if (p < 0) return;
			int last = noiseList[noiseCount - 1];
			noiseList[p] = last; noisePos[last] = p; noiseCount--; noisePos[c] = -1;
		}

		// a slot changed: every gate reading it runs (this step if it comes later, next step otherwise)
		// Controlling value: a NAND whose OTHER input is a driven 0 outputs 1 whatever this input does; if it ALREADY
		// outputs 1, waking it changes nothing (the output check matters: right after a compile a gate may have run
		// on a not-yet-computed floating input and hold a noise value). At a clock edge this is most of a RAM's
		// latch-input gates: their write-enable side is 0. (First attempt read the output through slot numbers from
		// before the renumbering — the 3xAND3 golden and exhaustive case caught it.)
		void MarkConsumers(int slot)
		{
			int[] consPos = slotConsPos, other = slotConsOther, outs = slotConsOut;
			uint[] st = states;
			ulong[] d = dirty;
			ulong[] top = dirtyTop;
			for (int j = slotConsStart[slot], e = slotConsStart[slot + 1]; j < e; j++)
			{
				int o = other[j];
				if (o >= 0 && (st[o] & 0x10001u) == 0 && st[outs[j]] == 1) continue;
				int p = consPos[j], w = p >> 6;
				d[w] |= 1UL << (p & 63);
				top[w >> 6] |= 1UL << (w & 63);
			}
		}

		void Write(uint[] st, int slot, uint v)
		{
			if (st[slot] != v) { st[slot] = v; MarkConsumers(slot); }
		}

		// a logic input of gate k: floating bits read as noise, and the gate joins the noise list
		unsafe uint In(uint* st, int slot, int k)
		{
			uint s = st[slot];
			if ((s & 0xFFFF0000u) == 0 || quiet[slot]) return s;
			ArmNoise(k);
			return Noisy(s);
		}

		unsafe void RunGate(int k, uint* st, bool clockHigh, SimAudio audio)
		{
			uint[] states = this.states;
			int g = sCanon[k];
			int[] ins = inSlots, outs = outSlots;
			int i = cInStart[g], os = cOutStart[g];
			switch (sType[k])
			{
				case MergeType:
					Merge(k, g, states);
					break;

				case (byte)ChipType.TriStateBuffer:
					// disabled: floats (noise), so it re-arms every step. If something else drives its net, the
					// merge node of that net writes the net's value back into this slot right after.
					// disabled: floats. If something else drives its net, the merge node of that net writes the
					// net's value back into this slot right after (a floating output reads its net).
					{
						uint en = In(st, sIn1[k], k); // floating enable: random
						Write(states, sOut0[k], (en & 1) == PinState.LogicHigh ? st[sIn0[k]] : PinState.FloatingLow);
					}
					break;

				case (byte)ChipType.Clock: // re-run by Step when the level changes, or when the user stops it / clicks it
				{
					uint[] cs = internalState[g];
					bool high = cs.Length >= 2 && cs[0] != 0 && Simulator.forcedClockState < 0 ? cs[1] != 0 : clockHigh; // (a test driving the clocks wins)
					Write(states, sOut0[k], high ? PinState.LogicHigh : PinState.LogicLow);
					break;
				}

				case (byte)ChipType.Vcc: Write(states, sOut0[k], PinState.LogicHigh); break;
				case (byte)ChipType.Gnd: Write(states, sOut0[k], PinState.LogicLow); break;

				case (byte)ChipType.Split_4To1Bit:
				{
					uint v = st[sIn0[k]];
					Write(states, outs[os], (v >> 3) & PinState.SingleBitMask);
					Write(states, outs[os + 1], (v >> 2) & PinState.SingleBitMask);
					Write(states, outs[os + 2], (v >> 1) & PinState.SingleBitMask);
					Write(states, outs[os + 3], v & PinState.SingleBitMask);
					break;
				}
				case (byte)ChipType.Merge_1To4Bit:
					Write(states, sOut0[k], (st[ins[i + 3]] & PinState.SingleBitMask) | (st[ins[i + 2]] & PinState.SingleBitMask) << 1 | (st[ins[i + 1]] & PinState.SingleBitMask) << 2 | (st[ins[i]] & PinState.SingleBitMask) << 3);
					break;
				case (byte)ChipType.Merge_1To8Bit:
				{
					uint r = 0;
					for (int b = 0; b < 8; b++) r |= (st[ins[i + 7 - b]] & PinState.SingleBitMask) << b;
					Write(states, sOut0[k], r);
					break;
				}
				case (byte)ChipType.Merge_4To8Bit:
				{
					uint r = 0;
					PinState.Set8BitFrom4BitSources(ref r, st[sIn1[k]], st[sIn0[k]]);
					Write(states, sOut0[k], r);
					break;
				}
				case (byte)ChipType.Split_8To4Bit:
				{
					uint v = st[sIn0[k]], a = 0, b = 0;
					PinState.Set4BitFrom8BitSource(ref a, v, false);
					PinState.Set4BitFrom8BitSource(ref b, v, true);
					Write(states, outs[os], a);
					Write(states, outs[os + 1], b);
					break;
				}
				case (byte)ChipType.Split_8To1Bit:
				{
					uint v = st[sIn0[k]];
					for (int b = 0; b < 8; b++) Write(states, outs[os + b], (v >> (7 - b)) & PinState.SingleBitMask);
					break;
				}

				case (byte)ChipType.Key: // re-run by Step when the keyboard state version changes
					Write(states, sOut0[k], SimKeyboardHelper.KeyIsHeld((char)internalState[g][0]) ? PinState.LogicHigh : PinState.LogicLow);
					break;

				case (byte)ChipType.Pulse:
				{
					uint[] mem = internalState[g];
					uint input = st[sIn0[k]];
					bool floating = PinState.GetTristateFlags(input) != 0;
					if (floating) input = In(st, sIn0[k], k);
					bool inputHigh = PinState.FirstBitHigh(input);
					uint remaining = mem[1];
					if (remaining == 0 && inputHigh && mem[2] == 0) { remaining = mem[0]; mem[1] = remaining; }
					uint o = PinState.LogicLow;
					if (remaining > 0) { mem[1]--; o = PinState.LogicHigh; }
					else if (floating) o = PinState.FloatingLow;
					Write(states, sOut0[k], o);
					mem[2] = inputHigh ? 1u : 0;
					if (remaining > 0) SetDirty(k); // counting down
					break;
				}

				case (byte)ChipType.DisplayRGB:
				{
					uint[] mem = internalState[g];
					uint address = In(st, ins[i], k), red = In(st, ins[i + 1], k), green = In(st, ins[i + 2], k), blue = In(st, ins[i + 3], k);
					uint reset = In(st, ins[i + 4], k), write = In(st, ins[i + 5], k), refresh = In(st, ins[i + 6], k), clock = In(st, ins[i + 7], k);
					bool high = PinState.FirstBitHigh(clock);
					bool rising = high && mem[^1] == 0;
					mem[^1] = high ? 1u : 0;
					if (rising)
					{
						if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a + 256] = 0;
						else if (PinState.FirstBitHigh(write))
							mem[(address & 0xFF) + 256] = (red & 0xF) | ((green & 0xF) << 4) | ((blue & 0xF) << 8);
						if (PinState.FirstBitHigh(refresh)) for (int a = 0; a < 256; a++) mem[a] = mem[a + 256];
					}
					uint col = mem[address & 0xFF];
					Write(states, outs[os], (col >> 0) & 0b1111);
					Write(states, outs[os + 1], (col >> 4) & 0b1111);
					Write(states, outs[os + 2], (col >> 8) & 0b1111);
					break;
				}

				case (byte)ChipType.DisplayDot:
				{
					uint[] mem = internalState[g];
					uint address = In(st, ins[i], k), pixel = In(st, ins[i + 1], k), reset = In(st, ins[i + 2], k), write = In(st, ins[i + 3], k), refresh = In(st, ins[i + 4], k), clock = In(st, ins[i + 5], k);
					bool high = PinState.FirstBitHigh(clock);
					bool rising = high && mem[^1] == 0;
					mem[^1] = high ? 1u : 0;
					if (rising)
					{
						if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a + 256] = 0;
						else if (PinState.FirstBitHigh(write)) mem[(address & 0xFF) + 256] = pixel & 1;
						if (PinState.FirstBitHigh(refresh)) for (int a = 0; a < 256; a++) mem[a] = mem[a + 256];
					}
					Write(states, sOut0[k], (ushort)mem[address & 0xFF]);
					break;
				}

				case (byte)ChipType.FastModel: // RUN FAST: a module run by its model
				{
					FastModel m = chipOfGate[g].Model;
					uint[] mi = m.In, mo = m.Out;
					for (int j = 0; j < mi.Length; j++) mi[j] = In(st, ins[i + j], k);
					m.Run();
					for (int j = 0; j < mo.Length; j++) Write(states, outs[os + j], mo[j]);
					if (m.RerunNextStep) { m.RerunNextStep = false; SetDirty(k); }
					break;
				}

				case (byte)ChipType.LcdDem122032:
				{
					uint db = In(st, ins[i], k), a0 = In(st, ins[i + 1], k), rw = In(st, ins[i + 2], k), e1 = In(st, ins[i + 3], k), e2 = In(st, ins[i + 4], k), res = In(st, ins[i + 5], k);
					uint o = LcdDem122032.Run(internalState[g], db & 0xFF, PinState.FirstBitHigh(a0), PinState.FirstBitHigh(rw), PinState.FirstBitHigh(e1), PinState.FirstBitHigh(e2), PinState.FirstBitHigh(res));
					Write(states, sOut0[k], o);
					break;
				}

				case (byte)ChipType.dev_Ram_8Bit:
				{
					uint[] mem = internalState[g];
					uint address = In(st, ins[i], k), data = In(st, ins[i + 1], k), we = In(st, ins[i + 2], k), reset = In(st, ins[i + 3], k), clock = In(st, ins[i + 4], k);
					bool high = PinState.FirstBitHigh(clock);
					bool rising = high && mem[^1] == 0;
					mem[^1] = high ? 1u : 0;
					if (rising)
					{
						if (PinState.FirstBitHigh(reset)) for (int a = 0; a < 256; a++) mem[a] = 0;
						else if (PinState.FirstBitHigh(we)) mem[address & 0xFF] = data & 0xFF;
					}
					Write(states, sOut0[k], (ushort)mem[address & 0xFF]);
					break;
				}

				case (byte)ChipType.Rom_256x16:
				{
					uint data = internalState[g][In(st, sIn0[k], k) & 0xFF];
					Write(states, outs[os], (data >> 8) & 0xFF);
					Write(states, outs[os + 1], data & 0xFF);
					break;
				}

				case (byte)ChipType.Buzzer:
					audio.RegisterNote((int)(In(st, sIn0[k], k) & 0xFF), In(st, sIn1[k], k) & 0xF);
					SetDirty(k); // the note must be registered every step
					break;
			}
		}

		// Several sources on one pin, merged PER BIT: a driven source always beats a floating one, driven
		// sources in conflict are resolved at random for the step, all-floating stays floating (no value).
		// Floating sources on a driven bit then take the net's value (a disabled
		// 3-state buffer's output pin, and the wire from it, read the voltage of the line they sit on).
		void Merge(int k, int g, uint[] st)
		{
			int[] ins = inSlots;
			int start = cInStart[g], count = cInCount[g];
			uint drivenAny = 0, orBits = 0, andBits = 0xFFFF;
			for (int j = 0; j < count; j++)
			{
				uint s = st[ins[start + j]];
				uint bits = s & 0xFFFF, tri = s >> 16, drv = ~tri & 0xFFFF;
				orBits |= bits & drv;
				andBits &= bits | tri;
				drivenAny |= drv;
			}
			uint conflict = (orBits ^ andBits) & drivenAny;
			uint chosen = conflict != 0 && Simulator.RandomBool() ? orBits : andBits;
			uint resTri = ~drivenAny & 0xFFFF;
			uint resBits = chosen & drivenAny;
			Write(st, sOut0[k], resBits | (resTri << 16));
			if (conflict != 0) SetDirty(k); // re-drawn every step while the conflict lasts

			if (drivenAny != 0)
			{
				SimPin winner = null;
				SimPin[] srcs = mergeSources[g];
				for (int j = 0; j < count; j++)
				{
					int slot = ins[start + j];
					uint s = st[slot];
					uint tri = s >> 16;
					uint take = tri & drivenAny;
					if (take != 0) Write(st, slot, ((s & 0xFFFF & ~take) | (resBits & take)) | (tri << 16));
					else if (winner == null && (~tri & 0xFFFF) != 0) winner = srcs[j];
				}
				if (winner != null)
				{
					SimPin t = mergeTarget[g];
					t.latestSourceID = winner.ID;
					t.latestSourceParentChipID = winner.parentChip.ID;
				}
			}
		}
	}
}
