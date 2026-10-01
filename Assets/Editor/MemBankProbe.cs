using System;
using System.IO;
using System.Linq;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;

// The memories EDIT MEMORY shows for a chip of the SAVE folder, from the project's cached layout (read-only):
//   Unity.exe -projectPath <proj> -executeMethod MemBankProbe.Run -probeChip prog_ram -quit
public static class MemBankProbe
{
    public static void Run()
    {
        string report;
        try
        {
            string chip = Arg("-probeChip") ?? "prog_ram";
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath("PC"), out ChipDescription[] chips);
            ChipDescription d = chips.First(c => c.Name == chip);
            var cache = MemoryLayout.LoadCache("PC");
            MemoryLayout.CacheEntry e = cache[chip];
            SimChip root = CircuitTester.BuildIsolatedSim(d, lib);
            SimChip target = CircuitTester.TargetOf(root);
            for (int i = 0; i < 5; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
            var banks = MemoryLayout.Banks(target, d, e.rules, e.polarity, lib, out string err);
            report = banks == null ? "ERROR " + err : string.Join("\n", banks.Select(b => $"{b.Name}: {b.WordCount} x {b.Bits} bits" + (b.Parts != null ? $" = {string.Join(" + ", b.Parts.Select(p => p.Name + " (" + p.Bits + ")"))}" : "")));
        }
        catch (Exception ex) { report = "EXCEPTION " + ex; }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "membankprobe.txt"), report);
        EditorApplication.Exit(0);
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
