using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Bench;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;

// Can a random initial state of the flip-flops' NANDs leave the user's Clock_4 stuck? N random starts (every NAND
// output random), then the clock runs; the last transitions must follow the Johnson sequence. Save-folder project.
public static class ClockInitProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath("PC"), out ChipDescription[] chips);
            ChipDescription c4 = chips.First(c => c.Name == "Clock_4");
            int iP = Array.FindIndex(c4.OutputPins, p => p.Name == "P"), iN = Array.FindIndex(c4.OutputPins, p => p.Name == "N");
            int trials = 20000, stuck = 0, nands = 0;
            var kinds = new Dictionary<string, int>();
            string example = null;
            var rnd = new Random(12345);
            for (int trial = 0; trial < trials; trial++)
            {
                Simulator.ResetForTests(trial + 1);
                SimChip root = CircuitTester.BuildIsolatedSim(c4, lib);
                SimChip t = CircuitTester.TargetOf(root);
                var nandList = new List<SimChip>();
                void Walk(SimChip c) { foreach (SimChip s in c.SubChips) { if (s.ChipType == ChipType.Nand) nandList.Add(s); else Walk(s); } }
                Walk(t);
                nands = nandList.Count;
                foreach (SimChip n in nandList) n.OutputPins[0].State = (uint)rnd.Next(2); // random start, as if never saved
                int[] next = { 2, 0, 3, 1 }; // PN: 00->10, 01->00, 10->11, 11->01
                var seq = new List<int>();
                for (int h = 0; h < 24; h++)
                {
                    root.InputPins[0].State = PinState.Make((ushort)(h & 1), 0);
                    for (int s = 0; s < 30; s++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                    if ((h & 1) == 1) seq.Add((int)((t.OutputPins[iP].State & 1) << 1 | (t.OutputPins[iN].State & 1)));
                }
                // the last 6 full cycles must each advance one Johnson step
                bool ok = true;
                for (int k = seq.Count - 6; k < seq.Count; k++) if (seq[k] != next[seq[k - 1]]) ok = false;
                if (!ok)
                {
                    stuck++;
                    string kind = string.Join(" ", seq.Skip(seq.Count - 7).Select(x => Convert.ToString(x, 2).PadLeft(2, '0')));
                    kinds[kind] = kinds.TryGetValue(kind, out int c) ? c + 1 : 1;
                    example ??= $"trial {trial}: PN per cycle {string.Join(" ", seq.Select(x => Convert.ToString(x, 2).PadLeft(2, '0')))}";
                }
            }
            sb.Append($"Clock_4: {nands} NANDs, {trials} random starts, {stuck} never reach the Johnson cycle\n");
            foreach (var kv in kinds.OrderByDescending(k => k.Value).Take(8)) sb.Append($"  {kv.Value} x last cycles {kv.Key}\n");
            if (example != null) sb.Append("  e.g. " + example + "\n");
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "clockinit.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }
}
