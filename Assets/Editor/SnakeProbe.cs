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

// RUN FAST on a save-folder chip with its SAVED memory (the program the user runs), batched like the sim thread:
// prints, every half second, the rate, the PC and what ran per half-period (read-only, nothing written but the report):
//   Unity.exe -projectPath <proj> -executeMethod SnakeProbe.Run [-probeChip CPU_2] [-probeSecs 8] [-probeGates 1] -quit
public static class SnakeProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string chip = Arg("-probeChip") ?? "CPU_2";
            double secs = double.Parse(Arg("-probeSecs") ?? "8", System.Globalization.CultureInfo.InvariantCulture);
            string dir = SavePaths.GetProjectPath("PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out ChipDescription[] chips);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            ChipDescription d = chips.First(c => c.Name == chip);
            int period = pd.Prefs_SimStepsPerClockTick;
            Simulator.ResetForTests(7);
            Simulator.stepsPerClockTransition = period;
            SimChip gates = Simulator.BuildSimChip(d, lib);
            SimChip root = gates;
            var inst = new List<FastBuilder.Instance>();
            if (Arg("-probeGates") == null)
            {
                FastCache cache = FastCacheFile.Load("PC");
                var swb = Stopwatch.StartNew();
                root = FastBuilder.Build(d, lib, cache, inst);
                FastState.CopyIn(gates, root, lib);
                sb.Append($"fast tree built in {swb.ElapsedMilliseconds} ms; models: {string.Join(", ", inst.GroupBy(i => i.Desc.Name + (i.Leaf.Model is LutModel ? " (table)" : " (model)")).Select(g => g.Count() + " x " + g.Key))}\n");
            }
            for (int i = 0; i < d.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
            var audio = new SimAudio();
            // the PC: CPU_2 > Instruction > PC (a model leaf or a chip; its first output pin)
            SimChip pcChip = Find(root, "Instruction", "PC", d, lib);
            Func<string> pc = () => pcChip == null ? "?" : string.Join("/", pcChip.OutputPins.Select(p => (PinState.GetBitStates(p.State)).ToString()));
            sb.Append($"{chip}: {root.Program?.GateCount} gates before the first step, period {period}\n");
            Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
            sb.Append($"compiled: {root.Program.GateCount} gates\n");
            var sw = Stopwatch.StartNew();
            long steps = 0, lastSteps = 0, lastReal = Simulator.RealSteps; double lastT = 0;
            root.Program.CollectStats = Arg("-probeStats") != null;
            root.Program.RecordSettle = true; // RECORD CLOCK STEPS NEEDED
            while (sw.Elapsed.TotalSeconds < secs)
            {
                steps += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, 4096);
                double t = sw.Elapsed.TotalSeconds;
                if (Arg("-probeSpace") != null) SimKeyboardHelper.SetVirtualKeys(t > 2.5 && t < 3 ? new HashSet<char> { ' ' } : new HashSet<char>());
                if (t - lastT >= 0.5)
                {
                    double halfs = (steps - lastSteps) / (double)period;
                    string runs = "";
                    if (root.Program.CollectStats)
                    {
                        long[] rt = root.Program.RunsByType;
                        runs = " runs/half: " + string.Join(", ", Enumerable.Range(0, 256).Where(k => rt[k] > 0).OrderByDescending(k => rt[k]).Take(8).Select(k => (k == 254 ? "NandNot" : k == 253 ? "Nop" : k == 255 ? "Merge" : ((ChipType)k).ToString()) + " " + (rt[k] / halfs).ToString("0.0")));
                        Array.Clear(rt, 0, 256);
                    }
                    sb.Append($"t={t:0.0}s {(steps - lastSteps) / (t - lastT) / 2.0 / period / 1000:0.0} kHz, real steps/half {(Simulator.RealSteps - lastReal) / halfs:0.00}, PC {pc()}, LCD {Lcd(root)}, clock steps needed {root.Program.SettleMax} max over {root.Program.SettleEdges} edges ({root.Program.SettleUnsettled} not settled){runs}\n");
                    lastT = t; lastSteps = steps; lastReal = Simulator.RealSteps;
                }
            }
            if (Arg("-probeProfile") != null)
            {
                Simulator.ProfInBatch = Simulator.ProfLoopSteps = Simulator.ProfStep = Simulator.ProfIdle = Simulator.ProfSteps = Simulator.ProfFirst = Simulator.ProfBatches = 0;
                Simulator.ProfGates = Simulator.ProfKernelGates = Simulator.ProfNoise = Simulator.ProfKernel = Simulator.ProfKernelCalls = 0;
                Array.Clear(Simulator.ProfBailTypes, 0, 256);
                Simulator.Profile = true;
                long s0 = steps;
                var sw3 = Stopwatch.StartNew();
                while (sw3.Elapsed.TotalSeconds < 3)
                {
                    long tr0 = Stopwatch.GetTimestamp();
                    int k = Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, 256);
                    Simulator.ProfInBatch += Stopwatch.GetTimestamp() - tr0; Simulator.ProfLoopSteps += k;
                    steps += k;
                }
                Simulator.Profile = false;
                double halfs = (steps - s0) / (double)period;
                double ns(long ticks) => ticks * 1e9 / Stopwatch.Frequency / halfs;
                sb.Append($"profile per half-period: wall {sw3.Elapsed.TotalMilliseconds * 1e6 / halfs:0} ns, inside RunSimulationSteps {ns(Simulator.ProfInBatch):0} ns (Step {ns(Simulator.ProfStep):0}, IdleSteps {ns(Simulator.ProfIdle):0}, first full steps {ns(Simulator.ProfFirst):0}), real steps {Simulator.ProfSteps / halfs:0.00}, gates run {Simulator.ProfGates / halfs:0.0} (kernel {Simulator.ProfKernelGates / halfs:0.0}), kernel calls {Simulator.ProfKernelCalls / halfs:0.00} taking {ns(Simulator.ProfKernel):0} ns, handed back: " + string.Join(", ", Enumerable.Range(0, 256).Where(x => Simulator.ProfBailTypes[x] > 0).Select(x => (x == 255 ? "Merge" : ((ChipType)x).ToString()) + " " + (Simulator.ProfBailTypes[x] / halfs).ToString("0.00"))) + "\n");
            }
            // the gates that ran the most (canonical) over a last second
            if (root.Program.CollectStats && root.Program.RunsByGate.Length > 0)
            {
                long[] rg = root.Program.RunsByGate;
                Array.Clear(rg, 0, rg.Length);
                long s0 = steps;
                var sw2 = Stopwatch.StartNew();
                while (sw2.Elapsed.TotalSeconds < 1) steps += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, 256);
                double halfs = (steps - s0) / (double)period;
                sb.Append("top gates per half-period: " + string.Join(", ", Enumerable.Range(0, rg.Length).OrderByDescending(k => rg[k]).Take(25).Where(k => rg[k] > 0).Select(k => Describe(root.Program, k, inst) + " " + (rg[k] / halfs).ToString("0.00"))) + "\n");
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e + "\n"); }
        SimKeyboardHelper.SetVirtualKeys(null);
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "snakeprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }

    static string Lcd(SimChip root)
    {
        foreach (SimChip c in root.SubChips)
            if (c != null && c.ChipType == ChipType.LcdSt7920) { uint h = 2166136261; foreach (uint w in c.InternalState) h = (h ^ w) * 16777619; return h.ToString("X8"); }
        return "-";
    }

    static string Describe(SimProgram prog, int g, List<FastBuilder.Instance> inst)
    {
        (SimChip c, byte type, uint _) = prog.GateInfo(g);
        string tn = type == 254 ? "NandNot" : type == 253 ? "Nop" : type == 255 ? "Merge" : ((ChipType)type).ToString();
        FastBuilder.Instance i = inst.FirstOrDefault(x => x.Leaf == c);
        if (i != null) return tn + "[" + i.Desc.Name + (i.Leaf.Model is LutModel ? " table" : " " + i.Leaf.Model.GetType().Name) + "]";
        return tn + (c == null ? "" : "[" + c.ChipType + " in " + (c.compileParent == null ? "-" : c.compileParent.ChipType + "#" + c.compileParent.ID) + "]");
    }

    // a sub-chip by the names of the chips on the path (first match at each level)
    static SimChip Find(SimChip root, string a, string b, ChipDescription d, ChipLibrary lib)
    {
        int ia = Array.FindIndex(d.SubChips, s => s.Name == a);
        if (ia < 0) return null;
        var (ok, ca) = root.TryGetSubChipFromID(d.SubChips[ia].ID);
        if (!ok) return null;
        ChipDescription da = lib.GetChipDescription(a);
        int ib = Array.FindIndex(da.SubChips, s => s.Name == b);
        if (ib < 0) return ca;
        var (ok2, cb) = ca.TryGetSubChipFromID(da.SubChips[ib].ID);
        return ok2 ? cb : ca;
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
