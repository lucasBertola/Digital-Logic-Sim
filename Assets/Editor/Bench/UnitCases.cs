using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEngine;

namespace DLS.Bench
{
    // Hand-written checks of the simulator's contract, on tiny circuits built in code. These are the rules a
    // rewrite of the core must keep. Each case returns null when it passes, else a message.
    public static class UnitCases
    {
        static readonly object buildLock = new();

        public static List<(string name, Func<string> run)> All(ChipLibrary fixtures, ChipDescription[] fixtureChips) => new()
        {
            ("nand truth table", NandTruthTable),
            ("combinational chain settles within one step", ChainSettlesInOneStep),
            ("sr latch (2 nand) holds its state", SrLatchHolds),
            ("unconnected input reads 0", UnconnectedInputReadsLow),
            ("vcc / gnd constants", VccGnd),
            ("tri-state: driven source wins over floating one", TriStateDrivenWins),
            ("tri-state: disabled buffer output takes its net's value", TriStateBufferTakesNet),
            ("tri-state: two enabled drivers in conflict give 0 or 1", TriStateConflict),
            ("floating line is noise (both values seen)", FloatingIsNoise),
            ("clock: level = ((frame / stepsPerClockTransition) & 1) == 0", ClockPattern),
            ("clock: forcedClockState pins every clock", ClockForced),
            ("key chip follows virtual keys", KeyChip),
            ("merge 1->4 then split 4->1 round trip", MergeSplitRoundTrip),
            ("same seed => same outputs on a sequential fixture (Bascule D)", () => Deterministic(fixtures, fixtureChips)),
            ("live modification: adding a NOT via the sim queue is applied", LiveModification),
            ("input pin values are saved with the chip and restored on load", () => InputValuesPersist(fixtures, fixtureChips)),
            ("batched stepping (idle skipping): clock + pulse identical to single stepping", BatchedClockPulse),
            ("batched stepping (idle skipping): a key press is seen", BatchedKey),
            ("batched stepping (idle skipping): clock running while an input moves (a game)", BatchedGameInput),
            ("batched stepping (idle skipping): a bus conflict keeps re-drawing", BatchedConflictNeverIdles),
            ("frame counter wrap keeps the clock phase", FrameWrapKeepsClockPhase),
        };

        // ---------------- helpers ----------------

        internal class Circuit
        {
            public readonly SimAudio audio = new();
            public ChipDescription desc;
            public ChipLibrary lib;
            public SimChip root, target;
            public Dictionary<string, int> inIdx = new(), outIdx = new();

            public void Set(string input, int value) => root.InputPins[inIdx[input]].State = PinState.Make((ushort)value, 0);
            public uint Out(string output) => target.OutputPins[outIdx[output]].State;
            public int OutBit(string output) => (int)(Out(output) & 1);
            public bool OutFloating(string output) => (PinState.GetTristateFlags(Out(output)) & 1) == 1;
            public void Step(int n = 1) { for (int i = 0; i < n; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio); }
            public int StepBatched(int n) { int done = 0; while (done < n) done += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, n - done); return done; }
            // value of a multi-bit output, -1 when any of its bits floats
            public int OutValue(string output)
            {
                int width = (int)desc.OutputPins[outIdx[output]].BitCount, mask = (1 << width) - 1;
                uint st = Out(output);
                return (PinState.GetTristateFlags(st) & mask) != 0 ? -1 : PinState.GetBitStates(st) & mask;
            }
        }

        // Builds a chip from lambdas and wraps it in the isolated harness
        // Pin names may carry a bit width: "DATA:8" (1, 4 or 8; default 1)
        internal static Circuit Build(string name, string[] inputs, string[] outputs, Action<Builder> build, ChipLibrary lib = null, int seed = 1)
        {
            lib ??= BenchProject.BuiltinsOnly();
            var b = new Builder(lib);
            b.inputPins = MakePins(inputs, true, ref b.nextID, out inputs);
            b.outputPins = MakePins(outputs, false, ref b.nextID, out outputs);
            build(b);
            ChipDescription desc = ChipEmitHelper.Assemble(name, Color.gray, NameDisplayLocation.Centre, Vector2.zero, b.inputPins, b.outputPins, b.subChips, b.wires);

            Simulator.ResetForTests(seed);
            Simulator.stepsPerClockTransition = 250;
            var c = new Circuit { desc = desc, lib = lib };
            lock (buildLock) c.root = CircuitTester.BuildIsolatedSim(desc, lib);
            c.target = CircuitTester.TargetOf(c.root);
            for (int i = 0; i < inputs.Length; i++) c.inIdx[inputs[i]] = i;
            for (int i = 0; i < outputs.Length; i++) c.outIdx[outputs[i]] = i;
            return c;
        }

        static PinDescription[] MakePins(string[] spec, bool isInput, ref int nextID, out string[] names)
        {
            names = spec.Select(s => s.Split(':')[0]).ToArray();
            PinDescription[] pins = ChipEmitHelper.MakePins(names, isInput, ref nextID);
            for (int i = 0; i < spec.Length; i++)
            {
                string[] parts = spec[i].Split(':');
                if (parts.Length > 1) pins[i].BitCount = parts[1] == "8" ? PinBitCount.Bit8 : parts[1] == "4" ? PinBitCount.Bit4 : PinBitCount.Bit1;
            }
            return pins;
        }

        internal class Builder
        {
            public readonly ChipLibrary lib;
            public int nextID = 1;
            public PinDescription[] inputPins, outputPins;
            public readonly List<SubChipDescription> subChips = new();
            public readonly List<WireDescription> wires = new();
            public Builder(ChipLibrary lib) { this.lib = lib; }

            public int Add(ChipType type) => ChipEmitHelper.AddSubChip(subChips, ref nextID, ChipTypeHelper.GetName(type), type);
            ChipDescription Desc(int id) => lib.GetChipDescription(subChips.First(s => s.ID == id).Name);
            public PinAddress In(int chipID, int pin) => new(chipID, Desc(chipID).InputPins[pin].ID);
            public PinAddress Out(int chipID, int pin) => new(chipID, Desc(chipID).OutputPins[pin].ID);
            public PinAddress Input(string name) => new(inputPins.First(p => p.Name == name).ID, 0);
            public PinAddress Output(string name) => new(outputPins.First(p => p.Name == name).ID, 0);
            public void Wire(PinAddress from, PinAddress to) => wires.Add(ChipEmitHelper.Wire(from, to));
            public uint[] Data(int chipID) => subChips.First(s => s.ID == chipID).InternalData; // ROM contents, pulse width, key...
        }

        static string Expect(bool cond, string msg) => cond ? null : msg;

        // ---------------- cases ----------------

        static string NandTruthTable()
        {
            var c = Build("t_nand", new[] { "A", "B" }, new[] { "Q" }, b =>
            {
                int n = b.Add(ChipType.Nand);
                b.Wire(b.Input("A"), b.In(n, 1)); // NAND pins: IN B (0), IN A (1)
                b.Wire(b.Input("B"), b.In(n, 0));
                b.Wire(b.Out(n, 0), b.Output("Q"));
            });
            foreach ((int a, int bb, int q) in new[] { (0, 0, 1), (0, 1, 1), (1, 0, 1), (1, 1, 0) })
            {
                c.Set("A", a); c.Set("B", bb); c.Step(2);
                if (c.OutBit("Q") != q) return $"NAND({a},{bb}) = {c.OutBit("Q")}, expected {q}";
            }
            return null;
        }

        static string ChainSettlesInOneStep()
        {
            // 7 NANDs used as inverters in a chain: a step processes chips as they become ready, so the
            // whole combinational chain settles within ONE simulation step.
            var c = Build("t_chain", new[] { "A" }, new[] { "Q" }, b =>
            {
                int prev = -1;
                for (int i = 0; i < 7; i++)
                {
                    int n = b.Add(ChipType.Nand);
                    PinAddress src = prev < 0 ? b.Input("A") : b.Out(prev, 0);
                    b.Wire(src, b.In(n, 0)); b.Wire(src, b.In(n, 1));
                    prev = n;
                }
                b.Wire(b.Out(prev, 0), b.Output("Q"));
            });
            c.Set("A", 0); c.Step(1);
            if (c.OutBit("Q") != 1) return $"chain of 7 inverters with A=0 gave {c.OutBit("Q")} after one step (expected 1)";
            c.Set("A", 1); c.Step(1);
            return Expect(c.OutBit("Q") == 0, $"chain with A=1 gave {c.OutBit("Q")} after one step (expected 0)");
        }

        static string SrLatchHolds()
        {
            var c = Build("t_sr", new[] { "S", "R" }, new[] { "Q" }, b =>
            {
                int n1 = b.Add(ChipType.Nand), n2 = b.Add(ChipType.Nand);
                b.Wire(b.Input("S"), b.In(n1, 1)); b.Wire(b.Out(n2, 0), b.In(n1, 0));
                b.Wire(b.Input("R"), b.In(n2, 1)); b.Wire(b.Out(n1, 0), b.In(n2, 0));
                b.Wire(b.Out(n1, 0), b.Output("Q"));
            });
            c.Set("S", 0); c.Set("R", 1); c.Step(4);
            if (c.OutBit("Q") != 1) return "set (S=0,R=1) did not give Q=1";
            c.Set("S", 1); c.Step(6);
            if (c.OutBit("Q") != 1) return "Q did not hold 1 with S=R=1";
            c.Set("R", 0); c.Step(4);
            if (c.OutBit("Q") != 0) return "reset (R=0) did not give Q=0";
            c.Set("R", 1); c.Step(6);
            return Expect(c.OutBit("Q") == 0, "Q did not hold 0 with S=R=1");
        }

        static string UnconnectedInputReadsLow()
        {
            var c = Build("t_unconnected", new[] { "A" }, new[] { "Q" }, b =>
            {
                int n = b.Add(ChipType.Nand);
                b.Wire(b.Input("A"), b.In(n, 1)); // IN B left unconnected -> reads 0 -> NAND = 1 whatever A
                b.Wire(b.Out(n, 0), b.Output("Q"));
            });
            for (int s = 0; s < 30; s++)
            {
                c.Set("A", s & 1); c.Step(1);
                if (c.OutBit("Q") != 1) return $"unconnected NAND input read as 1 at step {s + 1}";
            }
            return null;
        }

        static string VccGnd()
        {
            var c = Build("t_const", new string[0], new[] { "H", "L" }, b =>
            {
                int v = b.Add(ChipType.Vcc), g = b.Add(ChipType.Gnd);
                b.Wire(b.Out(v, 0), b.Output("H")); b.Wire(b.Out(g, 0), b.Output("L"));
            });
            c.Step(2);
            return Expect(c.OutBit("H") == 1 && c.OutBit("L") == 0, $"VCC={c.OutBit("H")} GND={c.OutBit("L")}");
        }

        // A NOT (NAND with tied inputs) fed from GND drives 1; a disabled buffer also feeds the same pin.
        static Circuit TriStateNet(string name)
        {
            return Build(name, new[] { "EN", "D" }, new[] { "Q", "BUF" }, b =>
            {
                int g = b.Add(ChipType.Gnd), inv = b.Add(ChipType.Nand), buf = b.Add(ChipType.TriStateBuffer), sink = b.Add(ChipType.Nand);
                b.Wire(b.Out(g, 0), b.In(inv, 0)); b.Wire(b.Out(g, 0), b.In(inv, 1));       // inv.OUT = NAND(0,0) = 1, driven
                b.Wire(b.Input("D"), b.In(buf, 0)); b.Wire(b.Input("EN"), b.In(buf, 1));    // buffer: IN, ENABLE
                b.Wire(b.Out(inv, 0), b.In(sink, 0)); b.Wire(b.Out(buf, 0), b.In(sink, 0)); // both on sink.IN B
                b.Wire(b.Out(inv, 0), b.In(sink, 1));                                        // sink.IN A = 1 -> sink = NOT(net)
                b.Wire(b.Out(sink, 0), b.Output("Q"));                                       // Q = NOT(net)
                b.Wire(b.Out(buf, 0), b.Output("BUF"));
            });
        }

        static string TriStateDrivenWins()
        {
            var c = TriStateNet("t_z_driven");
            c.Set("EN", 0); c.Set("D", 0);
            for (int s = 0; s < 40; s++)
            {
                c.Step(1);
                if (c.OutBit("Q") != 0) return $"net read as 0 at step {s + 1} although a driver holds it at 1 (Q=NOT(net)={c.OutBit("Q")})";
            }
            return null;
        }

        static string TriStateBufferTakesNet()
        {
            var c = TriStateNet("t_z_buf");
            c.Set("EN", 0); c.Set("D", 0);
            c.Step(3);
            for (int s = 0; s < 20; s++)
            {
                c.Step(1);
                if (c.OutBit("BUF") != 1) return $"disabled buffer output read {c.OutBit("BUF")} at step {s + 4}, expected the net's value 1";
                if (!c.OutFloating("BUF")) return "disabled buffer output is not flagged as floating";
            }
            return null;
        }

        static string TriStateConflict()
        {
            var c = TriStateNet("t_z_conflict");
            c.Set("EN", 1); c.Set("D", 0); // buffer drives 0 against the inverter's 1
            int zeros = 0, ones = 0;
            for (int s = 0; s < 60; s++)
            {
                c.Step(1);
                if (c.OutFloating("Q")) return "output of a NAND fed by a conflict is flagged floating";
                if (c.OutBit("Q") == 0) zeros++; else ones++;
            }
            return Expect(zeros + ones == 60, "conflict produced an invalid value");
        }

        static string FloatingIsNoise()
        {
            var c = Build("t_floating", new[] { "EN", "D" }, new[] { "Q" }, b =>
            {
                int buf = b.Add(ChipType.TriStateBuffer), inv = b.Add(ChipType.Nand);
                b.Wire(b.Input("D"), b.In(buf, 0)); b.Wire(b.Input("EN"), b.In(buf, 1));
                b.Wire(b.Out(buf, 0), b.In(inv, 0)); b.Wire(b.Out(buf, 0), b.In(inv, 1));
                b.Wire(b.Out(inv, 0), b.Output("Q"));
            });
            c.Set("EN", 0); c.Set("D", 1);
            int zeros = 0, ones = 0;
            // a gate reading a floating line re-draws its noise about every SimProgram.NoisePeriod steps (user rule:
            // "it may change randomly about once a second"), so sample long enough to see several re-draws
            int steps = SimProgram.NoisePeriod * 20;
            for (int s = 0; s < steps; s++) { c.Step(1); if (c.OutBit("Q") == 0) zeros++; else ones++; }
            return Expect(zeros > 0 && ones > 0, $"a gate fed by a floating line gave a constant value over {steps} steps ({zeros} zeros, {ones} ones)");
        }

        static string ClockPattern()
        {
            var c = Build("t_clock", new string[0], new[] { "C" }, b =>
            {
                int clk = b.Add(ChipType.Clock);
                b.Wire(b.Out(clk, 0), b.Output("C"));
            });
            Simulator.stepsPerClockTransition = 3;
            Simulator.forcedClockState = -1;
            for (int s = 1; s <= 24; s++)
            {
                c.Step(1); // frame == s during this step
                int expected = ((s / 3) & 1) == 0 ? 1 : 0;
                if (c.OutBit("C") != expected) return $"clock level at frame {s} = {c.OutBit("C")}, expected {expected} (stepsPerClockTransition=3)";
            }
            return null;
        }

        static string ClockForced()
        {
            var c = Build("t_clock_forced", new string[0], new[] { "C" }, b =>
            {
                int clk = b.Add(ChipType.Clock);
                b.Wire(b.Out(clk, 0), b.Output("C"));
            });
            try
            {
                Simulator.forcedClockState = 1;
                for (int s = 0; s < 10; s++) { c.Step(1); if (c.OutBit("C") != 1) return "forced HIGH clock read 0"; }
                Simulator.forcedClockState = 0;
                for (int s = 0; s < 10; s++) { c.Step(1); if (c.OutBit("C") != 0) return "forced LOW clock read 1"; }
            }
            finally { Simulator.forcedClockState = -1; }
            return null;
        }

        static string KeyChip()
        {
            var c = Build("t_key", new string[0], new[] { "K" }, b =>
            {
                int k = b.Add(ChipType.Key);
                b.subChips[^1].InternalData[0] = 'A';
                b.Wire(b.Out(k, 0), b.Output("K"));
            });
            try
            {
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char>());
                c.Step(2);
                if (c.OutBit("K") != 0) return "KEY read 1 while released";
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char> { 'A' });
                c.Step(2);
                if (c.OutBit("K") != 1) return "KEY read 0 while its key is held";
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char> { 'B' });
                c.Step(2);
                return Expect(c.OutBit("K") == 0, "KEY bound to A reacted to B");
            }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
        }

        static string MergeSplitRoundTrip()
        {
            var c = Build("t_mergesplit", new[] { "A", "B", "C", "D" }, new[] { "QA", "QB", "QC", "QD" }, b =>
            {
                int m = b.Add(ChipType.Merge_1To4Bit), s = b.Add(ChipType.Split_4To1Bit);
                // merge inputs are (IN D, IN C, IN B, IN A); split outputs (OUT D, OUT C, OUT B, OUT A)
                b.Wire(b.Input("D"), b.In(m, 0)); b.Wire(b.Input("C"), b.In(m, 1)); b.Wire(b.Input("B"), b.In(m, 2)); b.Wire(b.Input("A"), b.In(m, 3));
                b.Wire(b.Out(m, 0), b.In(s, 0));
                b.Wire(b.Out(s, 0), b.Output("QD")); b.Wire(b.Out(s, 1), b.Output("QC")); b.Wire(b.Out(s, 2), b.Output("QB")); b.Wire(b.Out(s, 3), b.Output("QA"));
            });
            for (int v = 0; v < 16; v++)
            {
                c.Set("A", v & 1); c.Set("B", (v >> 1) & 1); c.Set("C", (v >> 2) & 1); c.Set("D", (v >> 3) & 1);
                c.Step(2);
                int got = c.OutBit("QA") | (c.OutBit("QB") << 1) | (c.OutBit("QC") << 2) | (c.OutBit("QD") << 3);
                if (got != v) return $"merge/split of {v} gave {got}";
            }
            return null;
        }

        static string Deterministic(ChipLibrary fixtures, ChipDescription[] chips)
        {
            ChipDescription desc = chips.FirstOrDefault(c => ChipDescription.NameMatch(c.Name, "Bascule D")) ?? chips.FirstOrDefault(c => c.SubChips.Length > 0);
            if (desc == null) return "no fixture chip available";
            string a = Trace(desc, fixtures, 7), bb = Trace(desc, fixtures, 7);
            return Expect(a == bb, "two runs with the same seed gave different output traces");
        }

        static string Trace(ChipDescription desc, ChipLibrary lib, int seed)
        {
            Simulator.ResetForTests(seed);
            SimChip root;
            lock (buildLock) root = CircuitTester.BuildIsolatedSim(desc, lib);
            SimChip target = CircuitTester.TargetOf(root);
            var stim = new System.Random(99);
            var sb = new System.Text.StringBuilder();
            try
            {
                for (int s = 0; s < 40; s++)
                {
                    for (int i = 0; i < root.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)(stim.Next() & 1), 0);
                    Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                    foreach (SimPin p in target.OutputPins) sb.Append(p.State).Append(',');
                }
            }
            finally { Simulator.ClearTestSeed(); }
            return sb.ToString();
        }

        // The value the user sets on an input pin is part of the chip: it survives save + reload (user request).
        static string InputValuesPersist(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription desc = chips.FirstOrDefault(c => ChipDescription.NameMatch(c.Name, "Add4"));
            if (desc == null) return "fixture chip Add4 missing";
            (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(desc, lib);
            if (failed) return "Add4 failed to load";
            DevPinInstance[] ins = dev.GetInputPins();
            ins[0].Pin.PlayerInputState = 1;
            ins[2].Pin.PlayerInputState = 1;
            // save (through the real serializer) and load again
            string json = Saver.CreateSerializedChipDescription(DescriptionCreator.CreateChipDescription(dev));
            ChipDescription back = Serializer.DeserializeChipDescription(json);
            (DevChipInstance dev2, bool failed2) = DevChipInstance.LoadFromDescriptionTest(back, lib);
            if (failed2) return "reloaded Add4 failed to load";
            foreach (DevPinInstance p in dev2.GetInputPins())
            {
                uint expected = p.ID == ins[0].ID || p.ID == ins[2].ID ? 1u : 0u;
                if (p.Pin.PlayerInputState != expected) return $"input {p.Name} reloaded as {p.Pin.PlayerInputState}, expected {expected}";
            }
            return null;
        }

        // ---- batched stepping: the live sim thread skips idle steps; the state must be the one single steps give ----
        static Circuit ClockPulseCircuit() => Build("t_batch_cp", new string[0], new[] { "C", "P" }, b =>
        {
            int clk = b.Add(ChipType.Clock), p = b.Add(ChipType.Pulse);
            b.Data(p)[0] = 3;
            b.Wire(b.Out(clk, 0), b.Output("C"));
            b.Wire(b.Out(clk, 0), b.In(p, 0));
            b.Wire(b.Out(p, 0), b.Output("P"));
        });

        static string BatchedClockPulse()
        {
            // clock period 10 steps per level: rising edge at step 21 (frame 20), pulse high for 3 steps
            foreach (int steps in new[] { 5, 12, 21, 22, 24, 25, 40, 61 })
            {
                Circuit single = ClockPulseCircuit(); Simulator.stepsPerClockTransition = 10; single.Step(steps);
                Circuit batched = ClockPulseCircuit(); Simulator.stepsPerClockTransition = 10;
                int done = batched.StepBatched(steps);
                if (done != steps) return $"batched run accounted {done} steps instead of {steps}";
                if (single.OutBit("C") != batched.OutBit("C") || single.OutBit("P") != batched.OutBit("P"))
                    return $"after {steps} steps: single C={single.OutBit("C")} P={single.OutBit("P")}, batched C={batched.OutBit("C")} P={batched.OutBit("P")}";
            }
            return null;
        }

        // A game: the clock runs, and an outside element (the player) moves an input at arbitrary moments.
        // The circuit: N = NAND(A, clock), P = a 3-step pulse on N's rising edges, L = an SR latch set by A and
        // reset by the clock. Played once step by step and once in batches, with the input changed at the same
        // step numbers; every checkpoint must agree.
        static Circuit GameCircuit() => Build("t_batch_game", new[] { "A" }, new[] { "N", "P", "L" }, b =>
        {
            int clk = b.Add(ChipType.Clock), n = b.Add(ChipType.Nand), p = b.Add(ChipType.Pulse);
            b.Data(p)[0] = 3;
            b.Wire(b.Input("A"), b.In(n, 0)); b.Wire(b.Out(clk, 0), b.In(n, 1));
            b.Wire(b.Out(n, 0), b.Output("N"));
            b.Wire(b.Out(n, 0), b.In(p, 0)); b.Wire(b.Out(p, 0), b.Output("P"));
            // SR latch (active-low inputs): S = NAND(A, A) i.e. NOT A ; R = NAND(clock, clock)
            int notA = b.Add(ChipType.Nand), notC = b.Add(ChipType.Nand), q = b.Add(ChipType.Nand), qn = b.Add(ChipType.Nand);
            b.Wire(b.Input("A"), b.In(notA, 0)); b.Wire(b.Input("A"), b.In(notA, 1));
            b.Wire(b.Out(clk, 0), b.In(notC, 0)); b.Wire(b.Out(clk, 0), b.In(notC, 1));
            b.Wire(b.Out(notA, 0), b.In(q, 0)); b.Wire(b.Out(qn, 0), b.In(q, 1));
            b.Wire(b.Out(notC, 0), b.In(qn, 0)); b.Wire(b.Out(q, 0), b.In(qn, 1));
            b.Wire(b.Out(q, 0), b.Output("L"));
        });

        static string BatchedGameInput()
        {
            // (step, value of A): the player's moves, at moments chosen to fall before, on and after clock edges
            var moves = new (int step, int a)[] { (0, 0), (3, 1), (9, 0), (10, 1), (17, 0), (23, 1), (24, 1), (31, 0), (44, 1), (52, 0), (75, 1) };
            var checkpoints = new List<int> { 5, 9, 10, 11, 13, 19, 21, 23, 25, 27, 33, 40, 47, 60, 77, 90 };
            string Play(bool batched, Dictionary<int, string> log)
            {
                Circuit c = GameCircuit(); Simulator.stepsPerClockTransition = 10;
                int step = 0, m = 0;
                var stops = new SortedSet<int>(checkpoints);
                foreach (var mv in moves) stops.Add(mv.step);
                foreach (int stop in stops)
                {
                    int run = stop - step;
                    if (run > 0) { if (batched) { int done = c.StepBatched(run); if (done != run) return $"batched run accounted {done} of {run}"; } else c.Step(run); step = stop; }
                    while (m < moves.Length && moves[m].step == stop) { c.Set("A", moves[m].a); m++; }
                    if (checkpoints.Contains(stop)) log[stop] = $"N={c.OutBit("N")} P={c.OutBit("P")} L={c.OutBit("L")}";
                }
                return null;
            }
            var single = new Dictionary<int, string>(); var batch = new Dictionary<int, string>();
            string err = Play(false, single) ?? Play(true, batch);
            if (err != null) return err;
            foreach (int cp in checkpoints)
                if (single[cp] != batch[cp]) return $"at step {cp}: single {single[cp]}, batched {batch[cp]}";
            return null;
        }

        static string BatchedKey()
        {
            var c = Build("t_batch_key", new string[0], new[] { "Q" }, b =>
            {
                int k = b.Add(ChipType.Key);
                b.Data(k)[0] = 'K';
                b.Wire(b.Out(k, 0), b.Output("Q"));
            });
            try
            {
                SimKeyboardHelper.SetVirtualKeys(new System.Collections.Generic.HashSet<char>());
                c.StepBatched(300);
                if (c.OutBit("Q") != 0) return "key output high while no key is held";
                SimKeyboardHelper.SetVirtualKeys(new System.Collections.Generic.HashSet<char> { 'K' }); // pressed between two batches
                c.StepBatched(300);
                if (c.OutBit("Q") != 1) return "key press not seen by batched stepping";
                SimKeyboardHelper.SetVirtualKeys(new System.Collections.Generic.HashSet<char>());
                c.StepBatched(300);
                if (c.OutBit("Q") != 0) return "key release not seen by batched stepping";
                return null;
            }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
        }

        static string BatchedConflictNeverIdles()
        {
            // two enabled buffers drive 0 and 1 on one pin: the conflict is re-drawn every step, so over a
            // batch every step really runs and both values are seen (the frame count is exact either way)
            var c = Build("t_batch_conflict", new[] { "A", "B", "E" }, new[] { "Q" }, b =>
            {
                int bufA = b.Add(ChipType.TriStateBuffer), bufB = b.Add(ChipType.TriStateBuffer);
                b.Wire(b.Input("A"), b.In(bufA, 0)); b.Wire(b.Input("E"), b.In(bufA, 1));
                b.Wire(b.Input("B"), b.In(bufB, 0)); b.Wire(b.Input("E"), b.In(bufB, 1));
                b.Wire(b.Out(bufA, 0), b.Output("Q")); b.Wire(b.Out(bufB, 0), b.Output("Q"));
            });
            c.Set("A", 0); c.Set("B", 1); c.Set("E", 1);
            int zeros = 0, ones = 0;
            for (int i = 0; i < 64; i++) { c.StepBatched(1); if (c.OutBit("Q") == 0) zeros++; else ones++; }
            if (zeros == 0 || ones == 0) return $"conflict resolved the same way 64 times ({zeros} zeros, {ones} ones)";
            int before = Simulator.simulationFrame;
            int done = c.StepBatched(200);
            return Expect(done == 200 && Simulator.simulationFrame == before + 200, "batched stepping did not account exactly 200 steps");
        }

        static string FrameWrapKeepsClockPhase()
        {
            // the clock level depends on frame / period: wrapping the counter must not change it
            Circuit c = ClockPulseCircuit(); Simulator.stepsPerClockTransition = 10;
            Simulator.simulationFrame = Simulator.FrameWrapAt - 7;
            var levels = new System.Text.StringBuilder();
            long unwrapped = Simulator.FrameWrapAt - 7;
            for (int i = 0; i < 40; i++)
            {
                c.Step(1); unwrapped++;
                int expected = ((unwrapped / 10) & 1) == 0 ? 1 : 0;
                if (c.OutBit("C") != expected) return $"step {i + 1}: clock {c.OutBit("C")}, expected {expected} (frame counter {Simulator.simulationFrame})";
                levels.Append(c.OutBit("C"));
            }
            if (Simulator.simulationFrame >= Simulator.FrameWrapAt) return "frame counter was not wrapped";
            return null;
        }

        // The live editor mutates a running sim through Simulator.AddSubChip/AddConnection + ApplyModifications.
        static string LiveModification()
        {
            var c = Build("t_live", new[] { "A" }, new[] { "Q" }, b =>
            {
                b.Wire(b.Input("A"), b.Output("Q")); // Q = A
            });
            c.Set("A", 1); c.Step(2);
            if (c.OutBit("Q") != 1) return "initial pass-through failed";

            // Insert a NOT (NAND tied) between A and Q: remove A->Q, add NAND, wire A->NAND, NAND->Q
            ChipDescription nand = c.lib.GetChipDescription(ChipTypeHelper.GetName(ChipType.Nand));
            int nandID = 777;
            PinAddress a = new(c.desc.InputPins[0].ID, 0), q = new(c.desc.OutputPins[0].ID, 0);
            Simulator.RemoveConnection(c.target, a, q);
            Simulator.AddSubChip(c.target, nand, c.lib, nandID, null);
            Simulator.AddConnection(c.target, a, new PinAddress(nandID, nand.InputPins[0].ID));
            Simulator.AddConnection(c.target, a, new PinAddress(nandID, nand.InputPins[1].ID));
            Simulator.AddConnection(c.target, new PinAddress(nandID, nand.OutputPins[0].ID), q);
            Simulator.ApplyModifications();

            c.Set("A", 1); c.Step(3);
            if (c.OutBit("Q") != 0) return $"after inserting a NOT live, Q={c.OutBit("Q")} for A=1 (expected 0)";
            c.Set("A", 0); c.Step(3);
            return Expect(c.OutBit("Q") == 1, $"after inserting a NOT live, Q={c.OutBit("Q")} for A=0 (expected 1)");
        }
    }
}
