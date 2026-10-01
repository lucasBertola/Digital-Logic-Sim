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

// Long run of CPU_2's Clock_4 instances with the real CLOCK, several seeds: does the Johnson sequence ever break
// (N stuck, N = P, a state skipped)? Read-only, save-folder project.
public static class ClockProbe2
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath("PC"), out ChipDescription[] chips);
            ChipDescription c4 = chips.First(c => c.Name == "Clock_4"), cpu = chips.First(c => c.Name == "CPU_2");
            int iP = Array.FindIndex(c4.OutputPins, p => p.Name == "P"), iN = Array.FindIndex(c4.OutputPins, p => p.Name == "N");
            foreach (int steps in new[] { 70, 13, 4 })
            for (int seed = 1; seed <= 10; seed++)
            {
                Simulator.ResetForTests(seed * 7919);
                Simulator.stepsPerClockTransition = steps;
                SimChip r = CircuitTester.BuildIsolatedSim(cpu, lib);
                SimChip t = CircuitTester.TargetOf(r);
                for (int i = 0; i < cpu.InputPins.Length; i++) r.InputPins[i].State = PinState.Make((ushort)cpu.InputPins[i].InputState, 0);
                var inst = cpu.SubChips.Where(s => s.Name == "Clock_4").Select(s => t.GetSubChipFromID(s.ID)).ToArray();
                // sample just before each clock transition (state settled), 400 transitions
                int bad = 0, nChanges = 0, pChanges = 0; string firstBad = null;
                var prev = new int[inst.Length];
                for (int k = 0; k < inst.Length; k++) prev[k] = -1;
                for (int h = 0; h < 400; h++)
                {
                    for (int s = 0; s < steps; s++) Simulator.RunSimulationStep(r, Array.Empty<DevPinInstance>(), new SimAudio());
                    for (int k = 0; k < inst.Length; k++)
                    {
                        int pn = (int)((inst[k].OutputPins[iP].State & 1) << 1 | (inst[k].OutputPins[iN].State & 1));
                        if (prev[k] >= 0 && pn != prev[k])
                        {
                            if (((pn ^ prev[k]) & 1) != 0) nChanges++;
                            if (((pn ^ prev[k]) & 2) != 0) pChanges++;
                            // Johnson PN: 00 -> 10 -> 11 -> 01 -> 00 (one bit at a time, in that order)
                            int[] next = { 2, 0, 3, 1 }; // index = current PN: 00->10(2), 01->00(0), 10->11(3), 11->01(1)
                            if (pn != next[prev[k]]) { bad++; firstBad ??= $"#{k} at transition {h}: {Convert.ToString(prev[k], 2).PadLeft(2, '0')} -> {Convert.ToString(pn, 2).PadLeft(2, '0')}"; }
                        }
                        prev[k] = pn;
                    }
                }
                sb.Append($"steps/tick {steps}, seed {seed}: P changes {pChanges}, N changes {nChanges}, broken transitions {bad} {firstBad}\n");
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "clockprobe2.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }
}
