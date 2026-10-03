using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Newtonsoft.Json;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace DLS.Bench
{
    // The regression bench (editor-only, nothing written to disk except goldens in record mode):
    //   runTests.bat            -> RegressionBench.Run     verify everything, exit code 1 on any failure
    //   runTests.bat record     -> RegressionBench.Record  (re)record the goldens of the fixture projects
    // Cases run IN PARALLEL (the simulator's per-run state is thread-static). Fixtures live in
    // TestData/Bench/<project>/ (a copy of the user's PC project); goldens in TestData/Bench/Golden/<project>/.
    public static class RegressionBench
    {
        static readonly string[] FixtureProjects = { "PC" };

        public static void Run() => Execute(record: false);
        public static void Record() => Execute(record: true);

        static string Frames(Exception e)
        {
            string[] lines = (e.StackTrace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" <- ", lines.Take(4).Select(l => l.Trim()));
        }

        static void Execute(bool record)
        {
            Stopwatch total = Stopwatch.StartNew();
            var results = new ConcurrentBag<(string name, string error, double ms)>();
            var cases = new List<(string name, Func<string> run)>();
            var serial = new List<(string name, Func<string> run)>(); // run after the parallel batch, one at a time
            string fatal = null;

            try
            {
                foreach (string project in FixtureProjects)
                {
                    string dir = BenchProject.FixtureProjectDir(project);
                    if (!Directory.Exists(dir)) { fatal = "fixture project missing: " + dir; break; }
                    ChipLibrary lib = BenchProject.LoadLibrary(dir, out ChipDescription[] chips);
                    string goldenDir = BenchProject.GoldenDir(project);
                    Directory.CreateDirectory(goldenDir);

                    foreach (ChipDescription desc in chips)
                    {
                        ChipDescription d = desc;
                        string goldenPath = Path.Combine(goldenDir, d.Name + ".json");

                        // 1) the chip reloads identical to its file (the red "unsaved" star must not appear)
                        cases.Add(($"[{project}] {d.Name}: reloads clean", () =>
                        {
                            (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(d, lib);
                            if (failed) return "some element failed to load";
                            string a = Saver.CreateSerializedChipDescription(d);
                            string b = Saver.CreateSerializedChipDescription(DescriptionCreator.CreateChipDescription(dev));
                            return UnsavedChangeDetector.IsEquivalentJson(a, b) ? null : "description differs after reload";
                        }));

                        // 2) golden behaviour
                        if (record)
                        {
                            cases.Add(($"[{project}] {d.Name}: record golden", () =>
                            {
                                GoldenRecord rec = GoldenRunner.Record(d, lib);
                                File.WriteAllText(goldenPath, JsonConvert.SerializeObject(rec, Formatting.Indented));
                                int stable = rec.stable.Sum(s => s.Count(x => x)), all = rec.stable.Sum(s => s.Length);
                                return null; // recording never fails; stability is reported below
                            }));
                        }
                        else
                        {
                            cases.Add(($"[{project}] {d.Name}: golden behaviour", () =>
                            {
                                if (!File.Exists(goldenPath)) return "no golden recorded (run: runTests.bat record)";
                                GoldenRecord rec = JsonConvert.DeserializeObject<GoldenRecord>(File.ReadAllText(goldenPath));
                                return GoldenRunner.Verify(rec, d, lib);
                            }));
                        }
                    }

                    if (!record)
                    {
                        cases.AddRange(UnitCases.All(lib, chips));
                        cases.AddRange(BuiltinCases.All());
                        cases.AddRange(DirectedCases.All(lib, chips));
                        cases.AddRange(MemoryCases.All(lib, chips));
                        cases.AddRange(MemoryLayoutCases.All(lib, chips));
                        cases.AddRange(ChipExtractCases.All(lib, chips));
                        cases.AddRange(ClockStopCases.All());
                        cases.AddRange(SpeedPrefsCases.All());
                        cases.AddRange(TranscriptCopyCases.All());
                        cases.AddRange(UnconnectedCases.All());
                        cases.AddRange(ChipInterfaceCases.All());
                        cases.AddRange(LcdCases.All());
                        cases.AddRange(MemoryMergeCases.All(lib, chips));
                        serial.AddRange(ProjectCases.All(dir));
                        serial.Add(SpeedPrefsCases.Live(dir));
                        if (dir.EndsWith("PC")) serial.Add(MemoryMergeCases.Serial(lib, chips));
                        if (dir.EndsWith("PC")) serial.Add(("[PC] memory editor: base change shows the value in the new base; a new chip never shows the previous chip's fields", () => MemoryLayoutCases.MenuFieldsCase(lib, chips)));
                    }
                }
            }
            catch (Exception e) { fatal = e.ToString(); }

            if (fatal == null)
            {
                Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, c =>
                {
                    Stopwatch w = Stopwatch.StartNew();
                    string err;
                    try { err = c.run(); }
                    catch (Exception e) { err = "EXCEPTION " + e.GetType().Name + ": " + e.Message + " @ " + Frames(e); }
                    results.Add((c.name, err, w.Elapsed.TotalMilliseconds));
                });
                foreach (var c in serial)
                {
                    Stopwatch w = Stopwatch.StartNew();
                    string err;
                    try { err = c.run(); }
                    catch (Exception e) { err = "EXCEPTION " + e.GetType().Name + ": " + e.Message + " @ " + Frames(e); }
                    results.Add((c.name, err, w.Elapsed.TotalMilliseconds));
                }
            }

            total.Stop();
            var sb = new StringBuilder();
            sb.Append($"\n===== REGRESSION BENCH ({(record ? "record" : "verify")}) =====\n");
            if (fatal != null) sb.Append("FATAL: " + fatal + "\n");
            int failures = 0;
            foreach (var r in results.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            {
                if (r.error == null) sb.Append($"PASS  {r.name}  ({r.ms:0} ms)\n");
                else { failures++; sb.Append($"FAIL  {r.name}: {r.error}\n"); }
            }
            if (record)
            {
                // stability summary of what was just recorded
                foreach (string project in FixtureProjects)
                {
                    string goldenDir = BenchProject.GoldenDir(project);
                    if (!Directory.Exists(goldenDir)) continue;
                    foreach (string f in Directory.GetFiles(goldenDir, "*.json").OrderBy(x => x))
                    {
                        GoldenRecord rec = JsonConvert.DeserializeObject<GoldenRecord>(File.ReadAllText(f));
                        int stable = rec.stable.Sum(s => s.Count(x => x)), all = rec.stable.Sum(s => s.Length);
                        int pst = rec.probeStable.Sum(x => x.Count(y => y)), pall = rec.probeStable.Sum(x => x.Length);
                        sb.Append($"golden {rec.chip}: {rec.steps} steps, {stable}/{all} outputs + {pst}/{pall} probes stable, sim tree {rec.simChips} chips\n");
                    }
                }
            }
            sb.Append($"===== {results.Count - failures} passed, {failures} failed{(fatal != null ? ", FATAL" : "")} in {total.Elapsed.TotalSeconds:0.0} s ({Environment.ProcessorCount} threads) =====\n");
            Debug.Log(sb.ToString());
            File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "tests-summary.txt"), sb.ToString());
            EditorApplication.Exit(failures == 0 && fatal == null ? 0 : 1);
        }
    }
}
