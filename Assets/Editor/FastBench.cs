using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;

// RUN FAST speed on a save-folder chip: gates vs fast tree, batched like the live sim thread, inputs as saved, the
// project's steps per tick (read-only):  Unity.exe -projectPath <proj> -executeMethod FastBench.Run -benchChip CPU_2 -quit
public static class FastBench
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string chip = Environment.GetCommandLineArgs().SkipWhile(a => a != "-benchChip").Skip(1).FirstOrDefault() ?? "CPU_2";
            string dir = SavePaths.GetProjectPath("PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out ChipDescription[] chips);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            ChipDescription d = chips.First(c => c.Name == chip);
            var cache = new FastCache();
            var sw = Stopwatch.StartNew();
            var inst = new List<FastBuilder.Instance>();
            SimChip fast = FastBuilder.Build(d, lib, cache, inst);
            double buildMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>());
            double cachedMs = sw.Elapsed.TotalMilliseconds;
            sb.Append($"kernel compiled by Burst: {DLS.Simulation.SimKernel.CompiledByBurst() == 1}; ");
            sb.Append($"{chip}: fast tree built in {buildMs:0} ms ({cachedMs:0} ms with the cache); models: {string.Join(", ", inst.GroupBy(i => i.Desc.Name + (i.Leaf.Model is LutModel ? " (table)" : " (model)")).Select(g => g.Count() + " x " + g.Key))}\n");
            SimChip gates = Simulator.BuildSimChip(d, lib);
            foreach ((string name, SimChip root) in new[] { ("gates", gates), ("fast", fast) })
            {
                Simulator.ResetForTests(5);
                Simulator.stepsPerClockTransition = pd.Prefs_SimStepsPerClockTick;
                for (int i = 0; i < d.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
                var audio = new SimAudio();
                for (int i = 0; i < 2000; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                long real0 = Simulator.RealSteps;
                const int n = 3000000;
                sw.Restart();
                for (int i = 0; i < n;) i += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, Math.Min(256, n - i));
                double s = sw.Elapsed.TotalSeconds;
                sb.Append($"  {name}: {root.Program.GateCount} gates, {n / s:0} steps/s = {n / s / (2.0 * pd.Prefs_SimStepsPerClockTick) / 1000:0.0} kHz, real steps {(Simulator.RealSteps - real0) * 100.0 / n:0.00} %\n");
                {
                    // profile: gate runs per type over 200 000 batched steps
                    root.Program.CollectStats = true;
                    Array.Clear(root.Program.RunsByType, 0, 256);
                    long r0 = Simulator.RealSteps;
                    for (int i = 0; i < 200000;) i += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, Math.Min(256, 200000 - i));
                    root.Program.CollectStats = false;
                    long real = Simulator.RealSteps - r0; double halfs = 200000.0 / pd.Prefs_SimStepsPerClockTick;
                    long[] rt = root.Program.RunsByType;
                    sb.Append($"    per half-period: {real / halfs:0.0} real steps, {rt.Sum() / halfs:0.0} gate runs: " + string.Join(", ", Enumerable.Range(0, 256).Where(k => rt[k] > 0).OrderByDescending(k => rt[k]).Select(k => (k == 254 ? "NandNot" : k == 253 ? "Nop" : k == 255 ? "Merge" : ((ChipType)k).ToString()) + " " + (rt[k] / halfs).ToString("0.0"))) + "\n");
                }
                if (name == "fast")
                {
                    // where a real step's time goes: the models' Run, the idle check, the fixed cost of a step
                    var swp = Stopwatch.StartNew();
                    foreach (var grp in root.SubChips.Where(c => c.Model != null).GroupBy(c => c.Model.GetType().Name))
                    {
                        SimChip[] cs = grp.ToArray();
                        swp.Restart();
                        const int reps = 200000;
                        for (int r = 0; r < reps; r++) foreach (SimChip c in cs) c.Model.Run();
                        sb.Append($"    {grp.Key}.Run: {swp.Elapsed.TotalMilliseconds * 1e6 / (reps * (double)cs.Length):0} ns each ({cs.Length} models)\n");
                        foreach (SimChip c in cs) c.Model.RerunNextStep = false;
                    }
                    swp.Restart();
                    for (int r = 0; r < 1000000; r++) root.Program.IdleSteps(256);
                    sb.Append($"    IdleSteps: {swp.Elapsed.TotalMilliseconds:0} ns each\n");
                    Simulator.forcedClockState = 0;
                    for (int r = 0; r < 100; r++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                    swp.Restart();
                    for (int r = 0; r < 1000000; r++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                    sb.Append($"    RunSimulationStep with nothing to run: {swp.Elapsed.TotalMilliseconds:0} ns each ({root.Program.GatesRunLastStep} gates in the last)\n");
                    swp.Restart();
                    for (int r = 0; r < 1000000; r++) root.Program.Step(audio);
                    sb.Append($"    Program.Step with nothing to run: {swp.Elapsed.TotalMilliseconds:0} ns each\n");
                    Simulator.forcedClockState = -1;
                    // A/B in the same run (the machine's speed varies between runs): reference loop vs batched loop
                    double[] bestAB = new double[3];
                    for (int round = 0; round < 9; round++)
                    {
                        int mode = round % 3; DLS.Simulation.SimProgram.UseBurst = mode != 1;
                        SimChip ab = FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>());
                        Simulator.ResetForTests(5);
                        Simulator.stepsPerClockTransition = pd.Prefs_SimStepsPerClockTick;
                        for (int q = 0; q < d.InputPins.Length; q++) ab.InputPins[q].State = PinState.Make((ushort)d.InputPins[q].InputState, 0);
                        for (int q = 0; q < 2000; q++) Simulator.RunSimulationStep(ab, Array.Empty<DevPinInstance>(), audio);
                        long realAB = Simulator.RealSteps;
                        swp.Restart();
                        for (int i = 0; i < 3000000;)
                            i += mode == 0 ? Simulator.RunSimulationStepsReference(ab, Array.Empty<DevPinInstance>(), audio, Math.Min(256, 3000000 - i))
                                           : Simulator.RunSimulationSteps(ab, Array.Empty<DevPinInstance>(), audio, Math.Min(256, 3000000 - i));
                        bestAB[mode] = Math.Max(bestAB[mode], 3000000 / swp.Elapsed.TotalSeconds);
                        if (round >= 6) sb.Append($"      mode {mode}: real steps {(Simulator.RealSteps - realAB) * 100.0 / 3000000:0.00} %, kernel switched off {ab.Program.KernelSwitchedOff}" + "\n");
                    }
                    DLS.Simulation.SimProgram.UseBurst = true;
                    {
                        SimChip ab = FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>());
                        Simulator.ResetForTests(5);
                        Simulator.stepsPerClockTransition = pd.Prefs_SimStepsPerClockTick;
                        for (int q = 0; q < d.InputPins.Length; q++) ab.InputPins[q].State = PinState.Make((ushort)d.InputPins[q].InputState, 0);
                        for (int q = 0; q < 2000; q++) Simulator.RunSimulationStep(ab, Array.Empty<DevPinInstance>(), audio);
                        Simulator.ProfFirst = Simulator.ProfIdle = Simulator.ProfStep = Simulator.ProfBatches = Simulator.ProfSteps = 0; Simulator.ProfKernel = Simulator.ProfKernelCalls = 0; Array.Clear(Simulator.ProfBailTypes, 0, 256);
                        Simulator.Profile = true;
                        swp.Restart();
                        for (int i = 0; i < 3000000;) i += Simulator.RunSimulationSteps(ab, Array.Empty<DevPinInstance>(), audio, Math.Min(256, 3000000 - i));
                        double total = swp.Elapsed.TotalMilliseconds;
                        Simulator.Profile = false;
                        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
                        double halfs = 3000000.0 / pd.Prefs_SimStepsPerClockTick;
                        sb.Append($"    profile per half-period ({total * 1e6 / halfs:0} ns): first step of a batch {ms(Simulator.ProfFirst) * 1e6 / halfs:0} ns ({Simulator.ProfBatches / halfs:0.00} per half), Program.Step {ms(Simulator.ProfStep) * 1e6 / halfs:0} ns ({Simulator.ProfSteps / halfs:0.00} per half), IdleSteps {ms(Simulator.ProfIdle) * 1e6 / halfs:0} ns, rest {(total - ms(Simulator.ProfFirst) - ms(Simulator.ProfStep) - ms(Simulator.ProfIdle)) * 1e6 / halfs:0} ns; in Step: kernel calls {Simulator.ProfKernelCalls / halfs:0.00} per half taking {ms(Simulator.ProfKernel) * 1e6 / halfs:0} ns; handed back: " + string.Join(", ", Enumerable.Range(0, 256).Where(x => Simulator.ProfBailTypes[x] > 0).Select(x => (x == 255 ? "Merge" : ((ChipType)x).ToString()) + " " + (Simulator.ProfBailTypes[x] / halfs).ToString("0.00"))) + "\n");
                    }
                    sb.Append($"    A/B: reference loop {bestAB[0]:0} steps/s, batched without Burst {bestAB[1]:0}, batched with Burst {bestAB[2]:0} (x{bestAB[2] / bestAB[0]:0.00}); kernel switched off: {root.Program.KernelSwitchedOff}, kernel ran {root.Program.KernelRunsLastStep} of {root.Program.GatesRunLastStep} gates in the last step" + "\n");
                }
            }
            // equivalence on THIS chip: gates and fast run the same clock cycles; every top-level component's outputs
            // are compared just before each clock edge (settled points)
            {
                int period = pd.Prefs_SimStepsPerClockTick;
                SimChip ga = Simulator.BuildSimChip(d, lib), fa = FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>());
                // the gates start from the chip's saved memory; the fast tree takes it over exactly as RUN FAST does
                Simulator.ResetForTests(9);
                Simulator.stepsPerClockTransition = period;
                for (int i = 0; i < d.InputPins.Length; i++) ga.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
                Simulator.RunSimulationStep(ga, Array.Empty<DevPinInstance>(), new SimAudio());
                FastState.CopyIn(ga, fa, lib);
                var traces = new List<List<string>>();
                var early = new List<string>();
                foreach (SimChip root in new[] { ga, fa })
                {
                    Simulator.ResetForTests(9);
                    Simulator.stepsPerClockTransition = period;
                    for (int i = 0; i < d.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
                    var audio = new SimAudio();
                    var tr = new List<string>();
                    for (int half = 0; half < 600; half++)
                    {
                        // settled point: just BEFORE the next edge (the edge step itself shows the models a few steps ahead of
                        // the gates' propagation, which is latency, not behaviour)
                        for (int k = 0; k < period - 3; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                        if (half < 8) early.Add($"{(root == ga ? "gates" : "fast ")} half {half}: clock {root.SubChips.First(c => c.ChipType == ChipType.Clock).OutputPins[0].State & 1} PC {(root.SubChips.First(c => c.ID == 342691203).OutputPins[0].State & 0xFF)} Clock_4 {string.Join(",", root.SubChips.First(c => c.ID == 598350985).OutputPins.Select(o => o.State & 1))}");
                        if (half >= 4) tr.Add(string.Join(" ", root.SubChips.Where(c => c.OutputPins.Length > 0).Select(c => c.ID + ":" + string.Join(",", c.OutputPins.Select(o => Shown(o.State))))));
                        for (int k = 0; k < 3; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                    }
                    traces.Add(tr);
                }
                sb.Append(string.Join("\n", early.Select(e => "    " + e)) + "\n");
                // step-by-step trace of the top level around the first difference, both trees
                {
                    var names = d.SubChips.ToDictionary(x => x.ID, x => string.IsNullOrEmpty(x.Label) ? x.Name : x.Label);
                    foreach (SimChip root in new[] { Simulator.BuildSimChip(d, lib), (SimChip)null })
                    {
                        SimChip r = root, gsrc = null;
                        if (r == null) { gsrc = Simulator.BuildSimChip(d, lib); r = FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>()); }
                        Simulator.ResetForTests(9);
                        Simulator.stepsPerClockTransition = period;
                        SimChip init = gsrc ?? r;
                        for (int i = 0; i < d.InputPins.Length; i++) init.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
                        Simulator.RunSimulationStep(init, Array.Empty<DevPinInstance>(), new SimAudio());
                        if (gsrc != null)
                        {
                            for (int i = 0; i < d.InputPins.Length; i++) r.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
                            FastState.CopyIn(gsrc, r, lib);
                            Simulator.ResetForTests(9);
                            Simulator.stepsPerClockTransition = period;
                        }
                        else Simulator.ResetForTests(9);
                        var audio = new SimAudio();
                        string Line() => string.Join(" ", r.SubChips.Where(c => c.OutputPins.Length > 0 && names.ContainsKey(c.ID) && !names[c.ID].StartsWith("VCC") && !names[c.ID].StartsWith("BUS-T")).Select(c => names[c.ID] + "=" + string.Join(",", c.OutputPins.Select(o => (o.State & 0xFFFF).ToString("X") + ((o.State >> 16) != 0 ? "z" : "")))));
                        string prev = null;
                        for (int step = 1; step <= 22 * period; step++)
                        {
                            Simulator.RunSimulationStep(r, Array.Empty<DevPinInstance>(), audio);
                            if (step < 19 * period) continue;
                            string l = Line();
                            if (l != prev) sb.Append($"  {(gsrc == null ? "G" : "F")} step {step} (clock {r.SubChips.First(c => c.ChipType == ChipType.Clock).OutputPins[0].State & 1}): {l}\n");
                            prev = l;
                        }
                    }
                }
                int diff = Enumerable.Range(0, traces[0].Count).Where(i => traces[0][i] != traces[1][i]).DefaultIfEmpty(-1).First();
                sb.Append(diff < 0 ? $"  equivalence: {traces[0].Count} half-periods, every component output identical\n" : $"  equivalence: FIRST DIFFERENCE at half-period {diff + 4}:\n    gates {traces[0][diff]}\n    fast  {traces[1][diff]}\n");
            }
            Simulator.ClearTestSeed();
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "fastbench.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }

    // a pin's meaning: driven bits, and which bits float (the bits of a floating line are leftovers, not a value)
    static string Shown(uint state)
    {
        uint tri = state >> 16, bits = state & 0xFFFF & ~tri;
        return tri == 0 ? bits.ToString("X") : bits.ToString("X") + "z" + tri.ToString("X");
    }
}
