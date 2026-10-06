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
            Debug.Log("BundledProjects: " + FillFastCache(name, target, source));
            File.WriteAllText(Path.Combine(target, DLS.SaveSystem.BundledProjects.HashFileName), DLS.SaveSystem.BundledProjects.HashDirectory(target));
            done.Add(name);
        }
        AssetDatabase.Refresh();
        var leaked = DLS.SaveSystem.BundledProjects.PrivateFilesUnder(BundleTargetPath);
        if (leaked.Count > 0) throw new Exception("private files in the bundle (never shipped): " + string.Join(", ", leaked));
        return done.Count == 0 ? "no project bundled" : "bundled: " + string.Join(", ", done);
    }

    // Every RUN FAST decision of the shipped project computed now (FastBuilder.Prepare on every chip), so whoever
    // installs the release gets the automatic fast mode straight away, without computing a truth table. Written into
    // the bundle and back into the developer's save folder (same project, so the app has them too).
    public static string FillFastCache(string name, string bundleDir, string sourceDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string file = DLS.SaveSystem.BundledProjects.FastCacheFileName, path = Path.Combine(bundleDir, file);
        try
        {
            DLS.Game.ChipLibrary lib = DLS.Bench.BenchProject.LoadLibrary(bundleDir, out DLS.Description.ChipDescription[] chips);
            var cache = new DLS.Game.FastCache();
            if (File.Exists(path)) DLS.Game.FastCacheFile.FromJson(File.ReadAllText(path), cache);
            cache.Changed = false;
            int before = cache.Count, failed = 0;
            foreach (DLS.Description.ChipDescription c in chips)
            {
                try { DLS.Game.FastBuilder.Prepare(c, lib, cache); }
                catch (Exception e) { failed++; Debug.LogWarning($"fast cache of {c.Name}: {e.Message}"); }
            }
            if (cache.Changed)
            {
                File.WriteAllText(path, DLS.Game.FastCacheFile.ToJson(cache));
                try { File.Copy(path, Path.Combine(sourceDir, file), true); } catch (Exception) { /* the bundle has it */ }
            }
            return $"\"{name}\" fast cache {before} -> {cache.Count} chips{(failed > 0 ? $", {failed} failed" : "")} ({sw.Elapsed.TotalSeconds:0.0} s)";
        }
        catch (Exception e) { return $"\"{name}\" fast cache not prepared: {e.Message}"; }
    }

    public static void CopyProject(string from, string to)
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
        Directory.CreateDirectory(outputDir);
        WriteBuildInfo();
        string bundled;
        try { bundled = BundleProjects(); }
        catch (Exception e) { return (false, "BUILD FAILED: " + e.Message); }
        (bool ok, string msg) = BuildPlayerFor(BuildTarget.StandaloneWindows64, outputDir, ExeName);
        if (!ok) return (false, msg);
        // Freshen the staleness marker so lancerApp.bat launches directly instead of rebuilding.
        try { File.WriteAllText(Path.Combine(outputDir, ".lastbuild"), "built"); } catch { /* ignore */ }
        return (true, $"{msg}, {bundled})");
    }

    // One player: target, output folder, file name inside it (DigitalLogicSim.exe / .app / .x86_64)
    static (bool ok, string msg) BuildPlayerFor(BuildTarget target, string outputDir, string fileName)
    {
        if (Directory.Exists(outputDir) && target != BuildTarget.StandaloneWindows64) Directory.Delete(outputDir, true); // no stale files in a zip
        Directory.CreateDirectory(outputDir);
        string location = Path.Combine(outputDir, fileName);
        BuildPlayerOptions options = new()
        {
            scenes = new[] { ScenePath },
            locationPathName = location,
            target = target,
            options = BuildOptions.None
        };
        BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
        if (summary.result != BuildResult.Succeeded) return (false, $"BUILD FAILED ({target}): result={summary.result}, errors={summary.totalErrors}");
        var leaked = DLS.SaveSystem.BundledProjects.PrivateFilesUnder(outputDir);
        if (leaked.Count > 0) return (false, $"BUILD FAILED ({target}): private files in the build (Claude conversation / key, never shipped): " + string.Join(", ", leaked));
        return (true, $"BUILD SUCCEEDED -> {location} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:0.0}s");
    }

    // ---- Release: Windows + macOS + Linux (user, 2026-10-06) ----
    // Built from this Windows machine with the Mono backend (needs the "Mac Build Support (Mono)" and "Linux Build
    // Support (Mono)" modules). Burst AOT is off for macOS and Linux (ProjectSettings/BurstAotSettings_*.json): Burst
    // does not cross-compile between desktop platforms, so those players run the same kernel as plain C# (slower).
    // Mac and Linux are zipped with Unix permissions (UnixZip: an executable without its x bit does not start, and
    // Compress-Archive drops it). Windows is built LAST so the editor's active target is Windows again afterwards.
    public const string MacDir = "Builds/Mac", LinuxDir = "Builds/Linux";
    public const string MacZip = "Builds/DigitalLogicSim-Mac.zip", LinuxZip = "Builds/DigitalLogicSim-Linux.zip";

    public static void BuildRelease()
    {
        var lines = new System.Collections.Generic.List<string>();
        bool all = true;
        try
        {
            WriteBuildInfo();
            string bundled = BundleProjects();
            lines.Add(bundled);

            SetMacUniversal(lines);
            (bool ok, string msg) = BuildPlayerFor(BuildTarget.StandaloneOSX, MacDir, "DigitalLogicSim.app");
            lines.Add(msg);
            if (ok) lines.Add(UnixZip.Create(MacDir, MacZip, "", rel => rel.Contains(".app/Contents/MacOS/")));
            all &= ok;

            (ok, msg) = BuildPlayerFor(BuildTarget.StandaloneLinux64, LinuxDir, "DigitalLogicSim.x86_64");
            lines.Add(msg);
            if (ok) lines.Add(UnixZip.Create(LinuxDir, LinuxZip, "DigitalLogicSim/", rel => rel.EndsWith(".x86_64") || rel.EndsWith(".so")));
            all &= ok;

            (ok, msg) = BuildPlayerFor(BuildTarget.StandaloneWindows64, DefaultOutputDir, ExeName);
            lines.Add(msg);
            if (ok) try { File.WriteAllText(Path.Combine(DefaultOutputDir, ".lastbuild"), "built"); } catch { /* ignore */ }
            all &= ok;
        }
        catch (Exception e) { lines.Add("BUILD FAILED: " + e.Message); all = false; }
        foreach (string l in lines) Debug.Log("Release: " + l);
        Debug.Log(all ? "RELEASE BUILD SUCCEEDED (Windows, Mac, Linux)" : "RELEASE BUILD FAILED");
        EditorApplication.Exit(all ? 0 : 1);
    }

    // Intel + Apple silicon in one .app. Set by reflection: the type only exists with the Mac module installed.
    static void SetMacUniversal(System.Collections.Generic.List<string> log)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType("UnityEditor.OSXStandalone.UserBuildSettings");
            var prop = t?.GetProperty("architecture", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop == null) continue;
            try
            {
                prop.SetValue(null, Enum.Parse(prop.PropertyType, "x64ARM64"));
                log.Add("Mac architecture: " + prop.GetValue(null));
            }
            catch (Exception e) { log.Add("Mac architecture not set: " + e.Message); }
            return;
        }
        log.Add("Mac architecture: setting not found (Unity's default kept)");
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
