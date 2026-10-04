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
            // RUN FAST tree: its models (tables, registers, RAM) run in the kernel, floating bus reads (noise) included
            if (chips.Any(c => c.Name == "CPU"))
                list.Add(("burst stepper = managed stepper on the RUN FAST tree of the CPU (models run by the kernel, noise included)", () => Same(lib, chips.First(c => c.Name == "CPU"), true, 6000, true)));
            // A circuit with nothing for the kernel is found not worth it: the kernel is switched off and runs one step in 256
            // to re-measure. The circuit carrying this check changes as the kernel learns gate types: the RUN FAST tree until
            // 2026-10-03, then Tampon8 (3-state buffers + split / merge) until 2026-10-04, now ROMs and pulses.
            list.Add(("burst stepper = managed stepper on ROMs and pulses (nothing for the kernel: switched off and sampled)", NothingForTheKernel));
            list.Add(("a pin fed by two 3-state buffers takes its colour from the enabled one, in the batched Burst loop as in the managed one", MergeWinnerColour));
            // since 2026-10-04 the kernel runs 3-state buffers, splits / merges, merge nodes, clocks, VCC / GND and RAM65536s too
            if (chips.Any(c => c.Name == "Tampon8"))
                list.Add(("burst stepper = managed stepper on Tampon8 (3-state buffers, split / merge: now run by the kernel)", () => Same(lib, chips.First(c => c.Name == "Tampon8"), false, 6000)));
            return list;
        }

        // the merge node now runs in the kernel, which only records the winning source; the SimPins (wire colouring) are
        // updated from it after each kernel call
        static string MergeWinnerColour()
        {
            foreach (bool burst in new[] { true, false })
            {
                SimProgram.UseBurst = burst;
                try
                {
                    int buf1 = 0, buf2 = 0;
                    var c = UnitCases.Build("t_winner", new[] { "D1", "D2", "E1", "E2" }, new[] { "Q" }, b =>
                    {
                        buf1 = b.Add(ChipType.TriStateBuffer); buf2 = b.Add(ChipType.TriStateBuffer);
                        b.Wire(b.Input("D1"), b.In(buf1, 0)); b.Wire(b.Input("E1"), b.In(buf1, 1));
                        b.Wire(b.Input("D2"), b.In(buf2, 0)); b.Wire(b.Input("E2"), b.In(buf2, 1));
                        b.Wire(b.Out(buf1, 0), b.Output("Q"));
                        b.Wire(b.Out(buf2, 0), b.Output("Q"));
                    });
                    SimPin q = c.target.OutputPins[c.outIdx["Q"]];
                    foreach ((int e1, int e2, int d1, int d2, int want, int value) in new[] { (1, 0, 1, 0, 1, 1), (0, 1, 1, 0, 2, 0), (1, 0, 0, 1, 1, 0), (0, 1, 0, 1, 2, 1) })
                    {
                        c.Set("E1", e1); c.Set("E2", e2); c.Set("D1", d1); c.Set("D2", d2);
                        c.StepBatched(600);
                        int wantChip = want == 1 ? buf1 : buf2;
                        if (q.latestSourceParentChipID != wantChip) return $"{(burst ? "burst" : "managed")}: E1={e1} E2={e2}: Q coloured from chip {q.latestSourceParentChipID}, expected the enabled buffer {wantChip}";
                        if (c.OutBit("Q") != value || c.OutFloating("Q")) return $"{(burst ? "burst" : "managed")}: E1={e1} E2={e2}: Q = {c.Out("Q"):X}, expected a driven {value}";
                    }
                }
                finally { SimProgram.UseBurst = true; Simulator.ClearTestSeed(); }
            }
            return null;
        }

        static string NothingForTheKernel()
        {
            var c = UnitCases.Build("t_nokernel", new[] { "ADDR:8", "A" }, new[] { "HI:8", "LO:8", "Q" }, b =>
            {
                int r1 = b.Add(ChipType.Rom_256x16), r2 = b.Add(ChipType.Rom_256x16), p = b.Add(ChipType.Pulse);
                uint[] d1 = b.Data(r1), d2 = b.Data(r2);
                for (int i = 0; i < 256; i++) { d1[i] = (uint)(i * 37 + 11) & 0xFFFF; d2[i] = (uint)(i * 91 + 5) & 0xFFFF; }
                b.Data(p)[0] = 3;
                b.Wire(b.Input("ADDR"), b.In(r1, 0));
                b.Wire(b.Out(r1, 1), b.In(r2, 0)); // the second ROM reads the first one's low byte
                b.Wire(b.Out(r2, 0), b.Output("HI"));
                b.Wire(b.Out(r2, 1), b.Output("LO"));
                b.Wire(b.Input("A"), b.In(p, 0));
                b.Wire(b.Out(p, 0), b.Output("Q"));
            });
            return Same(c.lib, c.desc, false, 6000, false, true);
        }

        static string Same(ChipLibrary lib, ChipDescription d, bool fast, int steps, bool modelsInKernel = false, bool expectSwitchOff = false)
        {
            var traces = new List<uint[]>[2];
            var runs = new List<int>[2];
            int kernelRuns = 0, modelKernelRuns = 0; bool switchedOff = false;
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
                        if (burst) { kernelRuns += root.Program.KernelRunsLastStep; switchedOff |= root.Program.KernelSwitchedOff; if (fast) modelKernelRuns += root.Program.KernelRunsLastStep; }
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
            if (expectSwitchOff) return switchedOff ? null : "a circuit with nothing for the kernel never switched it off";
            if (modelsInKernel && modelKernelRuns == 0) return "the kernel never ran a model";
            return kernelRuns > 0 ? null : "the Burst kernel never ran a gate (the comparison compared the managed loop with itself)";
        }
    }
}
