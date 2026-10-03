using System;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;

// Which pins of a save-folder chip's top-level components float (drawn flickering), inputs as saved (read-only):
//   Unity.exe -projectPath <proj> -executeMethod FloatProbe.Run -probeChip ALU4 -quit
public static class FloatProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string chip = Environment.GetCommandLineArgs().SkipWhile(a => a != "-probeChip").Skip(1).FirstOrDefault() ?? "ALU4";
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath("PC"), out ChipDescription[] chips);
            ChipDescription d = chips.First(c => c.Name == chip);
            Simulator.ResetForTests(1);
            SimChip root = CircuitTester.BuildIsolatedSim(d, lib);
            SimChip t = CircuitTester.TargetOf(root);
            for (int i = 0; i < d.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)d.InputPins[i].InputState, 0);
            for (int s = 0; s < 3000; s++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
            int n = 0;
            foreach (SubChipDescription sd in d.SubChips)
            {
                ChipDescription cd = lib.GetChipDescriptionForSim(sd.Name);
                SimChip sc = t.GetSubChipFromID(sd.ID);
                for (int i = 0; i < sc.OutputPins.Length; i++)
                    if ((sc.OutputPins[i].DisplayState >> 16) != 0) { n++; sb.Append($"  floating: {sd.Name}.{cd.OutputPins[i].Name}\n"); }
            }
            for (int i = 0; i < t.OutputPins.Length; i++)
                if ((t.OutputPins[i].DisplayState >> 16) != 0) { n++; sb.Append($"  floating: output {d.OutputPins[i].Name}\n"); }
            sb.Insert(0, $"{chip}: {n} floating pins after 3000 steps\n");
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "floatprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }
}
