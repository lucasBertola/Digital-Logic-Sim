using System;
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

// Reproduces "in CPU_2, open MAR, toggle Reset_all: its wire never lights" on the SAVE-folder project, with a real
// Project and its sim thread, read-only:  Unity.exe -projectPath <proj> -executeMethod ResetProbe.Run [-chipA CPU_2] [-chipB MAR] [-pin Reset_all] -quit
public static class ResetProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string a = Arg("-chipA") ?? "CPU_2", b = Arg("-chipB") ?? "MAR", pinName = Arg("-pin") ?? "Reset_all";
            string dir = SavePaths.GetProjectPath("PC");
            ChipLibrary lib = BenchProject.LoadLibrary(dir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
            pd.ProjectName = "_ResetProbe_readonly";
            pd.Prefs_SimPaused = false;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            p.LoadDevChipOrCreateNewIfDoesntExist(a);
            p.StartSimulation();
            Pump(p, 1500);
            p.LoadDevChipOrCreateNewIfDoesntExist(b);
            Pump(p, 1500);
            DevPinInstance pin = p.ViewedChip.Elements.OfType<DevPinInstance>().First(d => d.IsInputPin && d.Name == pinName);
            foreach (uint v in new uint[] { 1, 0, 1 })
            {
                pin.Pin.PlayerInputState = v;
                Pump(p, 600);
                SimPin simPin = null;
                try { simPin = p.rootSimChip.GetSimPinFromAddress(pin.Pin.Address); } catch (Exception) { }
                sb.Append($"{pinName} := {v}: dev pin State {pin.Pin.State:X}, sim root pin {(simPin == null ? "NOT FOUND" : simPin.State.ToString("X"))}, root inputs {p.rootSimChip.InputPins.Length}, fast {p.FastModeActive}\n");
                foreach (WireInstance w in p.ViewedChip.Wires.Where(w => w.SourcePin == pin.Pin))
                    sb.Append($"   wire -> {w.TargetPin.parent} . {w.TargetPin.Name}: target State {w.TargetPin.State:X}, colour source state {w.SourcePin.State:X}\n");
            }
            sb.Append($"sim thread exceptions: {p.simThreadExceptions}; steps run by the program: {p.rootSimChip.Program?.StepsRun}\n");
            p.NotifyExit();
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e + "\n"); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "resetprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }

    static void Pump(Project p, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { p.TickMainThreadForTests(); Thread.Sleep(16); }
    }

    static string Arg(string n) { string[] a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
