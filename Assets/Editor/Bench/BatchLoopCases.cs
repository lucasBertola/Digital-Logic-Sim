using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // The batched loop the live sim thread runs (Simulator.RunSimulationSteps: bookkeeping in locals, audio once per
    // batch) must be the plain loop (RunSimulationStepsReference, one RunSimulationStep per real step), value for
    // value: same steps accounted, same real steps, every slot equal after every batch, with the same seed. Clock
    // running (gates and RUN FAST tree of the CPU), inputs moved between batches, batches of varied sizes.
    public static class BatchLoopCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var list = new List<(string, Func<string>)>();
            foreach (string name in new[] { "CPU", "Registre8", "RAM256", "PC" })
                if (chips.Any(c => c.Name == name))
                    list.Add(($"batched loop = reference loop, every slot after every batch: {name}", () => Same(lib, chips.First(c => c.Name == name), false)));
            if (chips.Any(c => c.Name == "CPU"))
                list.Add(("batched loop = reference loop on the RUN FAST tree of the CPU", () => Same(lib, chips.First(c => c.Name == "CPU"), true)));
            return list;
        }

        static string Same(ChipLibrary lib, ChipDescription d, bool fast)
        {
            var traces = new List<uint[]>[2];
            var counts = new List<long>[2];
            for (int pass = 0; pass < 2; pass++)
            {
                try
                {
                    SimChip root = fast ? FastBuilder.Build(d, lib, new FastCache(), new List<FastBuilder.Instance>()) : Simulator.BuildSimChip(d, lib);
                    Simulator.ResetForTests(31);
                    Simulator.stepsPerClockTransition = 70;
                    var rnd = new Random(8);
                    var audio = new SimAudio();
                    traces[pass] = new List<uint[]>();
                    counts[pass] = new List<long>();
                    long real0 = Simulator.RealSteps;
                    for (int batch = 0; batch < 400; batch++)
                    {
                        if (batch % 7 == 0)
                            for (int i = 0; i < root.InputPins.Length; i++)
                                if (rnd.Next(3) == 0) root.InputPins[i].State = PinState.Make((ushort)(rnd.Next(1 << (int)d.InputPins[i].BitCount) & 0xFFFF), 0);
                        int max = 1 + rnd.Next(300);
                        int done = pass == 0
                            ? Simulator.RunSimulationStepsReference(root, Array.Empty<DevPinInstance>(), audio, max)
                            : Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, max);
                        counts[pass].Add(done);
                        counts[pass].Add(Simulator.RealSteps - real0);
                        counts[pass].Add(Simulator.simulationFrame);
                        traces[pass].Add((uint[])root.Program.states.Clone());
                    }
                }
                finally { Simulator.ClearTestSeed(); }
            }
            for (int b = 0; b < traces[0].Count; b++)
            {
                if (counts[0][3 * b] != counts[1][3 * b]) return $"batch {b}: reference accounted {counts[0][3 * b]} steps, batched {counts[1][3 * b]}";
                if (counts[0][3 * b + 1] != counts[1][3 * b + 1]) return $"batch {b}: reference ran {counts[0][3 * b + 1]} real steps so far, batched {counts[1][3 * b + 1]}";
                if (counts[0][3 * b + 2] != counts[1][3 * b + 2]) return $"batch {b}: frame {counts[0][3 * b + 2]} vs {counts[1][3 * b + 2]}";
                uint[] a = traces[0][b], c = traces[1][b];
                for (int i = 0; i < a.Length; i++) if (a[i] != c[i]) return $"batch {b}: slot {i} reference {a[i]:X} batched {c[i]:X}";
            }
            long realTotal = counts[1][counts[1].Count - 2], stepsTotal = counts[1].Where((_, i) => i % 3 == 0).Sum();
            return realTotal < stepsTotal ? null : "no step was ever skipped: the comparison did not exercise the idle skipping";
        }
    }
}
