using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	// RUN FAST models of SEQUENTIAL modules. A spec says which pin plays which role (from the pin names and widths)
	// and the options (clock edge, active levels, reset kind); every candidate is CO-SIMULATED against the module's
	// gates on random stimulus (state synchronised first by reading it through the pins) and rejected at the first
	// difference; the state read / write procedures used to move the state between the gates and the model are checked
	// too (random state written through the pins, read back).
	public static class FastTemplates
	{
		public enum Kind { Register, Counter, Ram }

		public class Spec
		{
			public Kind Kind;
			public int D = -1, Clk = -1, Load = -1, Reset = -1, Oe = -1, Cs = -1, We = -1; // input pin indices
			public int[] Addr = Array.Empty<int>(); // address pins, least significant first (Adress0, Adress1... or one multi-bit pin)

			public int AddrBits(ChipDescription d) => Addr.Sum(i => (int)d.InputPins[i].BitCount);
			public uint GetAddr(uint[] inputs, ChipDescription d)
			{
				uint a = 0; int shift = 0;
				foreach (int i in Addr) { int b = (int)d.InputPins[i].BitCount; a |= (inputs[i] & (uint)((1 << b) - 1)) << shift; shift += b; }
				return a;
			}
			public void SetAddr(uint[] inputs, uint a, ChipDescription d)
			{
				int shift = 0;
				foreach (int i in Addr) { int b = (int)d.InputPins[i].BitCount; inputs[i] = (a >> shift) & (uint)((1 << b) - 1); shift += b; }
			}
			public int Q = -1, QN = -1;                                                               // output pin indices
			public bool Rising, LoadHigh = true, ResetHigh = true, OeHigh = true, CsHigh = true, WeHigh = true;
			public int ResetMode; // 0 none, 1 synchronous (at the edge), 2 asynchronous (level)
			public Spec Copy() => (Spec)MemberwiseClone();
			public override string ToString() => $"{Kind} D{D} Clk{Clk} Load{Load} Reset{Reset}/{ResetMode} Oe{Oe} Cs{Cs} Addr[{string.Join(",", Addr)}] We{We} Q{Q} QN{QN} {(Rising ? "rise" : "fall")} {(LoadHigh ? "" : "!load ")}{(ResetHigh ? "" : "!reset ")}{(OeHigh ? "" : "!oe ")}{(CsHigh ? "" : "!cs ")}{(WeHigh ? "" : "!we")}";
		}

		public static FastModel Find(ChipDescription d, ChipLibrary lib)
		{
			foreach (Spec s in Candidates(d))
			{
				var model = new SeqModel(s, d);
				if (Verify(model, d, lib)) return model;
			}
			return null;
		}

		// ---- candidates from the pin names and widths ----

		static string Key(string n) => new string((n ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
		static bool Has(string n, params string[] parts) => parts.Any(p => Key(n).Contains(p));

		public static IEnumerable<Spec> Candidates(ChipDescription d)
		{
			PinDescription[] ins = d.InputPins, outs = d.OutputPins;
			int Find1(Func<string, bool> pred, params int[] taken)
			{
				for (int i = 0; i < ins.Length; i++) if (!taken.Contains(i) && ins[i].BitCount == PinBitCount.Bit1 && pred(ins[i].Name)) return i;
				return -1;
			}
			bool IsClk(string n) => Has(n, "clock", "clk", "clck", "horloge") || Key(n) == "ck";
			bool IsReset(string n) => Has(n, "reset", "rst", "clr", "clear");
			bool IsOe(string n) => Key(n) == "oe" || Has(n, "outputenable");
			bool IsCs(string n) => Key(n) is "cs" or "ce" or "sel" or "select";
			bool IsWe(string n) => Has(n, "we", "write") || Key(n) == "w";

			if (outs.Length is 0 or > 2) yield break;
			int q = Array.FindIndex(outs, o => !Has(o.Name, "'") && !o.Name.EndsWith("'"));
			if (q < 0) q = 0;
			int qn = outs.Length == 2 ? 1 - q : -1;
			if (qn >= 0 && outs[qn].BitCount != outs[q].BitCount) yield break;
			int width = (int)outs[q].BitCount;

			int clk = Find1(IsClk);
			if (clk >= 0)
			{
				int reset = Find1(IsReset, clk), oe = Find1(IsOe, clk, reset);
				int load = Find1(n => Has(n, "load", "ld", "jump", "en", "we", "write"), clk, reset, oe);
				var dataPins = Enumerable.Range(0, ins.Length).Where(i => i != clk && i != reset && i != oe && i != load && (int)ins[i].BitCount == width).ToList();
				if (dataPins.Count != 1) yield break;
				int dp = dataPins[0];
				if (ins.Length != new[] { clk, reset, oe, load, dp }.Count(i => i >= 0)) yield break; // a pin with no role
				// counter: a jump / load selects the data, otherwise + 1 (only with a load pin)
				var kinds = new List<Kind> { Kind.Register };
				if (load >= 0 && qn < 0) kinds.Add(Kind.Counter);
				foreach (Kind kind in kinds)
					foreach (bool rising in new[] { false, true })
						foreach (int resetMode in reset >= 0 ? new[] { 1, 2 } : new[] { 0 })
							foreach (bool resetHigh in reset >= 0 ? new[] { true, false } : new[] { true })
								foreach (bool loadHigh in load >= 0 ? new[] { true, false } : new[] { true })
									foreach (bool oeHigh in oe >= 0 ? new[] { true, false } : new[] { true })
										yield return new Spec { Kind = kind, D = dp, Clk = clk, Load = load, Reset = reset, Oe = oe, Q = q, QN = qn, Rising = rising, ResetMode = resetMode, ResetHigh = resetHigh, LoadHigh = loadHigh, OeHigh = oeHigh };
				yield break;
			}

			// no clock: an addressed RAM written while WE is active
			if (qn >= 0) yield break;
			int we = Find1(IsWe);
			if (we < 0) yield break;
			int oe2 = Find1(IsOe, we), cs = Find1(IsCs, we, oe2);
			// address pins: one multi-bit pin, or 1-bit pins numbered by their names (Adress0 = the low bit)
			int Num(string n) { string digits = new string(n.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()); return digits.Length > 0 ? int.Parse(digits) : -1; }
			var addrPins = Enumerable.Range(0, ins.Length).Where(i => Has(ins[i].Name, "adr", "addr", "adress", "address", "adresse")).OrderBy(i => Num(ins[i].Name)).ToList();
			var data = Enumerable.Range(0, ins.Length).Where(i => i != we && i != oe2 && i != cs && !addrPins.Contains(i) && (int)ins[i].BitCount == width).ToList();
			if (addrPins.Count == 0 || data.Count != 1) yield break;
			if (addrPins.Count > 1 && addrPins.Any(i => ins[i].BitCount != PinBitCount.Bit1 || Num(ins[i].Name) < 0)) yield break;
			if (ins.Length != new[] { we, oe2, cs, data[0] }.Count(i => i >= 0) + addrPins.Count) yield break;
			foreach (bool weHigh in new[] { true, false })
				foreach (bool oeHigh in oe2 >= 0 ? new[] { true, false } : new[] { true })
					foreach (bool csHigh in cs >= 0 ? new[] { true, false } : new[] { true })
						yield return new Spec { Kind = Kind.Ram, D = data[0], Addr = addrPins.ToArray(), We = we, Oe = oe2, Cs = cs, Q = q, WeHigh = weHigh, OeHigh = oeHigh, CsHigh = csHigh };
		}

		// ---- verification: the model against the gates ----

		const int Settle = 24;

		sealed class Gates : IDisposable
		{
			public readonly SimChip Root, Target;
			readonly SimAudio audio = new();
			public Gates(ChipDescription d, ChipLibrary lib, int seed)
			{
				Simulator.ResetForTests(seed);
				Root = CircuitTester.BuildIsolatedSim(d, lib);
				Target = CircuitTester.TargetOf(Root);
			}
			public void Set(uint[] inputs)
			{
				for (int i = 0; i < inputs.Length; i++) Root.InputPins[i].State = PinState.Make((ushort)inputs[i], 0);
				for (int k = 0; k < Settle; k++) Simulator.RunSimulationStep(Root, Array.Empty<DevPinInstance>(), audio);
			}
			public uint Out(int o) => Target.OutputPins[o].State;
			public void Dispose() => Simulator.ClearTestSeed();
		}

		static bool Verify(SeqModel model, ChipDescription d, ChipLibrary lib) => Why(model, d, lib) == null;

		// null when the model matches the gates, else where it did not (quick rejection on a short run, then the full run)
		static string Why(SeqModel model, ChipDescription d, ChipLibrary lib)
		{
			return Run(model, d, lib, 25, 11) ?? Run(model, d, lib, 300, 12) ?? (StateRoundTrip(model, d, lib) ? null : "state write / read back through the pins failed");
		}

		public static List<string> Diagnose(ChipDescription d, ChipLibrary lib) =>
			Candidates(d).Select(s => s + " -> " + (Why(new SeqModel(s, d), d, lib) ?? "OK")).ToList();

		static string Run(SeqModel proto, ChipDescription d, ChipLibrary lib, int cycles, int seed)
		{
			Spec s = proto.Spec;
			var m = (SeqModel)proto.Clone();
			var rnd = new Random(seed);
			using var g = new Gates(d, lib, seed);
			uint[] cur = new uint[d.InputPins.Length];
			uint Mask(int pin) => (1u << (int)d.InputPins[pin].BitCount) - 1;
			uint Lv(bool high, bool active) => active == high ? 1u : 0u; // the pin value meaning "active" / "inactive"

			// idle start, then the state synchronised from the gates
			if (s.Clk >= 0) cur[s.Clk] = s.Rising ? 0u : 1u;
			if (s.Reset >= 0) cur[s.Reset] = Lv(s.ResetHigh, false);
			if (s.Load >= 0) cur[s.Load] = Lv(s.LoadHigh, false);
			if (s.We >= 0) cur[s.We] = Lv(s.WeHigh, false);
			g.Set(cur);
			m.LoadState(ReadState(s, d, g, cur));
			m.SetLastInputs(cur);
			g.Set(cur); // (the read forced the output enables on: back to the stimulus inputs)

			string fail = null;
			int cycle = -1;
			bool Same()
			{
				Array.Copy(cur, m.In, cur.Length);
				SeqModel.Settle(m);
				for (int o = 0; o < d.OutputPins.Length; o++)
					if (m.Out[o] != g.Out(o)) { fail = $"cycle {cycle}, inputs [{string.Join(",", cur)}]: {d.OutputPins[o].Name} model {m.Out[o]:X8} gates {g.Out(o):X8}"; return false; }
				return true;
			}
			if (!Same()) return fail;

			for (int c = 0; c < cycles; c++)
			{
				cycle = c;
				if (s.Kind == Kind.Ram)
				{
					s.SetAddr(cur, (uint)rnd.Next(1 << s.AddrBits(d)), d);
					cur[s.D] = (uint)rnd.Next((int)Mask(s.D) + 1);
					cur[s.We] = rnd.Next(3) == 0 ? Lv(s.WeHigh, true) : Lv(s.WeHigh, false);
					if (s.Oe >= 0) cur[s.Oe] = (uint)rnd.Next(2);
					if (s.Cs >= 0) cur[s.Cs] = rnd.Next(5) == 0 ? Lv(s.CsHigh, false) : Lv(s.CsHigh, true);
					g.Set(cur);
					if (!Same()) return fail;
					// a write ends with WE released (address and data held), as a real bus cycle does
					if (cur[s.We] == Lv(s.WeHigh, true)) { cur[s.We] = Lv(s.WeHigh, false); g.Set(cur); if (!Same()) return fail; }
					continue;
				}
				// clocked: data / controls change away from the edge, then the clock goes through a full period
				cur[s.D] = (uint)rnd.Next((int)Mask(s.D) + 1);
				if (s.Load >= 0) cur[s.Load] = (uint)rnd.Next(2);
				if (s.Oe >= 0) cur[s.Oe] = (uint)rnd.Next(4) == 0 ? Lv(s.OeHigh, false) : Lv(s.OeHigh, true);
				if (s.Reset >= 0) cur[s.Reset] = rnd.Next(12) == 0 ? Lv(s.ResetHigh, true) : Lv(s.ResetHigh, false);
				g.Set(cur);
				if (!Same()) return fail;
				cur[s.Clk] ^= 1; g.Set(cur); if (!Same()) return fail;
				cur[s.Clk] ^= 1; g.Set(cur); if (!Same()) return fail;
			}
			return null;
		}

		// write a random state through the pins, read it back: the transfer procedures work on this module
		static bool StateRoundTrip(SeqModel proto, ChipDescription d, ChipLibrary lib)
		{
			Spec s = proto.Spec;
			var rnd = new Random(77);
			using var g = new Gates(d, lib, 77);
			uint[] cur = IdleInputs(s, d);
			g.Set(cur);
			uint[] state = new uint[proto.StateLength];
			uint mask = (uint)((1UL << (int)d.OutputPins[s.Q].BitCount) - 1);
			for (int i = 0; i < state.Length; i++) state[i] = (uint)rnd.Next() & mask;
			WriteState(s, d, g, cur, state);
			uint[] back = ReadState(s, d, g, cur);
			return back.SequenceEqual(state);
		}

		static uint[] IdleInputs(Spec s, ChipDescription d)
		{
			uint[] cur = new uint[d.InputPins.Length];
			if (s.Clk >= 0) cur[s.Clk] = s.Rising ? 0u : 1u;
			if (s.Reset >= 0) cur[s.Reset] = s.ResetHigh ? 0u : 1u;
			if (s.Load >= 0) cur[s.Load] = s.LoadHigh ? 0u : 1u;
			if (s.We >= 0) cur[s.We] = s.WeHigh ? 0u : 1u;
			if (s.Oe >= 0) cur[s.Oe] = s.OeHigh ? 1u : 0u;
			if (s.Cs >= 0) cur[s.Cs] = s.CsHigh ? 1u : 0u;
			return cur;
		}

		// ---- the state through the pins (also used on an isolated copy of a live instance) ----

		// Reads without changing the clock (no edge): only the output / chip enables are forced on, and WE off
		static uint[] ReadState(Spec s, ChipDescription d, Gates g, uint[] inputs)
		{
			uint[] cur = (uint[])inputs.Clone();
			if (s.Oe >= 0) cur[s.Oe] = s.OeHigh ? 1u : 0u;
			if (s.Cs >= 0) cur[s.Cs] = s.CsHigh ? 1u : 0u;
			if (s.Kind != Kind.Ram)
			{
				g.Set(cur);
				return new[] { (uint)PinState.GetBitStates(g.Out(s.Q)) };
			}
			cur[s.We] = s.WeHigh ? 0u : 1u;
			int words = 1 << s.AddrBits(d);
			var mem = new uint[words];
			for (int a = 0; a < words; a++)
			{
				s.SetAddr(cur, (uint)a, d);
				g.Set(cur);
				mem[a] = PinState.GetBitStates(g.Out(s.Q));
			}
			return mem;
		}

		static void WriteState(Spec s, ChipDescription d, Gates g, uint[] inputs, uint[] state, uint[] skipWhereEqual = null)
		{
			uint[] cur = (uint[])inputs.Clone();
			if (s.Reset >= 0) cur[s.Reset] = s.ResetHigh ? 0u : 1u;
			if (s.Kind == Kind.Ram)
			{
				if (s.Cs >= 0) cur[s.Cs] = s.CsHigh ? 1u : 0u;
				for (int a = 0; a < state.Length; a++)
				{
					if (skipWhereEqual != null && a < skipWhereEqual.Length && skipWhereEqual[a] == state[a]) continue; // that word did not change
					s.SetAddr(cur, (uint)a, d); cur[s.D] = state[a];
					cur[s.We] = s.WeHigh ? 1u : 0u; g.Set(cur);
					cur[s.We] = s.WeHigh ? 0u : 1u; g.Set(cur);
				}
				return;
			}
			cur[s.D] = state[0];
			if (s.Load >= 0) cur[s.Load] = s.LoadHigh ? 1u : 0u;
			uint idle = s.Rising ? 0u : 1u;
			cur[s.Clk] = idle; g.Set(cur);
			cur[s.Clk] = idle ^ 1; g.Set(cur); // the edge
			if (s.Load >= 0) cur[s.Load] = s.LoadHigh ? 0u : 1u;
			cur[s.Clk] = idle; g.Set(cur);     // back to idle with LOAD off (a counter would count: its load is off only now)
			if (s.Kind == Kind.Counter)
			{
				// the return to idle is not an edge for this spec, so the counter holds the loaded value
			}
		}

		// ---- moving the state of a LIVE instance in and out (RUN FAST start / stop) ----

		public static uint[] ReadLive(SeqModel model, ChipDescription d, ChipLibrary lib, SimChip liveInstance)
		{
			using var g = new Gates(d, lib, 5);
			ChipMemoryState snap = MemorySnapshot.Capture(liveInstance);
			if (snap != null) MemorySnapshot.Apply(g.Target, snap);
			uint[] inputs = liveInstance.InputPins.Select(p => (uint)PinState.GetBitStates(p.State)).ToArray();
			// the copy's state is applied to its gates before they run: settle once with the live inputs, no edge
			g.Set(inputs);
			model.SetLastInputs(inputs);
			return ReadState(model.Spec, d, g, inputs);
		}

		public static void WriteLive(SeqModel model, ChipDescription d, ChipLibrary lib, SimChip liveInstance)
		{
			if (model.Initial.Length == model.State.Length && model.Initial.SequenceEqual(model.State)) return; // unchanged: the gates already hold it
			using var g = new Gates(d, lib, 6);
			ChipMemoryState snap = MemorySnapshot.Capture(liveInstance);
			if (snap != null) MemorySnapshot.Apply(g.Target, snap);
			uint[] inputs = model.LastInputs();
			g.Set(inputs);
			WriteState(model.Spec, d, g, inputs, model.State, model.Initial);
			g.Set(inputs); // back to the model's last inputs (no edge: the clock ends at its idle level, then its last level)
			ChipMemoryState written = MemorySnapshot.Capture(g.Target);
			if (written != null) MemorySnapshot.Apply(liveInstance, written);
		}
	}

	// The behaviour of a template, run as one gate
	public sealed class SeqModel : FastModel
	{
		public readonly FastTemplates.Spec Spec;
		public uint[] State;
		readonly uint mask;
		readonly uint floating;
		readonly int addrBits;
		readonly ChipDescription desc;
		public ChipDescription Desc => desc;
		uint[] lastIn;
		// The running scalars live in one array (Regs) so the compiled step (SimKernel) and Run() share them:
		// [0] last clock level; [1] D sampled before the edge; [2] the output shown; [3] show pending (1/0);
		// [4] LOAD sampled; [5] RESET sampled.
		// master-slave behaviour: D / LOAD / a synchronous reset are taken while the clock is at the level BEFORE the
		// edge, and applied at the edge — a value changed in the same step by a neighbour's new output (the counter's
		// increment logic) does not race through, as with the user's NAND flip-flops.
		// The output shows the state one step after it changed (clock-to-output delay): at the clock fall the user's
		// AND gate releases the RAM's WE in 1-2 steps while their master-slave registers change a few steps later; an
		// instant register output let the RAM see the NEW address with WE still high.
		public uint[] Regs = new uint[6];
		const int RLastClk = 0, RSampledD = 1, RShown = 2, RShowPending = 3, RSampledLoad = 4, RSampledReset = 5;

		public int StateLength => Spec.Kind == FastTemplates.Kind.Ram ? 1 << addrBits : 1;

		public SeqModel(FastTemplates.Spec spec, ChipDescription d) : base(d.InputPins.Length, d.OutputPins.Length)
		{
			Spec = spec;
			mask = (uint)((1UL << (int)d.OutputPins[spec.Q].BitCount) - 1);
			floating = mask << 16; // floating flags on the pin's own bits only, as the gates write them
			addrBits = spec.AddrBits(d);
			desc = d;
			State = new uint[StateLength];
			lastIn = new uint[d.InputPins.Length];
		}

		public override int KernelKind => 2;
		public override uint[][] KernelArrays() => new[] { State, Regs };

		// What the compiled step needs (SimKernel.RunSeq): kind, pins, polarities, mask, address pins + widths.
		public override int[] KernelParams()
		{
			FastTemplates.Spec s = Spec;
			int flags = (s.Rising ? 1 : 0) | (s.LoadHigh ? 2 : 0) | (s.ResetHigh ? 4 : 0) | (s.OeHigh ? 8 : 0) | (s.CsHigh ? 16 : 0) | (s.WeHigh ? 32 : 0);
			var p = new List<int> { (int)s.Kind, s.D, s.Clk, s.Load, s.Reset, s.Oe, s.Cs, s.We, s.Q, s.QN, flags, s.ResetMode, (int)mask, s.Addr.Length };
			foreach (int a in s.Addr) { p.Add(a); p.Add((int)desc.InputPins[a].BitCount); }
			return p.ToArray();
		}

		public override FastModel Clone()
		{
			var c = (SeqModel)MemberwiseClone();
			c.In = new uint[In.Length]; c.Out = new uint[Out.Length];
			c.State = (uint[])State.Clone();
			c.Initial = (uint[])Initial.Clone();
			c.lastIn = (uint[])lastIn.Clone();
			c.Regs = (uint[])Regs.Clone();
			return c;
		}

		public uint[] Initial = Array.Empty<uint>(); // the state taken over at RUN FAST (written back: only what differs)

		public void LoadState(uint[] s)
		{
			Array.Copy(s, State, Math.Min(s.Length, State.Length));
			Initial = (uint[])State.Clone();
			Regs[RShown] = State[0];
			Regs[RShowPending] = 0;
		}

		// runs again while it asks to (the verification and the state transfer have no step loop to do it)
		public static void Settle(FastModel m)
		{
			m.Run();
			for (int k = 0; k < 4 && m.RerunNextStep; k++) { m.RerunNextStep = false; m.Run(); }
			m.RerunNextStep = false;
		}
		public void SetLastInputs(uint[] inputs)
		{
			Array.Copy(inputs, lastIn, Math.Min(inputs.Length, lastIn.Length));
			if (Spec.Clk >= 0)
			{
				Regs[RLastClk] = inputs[Spec.Clk] & 1;
				Array.Copy(inputs, In, Math.Min(inputs.Length, In.Length));
				Sample();
			}
		}
		// the inputs it last ran on, read back from its pins (the compiled step does not keep In / lastIn)
		public void RefreshLastInputs(uint[] inputs) => Array.Copy(inputs, lastIn, Math.Min(inputs.Length, lastIn.Length));

		void Sample()
		{
			FastTemplates.Spec s = Spec;
			Regs[RSampledD] = In[s.D] & mask;
			Regs[RSampledLoad] = s.Load < 0 || Active(s.Load, s.LoadHigh) ? 1u : 0u;
			Regs[RSampledReset] = s.Reset >= 0 && Active(s.Reset, s.ResetHigh) ? 1u : 0u;
		}
		public uint[] LastInputs() => (uint[])lastIn.Clone();

		bool Active(int pin, bool high) => pin >= 0 && ((In[pin] & 1) == 1) == high;

		// (SimKernel.RunSeq is the same function on the program's slots: keep them in step)
		public override void Run()
		{
			FastTemplates.Spec s = Spec;
			uint[] r = Regs;
			Array.Copy(In, lastIn, In.Length);
			if (s.Kind == FastTemplates.Kind.Ram)
			{
				uint a = s.GetAddr(In, desc);
				bool sel = s.Cs < 0 || Active(s.Cs, s.CsHigh);
				if (sel && Active(s.We, s.WeHigh)) State[a] = In[s.D] & mask;
				Out[s.Q] = sel && (s.Oe < 0 || Active(s.Oe, s.OeHigh)) ? State[a] : floating;
				return;
			}
			if (r[RShowPending] != 0) { r[RShown] = State[0]; r[RShowPending] = 0; }
			uint clk = In[s.Clk] & 1;
			bool edge = s.Rising ? r[RLastClk] == 0 && clk == 1 : r[RLastClk] == 1 && clk == 0;
			bool beforeEdgeLevel = s.Rising ? clk == 0 : clk == 1;
			r[RLastClk] = clk;
			if (s.Reset >= 0 && s.ResetMode == 2 && Active(s.Reset, s.ResetHigh)) State[0] = 0;
			else if (edge)
			{
				if (s.Reset >= 0 && s.ResetMode == 1 && r[RSampledReset] != 0) State[0] = 0;
				else if (s.Kind == FastTemplates.Kind.Register) { if (r[RSampledLoad] != 0) State[0] = r[RSampledD]; }
				else State[0] = r[RSampledLoad] != 0 ? r[RSampledD] : (State[0] + 1) & mask;
			}
			if (beforeEdgeLevel) Sample();
			if (State[0] != r[RShown]) { r[RShowPending] = 1; RerunNextStep = true; }
			bool on = s.Oe < 0 || Active(s.Oe, s.OeHigh);
			Out[s.Q] = on ? r[RShown] : floating;
			if (s.QN >= 0) Out[s.QN] = on ? ~r[RShown] & mask : floating;
		}
	}
}
