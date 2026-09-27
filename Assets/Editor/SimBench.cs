using System;
using System.Diagnostics;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Diagnostic: how fast can the simulator step a given chip? Builds the isolated sim, runs N steps and
// reports ms/step, achievable steps/s and the size of the sim tree (chips / pins at every level).
//   Unity.exe -projectPath <proj> -executeMethod SimBench.Run -benchProject "PC" -benchChip "CPU" -quit -logFile <log>
public static class SimBench
{
    public static void Run()
    {
        string projectName = GetArg("-benchProject") ?? "PC";
        string chipName = GetArg("-benchChip") ?? "CPU";
        StringBuilder sb = new();
        try
        {
            Project project = Loader.LoadProject(projectName);
            ChipDescription desc = project.chipLibrary.GetChipDescription(chipName);
            SimChip root = CircuitTester.BuildIsolatedSim(desc, project.chipLibrary);

            int chips = 0, pins = 0, builtins = 0;
            void Count(SimChip c)
            {
                chips++;
                pins += c.InputPins.Length + c.OutputPins.Length;
                if (c.SubChips.Length == 0) builtins++;
                foreach (SimChip s in c.SubChips) Count(s);
            }
            Count(root);
            sb.Append($"\n=== SimBench \"{chipName}\" ({projectName}) : {chips} chips in the sim tree ({builtins} builtin leaves), {pins} pins ===\n");
            sb.Append($"project target: {project.description.Prefs_SimTargetStepsPerSecond} steps/s, {project.description.Prefs_SimStepsPerClockTick} steps per clock tick\n");

            SimAudio audio = new();
            Simulator.stepsPerClockTransition = project.description.Prefs_SimStepsPerClockTick;
            // warm-up (first steps include the ordering pass)
            for (int i = 0; i < 50; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);

            foreach (int n in new[] { 500, 2000 })
            {
                Stopwatch w = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                w.Stop();
                double msPerStep = w.Elapsed.TotalMilliseconds / n;
                sb.Append($"{n} steps: {w.Elapsed.TotalMilliseconds:0} ms -> {msPerStep:0.000} ms/step -> max {1000.0 / msPerStep:0} steps/s\n");
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION: " + e + "\n"); }

        Debug.Log(sb.ToString());
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
