using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using UnityEditor;
using Debug = UnityEngine.Debug;

// End-to-end check of the memory editor's analysis with the REAL Claude API (costs one or a few requests):
//   Unity.exe -projectPath <proj> -executeMethod MemoryLayoutProbe.Run [-probeChip CPU] -quit -logFile <log>
// Asks Claude for the layout of a fixture chip (TestData/Bench/PC), verifies it by simulation exactly like the
// editor does (up to 3 attempts, the failure fed back), and prints the rules and the banks found.
public static class MemoryLayoutProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string chipName = GetArg("-probeChip") ?? "CPU";
            ChipLibrary lib = BenchProject.LoadLibrary(BenchProject.FixtureProjectDir("PC"), out ChipDescription[] chips);
            ChipDescription desc = chips.First(c => ChipDescription.NameMatch(c.Name, chipName));
            string json = null, error = null;
            MemoryRules rules = null;
            var polarity = new Dictionary<string, int[]>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var sw = Stopwatch.StartNew();
                MemoryLayoutClaude.Start(desc, lib, "PC", json, error);
                while (MemoryLayoutClaude.State == MemoryLayoutClaude.Status.Running && sw.Elapsed.TotalMinutes < 8)
                {
                    System.Threading.Thread.Sleep(200);
                    MemoryLayoutClaude.Poll();
                }
                sb.Append($"attempt {attempt}: {MemoryLayoutClaude.State} in {sw.Elapsed.TotalSeconds:0} s\n");
                if (MemoryLayoutClaude.State != MemoryLayoutClaude.Status.Done) { sb.Append("  error: " + MemoryLayoutClaude.Error + "\n"); break; }
                json = MemoryLayoutClaude.ResultJson;
                MemoryLayoutClaude.Cancel();
                sb.Append("  rules: " + json + "\n");
                rules = MemoryLayout.ParseRules(json, out error);
                if (rules != null)
                {
                    polarity.Clear();
                    foreach (string t in MemoryLayout.BankTypes(desc, rules, lib))
                    {
                        error = MemoryLayout.Verify(t, rules, lib, polarity);
                        sb.Append($"  verify {t}: {error ?? "OK"}\n");
                        if (error != null) break;
                    }
                }
                if (error == null) break;
                sb.Append("  rejected: " + error + "\n");
            }
            if (error == null && rules != null)
            {
                SimChip target = CircuitTester.TargetOf(CircuitTester.BuildIsolatedSim(desc, lib));
                var banks = MemoryLayout.Banks(target, desc, rules, polarity, lib, out string e);
                sb.Append(banks == null ? "banks: " + e + "\n" : "banks: " + string.Join(", ", banks.Select(b => $"{b.Name} {b.WordCount}x{b.Bits}")) + "\n");
                sb.Append("RESULT: OK\n");
            }
            else sb.Append("RESULT: FAILED\n");
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e + "\n"); }
        Debug.Log("\n=== MemoryLayoutProbe ===\n" + sb);
        System.IO.File.WriteAllText(System.IO.Path.Combine(BenchProject.RepoRoot, "Builds", "memory-probe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i + 1 < args.Length ? args[i + 1] : "";
        return null;
    }
}
