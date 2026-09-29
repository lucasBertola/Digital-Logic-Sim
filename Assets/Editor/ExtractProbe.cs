using System;
using System.IO;
using System.Linq;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using UnityEditor;

// CREATE CHIP on a chip of the SAVE folder, read-only (nothing is written):
//   Unity.exe -projectPath <proj> -executeMethod ExtractProbe.Run -probeChip CPU_2 -probeLabels "ram_prog0,ram_prog1,ram_prog2" -quit
public static class ExtractProbe
{
    public static void Run()
    {
        string report;
        try
        {
            string chip = Arg("-probeChip") ?? "CPU_2";
            string[] labels = (Arg("-probeLabels") ?? "").Split(',');
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath(Arg("-probeProject") ?? "PC"), out ChipDescription[] chips);
            ChipDescription d = chips.First(c => c.Name == chip);
            int[] ids = d.SubChips.Where(s => labels.Contains(s.Label) || labels.Contains(s.Name)).Select(s => s.ID).ToArray();
            ChipExtractor.Result r = ChipExtractor.Extract(d, ids, "PROBE", lib, out string e);
            report = $"{ids.Length} selected -> " + (r == null ? "ERROR: " + e
                : $"OK: inputs [{string.Join(", ", r.NewChip.InputPins.Select(p => p.Name))}], outputs [{string.Join(", ", r.NewChip.OutputPins.Select(p => p.Name))}], parent {r.NewParent.SubChips.Length} components");
        }
        catch (Exception ex) { report = "EXCEPTION " + ex; }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "extractprobe.txt"), report);
        EditorApplication.Exit(0);
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
