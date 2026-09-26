using System;
using System.Diagnostics;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Regression bench: flattens every chip of a project to NAND-only, then runs the optimiser twice
// (bare NANDs, then the full package palette) plus a set of palette scenarios, and checks each result
// against the original over the whole input space. Nothing is written to disk.
//   Unity.exe -projectPath <proj> -executeMethod NandBench.Run -benchProject "ALU" -quit -logFile <log>
public static class NandBench
{
    public static void Run()
    {
        string projectName = GetArg("-benchProject") ?? "ALU";
        StringBuilder sb = new();
        int failures = 0;

        try
        {
            DLS.Game.Project project = Loader.LoadProject(projectName);
            string only = GetArg("-benchChip"); // restrict to one chip while iterating on the optimiser
            string[] names = project.chipLibrary.GetAllCustomChipNames();
            if (only != null) names = Array.FindAll(names, n => string.Equals(n, only, StringComparison.OrdinalIgnoreCase));
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);

            sb.Append($"\n=== NAND bench on project \"{projectName}\" ===\n");
            sb.Append("chip            flat -> NAND seuls (gain)   profondeur   verification\n");

            foreach (string name in names)
            {
                ChipDescription flat = NandFlattener.Flatten(name, project.chipLibrary, name + "#F", out NandFlattener.Report report);
                if (flat == null)
                {
                    sb.Append($"{name,-14} (not flattenable)\n");
                    continue;
                }

                // Register the flattened chip so the minimiser can resolve it by name.
                project.chipLibrary.NotifyChipSaved(flat);

                // ---- bare NANDs: only the single-gate NAND package is available ----
                bool[] nandOnly = new bool[GatePackages.All.Length];
                nandOnly[GatePackages.UnitNandIndex] = true;

                Stopwatch watch = Stopwatch.StartNew();
                GateMapper.Result r = GateMapper.Map(flat.Name, project.chipLibrary, name + "#M", nandOnly);
                watch.Stop();

                if (r.Error != null)
                {
                    sb.Append($"{name,-14} {report.NandCount,5} flat   -> ERROR: {r.Error.Replace("\n", " ")}\n");
                    if (r.Error.StartsWith("Verification ECHOUEE")) failures++;
                    continue;
                }

                float pct = r.OriginalNandCount == 0 ? 0 : 100f * (r.OriginalNandCount - r.GateTotal) / r.OriginalNandCount;
                string verify = r.VerifiedExhaustively ? $"OK all {r.VerifiedPatterns}" : $"OK {r.VerifiedPatterns} rnd";
                sb.Append($"{name,-14} {r.OriginalNandCount,5} -> {r.GateTotal,5}  ({pct,5:0.#}%)  prof {r.OriginalDepth,3} -> {r.Depth,-3} {verify,-16} {watch.ElapsedMilliseconds} ms\n");

                // ---- package mapping, with every package type available ----
                watch.Restart();
                GateMapper.Result m = GateMapper.Map(flat.Name, project.chipLibrary, name + "#P", GatePackages.NewSelection());
                watch.Stop();

                if (m.Error != null)
                {
                    sb.Append($"{"",-14}   packages -> ERROR: {m.Error.Replace("\n", " ")}\n");
                    if (m.Error.StartsWith("Verification ECHOUEE")) failures++;
                }
                else
                {
                    string mverify = m.VerifiedExhaustively ? $"OK all {m.VerifiedPatterns}" : $"OK {m.VerifiedPatterns} rnd";
                    StringBuilder bill = new();
                    foreach ((GateKind k, int gates) in m.GateUsage) bill.Append($"{k.ShortName}:{gates} ");
                    bill.Append("| ");
                    foreach ((GatePackage p, int c) in m.Bill) bill.Append($"{c}x[{p.Label}] ");
                    sb.Append($"{"",-14}   {m.PackageCount,3} boitiers, {m.GateTotal,4} portes, prof {m.OriginalDepth,3} -> {m.Depth,-3} {mverify,-16} {watch.ElapsedMilliseconds,5} ms  {bill}\n");
                }
            }

            // ---- palette scenarios: only the ticked packages may be used ----
            sb.Append("\n--- palette scenarios ---\n");
            foreach (string target in only != null ? new[] { only } : new[] { "FullA", "ALU5" })
            {
                if (!project.chipLibrary.HasChip(target + "#F")) continue;
                foreach ((string title, int[] ticked) in new[]
                         {
                             ("tout", null),
                             ("NAND2 seul", new[] { 0 }),
                             ("1xNOR2 seul (ligne 10)", new[] { 9 }),
                             ("4xNOR2 + 1xNOR2", new[] { 1, 9 }),
                             ("XOR2 seul", new[] { 5 }),
                             ("NOT + AND2 + OR2", new[] { 2, 3, 4 })
                         })
                {
                    bool[] sel = ticked == null ? GatePackages.NewSelection() : new bool[GatePackages.All.Length];
                    if (ticked != null)
                    {
                        foreach (int i in ticked) sel[i] = true;
                    }

                    GateMapper.Result m = GateMapper.Map(target + "#F", project.chipLibrary, target + "#S", sel);
                    if (m.Error != null)
                    {
                        sb.Append($"{target,-8} {title,-24} -> {m.Error.Replace("\n", " ")}\n");
                        continue;
                    }

                    StringBuilder bill = new();
                    foreach ((GatePackage pk, int c) in m.Bill) bill.Append($"{c}x[{pk.Label}] ");
                    string v = m.VerifiedExhaustively ? "verifie" : "echantillon";
                    sb.Append($"{target,-8} {title,-24} -> {m.PackageCount,3} boitiers, {m.GateTotal,4} portes  {v}  {bill}\n");

                    if (!AllTickedOnly(m, sel))
                    {
                        sb.Append("    !!! A UTILISE UN BOITIER NON COCHE !!!\n");
                        failures++;
                    }
                }
            }

            // ---- hierarchical input: Optimise must work straight off a brick made of sub-bricks ----
            sb.Append("\n--- hierarchical input (no 'Nand only' first) ---\n");
            foreach (string target in only != null ? new[] { only } : new[] { "FullA", "Add4", "ALU5" })
            {
                if (!project.chipLibrary.HasChip(target)) continue;

                bool enabled = NandMinimizer.CanOptimize(target, project.chipLibrary);
                GateMapper.Result m = GateMapper.Map(target, project.chipLibrary, target + "#H", GatePackages.NewSelection());

                if (m.Error != null)
                {
                    sb.Append($"{target,-8} menu actif: {enabled,-5} -> {m.Error.Replace("\n", " ")}\n");
                    failures++;
                    continue;
                }

                string v = m.VerifiedExhaustively ? "verifie" : "echantillon";
                sb.Append($"{target,-8} menu actif: {enabled,-5} -> {m.PackageCount,3} boitiers, {m.GateTotal,4} portes, depart {m.OriginalNandCount,4} NAND, prof {m.OriginalDepth,3} -> {m.Depth,-3} {v}\n");
                if (!enabled) failures++;
            }

            sb.Append(failures == 0 ? "=== ALL EQUIVALENCE CHECKS PASSED ===\n" : $"=== {failures} EQUIVALENCE FAILURES ===\n");
        }
        catch (Exception e)
        {
            sb.Append("BENCH EXCEPTION: " + e + "\n");
            failures++;
        }

        Debug.Log(sb.ToString());
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    // Guards the whole point of the palette: nothing outside the ticked lines may appear.
    static bool AllTickedOnly(GateMapper.Result r, bool[] selection)
    {
        foreach ((GatePackage package, int _) in r.Bill)
        {
            bool ticked = false;
            for (int i = 0; i < GatePackages.All.Length; i++)
            {
                if (selection[i] && ReferenceEquals(GatePackages.All[i], package)) ticked = true;
            }

            if (!ticked) return false;
        }

        return true;
    }

    static string GetArg(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
