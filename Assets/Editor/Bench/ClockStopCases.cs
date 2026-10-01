using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;

namespace DLS.Bench
{
    // CLOCK right-click TURN OFF / TURN ON, click on a stopped clock = toggle its level. The state is the clock's
    // InternalData [stopped, level] (saved with the chip; a running clock saves nothing, like before).
    public static class ClockStopCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("clock stopped: holds its level, a click toggles it, TURN ON follows the clock again", StopToggleRestart),
            ("clock stopped: same with batched stepping (idle skipping, what the app runs)", Batched),
            ("clock stopped: a stopped clock saved in the chip is stopped when the chip is built", FromDescription),
            ("clock stopped: the state is saved with the chip, a running clock saves exactly as before", SaveRoundTrip),
            ("clock stopped: a test driving the clocks (forcedClockState) still drives it", ForcedWins),
            ("clock stopped: it does not force a real step every clock period (idle spans are not cut)", NoRealStepsWhenStopped),
        };

        static UnitCases.Circuit ClockCircuit(uint[] data = null) => UnitCases.Build("t_clock_stop", new string[0], new[] { "C" }, b =>
        {
            int clk = b.Add(ChipType.Clock);
            if (data != null)
            {
                int i = b.subChips.FindIndex(s => s.ID == clk);
                SubChipDescription s = b.subChips[i];
                s.InternalData = data;
                b.subChips[i] = s;
            }
            b.Wire(b.Out(clk, 0), b.Output("C"));
        });

        static SimChip Clock(UnitCases.Circuit c) => c.target.SubChips.First(s => s.ChipType == ChipType.Clock);
        static int Expected() => ((Simulator.simulationFrame / Simulator.stepsPerClockTransition) & 1) == 0 ? 1 : 0;

        static string StopToggleRestart()
        {
            var c = ClockCircuit();
            Simulator.stepsPerClockTransition = 3;
            Simulator.forcedClockState = -1;
            SimChip clk = Clock(c);
            clk.UpdateInternalState(new uint[] { 1, 1 });
            for (int s = 0; s < 20; s++) { c.Step(1); if (c.OutBit("C") != 1) return $"stopped HIGH clock read 0 at step {s}"; }
            clk.UpdateInternalState(new uint[] { 1, 0 }); // click
            for (int s = 0; s < 20; s++) { c.Step(1); if (c.OutBit("C") != 0) return $"after a click, stopped LOW clock read 1 at step {s}"; }
            clk.UpdateInternalState(new uint[] { 1, 1 }); // click again
            c.Step(1);
            if (c.OutBit("C") != 1) return "second click did not bring it back HIGH";
            clk.UpdateInternalState(new uint[] { 0, 1 }); // TURN ON
            for (int s = 0; s < 24; s++) { c.Step(1); if (c.OutBit("C") != Expected()) return $"restarted clock at frame {Simulator.simulationFrame} = {c.OutBit("C")}, expected {Expected()}"; }
            return null;
        }

        static string Batched()
        {
            var c = ClockCircuit();
            Simulator.stepsPerClockTransition = 250;
            Simulator.forcedClockState = -1;
            SimChip clk = Clock(c);
            c.StepBatched(10);
            clk.UpdateInternalState(new uint[] { 1, 1 });
            for (int k = 0; k < 20; k++) { c.StepBatched(137); if (c.OutBit("C") != 1) return $"stopped HIGH clock read 0 after {k} batches"; }
            clk.UpdateInternalState(new uint[] { 1, 0 }); // click during a long idle span
            c.StepBatched(1);
            if (c.OutBit("C") != 0) return "a click during idle skipping was not seen at the next step";
            for (int k = 0; k < 20; k++) { c.StepBatched(137); if (c.OutBit("C") != 0) return $"stopped LOW clock read 1 after {k} batches"; }
            clk.UpdateInternalState(new uint[] { 0, 0 }); // TURN ON
            for (int k = 0; k < 40; k++) { c.StepBatched(61); if (c.OutBit("C") != Expected()) return $"restarted clock at frame {Simulator.simulationFrame} = {c.OutBit("C")}, expected {Expected()}"; }
            return null;
        }

        static string FromDescription()
        {
            var c = ClockCircuit(new uint[] { 1, 1 });
            Simulator.stepsPerClockTransition = 3;
            Simulator.forcedClockState = -1;
            for (int s = 0; s < 30; s++) { c.Step(1); if (c.OutBit("C") != 1) return $"clock saved stopped HIGH read 0 at step {s}"; }
            var d = ClockCircuit(new uint[] { 1, 0 });
            Simulator.stepsPerClockTransition = 3;
            for (int s = 0; s < 30; s++) { d.Step(1); if (d.OutBit("C") != 0) return $"clock saved stopped LOW read 1 at step {s}"; }
            return null;
        }

        static string SaveRoundTrip()
        {
            foreach (uint[] data in new[] { new uint[] { 1, 1 }, new uint[] { 1, 0 }, null })
            {
                var c = ClockCircuit(data);
                c.lib.FillMissingOutputPinColours(c.desc); // what registering a chip does (a generated description has none)
                (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(c.desc, c.lib);
                if (failed) return "load failed";
                SubChipInstance clk = dev.Elements.OfType<SubChipInstance>().First(s => s.ChipType == ChipType.Clock);
                if (clk.InternalData == null || clk.InternalData.Length != 2) return "a loaded clock has no [stopped, level] data";
                ChipDescription back = DescriptionCreator.CreateChipDescription(dev);
                uint[] saved = back.SubChips.First(s => s.ID == clk.ID).InternalData;
                string want = data == null ? "null" : string.Join(",", data), got = saved == null ? "null" : string.Join(",", saved);
                if (want != got) return $"clock data saved as {got}, expected {want}";
                if (!UnsavedChangeDetector.IsEquivalentJson(Saver.CreateSerializedChipDescription(c.desc), Saver.CreateSerializedChipDescription(back))) return $"chip with clock {want} does not reload clean";
            }
            return null;
        }

        static string NoRealStepsWhenStopped()
        {
            var c = ClockCircuit(new uint[] { 1, 0 });
            Simulator.stepsPerClockTransition = 10;
            Simulator.forcedClockState = -1;
            c.StepBatched(100);
            long before = Simulator.RealSteps;
            int n = c.StepBatched(100000);
            long real = Simulator.RealSteps - before;
            if (real > n / 100) return $"{real} real steps over {n} idle steps with the clock stopped (it cut the spans at every period of 10)";
            // and a running clock still does (its edges are real steps)
            Clock(c).UpdateInternalState(new uint[] { 0, 0 });
            before = Simulator.RealSteps;
            n = c.StepBatched(10000);
            real = Simulator.RealSteps - before;
            return real >= n / 10 ? null : $"running clock: only {real} real steps over {n} (period 10: an edge every 10 steps)";
        }

        static string ForcedWins()
        {
            var c = ClockCircuit(new uint[] { 1, 0 });
            try
            {
                Simulator.forcedClockState = 1;
                for (int s = 0; s < 5; s++) { c.Step(1); if (c.OutBit("C") != 1) return "forced HIGH did not drive the stopped clock"; }
            }
            finally { Simulator.forcedClockState = -1; }
            return null;
        }
    }
}
