using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Editor build entry point. Two ways to invoke:
//   1. Command line (batchmode):  Unity.exe -quit -batchmode -projectPath <proj> -executeMethod BuildTools.BuildWindows
//   2. From the open editor:      menu  Tools > Build Windows Player (fork)   or   Ctrl+Shift+K
// The menu path is needed because batchmode licensing is unavailable when Unity's licensing-client
// signing certificate is expired relative to the system clock; the interactive editor holds a valid
// license session so building from it still works.
// Kept in the global namespace so -executeMethod can reference it without a namespace prefix.
public static class BuildTools
{
    const string ScenePath = "Assets/Build/DLS.unity";
    const string DefaultOutputDir = "Builds/Windows";
    const string ExeName = "DigitalLogicSim.exe";

    // Batchmode entry (exits the editor with a status code).
    public static void BuildWindows()
    {
        (bool ok, string msg) = BuildCore(GetArg("-buildOutput") ?? DefaultOutputDir);
        if (ok) { Debug.Log(msg); EditorApplication.Exit(0); }
        else { Debug.LogError(msg); EditorApplication.Exit(1); }
    }

    // Interactive entry (does NOT exit the editor).
    [MenuItem("Tools/Build Windows Player (fork) %#k")]
    public static void BuildWindowsMenu()
    {
        (bool ok, string msg) = BuildCore(DefaultOutputDir);
        if (ok) Debug.Log(msg); else Debug.LogError(msg);
        EditorUtility.DisplayDialog(ok ? "Build reussi" : "Build echoue", msg, "OK");
    }

    // The main menu banner shows the fork's release version and the build date, not the upstream save-format
    // version. Written at build time into a Resources text asset (git-ignored): "<git describe> | <date>".
    const string BuildInfoPath = "Assets/Resources/BuildInfo.txt";

    static void WriteBuildInfo()
    {
        string version = "dev";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "describe --tags --always --dirty")
            {
                WorkingDirectory = Path.GetDirectoryName(Application.dataPath),
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (p.ExitCode == 0 && outp.Length > 0) version = outp;
        }
        catch (Exception) { /* no git: "dev" */ }
        Directory.CreateDirectory(Path.GetDirectoryName(BuildInfoPath));
        File.WriteAllText(BuildInfoPath, version + " | " + DateTime.Now.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        AssetDatabase.ImportAsset(BuildInfoPath, ImportAssetOptions.ForceSynchronousImport);
    }

    // Projects shipped with the app: names listed in BundledProjects.txt (repo root), copied from the developer's
    // save folder (the same folder the app uses) into StreamingAssets/BundledProjects at build time, with a
    // content hash (see DLS.SaveSystem.BundledProjects for what the app does with them at startup).
    const string BundleListPath = "BundledProjects.txt";
    const string BundleTargetPath = "Assets/StreamingAssets/" + DLS.SaveSystem.BundledProjects.BundleFolderName;

    static string BundleProjects()
    {
        string listPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), BundleListPath);
        if (Directory.Exists(BundleTargetPath)) Directory.Delete(BundleTargetPath, true);
        if (!File.Exists(listPath)) return "no " + BundleListPath;
        var done = new System.Collections.Generic.List<string>();
        foreach (string raw in File.ReadAllLines(listPath))
        {
            string name = raw.Trim();
            if (name.Length == 0 || name.StartsWith("#")) continue;
            string source = DLS.SaveSystem.SavePaths.GetProjectPath(name);
            if (!Directory.Exists(source)) { Debug.LogWarning($"BundledProjects: project \"{name}\" not found in {DLS.SaveSystem.SavePaths.ProjectsPath}, not bundled"); continue; }
            string target = Path.Combine(BundleTargetPath, name);
            CopyProject(source, target);
            File.WriteAllText(Path.Combine(target, DLS.SaveSystem.BundledProjects.HashFileName), DLS.SaveSystem.BundledProjects.HashDirectory(target));
            done.Add(name);
        }
        AssetDatabase.Refresh();
        return done.Count == 0 ? "no project bundled" : "bundled: " + string.Join(", ", done);
    }

    static void CopyProject(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            string fn = Path.GetFileName(f);
            if (!DLS.SaveSystem.BundledProjects.IsContentFile(fn)) continue; // markers, the Ask Claude conversation
            File.Copy(f, Path.Combine(to, fn), true);
        }
        foreach (string d in Directory.GetDirectories(from))
        {
            string dn = Path.GetFileName(d);
            if (dn == DLS.SaveSystem.BundledProjects.DeletedChipsFolder) continue;
            CopyProject(d, Path.Combine(to, dn));
        }
    }

    static (bool ok, string msg) BuildCore(string outputDir)
    {
        string exePath = Path.Combine(outputDir, ExeName);
        Directory.CreateDirectory(outputDir);
        WriteBuildInfo();
        string bundled = BundleProjects();

        BuildPlayerOptions options = new()
        {
            scenes = new[] { ScenePath },
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            // Freshen the staleness marker so lancerApp.bat launches directly instead of rebuilding.
            try { File.WriteAllText(Path.Combine(outputDir, ".lastbuild"), "built"); } catch { /* ignore */ }
            return (true, $"BUILD SUCCEEDED -> {exePath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:0.0}s, {bundled})");
        }

        return (false, $"BUILD FAILED: result={summary.result}, errors={summary.totalErrors}");
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }
}
