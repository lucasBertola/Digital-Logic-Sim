using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEngine;

namespace DLS.Game
{
    // Offline QA / debug harness. Builds an ISOLATED copy of a chip's simulation (so the live circuit the
    // user is looking at is never disturbed), drives its inputs step by step and reads back what can
    // actually be observed: output pins, on-chip displays (LED / 7-segment / RGB / dot) and, optionally,
    // internal probe pins. State is kept between steps, so sequential circuits (latches, counters, RAM)
    // behave as they would in the app.
    //
    // Must be called with the sim thread parked (Project.RunWithSimulationPaused): the Simulator is static.
    public static class CircuitTester
    {
        public const int MaxSteps = 64;
        public const int MaxTicksPerStep = 500;
        public const int DefaultTicksPerStep = 5;

        const int HarnessSubChipID = 100_000_000;
        const string TargetRefName = "__qa_target__";

        public const int MaxClockCyclesPerStep = 64;
        public const string ClockControlName = "CLOCK";

        // One step of a test sequence: (re)position some inputs, then let the sim run `ticks` ticks. When
        // `clocks` > 0, complete clock cycles are played instead (see RunSequence).
        public class Step
        {
            public readonly List<(string pin, string value)> sets = new();
            public int ticks = DefaultTicksPerStep;
            public int clocks;
            public string note;
        }

        // ---------------- isolated simulation harness ----------------

        // Wraps the chip as a single subchip of a container, with dev pins wired straight to it, so its own
        // logic actually runs (the simulator only processes subchips, not the root's own logic).
        public static ChipDescription MakeHarness(ChipDescription inner, string innerRefName)
        {
            var wires = new List<WireDescription>();
            foreach (PinDescription inPin in inner.InputPins)
                wires.Add(new WireDescription
                {
                    SourcePinAddress = new PinAddress(inPin.ID, 0),
                    TargetPinAddress = new PinAddress(HarnessSubChipID, inPin.ID),
                    ConnectionType = WireConnectionType.ToPins,
                    ConnectedWireIndex = -1,
                    ConnectedWireSegmentIndex = -1,
                    Points = Array.Empty<Vector2>()
                });
            foreach (PinDescription outPin in inner.OutputPins)
                wires.Add(new WireDescription
                {
                    SourcePinAddress = new PinAddress(HarnessSubChipID, outPin.ID),
                    TargetPinAddress = new PinAddress(outPin.ID, 0),
                    ConnectionType = WireConnectionType.ToPins,
                    ConnectedWireIndex = -1,
                    ConnectedWireSegmentIndex = -1,
                    Points = Array.Empty<Vector2>()
                });

            return new ChipDescription
            {
                Name = "__harness__",
                NameLocation = NameDisplayLocation.Centre,
                ChipType = ChipType.Custom,
                Size = Vector2.zero,
                Colour = new Color(0, 0, 0, 0),
                InputPins = inner.InputPins,
                OutputPins = inner.OutputPins,
                SubChips = new[] { new SubChipDescription(innerRefName, HarnessSubChipID, null, Vector2.zero, null, null) },
                Wires = wires.ToArray(),
                Displays = Array.Empty<DisplayDescription>()
            };
        }

        // Builds a fresh sim of `liveDesc` (its LIVE form, unsaved edits included) inside a harness. The
        // target is referenced under a temp name resolved through SimOverride, so this also works for a
        // chip that has never been saved.
        public static SimChip BuildIsolatedSim(ChipDescription liveDesc, ChipLibrary lib)
        {
            ChipDescription harness = MakeHarness(liveDesc, TargetRefName);
            lock (lib) // SimOverride is swapped for the duration of the build: builds on one library must not overlap
            {
                Func<string, ChipDescription> prevOverride = lib.SimOverride;
                lib.SimOverride = n => ChipDescription.NameMatch(n, TargetRefName) ? liveDesc : prevOverride?.Invoke(n);
                try { return Simulator.BuildSimChip(harness, lib); }
                finally { lib.SimOverride = prevOverride; }
            }
        }

        public static SimChip TargetOf(SimChip harnessRoot) => harnessRoot.GetSubChipFromID(HarnessSubChipID);

        // ---------------- test sequence ----------------

        public static string RunSequence(Project p, List<Step> steps, List<string> watchLabels)
        {
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            string chipName = string.IsNullOrEmpty(desc.Name) ? "(brique courante)" : desc.Name;

            if (steps == null || steps.Count == 0) return "Aucune etape fournie.";
            if (steps.Count > MaxSteps) return $"Trop d'etapes ({steps.Count}, max {MaxSteps}).";

            string[] inNames = CircuitExporter.Disambiguate(desc.InputPins.Select(x => x.Name).ToArray());
            string[] outNames = CircuitExporter.Disambiguate(desc.OutputPins.Select(x => x.Name).ToArray());
            int[] inBits = desc.InputPins.Select(x => Bits(x.BitCount)).ToArray();
            int[] outBits = desc.OutputPins.Select(x => Bits(x.BitCount)).ToArray();

            int savedFrame = Simulator.simulationFrame;
            var sb = new StringBuilder();

            try
            {
                SimChip root = BuildIsolatedSim(desc, p.chipLibrary);
                SimChip target = TargetOf(root);

                var keyChips = FindKeyChips(desc, target);
                var displays = FindDisplays(p.ViewedChip, desc);
                var probes = ResolveProbes(p, watchLabels, out List<string> probeErrors);

                if (desc.OutputPins.Length == 0 && displays.Count == 0 && probes.Count == 0)
                    return "Rien d'observable : ce module n'a ni sortie, ni afficheur, et aucun point de mesure (watch) valide.";

                bool hasClock = ContainsClock(target);

                // An input pin named like a clock (CLOCK, CLK, Clock_in...) is driven by "clocks" too: a chip
                // built from latches typically has such an input instead of a CLOCK component, and silently
                // ignoring "clocks" there once made the assistant conclude the circuit was broken.
                int clockInputIdx = Array.FindIndex(inNames, n => IsClockName(n));
                if (clockInputIdx < 0)
                {
                    int[] candidates = Enumerable.Range(0, inNames.Length).Where(i => inBits[i] == 1 && LooksLikeClockName(inNames[i])).ToArray();
                    if (candidates.Length == 1) clockInputIdx = candidates[0];
                }
                bool clocksDriveSomething = hasClock || clockInputIdx >= 0;

                // Resolve every step's targets up front, so a typo fails before anything is simulated.
                // kind 0 = input pin, 1 = KEY chip, 2 = the clock level
                var plan = new List<List<(int kind, int index, ushort value, string label)>>();
                bool anyClockDriving = false;
                foreach (Step st in steps)
                {
                    var actions = new List<(int, int, ushort, string)>();
                    anyClockDriving |= st.clocks > 0;
                    foreach ((string pin, string value) in st.sets)
                    {
                        string name = (pin ?? "").Trim();
                        int idx = Array.FindIndex(inNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            if (!TryParseValue(value, inBits[idx], out ushort v, out string verr))
                                return $"Etape {plan.Count + 1}, entree \"{name}\" : {verr}";
                            actions.Add((0, idx, v, name));
                            continue;
                        }

                        int keyIdx = keyChips.FindIndex(k => string.Equals(k.label, name, StringComparison.OrdinalIgnoreCase));
                        if (keyIdx >= 0)
                        {
                            if (!TryParseValue(value, 1, out ushort v, out string verr))
                                return $"Etape {plan.Count + 1}, touche \"{name}\" : {verr}";
                            actions.Add((1, keyIdx, v, name));
                            continue;
                        }

                        // "CLOCK" (or any CLOCK component label) drives every clock of the circuit: they are
                        // all in phase in this simulator, so one level is faithful.
                        if ((hasClock || clockInputIdx >= 0) && (string.Equals(name, ClockControlName, StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith(ClockControlName + "#", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!TryParseValue(value, 1, out ushort v, out string verr))
                                return $"Etape {plan.Count + 1}, horloge : {verr}";
                            actions.Add((2, 0, v, ClockControlName));
                            anyClockDriving = true;
                            continue;
                        }

                        var known = new List<string>(inNames);
                        known.AddRange(keyChips.Select(k => k.label));
                        if (hasClock) known.Add(ClockControlName);
                        return $"Etape {plan.Count + 1} : \"{name}\" n'est pas une entree de \"{chipName}\". " +
                               (known.Count == 0 ? "Ce module n'a aucune entree." : "Entrees disponibles : " + string.Join(", ", known) + ".");
                    }

                    plan.Add(actions);
                }

                // ---- run ----
                SimAudio audio = new();
                var inputStates = new ushort[desc.InputPins.Length]; // all inputs start LOW
                var heldKeys = new HashSet<char>();
                SimKeyboardHelper.SetVirtualKeys(heldKeys); // ignore the real keyboard while testing
                int clockLevel = 0;
                if (hasClock) Simulator.forcedClockState = 0; // the test drives the clock, real time does not

                sb.Append($"Test \"{chipName}\" ({steps.Count} etape{(steps.Count > 1 ? "s" : "")}). Depart : entrees a 0");
                if (keyChips.Count > 0) sb.Append(", touches relachees");
                if (hasClock) sb.Append(", CLOCK a 0");
                sb.AppendLine(". Memoires (RAM/bascules) non initialisees.");
                if (hasClock && !anyClockDriving)
                    sb.AppendLine("ATTENTION : ce circuit contient une CLOCK. Ici elle NE tourne PAS toute seule (le temps reel ne s'ecoule pas) : " +
                                  "elle reste a 0 tant que tu ne la pilotes pas. Mets \"clocks\": N dans une etape pour jouer N cycles complets " +
                                  "(front montant a chaque cycle), ou pilote-la a la main avec set {pin:\"CLOCK\", value:\"1\"/\"0\"}. " +
                                  "Ne modifie JAMAIS le circuit pour tester.");
                if (anyClockDriving)
                {
                    if (hasClock && clockInputIdx >= 0) sb.AppendLine($"\"clocks\" pilote le composant CLOCK et l'entree \"{inNames[clockInputIdx]}\" (en phase).");
                    else if (hasClock) sb.AppendLine("\"clocks\" pilote le composant CLOCK.");
                    else if (clockInputIdx >= 0) sb.AppendLine($"\"clocks\" pilote l'entree \"{inNames[clockInputIdx]}\" (0 -> 1 -> 0 par cycle).");
                    else sb.AppendLine("ATTENTION : \"clocks\" est IGNORE : ce circuit n'a ni composant CLOCK ni entree d'horloge (CLOCK/CLK). " +
                                       "Pilote l'entree qui sert d'horloge a la main avec set {pin:\"<nom>\", value:\"0\"} puis \"1\" dans des etapes separees.");
                }
                if (probeErrors.Count > 0) sb.AppendLine("Watch ignore : " + string.Join(" ; ", probeErrors));

                var prevGrids = new string[displays.Count];

                for (int s = 0; s < steps.Count; s++)
                {
                    Step st = steps[s];
                    foreach ((int kind, int index, ushort value, string _) in plan[s])
                    {
                        if (kind == 0) inputStates[index] = value;
                        else if (kind == 2) clockLevel = value;
                        else
                        {
                            char c = keyChips[index].key;
                            if (value != 0) heldKeys.Add(c);
                            else heldKeys.Remove(c);
                        }
                    }

                    int ticks = Mathf.Clamp(st.ticks <= 0 ? DefaultTicksPerStep : st.ticks, 1, MaxTicksPerStep);
                    int cycles = Mathf.Clamp(st.clocks, 0, MaxClockCyclesPerStep);

                    void RunTicks(int n)
                    {
                        if (hasClock) Simulator.forcedClockState = clockLevel;
                        if (clockInputIdx >= 0 && (cycles > 0 || plan[s].Exists(a => a.kind == 2))) inputStates[clockInputIdx] = (ushort)clockLevel;
                        for (int t = 0; t < n; t++)
                        {
                            for (int i = 0; i < inputStates.Length; i++) root.InputPins[i].State = PinState.Make(inputStates[i], 0);
                            Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
                        }
                    }

                    if (cycles > 0 && clocksDriveSomething)
                    {
                        // One cycle = clock LOW then HIGH (the rising edge that latches), then back to LOW so
                        // the state read at the end of the step is settled with the clock idle. The inputs set
                        // in this step are already applied, so a data input settles BEFORE the edge.
                        for (int c = 0; c < cycles; c++)
                        {
                            clockLevel = 0;
                            RunTicks(ticks);
                            clockLevel = 1;
                            RunTicks(ticks);
                        }

                        clockLevel = 0;
                        RunTicks(ticks);
                    }
                    else
                    {
                        RunTicks(ticks);
                    }

                    // ---- report ----
                    var line = new StringBuilder();
                    line.Append('#').Append(s + 1);
                    if (!string.IsNullOrWhiteSpace(st.note)) line.Append(" (").Append(st.note.Trim()).Append(')');
                    if (ticks != DefaultTicksPerStep) line.Append(" [").Append(ticks).Append(" ticks]");
                    if (cycles > 0 && clocksDriveSomething) line.Append(" [").Append(cycles).Append(" cycle").Append(cycles > 1 ? "s" : "").Append(hasClock ? " CLOCK]" : $" {inNames[clockInputIdx]}]");
                    else if (cycles > 0) line.Append(" [clocks IGNORE : pas d'horloge]");

                    if (inputStates.Length > 0)
                    {
                        line.Append(' ');
                        for (int i = 0; i < inputStates.Length; i++)
                        {
                            if (i > 0) line.Append(' ');
                            line.Append(inNames[i]).Append('=').Append(Fmt(inputStates[i], 0, inBits[i]));
                        }
                    }

                    if (keyChips.Count > 0)
                    {
                        line.Append(' ');
                        for (int i = 0; i < keyChips.Count; i++)
                        {
                            if (i > 0) line.Append(' ');
                            line.Append(keyChips[i].label).Append('=').Append(heldKeys.Contains(keyChips[i].key) ? "1" : "0");
                        }
                    }

                    if (hasClock) line.Append(" CLOCK=").Append(clockLevel);

                    if (desc.OutputPins.Length > 0)
                    {
                        line.Append(" ->");
                        for (int i = 0; i < desc.OutputPins.Length; i++)
                            line.Append(' ').Append(outNames[i]).Append('=').Append(FmtState(root.OutputPins[i].State, outBits[i]));
                    }

                    var gridBlocks = new List<string>();
                    for (int d = 0; d < displays.Count; d++)
                    {
                        DisplayRef dr = displays[d];
                        SimChip dsim = ResolveDisplaySim(target, dr);
                        if (dsim == null) continue;

                        if (dr.type == ChipType.DisplayLED)
                        {
                            line.Append(" | ").Append(dr.label).Append('=').Append(PinState.FirstBitHigh(dsim.InputPins[0].State) ? "ON" : "off");
                        }
                        else if (dr.type == ChipType.SevenSegmentDisplay)
                        {
                            line.Append(" | ").Append(dr.label).Append('=').Append(ReadSevenSeg(dsim));
                        }
                        else
                        {
                            string grid = dr.type == ChipType.DisplayRGB ? ReadRgbGrid(dsim) : ReadDotGrid(dsim);
                            if (grid != prevGrids[d])
                            {
                                prevGrids[d] = grid;
                                gridBlocks.Add(dr.label + " :\n" + grid);
                            }
                        }
                    }

                    if (probes.Count > 0)
                    {
                        line.Append(" |");
                        foreach (Probe pr in probes)
                            line.Append(' ').Append(pr.label).Append('=').Append(ReadProbe(target, pr));
                    }

                    sb.AppendLine(line.ToString());
                    foreach (string g in gridBlocks) sb.AppendLine(g);
                }

                if (displays.Any(d => d.type is ChipType.DisplayRGB or ChipType.DisplayDot))
                    sb.AppendLine("(Les grilles d'ecran ne sont reaffichees que lorsqu'elles changent.)");
            }
            catch (Exception e)
            {
                return "Erreur pendant le test : " + e.Message;
            }
            finally
            {
                SimKeyboardHelper.SetVirtualKeys(null);
                Simulator.forcedClockState = -1; // give the live sim its real-time clock back
                // Restore the live sim's frame counter and force it to re-establish its traversal order.
                Simulator.simulationFrame = savedFrame;
                Simulator.needsOrderPass = true;
            }

            return sb.ToString().TrimEnd();
        }

        // ---------------- truth table (text form of TruthTableComputer) ----------------

        public static string RunTruthTable(Project p)
        {
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            string chipName = string.IsNullOrEmpty(desc.Name) ? "(brique courante)" : desc.Name;
            bool seq = CircuitExporter.IsSequential(desc, p.chipLibrary);

            TruthTableComputer.Result r = TruthTableComputer.Compute(desc, p.chipLibrary, seq);
            if (!r.ok) return r.message;

            var sb = new StringBuilder();
            sb.AppendLine($"Table de verite \"{chipName}\" ({r.inputRows.Count} lignes, etat neuf a chaque ligne)" +
                          (seq ? " — ATTENTION : ce circuit est sequentiel, cette table n'est pas suffisante, teste-le avec test_sequence." : ""));
            sb.AppendLine(string.Join(" ", r.inputNames) + " -> " + string.Join(" ", r.outputNames));
            for (int i = 0; i < r.inputRows.Count; i++)
            {
                var parts = new List<string>();
                for (int c = 0; c < r.inputRows[i].Length; c++) parts.Add(Fmt((ushort)r.inputRows[i][c], 0, r.inputBits[c]));
                parts.Add("->");
                for (int c = 0; c < r.outputRows[i].Length; c++) parts.Add(Fmt((ushort)r.outputRows[i][c], 0, r.outputBits[c]));
                sb.AppendLine(string.Join(" ", parts));
            }

            return sb.ToString().TrimEnd();
        }

        // ---------------- observation helpers ----------------

        class DisplayRef
        {
            public string label;
            public ChipType type;
            public int[] path; // subchip IDs from the tested chip down to the display chip
        }

        class Probe
        {
            public string label;
            public PinAddress address;
            public int bits;
        }

        // Displays actually VISIBLE on the face of the tested chip: builtin display chips placed directly,
        // plus displays exposed on the face of a custom sub-chip (recursively) — mirrors DevSceneDrawer.
        static List<DisplayRef> FindDisplays(DevChipInstance devChip, ChipDescription desc)
        {
            var compNames = CircuitExporter.BuildComponentNames(desc);
            var list = new List<DisplayRef>();

            IEnumerable<SubChipInstance> ordered = devChip.GetSubchips()
                .OrderBy(c => c.Position.x).ThenByDescending(c => c.Position.y);

            foreach (SubChipInstance sc in ordered)
            {
                if (sc.Displays == null || sc.Displays.Count == 0) continue;
                string baseLabel = compNames.GetValueOrDefault(sc.ID, sc.Description.Name);
                foreach (DisplayInstance d in sc.Displays) Collect(d, new List<int> { sc.ID }, baseLabel);
            }

            return list;

            void Collect(DisplayInstance d, List<int> path, string baseLabel)
            {
                if (d?.Desc == null) return;

                if (d.DisplayType == ChipType.Custom)
                {
                    if (d.ChildDisplays == null) return;
                    var deeper = new List<int>(path) { d.Desc.SubChipID };
                    foreach (DisplayInstance child in d.ChildDisplays) Collect(child, deeper, baseLabel);
                    return;
                }

                if (d.DisplayType is not (ChipType.SevenSegmentDisplay or ChipType.DisplayLED or ChipType.DisplayRGB or ChipType.DisplayDot)) return;

                string label = path.Count == 1 ? baseLabel : baseLabel + ">" + ShortName(d.DisplayType);
                string unique = label;
                int n = 2;
                while (list.Any(x => x.label == unique)) unique = label + "#" + n++;
                list.Add(new DisplayRef { label = unique, type = d.DisplayType, path = path.ToArray() });
            }
        }

        static string ShortName(ChipType t) => t switch
        {
            ChipType.SevenSegmentDisplay => "7SEG",
            ChipType.DisplayLED => "LED",
            ChipType.DisplayRGB => "RGB",
            ChipType.DisplayDot => "DOT",
            _ => t.ToString()
        };

        static SimChip ResolveDisplaySim(SimChip target, DisplayRef dr)
        {
            SimChip s = target;
            foreach (int id in dr.path)
            {
                (bool found, SimChip next) = s.TryGetSubChipFromID(id);
                if (!found) return null;
                s = next;
            }

            return s;
        }

        // Is there a CLOCK anywhere in the tested chip (possibly nested in a sub-brick)? The forced-clock
        // override is global, so a nested clock is driven too.
        static bool IsClockName(string n)
        {
            string s = (n ?? "").Trim().ToUpperInvariant();
            return s == "CLOCK" || s == "CLK" || s == "HORLOGE";
        }

        static bool LooksLikeClockName(string n)
        {
            string s = (n ?? "").Trim().ToUpperInvariant();
            return s.Contains("CLOCK") || s.Contains("CLK") || s.Contains("HORLOGE");
        }

        static bool ContainsClock(SimChip chip)
        {
            if (chip == null) return false;
            if (chip.ChipType == ChipType.Clock) return true;
            foreach (SimChip sub in chip.SubChips)
                if (ContainsClock(sub)) return true;
            return false;
        }

        // KEY chips placed directly in the tested chip (the "buttons" a player presses), with the key they
        // are bound to (stored in their internal state).
        static List<(string label, char key)> FindKeyChips(ChipDescription desc, SimChip target)
        {
            var compNames = CircuitExporter.BuildComponentNames(desc);
            var list = new List<(string, char)>();
            foreach (SubChipDescription sub in desc.SubChips)
            {
                (bool found, SimChip sim) = target.TryGetSubChipFromID(sub.ID);
                if (!found || sim.ChipType != ChipType.Key || sim.InternalState.Length == 0) continue;
                list.Add((compNames.GetValueOrDefault(sub.ID, sub.Name), (char)sim.InternalState[0]));
            }

            return list;
        }

        static List<Probe> ResolveProbes(Project p, List<string> labels, out List<string> errors)
        {
            errors = new List<string>();
            var probes = new List<Probe>();
            if (labels == null || labels.Count == 0) return probes;

            Dictionary<string, PinInstance> map = CircuitExporter.BuildPinLabelMap(p.ViewedChip, p.chipLibrary);
            foreach (string raw in labels)
            {
                string label = (raw ?? "").Trim();
                if (label.Length == 0) continue;
                if (map.TryGetValue(label, out PinInstance pin)) probes.Add(new Probe { label = label, address = pin.Address, bits = (int)pin.bitCount });
                else errors.Add($"\"{label}\" introuvable");
            }

            return probes;
        }

        static string ReadProbe(SimChip target, Probe pr)
        {
            try
            {
                SimPin pin = target.GetSimPinFromAddress(pr.address);
                return FmtState(pin.State, pr.bits <= 0 ? 1 : pr.bits);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // Segment patterns (A B C D E F G) of the characters a 7-segment display can form.
        static readonly Dictionary<string, string> SevenSegChars = new()
        {
            { "1111110", "0" }, { "0110000", "1" }, { "1101101", "2" }, { "1111001", "3" },
            { "0110011", "4" }, { "1011011", "5" }, { "1011111", "6" }, { "1110000", "7" },
            { "1111111", "8" }, { "1111011", "9" }, { "1110111", "A" }, { "0011111", "b" },
            { "1001110", "C" }, { "0111101", "d" }, { "1001111", "E" }, { "1000111", "F" },
            { "0000001", "-" }
        };

        static string ReadSevenSeg(SimChip sim)
        {
            var pattern = new StringBuilder();
            for (int i = 0; i < 7; i++) pattern.Append(i < sim.InputPins.Length && PinState.FirstBitHigh(sim.InputPins[i].State) ? '1' : '0');
            string pat = pattern.ToString();

            if (pat == "0000000") return "vide";
            if (SevenSegChars.TryGetValue(pat, out string c)) return "\"" + c + "\"";

            // Not a recognisable character: list the lit segments so the fault is diagnosable.
            const string seg = "ABCDEFG";
            var lit = new List<char>();
            for (int i = 0; i < 7; i++)
                if (pat[i] == '1') lit.Add(seg[i]);
            return "segments " + new string(lit.ToArray());
        }

        static string ReadDotGrid(SimChip sim)
        {
            var sb = new StringBuilder();
            for (int y = 15; y >= 0; y--) // y = 0 is the bottom row in the app
            {
                sb.Append("  ");
                for (int x = 0; x < 16; x++) sb.Append((sim.InternalState[y * 16 + x] & 1) != 0 ? '#' : '.');
                if (y > 0) sb.Append('\n');
            }

            return sb.ToString();
        }

        static string ReadRgbGrid(SimChip sim)
        {
            var sb = new StringBuilder();
            for (int y = 15; y >= 0; y--)
            {
                sb.Append("  ");
                for (int x = 0; x < 16; x++)
                {
                    uint px = sim.InternalState[y * 16 + x];
                    int r = (int)(px & 0b1111), g = (int)((px >> 4) & 0b1111), b = (int)((px >> 8) & 0b1111);
                    sb.Append(ColourChar(r, g, b));
                }

                if (y > 0) sb.Append('\n');
            }

            return sb.ToString();
        }

        // One char per pixel: dominant colour (upper case = bright), '.' = off, 'w'/'W' = grey/white.
        static char ColourChar(int r, int g, int b)
        {
            int max = Mathf.Max(r, Mathf.Max(g, b));
            if (max == 0) return '.';
            bool hi = max >= 8;
            bool R = r * 2 >= max, G = g * 2 >= max, B = b * 2 >= max;
            char c = (R, G, B) switch
            {
                (true, false, false) => 'r',
                (false, true, false) => 'g',
                (false, false, true) => 'b',
                (true, true, false) => 'y',
                (false, true, true) => 'c',
                (true, false, true) => 'm',
                _ => 'w'
            };
            return hi ? char.ToUpperInvariant(c) : c;
        }

        // ---------------- value formatting / parsing ----------------

        static string FmtState(uint state, int bits)
        {
            ushort bitStates = PinState.GetBitStates(state);
            ushort tri = PinState.GetTristateFlags(state);
            return Fmt(bitStates, tri, bits);
        }

        static string Fmt(ushort bitStates, ushort tristateFlags, int bits)
        {
            if (bits <= 1) return (tristateFlags & 1) != 0 ? "Z" : ((bitStates & 1) != 0 ? "1" : "0");

            var sb = new StringBuilder();
            bool anyTri = false;
            for (int i = bits - 1; i >= 0; i--)
            {
                bool tri = ((tristateFlags >> i) & 1) != 0;
                anyTri |= tri;
                sb.Append(tri ? 'Z' : (((bitStates >> i) & 1) != 0 ? '1' : '0'));
            }

            if (anyTri) return sb.ToString();
            int value = bitStates & ((1 << bits) - 1);
            return sb.Append('(').Append(value).Append(')').ToString();
        }

        static bool TryParseValue(string raw, int bits, out ushort value, out string error)
        {
            value = 0;
            error = null;
            string s = (raw ?? "").Trim();
            if (s.Length == 0) { error = "valeur vide."; return false; }

            int parsed;
            string lower = s.ToLowerInvariant();
            if (lower is "on" or "high" or "true" or "h") parsed = 1;
            else if (lower is "off" or "low" or "false" or "l") parsed = 0;
            else if (lower.StartsWith("0b"))
            {
                if (!TryParseBinary(lower.Substring(2), out parsed)) { error = $"binaire invalide \"{s}\"."; return false; }
            }
            else if (lower.StartsWith("0x"))
            {
                if (!int.TryParse(lower.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out parsed)) { error = $"hexa invalide \"{s}\"."; return false; }
            }
            else if (!int.TryParse(s, out parsed))
            {
                error = $"valeur \"{s}\" incomprise (attendu : 0/1, un nombre decimal, ou 0b1010).";
                return false;
            }

            int max = (1 << bits) - 1;
            if (parsed < 0 || parsed > max) { error = $"valeur {parsed} hors limites (entree sur {bits} bit{(bits > 1 ? "s" : "")}, max {max})."; return false; }

            value = (ushort)parsed;
            return true;
        }

        static bool TryParseBinary(string s, out int value)
        {
            value = 0;
            if (s.Length == 0 || s.Length > 16) return false;
            foreach (char c in s)
            {
                if (c != '0' && c != '1') return false;
                value = (value << 1) | (c == '1' ? 1 : 0);
            }

            return true;
        }

        static int Bits(PinBitCount c) => c switch { PinBitCount.Bit1 => 1, PinBitCount.Bit4 => 4, _ => 8 };
    }
}
