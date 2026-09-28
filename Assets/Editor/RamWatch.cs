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
using Debug = UnityEngine.Debug;

// What the memory editor shows for the RAM of a running CPU, against what the RAM really does:
//   Unity.exe -projectPath <proj> -executeMethod RamWatch.Run [-watchProject PC] [-watchChip CPU] [-watchRam RAM256] [-watchSteps 70] -quit -logFile <log>
// Uses the project of the SAVE folder (the user's current version, saved inputs and memory state) and the memory
// layout cached by the app. Part 1: every RAM change over 30 clock cycles, with MAR / A / the clock at that step.
// Part 2: the sim on its own thread (like the app), the editor's live reads (MemoryEditMenu.Follow) compared to
// every value each word really held at step boundaries: a value it never held is a torn read.
public static class RamWatch
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            string project = Arg("-watchProject") ?? "PC", chipName = Arg("-watchChip") ?? "CPU", ramName = Arg("-watchRam") ?? "RAM256";
            int stepsPerTick = int.Parse(Arg("-watchSteps") ?? "70");
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath(project), out ChipDescription[] chips);
            ChipDescription cpu = chips.First(c => ChipDescription.NameMatch(c.Name, chipName));
            ChipDescription ramDesc = chips.First(c => ChipDescription.NameMatch(c.Name, ramName));
            var cache = MemoryLayout.LoadCache(project);
            MemoryRules rules = cache.TryGetValue(ramDesc.Name, out var e) ? e.rules : MemoryLayout.RulesFromCache(ramDesc, cache, lib, out _);
            var pol = new Dictionary<string, int[]>();
            string err = MemoryLayout.Verify(ramDesc.Name, rules, lib, pol);
            sb.Append($"layout of {ramDesc.Name} from the app's cache: verify {err ?? "OK"}\n");
            sb.Append("saved inputs: " + string.Join(", ", cpu.InputPins.Select(p => $"{p.Name}={p.InputState}")) + "\n");

            // ---- part 1: single-threaded, every step
            Simulator.ResetForTests(12345);
            Simulator.stepsPerClockTransition = stepsPerTick;
            SimChip root = CircuitTester.BuildIsolatedSim(cpu, lib);
            SimChip target = CircuitTester.TargetOf(root);
            for (int i = 0; i < cpu.InputPins.Length; i++) root.InputPins[i].State = PinState.Make((ushort)cpu.InputPins[i].InputState, 0);
            SimChip Sub(string labelOrName)
            {
                SubChipDescription sd = cpu.SubChips.FirstOrDefault(s => s.Label == labelOrName);
                if (sd.Name == null) sd = cpu.SubChips.FirstOrDefault(s => s.Name == labelOrName);
                if (sd.Name == null) return null;
                (bool ok, SimChip c) = target.TryGetSubChipFromID(sd.ID);
                return ok ? c : null;
            }
            SimChip ram = Sub(ramDesc.Name), mar = Sub("MAR"), regA = Sub("Registre A"), clock = target.SubChips.FirstOrDefault(c => c.ChipType == ChipType.Clock);
            int Out(SimChip c) => c == null ? -1 : (int)(c.OutputPins[0].State & 0xFF);
            List<MemoryBank> banks = MemoryLayout.Banks(ram, ramDesc, rules, pol, lib, out err);
            if (banks == null) throw new Exception("banks: " + err);
            MemoryBank bank = banks[0];
            Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio()); // compiles: before it the cells read their unbuilt value
            uint[] prev = Enumerable.Range(0, bank.WordCount).Select(w => bank.Read(w)).ToArray();
            sb.Append($"\nPART 1: {bank.WordCount} words, {stepsPerTick} steps per clock transition, 30 cycles\n");
            int totalChanges = 0, changesOffMar = 0;
            for (int step = 1; step <= 30 * 2 * stepsPerTick; step++)
            {
                Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                for (int w = 0; w < bank.WordCount; w++)
                {
                    uint v = bank.Read(w);
                    if (v == prev[w]) continue;
                    totalChanges++;
                    if (w != Out(mar)) changesOffMar++;
                    if (totalChanges <= 120)
                        sb.Append($"  step {step,5} (cycle {step / (2 * stepsPerTick)}, clock {Out(clock)}): RAM[{w}] {prev[w]} -> {v}   MAR={Out(mar)} A={Out(regA)}\n");
                    prev[w] = v;
                }
            }
            sb.Append($"  total RAM changes: {totalChanges}, at an address other than MAR's current value: {changesOffMar}\n");

            // ---- part 2: sim thread + live reads like the editor
            Simulator.ResetForTests(777);
            SimChip root2 = CircuitTester.BuildIsolatedSim(cpu, lib);
            SimChip target2 = CircuitTester.TargetOf(root2);
            for (int i = 0; i < cpu.InputPins.Length; i++) root2.InputPins[i].State = PinState.Make((ushort)cpu.InputPins[i].InputState, 0);
            SubChipDescription ramSd = cpu.SubChips.First(s => s.Name == ramDesc.Name);
            (bool ok2, SimChip ram2) = target2.TryGetSubChipFromID(ramSd.ID);
            List<MemoryBank> banks2 = MemoryLayout.Banks(ram2, ramDesc, rules, pol, lib, out err);
            MemoryBank b2 = banks2[0];
            var held = Enumerable.Range(0, b2.WordCount).Select(w => new HashSet<uint> { b2.Read(w) }).ToArray();
            bool stop = false;
            long simSteps = 0;
            var simThread = new Thread(() =>
            {
                Simulator.ResetForTests(777);
                Simulator.stepsPerClockTransition = stepsPerTick;
                while (!Volatile.Read(ref stop))
                {
                    Simulator.RunSimulationStep(root2, Array.Empty<DevPinInstance>(), new SimAudio());
                    for (int w = 0; w < b2.WordCount; w++) { uint v = b2.Read(w); lock (held[w]) held[w].Add(v); }
                    simSteps++;
                }
            });
            simThread.Start();
            uint[][] original = { Enumerable.Range(0, b2.WordCount).Select(w => b2.Read(w)).ToArray() };
            string[][] texts = { original[0].Select(v => v.ToString("X2")).ToArray() };
            var seen = new List<(int w, uint v)>();
            int frames = 0, updates = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                foreach ((int _, int w) in DLS.Graphics.MemoryEditMenu.Follow(banks2, original, texts, 0)) { updates++; seen.Add((w, Convert.ToUInt32(texts[0][w], 16))); }
                frames++;
                Thread.Sleep(16);
            }
            Volatile.Write(ref stop, true);
            simThread.Join();
            int torn = seen.Count(s => !held[s.w].Contains(s.v));
            sb.Append($"\nPART 2: {simSteps} steps on the sim thread in 3 s, {frames} editor frames, {updates} field updates, torn values shown: {torn}\n");
            foreach (var s in seen.Where(s => !held[s.w].Contains(s.v)).Take(10)) sb.Append($"  torn: word {s.w} showed {s.v:X2}, it held {string.Join(",", held[s.w].Select(x => x.ToString("X2")))}\n");
        }
        catch (Exception ex) { sb.Append("EXCEPTION " + ex + "\n"); }
        string path = Path.Combine(BenchProject.RepoRoot, "Builds", "ramwatch.txt");
        File.WriteAllText(path, sb.ToString());
        Debug.Log(sb.ToString());
        EditorApplication.Exit(0);
    }

    static string Arg(string name)
    {
        string[] a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
