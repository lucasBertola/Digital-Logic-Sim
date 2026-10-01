using System;
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
            string dir = SavePaths.GetProjectPath(Arg("-rateProject") ?? "PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            pd.ProjectName = "_LiveRate_readonly"; // nothing is saved anyway; never the user's project
            pd.Prefs_SimPaused = false;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            p.LoadDevChipOrCreateNewIfDoesntExist(chip);
            p.StartSimulation();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double best = 0;
            while (sw.ElapsedMilliseconds < 4000) { p.TickMainThreadForTests(); Thread.Sleep(16); if (sw.ElapsedMilliseconds > 1500) best = Math.Max(best, p.simAvgTicksPerSec); }
            p.NotifyExit();
            report = $"{chip}: {best:0} steps/s in the app's sim thread (target {pd.Prefs_SimTargetStepsPerSecond}, {pd.Prefs_SimStepsPerClockTick} steps per tick) = {best / (2.0 * pd.Prefs_SimStepsPerClockTick) / 1000:0} kHz shown";
        }
        catch (Exception e) { report = "EXCEPTION " + e; }
        File.AppendAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "liverate.txt"), report + "\n");
        EditorApplication.Exit(0);
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
