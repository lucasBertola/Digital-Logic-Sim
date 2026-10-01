using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;

// Reproduces "switching chip: wires change colour for ~2 s, and is my RAM touched?" on the SAVE-folder project,
// with a real Project and its sim thread, read-only (nothing is saved):
//   Unity.exe -projectPath <proj> -executeMethod SwitchProbe.Run [-chipA CPU_2] [-chipB prog_ram] -quit
public static class SwitchProbe
{
    static readonly StringBuilder sb = new();

    public static void Run()
    {
        try
        {
            string a = Arg("-chipA") ?? "CPU_2", b = Arg("-chipB") ?? "prog_ram";
            string dir = SavePaths.GetProjectPath("PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out ChipDescription[] chips);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            pd.ProjectName = "_SwitchProbe_readonly";
            pd.Prefs_SimPaused = false;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            var cache = MemoryLayout.LoadCache("PC");
            p.LoadDevChipOrCreateNewIfDoesntExist(a);
            p.StartSimulation();
            Watch(p, $"open {a}", 2500);

            List<MemoryBank> Ram()
            {
                ChipDescription cpu = p.chipLibrary.GetChipDescriptionForSim(a);
                SubChipDescription ram = cpu.SubChips.First(s => s.Name == "RAM256");
                ChipDescription rd = p.chipLibrary.GetChipDescription("RAM256");
                SimChip sc = p.rootSimChip.GetSubChipFromID(ram.ID);
                var e = cache["RAM256"];
                return MemoryLayout.Banks(sc, rd, e.rules, e.polarity, p.chipLibrary, out _);
            }
            uint[] Words(List<MemoryBank> bk) { uint[] w = null; p.RunWithSimulationPaused(() => w = Enumerable.Range(0, bk[0].WordCount).Select(i => bk[0].Read(i)).ToArray()); return w; }

            List<MemoryBank> banks = Ram();
            uint[] before = Words(banks);
            // a fresh build of the same chip from the same description: is it identical to this one?
            SimChip fresh = Simulator.BuildSimChip(p.chipLibrary.GetChipDescriptionForSim(a), p.chipLibrary);
            uint edited = (before[5] ^ 0xA5) & 0xFF;
            p.RunWithSimulationPaused(() => banks[0].Write(5, edited, p.rootSimChip.Program)); // what SAVE in the memory editor does
            Watch(p, $"{a} after editing RAM[5] {before[5]} -> {edited}", 500);
            uint[] afterEdit = Words(banks);
            sb.Append($"   RAM[5] reads {afterEdit[5]} (edited {edited}); other words changed by the edit: {Enumerable.Range(0, 256).Count(i => i != 5 && afterEdit[i] != before[i])}\n");

            p.LoadDevChipOrCreateNewIfDoesntExist(b);
            Watch(p, $"switch to {b}", 3000);
            p.LoadDevChipOrCreateNewIfDoesntExist(a);
            Watch(p, $"switch back to {a}", 3000);
            uint[] back = Words(Ram());
            var diff = Enumerable.Range(0, 256).Where(i => i != 5 && back[i] != before[i]).ToList();
            sb.Append("   differing words (before the edit -> after the round trip): " + string.Join(", ", diff.Take(24).Select(i => $"[{i}] {before[i]}->{back[i]}")) + "\n");
            sb.Append($"   after the round trip: RAM[5] = {back[5]} (edited {edited}, before the edit {before[5]}); words different from before the edit (5 excluded): {Enumerable.Range(0, 256).Count(i => i != 5 && back[i] != before[i])}\n");
            p.NotifyExit();
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e + "\n"); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "switchprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }

    // per main frame (16 ms): how many pins of the viewed chip changed their displayed state since the last frame
    static void Watch(Project p, string title, int ms)
    {
        uint[] Snapshot()
        {
            var l = new List<uint>();
            foreach (IMoveable e in p.ViewedChip.Elements)
            {
                if (e is SubChipInstance s) { foreach (PinInstance pin in s.InputPins) l.Add(pin.State); foreach (PinInstance pin in s.OutputPins) l.Add(pin.State); }
                else if (e is DevPinInstance d) l.Add(d.Pin.State);
            }
            return l.ToArray();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        uint[] prev = null;
        int lastChangeMs = -1, frames = 0, framesWithChanges = 0, maxChanged = 0, lastFloatingMs = -1, maxFloating = 0;
        var timeline = new List<string>();
        var everFloating = new HashSet<PinInstance>();
        while (sw.ElapsedMilliseconds < ms)
        {
            p.TickMainThreadForTests();
            Thread.Sleep(16);
            uint[] cur = Snapshot();
            frames++;
            int floating = cur.Count(x => (x >> 16) != 0); // tristate flags: the display draws these as flicker
            foreach (IMoveable e in p.ViewedChip.Elements)
            {
                IEnumerable<PinInstance> pins = e is SubChipInstance sc ? sc.InputPins.Concat(sc.OutputPins) : e is DevPinInstance dp ? new[] { dp.Pin } : Array.Empty<PinInstance>();
                foreach (PinInstance pin in pins) if ((pin.State >> 16) != 0) everFloating.Add(pin);
            }
            if (floating > 0) { lastFloatingMs = (int)sw.ElapsedMilliseconds; maxFloating = Math.Max(maxFloating, floating); if (timeline.Count < 12) timeline.Add($"{sw.ElapsedMilliseconds}ms:{floating} floating"); }
            if (prev != null && prev.Length == cur.Length)
            {
                int changed = 0;
                for (int i = 0; i < cur.Length; i++) if ((cur[i] & 0xFFFF) != (prev[i] & 0xFFFF)) changed++;
                if (changed > 0) { framesWithChanges++; lastChangeMs = (int)sw.ElapsedMilliseconds; maxChanged = Math.Max(maxChanged, changed); if (timeline.Count < 12) timeline.Add($"{sw.ElapsedMilliseconds}ms:{changed}"); }
            }
            prev = cur;
        }
        var names = new List<string>();
        foreach (IMoveable e in p.ViewedChip.Elements)
        {
            IEnumerable<PinInstance> pins = e is SubChipInstance sc ? sc.InputPins.Concat(sc.OutputPins) : e is DevPinInstance dp ? new[] { dp.Pin } : Array.Empty<PinInstance>();
            foreach (PinInstance pin in pins) if (everFloating.Contains(pin) && (pin.State >> 16) == 0) names.Add($"{(e is SubChipInstance s2 ? (string.IsNullOrEmpty(s2.Label) ? s2.Description.Name : s2.Label) : "pin")}.{pin.Name}");
        }
        if (names.Count > 0) sb.Append("   floating early, driven at the end: " + string.Join(", ", names) + "\n");
        sb.Append($"{title}: {frames} frames, {framesWithChanges} with pins changing (max {maxChanged} pins in one frame), last change at {lastChangeMs} ms, floating (flicker) pins: max {maxFloating} of {prev?.Length}, last seen at {lastFloatingMs} ms, steps/s {p.simAvgTicksPerSec:0}\n   {string.Join("  ", timeline)}\n");
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
