using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;

namespace DLS.Bench
{
    // RECORD CLOCK STEPS NEEDED (user, 2026-10-04): after every clock edge, the steps until nothing is pending; the largest
    // is how low "steps per clock tick" can go. Counted by SimProgram.SettleAfterStep (managed step) and
    // SimKernel.SettleAfterStep (batch loop in Burst).
    public static class SettleCases
    {
        const int Period = 70;

        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var list = new List<(string, Func<string>)>
            {
                ("clock steps needed: a clock driving one inverter settles in the edge step (1)", OneStep),
                ("clock steps needed: a bus conflict re-drawn every step never settles (reported as not settled, a full period)", NeverSettles),
                ("clock steps needed: nothing recorded while the clock is stopped", StoppedClock),
            };
            foreach (bool fast in new[] { false, true })
                if (chips.Any(c => c.Name == "CPU"))
                {
                    ChipDescription cpu = chips.First(c => c.Name == "CPU");
                    string tree = fast ? "the CPU's RUN FAST tree" : "the CPU";
                    list.Add(($"clock steps needed on {tree}: never below the steps its slots keep changing after an edge, at most 4 above", () => NotBelowChanges(lib, cpu, fast)));
                    list.Add(($"clock steps needed on {tree}: the batched Burst loop records what the reference loop records", () => KernelSameAsReference(lib, cpu, fast)));
                }
            return list;
        }

        static SimChip Build(ChipLibrary lib, ChipDescription d, bool fast, int seed)
        {
            SimChip root = fast ? FastBuilder.Build(d, lib, new FastCache(), new List<FastBuilder.Instance>()) : Simulator.BuildSimChip(d, lib);
            Simulator.ResetForTests(seed);
            Simulator.stepsPerClockTransition = Period;
            for (int i = 0; i < d.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
            Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio()); // compiled
            root.Program.ResetSettle();
            root.Program.RecordSettle = true;
            return root;
        }

        static string NotBelowChanges(ChipLibrary lib, ChipDescription d, bool fast)
        {
            try
            {
                SimChip root = Build(lib, d, fast, 41);
                var audio = new SimAudio();
                uint[] prev = (uint[])root.Program.states.Clone();
                int contiguousMax = 0; bool inRun = false;
                for (int s = 0; s < Period * 2 * 40; s++)
                {
                    Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                    uint[] st = root.Program.states;
                    bool changed = false;
                    for (int i = 0; i < st.Length; i++) if (st[i] != prev[i]) { changed = true; break; }
                    Array.Copy(st, prev, st.Length);
                    int off = Simulator.simulationFrame % Period;
                    if (off == 0) { inRun = true; contiguousMax = Math.Max(contiguousMax, 1); }
                    else if (inRun) { if (changed) contiguousMax = Math.Max(contiguousMax, off + 1); else inRun = false; }
                }
                SimProgram pr = root.Program;
                if (pr.SettleEdges < 70) return $"only {pr.SettleEdges} edges recorded in 40 clock cycles";
                if (pr.SettleUnsettled > 0) return $"{pr.SettleUnsettled} edges not settled within {Period} steps";
                if (pr.SettleMax < contiguousMax) return $"recorded {pr.SettleMax} steps, but the slots kept changing for {contiguousMax} steps after an edge";
                if (pr.SettleMax > contiguousMax + 4) return $"recorded {pr.SettleMax} steps, the slots stopped changing after {contiguousMax}";
                return null;
            }
            finally { Simulator.ClearTestSeed(); }
        }

        static string KernelSameAsReference(ChipLibrary lib, ChipDescription d, bool fast)
        {
            var res = new (int max, long edges, long uns)[2];
            for (int pass = 0; pass < 2; pass++)
            {
                try
                {
                    SimChip root = Build(lib, d, fast, 43);
                    var audio = new SimAudio();
                    var rnd = new Random(3);
                    for (int b = 0; b < 400; b++)
                    {
                        int n = 2 + rnd.Next(400), done = 0;
                        while (done < n) done += pass == 0 ? Simulator.RunSimulationStepsReference(root, Array.Empty<DevPinInstance>(), audio, n - done) : Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, n - done);
                    }
                    if (pass == 1 && !root.Program.CanRunBatchKernel) return "the batch kernel was not used";
                    res[pass] = (root.Program.SettleMax, root.Program.SettleEdges, root.Program.SettleUnsettled);
                }
                finally { Simulator.ClearTestSeed(); }
            }
            if (res[0].edges == 0) return "no edge recorded";
            return res[0] == res[1] ? null : $"reference loop: {res[0].max} steps max over {res[0].edges} edges ({res[0].uns} not settled); batched Burst loop: {res[1].max} over {res[1].edges} ({res[1].uns})";
        }

        // a builtins-only circuit with a running CLOCK, stepped both ways; returns the recorded (max, edges, unsettled)
        static string Circuit(string name, Action<UnitCases.Builder> build, Func<SimProgram, string> check, uint[] clockState = null)
        {
            foreach (bool batched in new[] { false, true })
            {
                try
                {
                    var c = UnitCases.Build(name, Array.Empty<string>(), new[] { "Q" }, build);
                    Simulator.stepsPerClockTransition = 10;
                    var audio = new SimAudio();
                    Simulator.RunSimulationStep(c.root, Array.Empty<DevPinInstance>(), audio);
                    c.root.Program.ResetSettle();
                    c.root.Program.RecordSettle = true;
                    int steps = 10 * 2 * 30;
                    if (batched) { int done = 0; while (done < steps) done += Simulator.RunSimulationSteps(c.root, Array.Empty<DevPinInstance>(), audio, Math.Min(64, steps - done)); }
                    else for (int s = 0; s < steps; s++) Simulator.RunSimulationStep(c.root, Array.Empty<DevPinInstance>(), audio);
                    string err = check(c.root.Program);
                    if (err != null) return (batched ? "batched: " : "single steps: ") + err;
                }
                finally { Simulator.ClearTestSeed(); }
            }
            return null;
        }

        static string OneStep() => Circuit("t_settle1", b =>
        {
            int clk = b.Add(ChipType.Clock), n = b.Add(ChipType.Nand);
            b.Wire(b.Out(clk, 0), b.In(n, 0)); b.Wire(b.Out(clk, 0), b.In(n, 1));
            b.Wire(b.Out(n, 0), b.Output("Q"));
        }, p => p.SettleEdges < 50 ? $"{p.SettleEdges} edges in 30 cycles" : p.SettleMax != 1 || p.SettleUnsettled != 0 ? $"recorded {p.SettleMax} ({p.SettleUnsettled} not settled), expected 1" : null);

        static string NeverSettles() => Circuit("t_settle_conflict", b =>
        {
            int clk = b.Add(ChipType.Clock), vcc = b.Add(ChipType.Vcc), gnd = b.Add(ChipType.Gnd), n = b.Add(ChipType.Nand);
            b.Wire(b.Out(vcc, 0), b.Output("Q")); // two drivers in conflict on Q: re-drawn at random every step
            b.Wire(b.Out(gnd, 0), b.Output("Q"));
            b.Wire(b.Out(clk, 0), b.In(n, 0)); b.Wire(b.Out(clk, 0), b.In(n, 1));
        }, p => p.SettleUnsettled == 0 ? $"a conflict re-drawn every step reported as settled ({p.SettleMax} steps over {p.SettleEdges} edges)" : p.SettleMax != 10 ? $"not settled but the max is {p.SettleMax}, expected the full period 10" : null);

        static string StoppedClock() => Circuit("t_settle_stopped", b =>
        {
            int clk = b.Add(ChipType.Clock), n = b.Add(ChipType.Nand);
            int i = b.subChips.FindIndex(s => s.ID == clk);
            SubChipDescription sd = b.subChips[i];
            sd.InternalData = new uint[] { 1, 0 }; // TURN OFF at level 0
            b.subChips[i] = sd;
            b.Wire(b.Out(clk, 0), b.In(n, 0)); b.Wire(b.Out(clk, 0), b.In(n, 1));
            b.Wire(b.Out(n, 0), b.Output("Q"));
        }, p => p.SettleEdges != 0 ? $"{p.SettleEdges} edges recorded with the clock stopped" : null);

        // the app: right-click > RECORD CLOCK STEPS NEEDED on a running project, read what the top right shows
        public static (string name, Func<string> run) Live(string projectDir) =>
            ("live project: RECORD CLOCK STEPS NEEDED on the running CPU (Max speed, RUN FAST too): edges counted, a max shown, STOP stops, a new record starts over", () => LiveRecord(projectDir));

        static string LiveRecord(string projectDir)
        {
            ChipLibrary lib = BenchProject.LoadLibrary(projectDir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(projectDir, "ProjectDescription.json")));
            pd.ProjectName = "_BenchSettle";
            pd.Prefs_SimPaused = false;
            pd.Prefs_SimMaxSpeed = true;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            try
            {
                p.LoadDevChipOrCreateNewIfDoesntExist("CPU");
                p.StartSimulation();
                bool Pump(Func<bool> ok, int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { p.TickMainThreadForTests(); if (ok()) return true; Thread.Sleep(5); } return ok(); }
                Pump(() => false, 300);
                if (p.SettleRecording || p.SettleEdgesRecorded != 0) return "recording before it was asked for";
                p.StartSettleRecord();
                if (!Pump(() => p.SettleEdgesRecorded > 200, 4000)) return $"only {p.SettleEdgesRecorded} edges recorded in 4 s";
                int perTick = p.stepsPerClockTransition;
                if (p.SettleMaxRecorded < 1 || p.SettleMaxRecorded > perTick) return $"max {p.SettleMaxRecorded} steps with {perTick} per tick";
                if (p.SettleUnsettledRecorded != 0) return $"{p.SettleUnsettledRecorded} edges not settled on the CPU (it settles in ~11 steps)";
                int gatesMax = p.SettleMaxRecorded;
                // RUN FAST: a new program, the record goes on
                long before = p.SettleEdgesRecorded;
                p.RunFastNowForTests();
                if (!p.FastModeActive) return "fast mode did not start: " + p.FastModeStatus;
                if (!Pump(() => p.SettleEdgesRecorded > before + 200, 4000)) return "no edge recorded after RUN FAST";
                if (p.SettleMaxRecorded < gatesMax) return "the max went down across RUN FAST";
                p.StopSettleRecord();
                Pump(() => false, 200);
                long stopped = p.SettleEdgesRecorded;
                Pump(() => false, 300);
                if (p.SettleEdgesRecorded != stopped) return "edges still counted after STOP";
                if (p.rootSimChip.Program.RecordSettle) return "the program still records after STOP";
                p.description.Prefs_SimPaused = true; // nothing counted meanwhile: the new record must read exactly 0
                Pump(() => false, 100);
                p.StartSettleRecord();
                if (p.SettleEdgesRecorded != 0 || p.SettleMaxRecorded != 0) return $"a new record did not start over ({p.SettleEdgesRecorded} edges, max {p.SettleMaxRecorded}; {stopped} edges before)";
                p.description.Prefs_SimPaused = false;
                if (!Pump(() => p.SettleEdgesRecorded > 0, 2000)) return "the new record counts nothing";
                return null;
            }
            finally { p.NotifyExit(); Thread.Sleep(30); }
        }
    }
}
