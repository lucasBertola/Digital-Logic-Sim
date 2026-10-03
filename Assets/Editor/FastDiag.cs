using System;
using System.IO;
using System.Linq;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using UnityEditor;

// Why a module gets / does not get a RUN FAST template: every candidate and where it failed against the gates.
//   Unity.exe -projectPath <proj> -executeMethod FastDiag.Run -diagChip Registre8 [-diagSave] -quit
public static class FastDiag
{
    public static void Run()
    {
        string report;
        try
        {
            string[] a = Environment.GetCommandLineArgs();
            string chip = a.SkipWhile(x => x != "-diagChip").Skip(1).FirstOrDefault() ?? "Registre8";
            string dir = a.Contains("-diagSave") ? DLS.SaveSystem.SavePaths.GetProjectPath("PC") : BenchProject.FixtureProjectDir("PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out ChipDescription[] chips);
            ChipDescription d = chips.First(c => c.Name == chip);
            report = chip + ":\n" + string.Join("\n", FastTemplates.Diagnose(d, lib));
        }
        catch (Exception e) { report = "EXCEPTION " + e; }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "fastdiag.txt"), report);
        EditorApplication.Exit(0);
    }
}
