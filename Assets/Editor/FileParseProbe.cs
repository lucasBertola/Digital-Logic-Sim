using System;
using System.IO;
using System.Linq;
using DLS.Bench;
using DLS.Graphics;
using UnityEditor;

// What LOAD FROM FILE would read from a file (read-only):
//   Unity.exe -projectPath <proj> -executeMethod FileParseProbe.Run -probeFile <path> [-probeBits 32] [-probeMode 0|1|2] -quit
public static class FileParseProbe
{
    public static void Run()
    {
        string report;
        try
        {
            string path = Arg("-probeFile");
            int bits = int.Parse(Arg("-probeBits") ?? "32"), mode = int.Parse(Arg("-probeMode") ?? "0");
            var values = MemoryEditMenu.ParsePasted(File.ReadAllText(path), bits, mode, out string error);
            report = $"{values.Count} words{(error != null ? ", error: " + error : "")}; first: {string.Join(" ", values.Take(3).Select(v => v.ToString("X8")))}; last: {(values.Count > 0 ? values[^1].ToString("X8") : "-")}";
        }
        catch (Exception e) { report = "EXCEPTION " + e; }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "fileparseprobe.txt"), report);
        EditorApplication.Exit(0);
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
