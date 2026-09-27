using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // Golden (recorded-behaviour) test of one chip: a deterministic random stimulus is played on the isolated
    // simulation (clock driven by the harness), and after every step the outputs AND the output pins of every
    // top-level sub-chip (probes) are compared to what was recorded. Every run is seeded (Simulator.ResetForTests),
    // so a run is exactly reproducible. Values that were not identical across three differently seeded recording
    // runs (race conditions, floating buses) are masked out: they depend on the random stream, not on the logic.
    public class GoldenRecord
    {
        public string chip;
        public int seed;
        public int steps;
        public int ticksPerStep;
        public string[] inputs;
        public int[] inputBits;
        public string[] outputs;
        public int[] outputBits;
        public bool clockComponent;   // the chip (or a sub-chip) contains a CLOCK component, driven via forcedClockState
        public int clockInput = -1;   // index of an input pin named like a clock, driven 0/1/0/1...
        public List<ushort[]> inputVectors = new();
        public List<ushort[]> outBits = new();
        public List<ushort[]> outTri = new();
        public List<bool[]> stable = new();
        public string[] probes;       // "SubChip#id.PIN": output pins of the top-level sub-chips
        public int[] probeBits;
        public List<ushort[]> probeVals = new();
        public List<ushort[]> probeTri = new();
        public List<bool[]> probeStable = new();
        public int simChips;          // informational: size of the sim tree when recorded
        public int simPins;
    }

    public static class GoldenRunner
    {
        // BuildIsolatedSim temporarily rewires the library's SimOverride: builds must not overlap.
        static readonly object buildLock = new();

        public static int StepsFor(ChipDescription desc, ChipLibrary lib)
        {
            // big chips (CPU: ~63 000 sim chips) get fewer steps so the whole bench stays fast
            int size = CountChips(desc, lib, 0);
            return size > 20000 ? 30 : size > 3000 ? 45 : 60;
        }

        static int CountChips(ChipDescription desc, ChipLibrary lib, int depth)
        {
            if (desc.SubChips == null || depth > 20) return 1;
            int n = 1;
            foreach (var sd in desc.SubChips)
                if (lib.TryGetChipDescription(sd.Name, out ChipDescription d)) n += CountChips(d, lib, depth + 1);
            return n;
        }

        class Trace
        {
            public List<ushort[]> bits = new(), tri = new(), pBits = new(), pTri = new();
            public bool hasClock;
            public int simChips, simPins;
        }

        public static GoldenRecord Record(ChipDescription desc, ChipLibrary lib)
        {
            var rec = new GoldenRecord
            {
                chip = desc.Name,
                seed = StableHash(desc.Name),
                steps = StepsFor(desc, lib),
                ticksPerStep = 4,
                inputs = desc.InputPins.Select(p => p.Name).ToArray(),
                inputBits = desc.InputPins.Select(p => (int)p.BitCount).ToArray(),
                outputs = desc.OutputPins.Select(p => p.Name).ToArray(),
                outputBits = desc.OutputPins.Select(p => (int)p.BitCount).ToArray()
            };
            rec.clockInput = FindClockInput(rec.inputs, rec.inputBits);
            (rec.probes, rec.probeBits) = ProbeList(desc, lib);

            // stimulus (independent of the sim RNG)
            var stim = new Random(rec.seed);
            for (int s = 0; s < rec.steps; s++)
            {
                var v = new ushort[rec.inputs.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    int mask = (1 << rec.inputBits[i]) - 1;
                    v[i] = i == rec.clockInput ? (ushort)(s & 1) : (ushort)(stim.Next() & mask);
                }
                rec.inputVectors.Add(v);
            }

            // five runs with different sim seeds: only what agrees is asserted (a random conflict that happened
            // to resolve the same way in every run would otherwise be recorded as a fact)
            var runs = new List<Trace>();
            foreach (int simSeed in new[] { rec.seed, rec.seed + 101, rec.seed + 202, rec.seed + 303, rec.seed + 404 })
                runs.Add(Play(desc, lib, rec, simSeed));

            rec.clockComponent = runs[0].hasClock;
            rec.simChips = runs[0].simChips;
            rec.simPins = runs[0].simPins;
            rec.outBits = runs[0].bits;
            rec.outTri = runs[0].tri;
            rec.probeVals = runs[0].pBits;
            rec.probeTri = runs[0].pTri;
            for (int s = 0; s < rec.steps; s++)
            {
                rec.stable.Add(StableMask(runs.Select(r => (r.bits[s], r.tri[s])).ToList()));
                rec.probeStable.Add(StableMask(runs.Select(r => (r.pBits[s], r.pTri[s])).ToList()));
            }
            return rec;
        }

        static bool[] StableMask(List<(ushort[] bits, ushort[] tri)> runs)
        {
            var st = new bool[runs[0].bits.Length];
            for (int j = 0; j < st.Length; j++)
                st[j] = runs.All(r => r.bits[j] == runs[0].bits[j] && r.tri[j] == runs[0].tri[j]);
            return st;
        }

        // Returns null when the chip behaves as recorded, else a description of the first mismatches.
        public static string Verify(GoldenRecord rec, ChipDescription desc, ChipLibrary lib)
        {
            if (rec.inputs.Length != desc.InputPins.Length || rec.outputs.Length != desc.OutputPins.Length)
                return $"interface changed: {desc.InputPins.Length} in / {desc.OutputPins.Length} out vs recorded {rec.inputs.Length} / {rec.outputs.Length} (re-record)";
            (string[] probes, _) = ProbeList(desc, lib);
            if (rec.probes != null && !probes.SequenceEqual(rec.probes))
                return "sub-chips changed since the golden was recorded (re-record)";

            Trace run = Play(desc, lib, rec, rec.seed);
            var sb = new StringBuilder();
            int mismatches = 0, asserted = 0;
            for (int s = 0; s < rec.steps; s++)
            {
                Compare(s, rec.outputs, rec.outputBits, rec.stable[s], run.bits[s], run.tri[s], rec.outBits[s], rec.outTri[s], ref mismatches, ref asserted, sb);
                if (rec.probes != null)
                    Compare(s, rec.probes, rec.probeBits, rec.probeStable[s], run.pBits[s], run.pTri[s], rec.probeVals[s], rec.probeTri[s], ref mismatches, ref asserted, sb);
            }
            if (mismatches == 0) return null;
            return $"{mismatches}/{asserted} asserted values differ: {sb}";
        }

        static void Compare(int s, string[] names, int[] widths, bool[] stable, ushort[] gotB, ushort[] gotT, ushort[] expB, ushort[] expT, ref int mismatches, ref int asserted, StringBuilder sb)
        {
            for (int j = 0; j < names.Length; j++)
            {
                if (!stable[j]) continue;
                asserted++;
                if (gotB[j] == expB[j] && gotT[j] == expT[j]) continue;
                mismatches++;
                if (mismatches <= 5)
                    sb.Append($"step {s + 1} {names[j]}: got {Fmt(gotB[j], gotT[j], widths[j])} expected {Fmt(expB[j], expT[j], widths[j])}; ");
            }
        }

        static string Fmt(ushort bits, ushort tri, int width)
        {
            var sb = new StringBuilder();
            for (int b = width - 1; b >= 0; b--)
            {
                bool t = ((tri >> b) & 1) == 1;
                sb.Append(t ? 'Z' : (((bits >> b) & 1) == 1 ? '1' : '0'));
            }
            return sb.ToString();
        }

        // Output pins of the top-level sub-chips, in description order (the CPU has no output pin at all:
        // its registers, ALU and bus are what a regression would show up on).
        static (string[] names, int[] bits) ProbeList(ChipDescription desc, ChipLibrary lib)
        {
            var names = new List<string>();
            var bits = new List<int>();
            foreach (SubChipDescription sd in desc.SubChips ?? Array.Empty<SubChipDescription>())
            {
                if (!lib.TryGetChipDescription(sd.Name, out ChipDescription d)) continue;
                for (int j = 0; j < d.OutputPins.Length; j++)
                {
                    names.Add($"{sd.Name}#{sd.ID}.{d.OutputPins[j].Name}");
                    bits.Add((int)d.OutputPins[j].BitCount);
                }
            }
            return (names.ToArray(), bits.ToArray());
        }

        static List<SimPin> ProbePins(ChipDescription desc, ChipLibrary lib, SimChip target)
        {
            var pins = new List<SimPin>();
            foreach (SubChipDescription sd in desc.SubChips ?? Array.Empty<SubChipDescription>())
            {
                if (!lib.TryGetChipDescription(sd.Name, out ChipDescription d)) continue;
                (bool ok, SimChip sc) = target.TryGetSubChipFromID(sd.ID);
                for (int j = 0; j < d.OutputPins.Length; j++) pins.Add(ok && j < sc.OutputPins.Length ? sc.OutputPins[j] : null);
            }
            return pins;
        }

        static Trace Play(ChipDescription desc, ChipLibrary lib, GoldenRecord rec, int simSeed)
        {
            Simulator.ResetForTests(simSeed);
            Simulator.stepsPerClockTransition = 250;
            SimChip root;
            lock (buildLock) root = CircuitTester.BuildIsolatedSim(desc, lib);
            SimChip target = CircuitTester.TargetOf(root);
            List<SimPin> probePins = ProbePins(desc, lib, target);

            var t = new Trace();
            void Walk(SimChip c) { t.simChips++; t.simPins += c.InputPins.Length + c.OutputPins.Length; if (c.ChipType == ChipType.Clock) t.hasClock = true; foreach (SimChip s in c.SubChips) Walk(s); }
            Walk(root);

            var audio = new SimAudio();
            try
            {
                Simulator.forcedClockState = t.hasClock ? 0 : -1;
                for (int s = 0; s < rec.steps; s++)
                {
                    ushort[] v = rec.inputVectors[s];
                    for (int i = 0; i < v.Length && i < root.InputPins.Length; i++) root.InputPins[i].State = PinState.Make(v[i], 0);
                    if (t.hasClock) Simulator.forcedClockState = s & 1;

                    for (int k = 0; k < rec.ticksPerStep; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);

                    Sample(target.OutputPins, rec.outputBits, out ushort[] b, out ushort[] tr);
                    t.bits.Add(b); t.tri.Add(tr);
                    Sample(probePins, rec.probeBits ?? Array.Empty<int>(), out ushort[] pb, out ushort[] pt);
                    t.pBits.Add(pb); t.pTri.Add(pt);
                }
            }
            finally
            {
                Simulator.forcedClockState = -1;
                Simulator.ClearTestSeed();
            }
            return t;
        }

        static void Sample(IReadOnlyList<SimPin> pins, int[] widths, out ushort[] bits, out ushort[] tri)
        {
            int n = Math.Min(pins.Count, widths.Length);
            bits = new ushort[n];
            tri = new ushort[n];
            for (int j = 0; j < n; j++)
            {
                if (pins[j] == null) continue;
                uint st = pins[j].State;
                int mask = (1 << widths[j]) - 1;
                bits[j] = (ushort)(PinState.GetBitStates(st) & mask);
                tri[j] = (ushort)(PinState.GetTristateFlags(st) & mask);
            }
        }

        public static int FindClockInput(string[] names, int[] bits)
        {
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i].Trim().ToUpperInvariant();
                if (n == "CLOCK" || n == "CLK" || n == "HORLOGE") return i;
            }
            var candidates = new List<int>();
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i].ToUpperInvariant();
                if (bits[i] == 1 && (n.Contains("CLOCK") || n.Contains("CLK") || n.Contains("HORLOGE"))) candidates.Add(i);
            }
            return candidates.Count == 1 ? candidates[0] : -1;
        }

        // Deterministic across runs and machines (string.GetHashCode is randomised per process)
        public static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7FFFFFFF;
            }
        }
    }
}
