using System;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Diagnostic: for every chip of a project, load it exactly like the editor does, re-create its description
// from the live instance, and report the first JSON difference — i.e. why a freshly opened chip would show
// the red "unsaved changes" star. Nothing is written to disk.
//   Unity.exe -projectPath <proj> -executeMethod DirtyCheck.Run -benchProject "PC" -quit -logFile <log>
public static class DirtyCheck
{
    public static void Run()
    {
        string projectName = GetArg("-benchProject") ?? "PC";
        StringBuilder sb = new();
        int dirty = 0;

        try
        {
            Project project = Loader.LoadProject(projectName);
            string[] names = project.chipLibrary.GetAllCustomChipNames();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            sb.Append($"\n=== DirtyCheck on project \"{projectName}\" ({names.Length} chips) ===\n");

            foreach (string name in names)
            {
                ChipDescription saved = project.chipLibrary.GetChipDescription(name);
                (DevChipInstance devChip, bool failed) = DevChipInstance.LoadFromDescriptionTest(saved, project.chipLibrary);
                ChipDescription live = DescriptionCreator.CreateChipDescription(devChip);
                string jsonA = Saver.CreateSerializedChipDescription(saved);
                string jsonB = Saver.CreateSerializedChipDescription(live);
                bool same = UnsavedChangeDetector.IsEquivalentJson(jsonA, jsonB);
                if (same)
                {
                    sb.Append($"{name,-16} clean{(failed ? "  (some element failed to load)" : "")}\n");
                    continue;
                }

                dirty++;
                string diff = FirstDifference(JToken.Parse(jsonA), JToken.Parse(jsonB), "$");
                sb.Append($"{name,-16} DIRTY{(failed ? " (some element failed to load)" : "")} -> {diff}\n");
            }
        }
        catch (Exception e)
        {
            sb.Append($"EXCEPTION: {e}\n");
            dirty++;
        }

        sb.Append($"=== {dirty} dirty chip(s) ===\n");
        Debug.Log(sb.ToString());
        if (UnityEngine.Application.isBatchMode || GetArg("-quit") != null) EditorApplication.Exit(dirty == 0 ? 0 : 1);
    }

    static string FirstDifference(JToken a, JToken b, string path)
    {
        if (a == null || b == null) return $"{path}: {(a == null ? "null" : Short(a))} vs {(b == null ? "null" : Short(b))}";
        if (a.Type != b.Type) return $"{path}: type {a.Type} vs {b.Type} ({Short(a)} vs {Short(b)})";

        switch (a.Type)
        {
            case JTokenType.Object:
            {
                JObject oa = (JObject)a, ob = (JObject)b;
                foreach (JProperty p in oa.Properties())
                {
                    if (!ob.TryGetValue(p.Name, out JToken vb)) return $"{path}.{p.Name}: missing in live";
                    string d = FirstDifference(p.Value, vb, $"{path}.{p.Name}");
                    if (d != null) return d;
                }
                foreach (JProperty p in ob.Properties())
                    if (oa[p.Name] == null) return $"{path}.{p.Name}: only in live ({Short(p.Value)})";
                return null;
            }
            case JTokenType.Array:
            {
                JArray aa = (JArray)a, ab = (JArray)b;
                if (aa.Count != ab.Count) return $"{path}: array length {aa.Count} vs {ab.Count}";
                for (int i = 0; i < aa.Count; i++)
                {
                    string d = FirstDifference(aa[i], ab[i], $"{path}[{i}]");
                    if (d != null) return d;
                }
                return null;
            }
            case JTokenType.Float:
                return Math.Abs((float)a - (float)b) < 0.0001f ? null : $"{path}: {a} vs {b}";
            default:
                return JToken.DeepEquals(a, b) ? null : $"{path}: {Short(a)} vs {Short(b)}";
        }
    }

    static string Short(JToken t)
    {
        string s = t.ToString(Newtonsoft.Json.Formatting.None);
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i + 1 < args.Length ? args[i + 1] : "";
        }
        return null;
    }
}
