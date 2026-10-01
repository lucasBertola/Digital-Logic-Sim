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

// N / P of the user's Clock_4 (2 flip-flops in a ring): alone with a driven clock input, then inside CPU_2 with its
// real CLOCK (save-folder project, read-only):
//   Unity.exe -projectPath <proj> -executeMethod ClockProbe.Run -quit
public static class ClockProbe
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            ChipLibrary lib = BenchProject.LoadLibrary(SavePaths.GetProjectPath("PC"), out ChipDescription[] chips);
            ChipDescription c4 = chips.First(c => c.Name == "Clock_4");
            int Bit(SimChip chip, string pin, ChipDescription d) => (int)(chip.OutputPins[Array.FindIndex(d.OutputPins, p => p.Name == pin)].State & 1);

            // alone
            Simulator.ResetForTests(1);
            SimChip root = CircuitTester.BuildIsolatedSim(c4, lib);
            SimChip t = CircuitTester.TargetOf(root);
            sb.Append("Clock_4 alone, Clock input driven (level: P N):\n  ");
            for (int h = 0; h < 16; h++)
            {
                root.InputPins[0].State = PinState.Make((ushort)(h & 1), 0);
                for (int s = 0; s < 30; s++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                sb.Append($"{h & 1}:{Bit(t, "P", c4)}{Bit(t, "N", c4)}  ");
            }
            sb.Append("\n");

            // in CPU_2, real CLOCK, saved inputs
            ChipDescription cpu = chips.First(c => c.Name == "CPU_2");
            Simulator.ResetForTests(2);
            Simulator.stepsPerClockTransition = 70;
            SimChip r2 = CircuitTester.BuildIsolatedSim(cpu, lib);
            SimChip cpuSim = CircuitTester.TargetOf(r2);
            for (int i = 0; i < cpu.InputPins.Length; i++) r2.InputPins[i].State = PinState.Make((ushort)cpu.InputPins[i].InputState, 0);
            var instances = cpu.SubChips.Where(s => s.Name == "Clock_4").ToArray();
            SimChip clock = cpuSim.SubChips.First(s => s.ChipType == ChipType.Clock);
            sb.Append($"CPU_2: CLOCK data [{string.Join(",", clock.InternalState)}], {instances.Length} Clock_4 instances; per half period (CLOCK: P N of each):\n  ");
            for (int h = 0; h < 16; h++)
            {
                for (int s = 0; s < 70; s++) Simulator.RunSimulationStep(r2, Array.Empty<DevPinInstance>(), new SimAudio());
                sb.Append($"{clock.OutputPins[0].State & 1}:" + string.Join("/", instances.Select(i => { SimChip ci = cpuSim.GetSubChipFromID(i.ID); return $"{Bit(ci, "P", c4)}{Bit(ci, "N", c4)}"; })) + "  ");
            }
            sb.Append($"\n  program: {r2.Program.FusedInverters} fused, {r2.Program.CommonGatesMerged} merged\n");
            // what the APP shows: real Project + sim thread, the user's prefs; displayed pin states per main frame
            {
                string dir = SavePaths.GetProjectPath("PC");
                ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(dir, "ProjectDescription.json")));
                pd.ProjectName = "_ClockProbe_readonly";
                pd.Prefs_SimPaused = false;
                var p = new Project(pd, lib) { audioState = new AudioState() };
                p.LoadDevChipOrCreateNewIfDoesntExist("CPU_2");
                p.StartSimulation();
                var inst = p.ViewedChip.Elements.OfType<SubChipInstance>().Where(x => x.Description.Name == "Clock_4").ToArray();
                SubChipInstance clk = p.ViewedChip.Elements.OfType<SubChipInstance>().First(x => x.ChipType == ChipType.Clock);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int frames = 0;
                var changes = new int[inst.Length * 2 + 1];
                uint[] prev = null;
                var seq = new StringBuilder();
                while (sw.ElapsedMilliseconds < 3000)
                {
                    p.TickMainThreadForTests();
                    System.Threading.Thread.Sleep(16);
                    var cur = new uint[changes.Length];
                    for (int i = 0; i < inst.Length; i++)
                    {
                        cur[2 * i] = inst[i].OutputPins.First(o => o.Name == "P").State & 1;
                        cur[2 * i + 1] = inst[i].OutputPins.First(o => o.Name == "N").State & 1;
                    }
                    cur[changes.Length - 1] = clk.OutputPins[0].State & 1;
                    if (prev != null) for (int k = 0; k < cur.Length; k++) if (cur[k] != prev[k]) changes[k]++;
                    if (frames < 40) seq.Append(string.Join("", cur.Take(cur.Length - 1)) + " ");
                    prev = cur; frames++;
                }
                sb.Append($"APP display ({pd.Prefs_SimTargetStepsPerSecond} target, max {pd.Prefs_SimMaxSpeed}, {pd.Prefs_SimStepsPerClockTick}/tick), {frames} frames, {p.simAvgTicksPerSec:0} steps/s; changes seen per pin: " + string.Join(", ", Enumerable.Range(0, inst.Length).Select(i => $"#{i} P={changes[2 * i]} N={changes[2 * i + 1]}")) + $", CLOCK={changes[changes.Length - 1]}\n  PN/PN per frame: {seq}\n");
                p.NotifyExit();
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION " + e); }
        File.WriteAllText(Path.Combine(BenchProject.RepoRoot, "Builds", "clockprobe.txt"), sb.ToString());
        EditorApplication.Exit(0);
    }
}
