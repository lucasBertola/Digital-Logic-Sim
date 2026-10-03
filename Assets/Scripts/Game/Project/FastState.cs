using System;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	// RUN FAST state transfer between the gate-level tree (the chip as built) and its fast tree, walked together by
	// sub-chip ID: builtin chips copy their outputs and internal state (latch NANDs, RAM, ROM...), a sequential model
	// reads / writes its state through the pins of an isolated copy of the real instance (FastTemplates.ReadLive /
	// WriteLive), a table model has none. In: at RUN FAST. Out: when it stops, so the circuit continues from there.
	public static class FastState
	{
		public static void CopyIn(SimChip live, SimChip fast, ChipLibrary lib) => Walk(live, fast, lib, true);
		public static void CopyOut(SimChip fast, SimChip live, ChipLibrary lib) => Walk(live, fast, lib, false);

		static void Walk(SimChip live, SimChip fast, ChipLibrary lib, bool intoFast)
		{
			if (live == null || fast == null) return;
			foreach (SimChip fc in fast.SubChips)
			{
				if (fc == null) continue;
				(bool found, SimChip lc) = live.TryGetSubChipFromID(fc.ID);
				if (!found || lc == null) continue;
				if (fc.Model != null)
				{
					if (fc.Model is SeqModel m)
					{
						if (intoFast) m.LoadState(FastTemplates.ReadLive(m, m.Desc, lib, lc));
						else FastTemplates.WriteLive(m, m.Desc, lib, lc);
					}
					if (intoFast)
					{
						// its outputs right away, from its state and the real inputs: a module reading them must not see
						// 0s until this model first runs (a Johnson counter's flip-flop read its neighbour's Q' as 0)
						for (int i = 0; i < Math.Min(fc.Model.In.Length, lc.InputPins.Length); i++) fc.Model.In[i] = lc.InputPins[i].State;
						SeqModel.Settle(fc.Model);
						for (int o = 0; o < Math.Min(fc.Model.Out.Length, fc.OutputPins.Length); o++) fc.OutputPins[o].State = fc.Model.Out[o];
					}
					continue;
				}
				if (fc.IsBuiltin)
				{
					SimChip from = intoFast ? lc : fc, to = intoFast ? fc : lc;
					if (from.InternalState.Length > 0 && to.InternalState.Length > 0) Array.Copy(from.InternalState, to.InternalState, Math.Min(from.InternalState.Length, to.InternalState.Length));
					for (int o = 0; o < Math.Min(from.OutputPins.Length, to.OutputPins.Length); o++) to.OutputPins[o].State = from.OutputPins[o].State;
					continue;
				}
				Walk(lc, fc, lib, intoFast);
			}
		}
	}
}
