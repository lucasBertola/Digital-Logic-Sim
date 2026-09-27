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
// reports ms/step, achievable steps/s, how many gates each step actually ran, and the size of the sim tree.
//   Unity.exe -projectPath <proj> -executeMethod SimBench.Run -benchProject "PC" -benchChip "CPU" [-benchSet "OE_entree=1,ENTREE=5"] [-benchToggle "clock"] -quit -logFile <log>
// -benchToggle flips the named 1-bit input every step (activity: a step where nothing changes costs nothing).
// -benchBatch: step through Simulator.RunSimulationSteps (batches of 256, idle steps skipped) like the live sim thread.
// -benchSettle: with a builtin CLOCK, measures how many steps after each clock edge the chip's pins keep changing
//   (the minimum "steps per clock tick" the montage needs), over 40 edges, inputs as held by -benchSet.
// -benchScenario cpu: drives the user's CPU chip through a micro-program in a loop (A = A + 1; MAR = A;
//   RAM[MAR] = A), clock edges included, so registers, ALU, bus, decoders and RAM all work every cycle.
//   This is the number to compare cores on: the tree walker cost the same whatever the activity.
// -benchSet holds root input pins at the given values for the whole run (e.g. to drive a bus instead of
// leaving it floating, which makes everything downstream recompute the noise every step).
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

            // like the app: every input pin is driven, with the value SAVED in the chip (what the user set), unless -benchSet overrides it
            var saved = new System.Collections.Generic.List<string>();
            for (int i = 0; i < root.InputPins.Length; i++)
            {
                ushort v = (ushort)(desc.InputPins[i].InputState & 0xFFFF);
                root.InputPins[i].State = PinState.Make(v, 0);
                if (v != 0) saved.Add(desc.InputPins[i].Name + "=" + v);
            }
            sb.AppendLine("inputs as saved in the chip: " + (saved.Count == 0 ? "(all 0)" : string.Join(", ", saved)));
            string set = GetArg("-benchSet");
            if (!string.IsNullOrEmpty(set))
            {
                foreach (string kv in set.Split(','))
                {
                    string[] parts = kv.Split('=');
                    if (parts.Length != 2) continue;
                    int idx = Array.FindIndex(desc.InputPins, p => p.Name == parts[0].Trim());
                    if (idx >= 0) root.InputPins[idx].State = PinState.Make(ushort.Parse(parts[1].Trim()), 0);
                    sb.Append($"input {parts[0].Trim()} = {parts[1].Trim()}{(idx < 0 ? " (NOT FOUND)" : "")}\n");
                }
            }

            bool batch = GetArg("-benchBatch") != null;
            if (batch) sb.Append("batched stepping (idle steps skipped)\n");
            string toggle = GetArg("-benchToggle");
            int toggleIdx = string.IsNullOrEmpty(toggle) ? -1 : Array.FindIndex(desc.InputPins, p => p.Name == toggle.Trim());
            if (!string.IsNullOrEmpty(toggle)) sb.Append($"toggling input {toggle} every step{(toggleIdx < 0 ? " (NOT FOUND)" : "")}\n");
            uint toggleState = 0;

            // scenario: a cyclic list of (assignments, steps to hold them)
            string scenario = GetArg("-benchScenario");
            var script = new System.Collections.Generic.List<(string set, int steps)>();
            if (scenario == "cpu")
            {
                const string idle = "OE_ALu=0,OE_entree=0,We_RAM=0,Oe_ram=0,Load_A=0,Load_B=0,Load_MAR=0";
                script.Add((idle + ",Reset_all=1,clock=0", 3)); script.Add(("Reset_all=0", 3));
                sb.Append("scenario cpu: loop { A = A + 1 ; MAR = A ; RAM[MAR] = A } with clock edges\n");
            }
            var loop = new System.Collections.Generic.List<(string set, int steps)>();
            if (scenario == "cpu")
            {
                // A <- A + 1 : ALU INC (OP=2) on the bus, Load_A, clock pulse
                loop.Add(("OE_ALu=1,OP2=0,OP1=1,OP0=0,Load_A=1", 3)); loop.Add(("clock=1", 3)); loop.Add(("clock=0,Load_A=0", 3));
                // MAR <- A : ALU pass A (OP=4), Load_MAR, clock pulse
                loop.Add(("OP2=1,OP1=0,OP0=0,Load_MAR=1", 3)); loop.Add(("clock=1", 3)); loop.Add(("clock=0,Load_MAR=0", 3));
                // RAM[MAR] <- bus (A) : We_RAM pulse while the ALU drives the bus
                loop.Add(("We_RAM=1", 3)); loop.Add(("We_RAM=0,OE_ALu=0", 3));
            }
            void Apply(string set)
            {
                foreach (string kv in set.Split(','))
                {
                    string[] parts = kv.Split('=');
                    int idx = Array.FindIndex(desc.InputPins, p => p.Name == parts[0].Trim());
                    if (idx >= 0) root.InputPins[idx].State = PinState.Make(ushort.Parse(parts[1].Trim()), 0);
                }
            }
            int scriptPos = 0, scriptHold = 0;
            void ScenarioStep()
            {
                if (loop.Count == 0) return;
                if (scriptHold == 0)
                {
                    var cur = script.Count > 0 ? script[0] : loop[scriptPos % loop.Count];
                    if (script.Count > 0) script.RemoveAt(0); else scriptPos++;
                    Apply(cur.set); scriptHold = cur.steps;
                }
                scriptHold--;
            }

            if (GetArg("-benchTrace") != null)
            {
                // print the gates run on a few consecutive steps in the middle of a clock phase
                SimAudio a = new();
                Simulator.stepsPerClockTransition = project.description.Prefs_SimStepsPerClockTick;
                for (int i = 0; i < 300; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), a);
                var names = new System.Collections.Generic.Dictionary<SimChip, string>();
                void Name(SimChip c, ChipDescription d, string path)
                {
                    names[c] = path;
                    for (int i = 0; i < c.SubChips.Length && i < d.SubChips.Length; i++)
                        if (project.chipLibrary.TryGetChipDescription(d.SubChips[i].Name, out ChipDescription sd)) Name(c.SubChips[i], sd, path + "/" + d.SubChips[i].Name + (string.IsNullOrEmpty(d.SubChips[i].Label) ? "" : "[" + d.SubChips[i].Label + "]") + "#" + (d.SubChips[i].ID % 1000));
                }
                Name(CircuitTester.TargetOf(root), desc, chipName);
                root.Program.TraceGates = true;
                for (int s = 0; s < 6; s++)
                {
                    Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), a);
                    sb.Append($"step {Simulator.simulationFrame}: {root.Program.LastStepGates.Count} gates\n");
                    int shown = 0;
                    foreach (int g in root.Program.LastStepGates)
                    {
                        (SimChip c, byte t, uint o) = root.Program.GateInfo(g);
                        string path = c.compileParent != null && names.TryGetValue(c.compileParent, out string pn) ? pn : "?";
                        sb.Append($"   {path} > {(t == 255 ? "MERGE" : ((ChipType)t).ToString())} out={(o & 0xFFFF)}{((o >> 16) != 0 ? "Z" : "")}\n");
                        if (++shown >= 40) { sb.Append("   ...\n"); break; }
                    }
                }
                Debug.Log(sb.ToString());
                EditorApplication.Exit(0);
                return;
            }
            if (GetArg("-benchSettle") != null)
            {
                SimAudio a = new();
                Simulator.stepsPerClockTransition = 400; // long enough for anything to settle
                for (int i = 0; i < 800; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), a);
                var watched = new System.Collections.Generic.List<SimPin>();
                void Collect(SimChip c, int depth) { watched.AddRange(c.InputPins); watched.AddRange(c.OutputPins); if (depth < 1) foreach (SimChip s in c.SubChips) Collect(s, depth + 1); }
                Collect(CircuitTester.TargetOf(root), 0);
                var snapshot = new uint[watched.Count];
                int worst = 0, edges = 0; var hist = new System.Collections.Generic.Dictionary<int, int>();
                while (edges < 40)
                {
                    Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), a);
                    if (Simulator.simulationFrame % 400 != 0) continue; // this step is a clock edge
                    edges++;
                    int last = 0;
                    for (int p = 0; p < watched.Count; p++) snapshot[p] = watched[p].State;
                    for (int t = 1; t < 380; t++)
                    {
                        Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), a);
                        bool changed = false;
                        for (int p = 0; p < watched.Count; p++) { uint st = watched[p].State; if ((st & 0xFFFF) != (snapshot[p] & 0xFFFF) && (st >> 16) == 0) { changed = true; snapshot[p] = st; } else snapshot[p] = st; }
                        if (changed) last = t;
                    }
                    hist[last] = hist.TryGetValue(last, out int cnt) ? cnt + 1 : 1;
                    if (last > worst) worst = last;
                }
                var h = new System.Collections.Generic.List<string>();
                foreach (var kv in hist) h.Add($"{kv.Key} steps x{kv.Value}");
                h.Sort();
                sb.Append($"settle after a clock edge (pins of the chip and of its direct sub-chips, driven values only): worst {worst} steps over {edges} edges; {string.Join(", ", h)}\n");
                sb.Append($"=> a safe 'steps per clock tick' for this montage is about {worst + 2} (currently {project.description.Prefs_SimStepsPerClockTick})\n");
                Debug.Log(sb.ToString());
                EditorApplication.Exit(0);
                return;
            }

            SimAudio audio = new();
            Simulator.stepsPerClockTransition = project.description.Prefs_SimStepsPerClockTick;
            // warm-up (first steps include the compile)
            for (int i = 0; i < 50; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
            sb.Append($"program: {root.Program.GateCount} gates, {root.Program.SlotCount} state slots\n");

            root.Program.CollectStats = true;
            foreach (int n in batch ? new[] { 20000, 200000 } : new[] { 500, 2000 })
            {
                Array.Clear(root.Program.RunsByType, 0, 256);
                root.Program.RescheduleMs = 0;
                Stopwatch w = Stopwatch.StartNew();
                long gatesRun = 0;
                for (int i = 0; i < n; i++)
                {
                    if (toggleIdx >= 0) { toggleState ^= 1; root.InputPins[toggleIdx].State = PinState.Make((ushort)toggleState, 0); }
                    ScenarioStep();
                    if (batch && loop.Count == 0 && toggleIdx < 0) { i += Simulator.RunSimulationSteps(root, Array.Empty<DevPinInstance>(), audio, Math.Min(256, n - i)) - 1; }
                    else Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                    gatesRun += root.Program.GatesRunLastStep;
                }
                w.Stop();
                double msPerStep = w.Elapsed.TotalMilliseconds / n;
                sb.Append($"{n} steps: {w.Elapsed.TotalMilliseconds:0} ms -> {msPerStep:0.000} ms/step -> max {1000.0 / msPerStep:0} steps/s, {gatesRun / (double)n:0} gates run per step, noise list {root.Program.NoiseCount}, reschedule {root.Program.RescheduleMs:0.0} ms total\n");
                var byType = new System.Collections.Generic.List<string>();
                for (int t = 0; t < 256; t++) if (root.Program.RunsByType[t] > 0) byType.Add($"{(t == 255 ? "MERGE" : ((ChipType)t).ToString())}={root.Program.RunsByType[t] / (double)n:0}");
                sb.Append("   per step by type: " + string.Join(", ", byType) + "\n");
                if (scenario == "cpu")
                {
                    SimChip target = CircuitTester.TargetOf(root);
                    foreach (SubChipDescription sd in desc.SubChips)
                    {
                        if (string.IsNullOrEmpty(sd.Label) || !project.chipLibrary.TryGetChipDescription(sd.Name, out ChipDescription d)) continue;
                        (bool ok, SimChip sc) = target.TryGetSubChipFromID(sd.ID);
                        if (ok && sc.OutputPins.Length > 0) sb.Append($"   {sd.Label}.{d.OutputPins[0].Name} = {PinState.GetBitStates(sc.OutputPins[0].State) & 0xFF}\n");
                    }
                }
                // where are the gates that read a floating line? (parent chip name / grandparent)
                var names = new System.Collections.Generic.Dictionary<SimChip, string>();
                void Name(SimChip c, ChipDescription d, string path)
                {
                    names[c] = path;
                    for (int i = 0; i < c.SubChips.Length && i < d.SubChips.Length; i++)
                        if (project.chipLibrary.TryGetChipDescription(d.SubChips[i].Name, out ChipDescription sd)) Name(c.SubChips[i], sd, d.SubChips[i].Name);
                }
                Name(CircuitTester.TargetOf(root), desc, chipName);
                var where = new System.Collections.Generic.Dictionary<string, int>();
                foreach (SimChip c in root.Program.ArmedGateChips())
                {
                    string key = (c.compileParent != null && names.TryGetValue(c.compileParent, out string pn) ? pn : "?") + " > " + (names.TryGetValue(c, out string cn) ? cn : c.ChipType.ToString());
                    where[key] = where.TryGetValue(key, out int cnt) ? cnt + 1 : 1;
                }
                // which chips do the running? (gate runs per step, by grandparent > parent chip)
                var runsBy = new System.Collections.Generic.Dictionary<string, double>();
                foreach ((SimChip c, long runs) in root.Program.GateRuns())
                {
                    string gp = c.compileParent != null && c.compileParent.compileParent != null && names.TryGetValue(c.compileParent.compileParent, out string gn) ? gn : "?";
                    string key = gp + " > " + (c.compileParent != null && names.TryGetValue(c.compileParent, out string pn) ? pn : "?");
                    runsBy[key] = (runsBy.TryGetValue(key, out double r) ? r : 0) + runs / (double)n;
                }
                var runTop = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>(runsBy);
                runTop.Sort((x, y) => y.Value.CompareTo(x.Value));
                var runLines = new System.Collections.Generic.List<string>();
                for (int t = 0; t < Math.Min(10, runTop.Count); t++) runLines.Add($"{runTop[t].Key}={runTop[t].Value:0.0}");
                sb.Append("   gate runs per step by chip: " + string.Join(", ", runLines) + "\n");
                Array.Clear(root.Program.RunsByGate, 0, root.Program.RunsByGate.Length);
                var top = new System.Collections.Generic.List<string>();
                foreach (var kv in where) top.Add($"{kv.Key}={kv.Value}");
                top.Sort((x, y) => int.Parse(y.Substring(y.LastIndexOf('=') + 1)).CompareTo(int.Parse(x.Substring(x.LastIndexOf('=') + 1))));
                sb.Append("   noise readers (parent > chip): " + string.Join(", ", top.GetRange(0, Math.Min(12, top.Count))) + "\n");
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
