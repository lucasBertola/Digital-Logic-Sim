using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;

namespace DLS.SaveSystem
{
    // Produces a text description of a chip and its sub-circuits, meant to be pasted to an LLM so it
    // can understand the montage: base primitives + interface + components + netlist (in propagation order).
    // Pure structure (no simulation) -> safe to call on the main thread while the sim runs.
    public static class CircuitExporter
    {
        static readonly HashSet<ChipType> StatefulBuiltins = new()
        {
            ChipType.Clock, ChipType.Pulse, ChipType.dev_Ram_8Bit, ChipType.DisplayRGB, ChipType.DisplayDot
        };

        // Behaviour of the ATOMIC builtin primitives (the only ones that can't be rebuilt from NAND).
        // Keys are the exact chip names (ChipTypeHelper.Names).
        static readonly Dictionary<string, string> BuiltinDesc = new(ChipDescription.NameComparer)
        {
            { "NAND", "porte NON-ET (2 entrees, 1 sortie). Primitive UNIVERSELLE : NOT, AND, OR, XOR et tout le combinatoire se construisent uniquement avec des NAND." },
            { "3-STATE BUFFER", "buffer 3 etats : recopie l'entree si ENABLE=1, sinon sortie deconnectee (haute impedance) — pour les bus partages." },
            { "CLOCK", "horloge : sortie qui alterne 0/1 automatiquement dans le temps." },
            { "PULSE", "genere une breve impulsion sur front montant de l'entree." },
            { "dev.RAM-8", "RAM 8 bits adressable (lecture/ecriture)." },
            { "ROM 256×16", "memoire morte : 256 mots de 16 bits (lecture seule)." },
            { "1-4BIT", "MERGE : combine 4 fils 1-bit en un bus 4 bits." },
            { "1-8BIT", "MERGE : combine 8 fils 1-bit en un bus 8 bits." },
            { "4-8BIT", "MERGE : combine deux bus 4 bits en un bus 8 bits." },
            { "4-1BIT", "SPLIT : separe un bus 4 bits en 4 fils 1-bit." },
            { "8-1BIT", "SPLIT : separe un bus 8 bits en 8 fils 1-bit." },
            { "8-4BIT", "SPLIT : separe un bus 8 bits en deux bus 4 bits." },
            { "KEY", "entree clavier : sort 1 quand la touche liee est pressee." },
            { "VCC", "source constante : sortie toujours a 1. N'apparait PAS dans l'interface du module (c'est un composant, pas une entree) — sert a cabler des valeurs en dur (LUT, table de verite figee, tie-off)." },
            { "GND", "source constante : sortie toujours a 0. N'apparait PAS dans l'interface du module — meme usage que VCC." },
            { "BUZZER", "buzzer audio (frequence + volume)." },
            { "LED", "diode d'affichage." },
            { "7-SEGMENT", "afficheur 7 segments." },
            { "RGB DISPLAY", "afficheur RGB." },
            { "DOT DISPLAY", "afficheur matriciel (points)." },
        };

        // One-line behaviour description of a builtin primitive (by its chip name), or null.
        public static string BuiltinInfo(string name) => BuiltinDesc.TryGetValue(name, out var d) ? d : null;

        // Compact pin interface of a chip, e.g. "entrees: IN A, IN B | sorties: OUT" (disambiguated names,
        // matching the labels the connect tool expects; [Nb] tags multi-bit pins).
        public static string ComponentInterfaceLine(ChipDescription d)
        {
            static string Tag(PinBitCount b) => (int)b > 1 ? $"[{(int)b}b]" : "";
            var inNames = Disambiguate(d.InputPins.Select(p => p.Name).ToArray());
            var outNames = Disambiguate(d.OutputPins.Select(p => p.Name).ToArray());
            string ins = d.InputPins.Length == 0 ? "-" : string.Join(", ", d.InputPins.Select((p, i) => inNames[i] + Tag(p.BitCount)));
            string outs = d.OutputPins.Length == 0 ? "-" : string.Join(", ", d.OutputPins.Select((p, i) => outNames[i] + Tag(p.BitCount)));
            return $"entrees: {ins} | sorties: {outs}";
        }

        // Current chip + every custom sub-circuit it uses (recursively), leaves first.
        public static string ExportChipWithDeps(ChipDescription desc, ChipLibrary lib, string projectName = null)
        {
            var sb = new StringBuilder();
            foreach (var section in ExportChipSections(desc, lib)) sb.Append(section.text);
            return sb.ToString();
        }

        // Same set/order as ExportChipWithDeps, but returns one (name, text) entry per circuit so callers
        // can diff circuit-by-circuit between successive exports. The current chip uses the LIVE
        // description passed in (reflects unsaved edits); dependencies come from the library.
        public static List<(string name, string text)> ExportChipSections(ChipDescription desc, ChipLibrary lib)
        {
            var seen = new HashSet<string>(ChipDescription.NameComparer);
            void Collect(ChipDescription d)
            {
                foreach (var s in d.SubChips)
                    if (!lib.IsBuiltinChip(s.Name) && seen.Add(s.Name) && Live(lib, s.Name) is { } sd)
                        Collect(sd);
            }
            Collect(desc);
            seen.Add(desc.Name);

            var ordered = OrderByDependency(seen.ToArray(), lib).Where(lib.HasChip).ToList();
            ordered.Remove(desc.Name);
            ordered.Add(desc.Name); // requested chip last (most complex)

            var list = new List<(string name, string text)>();
            foreach (string n in ordered)
            {
                ChipDescription d = ChipDescription.NameMatch(n, desc.Name) ? desc : Live(lib, n);
                if (d != null) list.Add((n, ExportChip(d, lib)));
            }
            return list;
        }

        // The LIVE description of a chip: the in-memory (possibly unsaved) version when it is open,
        // otherwise the saved one. Nothing auto-saves, so the export must reflect unsaved edits.
        static ChipDescription Live(ChipLibrary lib, string name) => lib.GetChipDescriptionForSim(name);

        // Maps the pin labels exactly as they appear in the export (dev pins by name, subchip pins as
        // "Comp.Pin" / "Comp#n.Pin") to the live PinInstance, so tools can resolve Claude's references.
        public static Dictionary<string, PinInstance> BuildPinLabelMap(DevChipInstance devChip, ChipLibrary lib)
        {
            var desc = DescriptionCreator.CreateChipDescription(devChip);
            var inNames = Disambiguate(desc.InputPins.Select(p => p.Name).ToArray());
            var outNames = Disambiguate(desc.OutputPins.Select(p => p.Name).ToArray());
            var compName = BuildComponentNames(desc);

            var elemById = new Dictionary<int, IMoveable>();
            foreach (IMoveable e in devChip.Elements) elemById[e.ID] = e;

            var map = new Dictionary<string, PinInstance>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < desc.InputPins.Length; i++)
                if (elemById.TryGetValue(desc.InputPins[i].ID, out var el) && el is DevPinInstance dp) map[inNames[i]] = dp.Pin;
            for (int i = 0; i < desc.OutputPins.Length; i++)
                if (elemById.TryGetValue(desc.OutputPins[i].ID, out var el) && el is DevPinInstance dp) map[outNames[i]] = dp.Pin;
            foreach (var sub in desc.SubChips)
            {
                if (!elemById.TryGetValue(sub.ID, out var el)) continue;
                if (el is not SubChipInstance sc) continue;
                string cn = compName[sub.ID];
                // Use the SAME disambiguated names that the interface/netlist show (raw pin names can
                // collide, e.g. two inputs both literally named "IN" -> displayed IN1/IN2).
                var subIn = Disambiguate(sc.InputPins.Select(p => p.Name).ToArray());
                var subOut = Disambiguate(sc.OutputPins.Select(p => p.Name).ToArray());
                for (int i = 0; i < sc.InputPins.Length; i++) map[$"{cn}.{subIn[i]}"] = sc.InputPins[i];
                for (int i = 0; i < sc.OutputPins.Length; i++) map[$"{cn}.{subOut[i]}"] = sc.OutputPins[i];
            }

            return map;
        }

        public static string ExportChip(ChipDescription desc, ChipLibrary lib)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## Circuit : {desc.Name}");
            sb.AppendLine();

            var inNames = Disambiguate(desc.InputPins.Select(p => p.Name).ToArray());
            var outNames = Disambiguate(desc.OutputPins.Select(p => p.Name).ToArray());
            sb.AppendLine("Interface :");
            sb.AppendLine("  Entrées : " + (desc.InputPins.Length == 0 ? "(aucune)" :
                string.Join(", ", desc.InputPins.Select((p, i) => Iface(inNames[i], p.BitCount)))));
            sb.AppendLine("  Sorties : " + (desc.OutputPins.Length == 0 ? "(aucune)" :
                string.Join(", ", desc.OutputPins.Select((p, i) => Iface(outNames[i], p.BitCount)))));
            sb.AppendLine();

            var compName = BuildComponentNames(desc);
            if (desc.SubChips.Length > 0)
            {
                sb.AppendLine($"Composants ({desc.SubChips.Length}) :");
                foreach (var sub in desc.SubChips)
                {
                    string io = "";
                    string geo = $"  @({sub.Position.x:0.#}, {sub.Position.y:0.#})";
                    if (Live(lib, sub.Name) is { } sd)
                    {
                        io = $"  ({BitsInOut(BitsSum(sd.InputPins), "entrant")}, {BitsInOut(BitsSum(sd.OutputPins), "sortant")})";
                        geo += $" {sd.Size.x:0.#}x{sd.Size.y:0.#}";
                    }
                    string extra = string.IsNullOrEmpty(sub.Label) ? "" : $"  (label: \"{sub.Label}\")";
                    sb.AppendLine($"  {compName[sub.ID]}{io}{geo}{extra}");
                }
                sb.AppendLine();
            }

            var nets = BuildNets(desc, lib, compName, inNames, outNames);
            sb.AppendLine("Connexions :");
            if (nets.Count == 0) sb.AppendLine("  (aucune)");
            int curLevel = -1;
            foreach (var net in nets)
            {
                if (net.level != curLevel)
                {
                    curLevel = net.level;
                    sb.AppendLine($"  — étape {curLevel + 1} —");
                }
                sb.AppendLine($"    {net.driver,-20} → {string.Join(", ", net.sinks)}");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            return sb.ToString();
        }

        static string PrimitivesSection(IEnumerable<string> chipNames, ChipLibrary lib)
        {
            var used = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cn in chipNames)
                if (lib.TryGetChipDescription(cn, out var d))
                    foreach (var s in d.SubChips)
                        if (lib.IsBuiltinChip(s.Name)) used.Add(s.Name);

            if (used.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("## Briques de base (primitives fournies par le simulateur — PAS construites par l'utilisateur)");
            foreach (var name in used)
                sb.AppendLine($"- **{name}** : {(BuiltinDesc.TryGetValue(name, out var d) ? d : "primitive intégrée.")}");
            sb.AppendLine();
            return sb.ToString();
        }

        // ---------------- netlist ----------------
        class Net { public string driver; public List<string> sinks = new(); public int level; }
        struct PinC { public string label; public bool isDriver; public int subId; public bool isSubInput; }

        static List<Net> BuildNets(ChipDescription desc, ChipLibrary lib, Dictionary<int, string> compName, string[] inNames, string[] outNames)
        {
            var uf = new UnionFind();
            string PinKey(PinAddress a) => "P:" + a.PinOwnerID + ":" + a.PinID;
            string WireKey(int i) => "W:" + i;

            for (int i = 0; i < desc.Wires.Length; i++)
            {
                var w = desc.Wires[i];
                string wk = WireKey(i);
                if (w.ConnectionType == WireConnectionType.ToWireSource) uf.Union(wk, WireKey(w.ConnectedWireIndex));
                else uf.Union(wk, PinKey(w.SourcePinAddress));
                if (w.ConnectionType == WireConnectionType.ToWireTarget) uf.Union(wk, WireKey(w.ConnectedWireIndex));
                else uf.Union(wk, PinKey(w.TargetPinAddress));
            }

            var pinInfo = new Dictionary<string, PinC>();
            void Note(PinAddress a) { string k = PinKey(a); if (!pinInfo.ContainsKey(k)) pinInfo[k] = Classify(a, desc, lib, compName, inNames, outNames); }
            foreach (var w in desc.Wires)
            {
                if (w.ConnectionType != WireConnectionType.ToWireSource) Note(w.SourcePinAddress);
                if (w.ConnectionType != WireConnectionType.ToWireTarget) Note(w.TargetPinAddress);
            }

            var groups = new Dictionary<string, List<string>>();
            foreach (var kv in pinInfo)
            {
                string root = uf.Find(kv.Key);
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<string>();
                list.Add(kv.Key);
            }

            var entries = new List<(Net net, int driverSub)>();
            var inputNets = new Dictionary<int, List<string>>();
            var netDriverSub = new Dictionary<string, int>();

            foreach (var kv in groups)
            {
                var keys = kv.Value;
                var drivers = keys.Where(k => pinInfo[k].isDriver).ToList();
                var sinks = keys.Where(k => !pinInfo[k].isDriver).ToList();
                if (sinks.Count == 0) continue;

                int driverSub = -1;
                foreach (var k in drivers) if (pinInfo[k].subId >= 0) { driverSub = pinInfo[k].subId; break; }
                netDriverSub[kv.Key] = driverSub;

                foreach (var k in sinks)
                    if (pinInfo[k].isSubInput)
                    {
                        if (!inputNets.TryGetValue(pinInfo[k].subId, out var l)) inputNets[pinInfo[k].subId] = l = new List<string>();
                        l.Add(kv.Key);
                    }

                entries.Add((new Net
                {
                    driver = drivers.Count == 0 ? "(non connecté)" : string.Join(" & ", drivers.Select(k => pinInfo[k].label).OrderBy(s => s)),
                    sinks = sinks.Select(k => pinInfo[k].label).OrderBy(s => s).ToList()
                }, driverSub));
            }

            var subLevel = new Dictionary<int, int>();
            int SubLevel(int sub, HashSet<int> visiting)
            {
                if (subLevel.TryGetValue(sub, out int cached)) return cached;
                if (!visiting.Add(sub)) return 0;
                int m = -1;
                if (inputNets.TryGetValue(sub, out var ins))
                    foreach (var root in ins)
                    {
                        int ds = netDriverSub.GetValueOrDefault(root, -1);
                        int lvl = ds < 0 ? 0 : SubLevel(ds, visiting);
                        if (lvl > m) m = lvl;
                    }
                visiting.Remove(sub);
                int result = m < 0 ? 0 : m + 1;
                subLevel[sub] = result;
                return result;
            }

            foreach (var e in entries)
                e.net.level = e.driverSub < 0 ? 0 : SubLevel(e.driverSub, new HashSet<int>());

            return entries.Select(e => e.net).OrderBy(n => n.level).ThenBy(n => n.driver).ToList();
        }

        static PinC Classify(PinAddress a, ChipDescription desc, ChipLibrary lib, Dictionary<int, string> compName, string[] inNames, string[] outNames)
        {
            for (int i = 0; i < desc.InputPins.Length; i++)
                if (desc.InputPins[i].ID == a.PinOwnerID) return new PinC { label = inNames[i], isDriver = true, subId = -1 };
            for (int i = 0; i < desc.OutputPins.Length; i++)
                if (desc.OutputPins[i].ID == a.PinOwnerID) return new PinC { label = outNames[i], isDriver = false, subId = -1 };
            foreach (var sub in desc.SubChips)
            {
                if (sub.ID != a.PinOwnerID) continue;
                if (Live(lib, sub.Name) is not { } sd) break;
                var subIn = Disambiguate(sd.InputPins.Select(p => p.Name).ToArray());
                for (int i = 0; i < sd.InputPins.Length; i++) if (sd.InputPins[i].ID == a.PinID) return new PinC { label = $"{compName[sub.ID]}.{subIn[i]}", isDriver = false, subId = sub.ID, isSubInput = true };
                var subOut = Disambiguate(sd.OutputPins.Select(p => p.Name).ToArray());
                for (int i = 0; i < sd.OutputPins.Length; i++) if (sd.OutputPins[i].ID == a.PinID) return new PinC { label = $"{compName[sub.ID]}.{subOut[i]}", isDriver = true, subId = sub.ID };
            }
            return new PinC { label = $"?{a.PinOwnerID}:{a.PinID}", isDriver = false, subId = -1 };
        }

        // Component labels as shown everywhere else ("NAND", "NAND#2"...), keyed by subchip ID.
        public static Dictionary<int, string> BuildComponentNames(ChipDescription desc)
        {
            var map = new Dictionary<int, string>();
            var counter = new Dictionary<string, int>(ChipDescription.NameComparer);
            var total = new Dictionary<string, int>(ChipDescription.NameComparer);
            foreach (var s in desc.SubChips) total[s.Name] = total.GetValueOrDefault(s.Name) + 1;
            foreach (var s in desc.SubChips)
            {
                counter[s.Name] = counter.GetValueOrDefault(s.Name) + 1;
                map[s.ID] = total[s.Name] > 1 ? $"{s.Name}#{counter[s.Name]}" : s.Name;
            }
            return map;
        }

        // ---------------- nature (also used by the truth-table view) ----------------
        public static bool IsSequential(ChipDescription desc, ChipLibrary lib) => IsSequential(desc, lib, new HashSet<string>(ChipDescription.NameComparer));

        static bool IsSequential(ChipDescription desc, ChipLibrary lib, HashSet<string> visited)
        {
            if (!visited.Add(desc.Name)) return false;
            foreach (var sub in desc.SubChips)
            {
                // Live description: the simulation runs the unsaved version, so classify that one.
                if (Live(lib, sub.Name) is not { } sd) continue;
                if (StatefulBuiltins.Contains(sd.ChipType)) return true;
                if (sd.ChipType == ChipType.Custom && IsSequential(sd, lib, visited)) return true;
            }
            return HasFeedbackCycle(desc, lib);
        }

        static bool HasFeedbackCycle(ChipDescription desc, ChipLibrary lib)
        {
            var uf = new UnionFind();
            string PinKey(PinAddress a) => "P:" + a.PinOwnerID + ":" + a.PinID;
            string WireKey(int i) => "W:" + i;
            for (int i = 0; i < desc.Wires.Length; i++)
            {
                var w = desc.Wires[i];
                string wk = WireKey(i);
                if (w.ConnectionType == WireConnectionType.ToWireSource) uf.Union(wk, WireKey(w.ConnectedWireIndex));
                else uf.Union(wk, PinKey(w.SourcePinAddress));
                if (w.ConnectionType == WireConnectionType.ToWireTarget) uf.Union(wk, WireKey(w.ConnectedWireIndex));
                else uf.Union(wk, PinKey(w.TargetPinAddress));
            }

            var outByNet = new Dictionary<string, HashSet<int>>();
            var inByNet = new Dictionary<string, HashSet<int>>();
            void Add(Dictionary<string, HashSet<int>> d, string net, int id) { if (!d.TryGetValue(net, out var s)) d[net] = s = new HashSet<int>(); s.Add(id); }
            foreach (var sub in desc.SubChips)
            {
                if (Live(lib, sub.Name) is not { } sd) continue;
                foreach (var p in sd.InputPins) Add(inByNet, uf.Find(PinKey(new PinAddress(sub.ID, p.ID))), sub.ID);
                foreach (var p in sd.OutputPins) Add(outByNet, uf.Find(PinKey(new PinAddress(sub.ID, p.ID))), sub.ID);
            }

            var edges = new Dictionary<int, HashSet<int>>();
            foreach (var net in outByNet.Keys)
            {
                if (!inByNet.TryGetValue(net, out var sinks)) continue;
                foreach (int s in outByNet[net])
                    foreach (int t in sinks)
                        if (s != t) { if (!edges.TryGetValue(s, out var set)) edges[s] = set = new HashSet<int>(); set.Add(t); }
            }

            var state = new Dictionary<int, int>();
            bool Dfs(int n)
            {
                state[n] = 1;
                if (edges.TryGetValue(n, out var outs))
                    foreach (int m in outs)
                    {
                        int st = state.GetValueOrDefault(m);
                        if (st == 1) return true;
                        if (st == 0 && Dfs(m)) return true;
                    }
                state[n] = 2;
                return false;
            }
            foreach (var s in desc.SubChips)
                if (state.GetValueOrDefault(s.ID) == 0 && Dfs(s.ID)) return true;
            return false;
        }

        // ---------------- helpers ----------------
        static string[] OrderByDependency(string[] names, ChipLibrary lib)
        {
            var depth = new Dictionary<string, int>(ChipDescription.NameComparer);
            int Depth(string name, HashSet<string> stack)
            {
                if (depth.TryGetValue(name, out int d)) return d;
                if (!lib.TryGetChipDescription(name, out var desc) || !stack.Add(name)) return 0;
                int m = 0;
                foreach (var s in desc.SubChips) if (!lib.IsBuiltinChip(s.Name)) m = Math.Max(m, Depth(s.Name, stack));
                stack.Remove(name);
                return depth[name] = m + 1;
            }
            foreach (var n in names) Depth(n, new HashSet<string>(ChipDescription.NameComparer));
            return names.OrderBy(n => depth.GetValueOrDefault(n)).ThenBy(n => n).ToArray();
        }

        // Duplicate raw pin names get a 1-based suffix (two pins named "IN" -> "IN1", "IN2"). MUST be used
        // by every layer that shows or resolves pin labels, or tool wiring silently breaks.
        public static string[] Disambiguate(string[] names)
        {
            var total = names.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<string, int>();
            var res = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                if (total[n] > 1) { seen[n] = seen.GetValueOrDefault(n) + 1; res[i] = $"{n}{seen[n]}"; }
                else res[i] = n;
            }
            return res;
        }

        static int Bits(PinBitCount c) => c switch { PinBitCount.Bit1 => 1, PinBitCount.Bit4 => 4, _ => 8 };
        static string Iface(string name, PinBitCount c) { int b = Bits(c); return $"{name} [{b} bit{(b > 1 ? "s" : "")}]"; }
        static int BitsSum(PinDescription[] pins) => pins?.Sum(p => Bits(p.BitCount)) ?? 0;
        static string BitsInOut(int n, string word) => $"{n} bit{(n > 1 ? "s" : "")} {word}{(n > 1 ? "s" : "")}";

        class UnionFind
        {
            readonly Dictionary<string, string> parent = new();
            public string Find(string x)
            {
                if (!parent.ContainsKey(x)) parent[x] = x;
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            public void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
        }
    }
}
