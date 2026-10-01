using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

namespace DLS.Bench
{
    // Runs one serial live-project case N times (hunting a flaky case):
    //   Unity.exe -projectPath <proj> -executeMethod DLS.Bench.RepeatCase.Run -case "switching chip" -times 20 -quit
    public static class RepeatCase
    {
        public static void Run()
        {
            string[] a = Environment.GetCommandLineArgs();
            string Arg(string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            string match = Arg("-case") ?? "switching chip";
            int times = int.Parse(Arg("-times") ?? "20");
            var sb = new StringBuilder();
            var c = ProjectCases.All(BenchProject.FixtureProjectDir("PC")).First(x => x.name.Contains(match));
            int fails = 0;
            for (int i = 0; i < times; i++)
            {
                string r;
                try { r = c.run(); } catch (Exception e) { r = "EXCEPTION " + e.Message; }
                if (r != null) { fails++; sb.AppendLine($"run {i}: {r}"); }
            }
            sb.AppendLine($"{c.name}: {fails} failures in {times} runs");
            File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "repeatcase.txt"), sb.ToString());
            EditorApplication.Exit(0);
        }
    }
}
