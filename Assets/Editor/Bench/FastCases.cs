using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // RUN FAST: the fast tree of a chip (modules replaced by their models) must give the SAME outputs as the gates at
    // every settled point, on the user's chips. Gates first, then fast (two roots stepped alternately would recompile
    // at every switch).
    public static class FastCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var list = new List<(string, Func<string>)>();
            foreach (string c in new[] { "ALU8", "ALU4", "Add4", "MUX8-1", "4MUX4-1", "Dec-2-4", "Tampon8" })
            {
                string chip = c;
                list.Add(($"[PC] run fast: {chip} (combinational) gives the gates' outputs on random inputs", () => Combinational(lib, chips, chip)));
            }
            foreach (string c in new[] { "Registre8", "PC", "Bascule D" })
            {
                string chip = c;
                list.Add(($"[PC] run fast: {chip} (sequential) gives the gates' outputs, cycle after cycle", () => Sequential(lib, chips, chip)));
            }
            list.Add(("[PC] run fast: ALU8 is built from table models (its ALU4s), the CPU's clock is never inside a model", () => Decisions(lib, chips)));
            list.Add(("[PC] run fast: the CPU micro-program runs on the fast tree", () => CpuProgram(lib, chips)));
            list.Add(("[PC] automatic run fast: an empty cache is not \"ready\"; after Prepare it is, Build then computes nothing and models exactly the count announced (ALU8, Registre8)", () => PrepareCovers(lib, chips)));
            list.Add(("[PC] run fast: sequential modules get a verified model (Registre8 register, PC counter, RAMs, Bascule D flip-flop)", () => Templates(lib, chips)));
            return list;
        }

        static readonly object cacheLock = new();
        static FastCache shared;
        static FastCache Cache() { lock (cacheLock) return shared ??= new FastCache(); } // the decisions are pure: share them between cases

        static SimChip FastRoot(ChipDescription d, ChipLibrary lib, List<FastBuilder.Instance> inst = null)
        {
            return FastBuilder.Build(d, lib, Cache(), inst ?? new List<FastBuilder.Instance>());
        }

        static uint[] Outputs(SimChip root) => root.OutputPins.Select(p => p.State).ToArray();

        static List<uint[]> Run(SimChip root, bool isolated, List<uint[]> stimulus, int settle)
        {
            SimChip ins = root, outs = isolated ? CircuitTester.TargetOf(root) : root;
            var results = new List<uint[]>();
            foreach (uint[] s in stimulus)
            {
                for (int i = 0; i < s.Length; i++) ins.InputPins[i].State = PinState.Make((ushort)s[i], 0);
                for (int k = 0; k < settle; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                results.Add(outs.OutputPins.Select(p => p.State).ToArray());
            }
            return results;
        }

        static string Compare(ChipDescription d, List<uint[]> stimulus, List<uint[]> gates, List<uint[]> fast, int skip = 0)
        {
            for (int t = skip; t < stimulus.Count; t++)
                for (int o = 0; o < d.OutputPins.Length; o++)
                    if (gates[t][o] != fast[t][o])
                        return $"vector {t} (inputs {string.Join(",", stimulus[t])}): {d.OutputPins[o].Name} = {fast[t][o]:X8} in fast mode, {gates[t][o]:X8} with the gates";
            return null;
        }

        static string Combinational(ChipLibrary lib, ChipDescription[] chips, string chip)
        {
            ChipDescription d = chips.First(c => c.Name == chip);
            var rnd = new Random(chip.GetHashCode());
            var stimulus = Enumerable.Range(0, 300).Select(_ => d.InputPins.Select(p => (uint)rnd.Next(1 << (int)p.BitCount)).ToArray()).ToList();
            Simulator.ResetForTests(1);
            SimChip g;
            g = CircuitTester.BuildIsolatedSim(d, lib);
            var gates = Run(g, true, stimulus, 16);
            Simulator.ResetForTests(1);
            var inst = new List<FastBuilder.Instance>();
            var fast = Run(FastRoot(d, lib, inst), false, stimulus, 16);
            Simulator.ClearTestSeed();
            return Compare(d, stimulus, gates, fast);
        }

        // reset first (both start in a known state), then cycles: data changes while the clock is low, clock high, clock low
        static string Sequential(ChipLibrary lib, ChipDescription[] chips, string chip)
        {
            ChipDescription d = chips.First(c => c.Name == chip);
            int clk = Array.FindIndex(d.InputPins, p => p.Name.ToLower().Contains("cl"));
            int rst = Array.FindIndex(d.InputPins, p => p.Name.ToLower().Contains("reset"));
            if (clk < 0) return "test setup: no clock input";
            var rnd = new Random(chip.GetHashCode());
            var stimulus = new List<uint[]>();
            uint[] cur = new uint[d.InputPins.Length];
            void Add(uint[] s) => stimulus.Add((uint[])s.Clone());
            if (rst >= 0) { cur[rst] = 1; Add(cur); cur[clk] = 1; Add(cur); cur[clk] = 0; Add(cur); cur[rst] = 0; Add(cur); }
            for (int cycle = 0; cycle < 120; cycle++)
            {
                for (int i = 0; i < cur.Length; i++)
                    if (i != clk && i != rst) cur[i] = (uint)rnd.Next(1 << (int)d.InputPins[i].BitCount);
                if (rst >= 0) cur[rst] = rnd.Next(25) == 0 ? 1u : 0u;
                cur[clk] = 0; Add(cur);
                cur[clk] = 1; Add(cur);
                cur[clk] = 0; Add(cur);
            }
            Simulator.ResetForTests(3);
            SimChip g;
            g = CircuitTester.BuildIsolatedSim(d, lib);
            var gates = Run(g, true, stimulus, 24);
            Simulator.ResetForTests(3);
            var fast = Run(FastRoot(d, lib), false, stimulus, 24);
            Simulator.ClearTestSeed();
            return Compare(d, stimulus, gates, fast, rst >= 0 ? 4 : 30);
        }

        static string Decisions(ChipLibrary lib, ChipDescription[] chips)
        {
            var inst = new List<FastBuilder.Instance>();
            FastRoot(chips.First(c => c.Name == "ALU8"), lib, inst);
            if (!inst.Any(i => i.Desc.Name == "ALU4")) return "ALU8's ALU4s are not table models: " + string.Join(", ", inst.Select(i => i.Desc.Name));
            var cpuInst = new List<FastBuilder.Instance>();
            SimChip cpu = FastRoot(chips.First(c => c.Name == "CPU"), lib, cpuInst);
            if (cpu.SubChips.All(s => s.ChipType != ChipType.Clock)) return "the CPU's CLOCK is missing from the fast tree";
            return cpuInst.Count == 0 ? "nothing modelled in the CPU" : null;
        }

        static string PrepareCovers(ChipLibrary lib, ChipDescription[] chips)
        {
            foreach (string name in new[] { "ALU8", "Registre8" })
            {
                ChipDescription d = chips.First(c => c.Name == name);
                var cache = new FastCache(); // its own: the shared one may already hold everything
                if (FastBuilder.CachedModels(d, lib, cache) != -1) return $"{name}: an empty cache counts as ready";
                if (FastBuilder.IsDecided(d, lib, cache)) return $"{name}: decided in an empty cache";
                FastBuilder.Prepare(d, lib, cache);
                int announced = FastBuilder.CachedModels(d, lib, cache);
                if (announced < 0) return $"{name}: still not ready after Prepare";
                if (!FastBuilder.IsDecided(d, lib, cache)) return $"{name}: its own decision (as a module) is not in the cache after Prepare";
                cache.Changed = false;
                var inst = new List<FastBuilder.Instance>();
                FastBuilder.Build(d, lib, cache, inst);
                if (cache.Changed) return $"{name}: Build still computed decisions after Prepare";
                if (inst.Count != announced) return $"{name}: {announced} models announced, Build made {inst.Count}";
                if (announced == 0) return $"{name}: nothing modelled";
            }
            return null;
        }

        static string Templates(ChipLibrary lib, ChipDescription[] chips)
        {
            var expect = new (string chip, FastTemplates.Kind kind)[]
            {
                ("Registre8", FastTemplates.Kind.Register), ("Bascule D", FastTemplates.Kind.Register), ("PC", FastTemplates.Kind.Counter),
                ("RAM4", FastTemplates.Kind.Ram), ("RAM16", FastTemplates.Kind.Ram), ("RAM64", FastTemplates.Kind.Ram), ("RAM256", FastTemplates.Kind.Ram),
            };
            var found = new List<string>();
            foreach (var (chip, kind) in expect)
            {
                FastModel m = FastBuilder.Decide(chips.First(c => c.Name == chip), lib, Cache());
                if (m is not SeqModel s || s.Spec.Kind != kind) return $"{chip}: expected a verified {kind} model, got {(m == null ? "none" : m is SeqModel sm ? sm.Spec.ToString() : m.GetType().Name)}";
                found.Add($"{chip}: {s.Spec}");
            }
            UnityEngine.Debug.Log("run fast templates: " + string.Join(" | ", found));
            return null;
        }

        static string CpuProgram(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            if (!DirectedCases.Bodies.TryGetValue("CPU: micro-program: A=5, B=3, A+B on the bus, store to RAM[9], read back, A-B", out var program)) return "micro-program case not registered";
            using var f = new DirectedCases.Fixture(cpu, lib, () => FastBuilder.Build(cpu, lib, Cache(), new List<FastBuilder.Instance>()));
            string err = program(f);
            return err == null ? null : "micro-program on the fast tree: " + err;
        }
    }
}
