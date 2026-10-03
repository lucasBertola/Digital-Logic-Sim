using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using DLS.Bench;
using DLS.Simulation;
using UnityEditor;

// EXPERIMENT: Mono vs Burst on the hot-loop kernel, on a random NAND network the size of the user's CPU.
public static unsafe class BurstProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            const int inputs = 64, gates = 44000;
            int slots = inputs + gates;
            var rnd = new Random(1);
            int[] in0 = new int[gates], in1 = new int[gates], outSlot = new int[gates];
            for (int g = 0; g < gates; g++)
            {
                int Pick() => rnd.Next(100) < 0 ? rnd.Next(slots) : (g < 50 ? rnd.Next(inputs) : inputs + Math.Max(0, g - 1 - rnd.Next(40)));
                in0[g] = Pick(); in1[g] = Pick(); outSlot[g] = inputs + g;
            }
            // consumers of each slot (CSR)
            int[] count = new int[slots + 1];
            for (int g = 0; g < gates; g++) { count[in0[g]]++; count[in1[g]]++; }
            int[] consStart = new int[slots + 1];
            for (int s = 0; s < slots; s++) consStart[s + 1] = consStart[s] + count[s];
            int[] fill = (int[])consStart.Clone();
            int[] consList = new int[consStart[slots]];
            for (int g = 0; g < gates; g++) { consList[fill[in0[g]]++] = g; consList[fill[in1[g]]++] = g; }
            int dirtyWords = (gates + 63) / 64;

            foreach (string mode in new[] { "Mono", "Burst", "Mono", "Burst" })
            {
                uint[] states = new uint[slots];
                ulong[] dirty = new ulong[dirtyWords];
                for (int g = 0; g < gates; g++) dirty[g >> 6] |= 1UL << (g & 63);
                var r2 = new Random(7);
                long runs = 0;
                const int steps = 300000;
                Stopwatch sw = Stopwatch.StartNew();
                fixed (uint* st = states) fixed (int* a = in0) fixed (int* b = in1) fixed (int* o = outSlot) fixed (ulong* d = dirty) fixed (int* cs = consStart) fixed (int* cl = consList)
                {
                    for (int step = 0; step < steps; step++)
                    {
                        if ((step & 7) == 0)
                        {
                            int i = r2.Next(inputs);
                            st[i] ^= 1;
                            for (int c = cs[i]; c < cs[i + 1]; c++) { int k = cl[c]; d[k >> 6] |= 1UL << (k & 63); }
                        }
                        runs += mode == "Mono" ? PropagateMono.Step(st, a, b, o, d, dirtyWords, cs, cl) : PropagateBurst.Step(st, a, b, o, d, dirtyWords, cs, cl);
                    }
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                sb.Append($"{mode}: {steps} steps in {ms:0} ms -> {ms * 1000.0 / steps:0.00} us/step, {runs / (double)steps:0.0} gate runs/step, {ms * 1e6 / Math.Max(1, runs):0.0} ns per gate run\n");
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "burstprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }
}
