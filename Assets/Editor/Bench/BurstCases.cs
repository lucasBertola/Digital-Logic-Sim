using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // Burst experiment: the step with its NAND gates run by the Burst kernel (SimKernel) must be the managed step,
    // value for value — every slot of the state, after every step, with the same seed (so the same random draws:
    // noise, conflicts, picks). Chips with latches, buses, disabled buffers (floating lines read as noise) and RAM.
    public static class BurstCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var list = new List<(string, Func<string>)>();
            foreach (string name in new[] { "CPU", "RAM256", "Registre8", "ALU8", "Bascule D", "PC" })
                if (chips.Any(c => c.Name == name))
                    list.Add(($"burst stepper = managed stepper, every slot after every step: {name}", () => Same(lib, chips.First(c => c.Name == name), false, 1200)));
            // RUN FAST tree: mostly model gates, so the kernel is found not worth it and runs one step in 256 to re-measure;
            // long enough to go through that switch both ways
            if (chips.Any(c => c.Name == "CPU"))
                list.Add(("burst stepper = managed stepper on the RUN FAST tree of the CPU (kernel switched off and sampled)", () => Same(lib, chips.First(c => c.Name == "CPU"), true, 6000)));
            return list;
        }

        static string Same(ChipLibrary lib, ChipDescription d, bool fast, int steps)
        {
            var traces = new List<uint[]>[2];
            var runs = new List<int>[2];
            int kernelRuns = 0; bool switchedOff = false;
            for (int pass = 0; pass < 2; pass++)
            {
                bool burst = pass == 1;
                SimProgram.UseBurst = burst;
                try
                {
                    SimChip root;
                    root = fast ? FastBuilder.Build(d, lib, new FastCache(), new List<FastBuilder.Instance>()) : Simulator.BuildSimChip(d, lib);
                    Simulator.ResetForTests(77);
                    var rnd = new Random(5);
                    var audio = new SimAudio();
                    traces[pass] = new List<uint[]>();
                    runs[pass] = new List<int>();
                    for (int s = 0; s < steps; s++)
                    {
                        if (s % 6 == 0)
                            for (int i = 0; i < root.InputPins.Length; i++)
                            {
                                int bits = (int)d.InputPins[i].BitCount;
                                // inputs left alone half the time, so memories hold and settle
                                if (rnd.Next(2) == 0) root.InputPins[i].State = PinState.Make((ushort)(rnd.Next(1 << bits) & 0xFFFF), 0);
                            }
                        Simulator.forcedClockState = (s / 9) & 1;
                        Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                        traces[pass].Add((uint[])root.Program.states.Clone());
                        runs[pass].Add(root.Program.GatesRunLastStep);
                        if (burst) { kernelRuns += root.Program.KernelRunsLastStep; switchedOff |= root.Program.KernelSwitchedOff; }
                    }
                }
                finally
                {
                    SimProgram.UseBurst = true;
                    Simulator.forcedClockState = -1;
                    Simulator.ClearTestSeed();
                }
            }
            for (int s = 0; s < steps; s++)
            {
                if (runs[0][s] != runs[1][s]) return $"step {s}: managed ran {runs[0][s]} gates, burst {runs[1][s]}";
                uint[] a = traces[0][s], b = traces[1][s];
                if (a.Length != b.Length) return $"step {s}: state sizes differ";
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return $"step {s}: slot {i} managed {a[i]:X} burst {b[i]:X}";
            }
            if (fast) return switchedOff ? null : "the RUN FAST tree (models only) never switched the kernel off";
            return kernelRuns > 0 ? null : "the Burst kernel never ran a gate (the comparison compared the managed loop with itself)";
        }
    }
}
