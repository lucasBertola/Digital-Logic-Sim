using System;
using System.Linq;
using DLS.Simulation;
using System.IO;
using System.Threading;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using UnityEditor;

// The rate the APP reaches (its real sim thread, the project's prefs), on a chip of the SAVE folder, read-only:
//   Unity.exe -projectPath <proj> -executeMethod LiveRate.Run -rateChip CPU_2 -quit
public static class LiveRate
{
    public static void Run()
    {
        string report;
        try
        {
            string chip = Arg("-rateChip") ?? "CPU_2";
            DLS.Simulation.SimProgram.BurstEnabled = Arg("-noBurst") == null;
            string dir = SavePaths.GetProjectPath(Arg("-rateProject") ?? "PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            pd.ProjectName = "_LiveRate_readonly"; // nothing is saved anyway; never the user's project
            pd.Prefs_SimPaused = false;
            if (Arg("-rateMax") != null) pd.Prefs_SimMaxSpeed = true;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            p.LoadDevChipOrCreateNewIfDoesntExist(chip);
            p.StartSimulation();
            if (Arg("-rateFast") != null) { p.RunFastNowForTests(); if (!p.FastModeActive) throw new Exception("fast mode did not start: " + p.FastModeStatus); }
            bool prof = Arg("-rateProfile") != null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long profStart = 0;
            double best = 0;
            while (sw.ElapsedMilliseconds < 4000) { p.TickMainThreadForTests(); Thread.Sleep(16); if (sw.ElapsedMilliseconds > 1500) best = Math.Max(best, p.simAvgTicksPerSec); if (prof && profStart == 0 && sw.ElapsedMilliseconds > 1500) { Simulator.ProfInBatch = Simulator.ProfLoopSteps = Simulator.ProfStep = Simulator.ProfIdle = Simulator.ProfSteps = Simulator.ProfFirst = Simulator.ProfBatches = 0; Simulator.ProfGates = Simulator.ProfKernelGates = Simulator.ProfNoise = Simulator.ProfKernel = Simulator.ProfKernelCalls = 0; Array.Clear(Simulator.ProfBailTypes, 0, 256); Simulator.Profile = true; profStart = System.Diagnostics.Stopwatch.GetTimestamp(); } }
            Simulator.Profile = false;
            string profReport = "";
            if (prof)
            {
                double wall = (System.Diagnostics.Stopwatch.GetTimestamp() - profStart) * 1e9 / System.Diagnostics.Stopwatch.Frequency;
                double halfs = Simulator.ProfLoopSteps / (double)pd.Prefs_SimStepsPerClockTick;
                double ns(long ticks) => ticks * 1e9 / System.Diagnostics.Stopwatch.Frequency / halfs;
                profReport = $" | per half-period: wall {wall / halfs:0} ns, inside RunSimulationSteps {ns(Simulator.ProfInBatch):0} ns (Step {ns(Simulator.ProfStep):0}, IdleSteps {ns(Simulator.ProfIdle):0}, first full steps {ns(Simulator.ProfFirst):0} x{Simulator.ProfBatches / halfs:0.00}), real steps {Simulator.ProfSteps / halfs:0.00}, gates run {Simulator.ProfGates / halfs:0.0} (kernel {Simulator.ProfKernelGates / halfs:0.0}), kernel calls {Simulator.ProfKernelCalls / halfs:0.00} taking {ns(Simulator.ProfKernel):0} ns, noise list {Simulator.ProfNoise / (double)Math.Max(1, Simulator.ProfSteps):0.0}, handed back: " + string.Join(", ", Enumerable.Range(0, 256).Where(x => Simulator.ProfBailTypes[x] > 0).Select(x => (x == 255 ? "Merge" : ((DLS.Description.ChipType)x).ToString()) + " " + (Simulator.ProfBailTypes[x] / halfs).ToString("0.00")));
            }
            p.NotifyExit();
            report = $"{chip}: {best:0} steps/s in the app's sim thread (target {(pd.Prefs_SimMaxSpeed ? "MAX" : pd.Prefs_SimTargetStepsPerSecond.ToString())}{(Arg("-rateFast") != null ? ", FAST MODE" : "")}, {pd.Prefs_SimStepsPerClockTick} steps per tick) = {best / (2.0 * pd.Prefs_SimStepsPerClockTick) / 1000:0} kHz shown" + profReport;
        }
        catch (Exception e) { report = "EXCEPTION " + e; }
        File.AppendAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "liverate.txt"), report + "\n");
        EditorApplication.Exit(0);
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
