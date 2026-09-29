using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;

namespace DLS.Bench
{
    // CREATE CHIP (ChipExtractor): the pins are named after what was on the other side of the wire, and the
    // circuit behaves exactly as before, on hand-built circuits and on the user's chips (ALU8, Registre8, and the
    // CPU running its micro-program with RAM + buffer extracted).
    public static class ChipExtractCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips) => new()
        {
            ("create chip: pins named after the other end of the wire, several joined with '-'", Names),
            ("create chip: VCC / GND never become inputs (moved in when they only feed the selection, copied in when shared)", Constants),
            ("[PC] create chip: ALU8 with half its components extracted computes the same", () => Combinational(lib, chips, "ALU8", 0.5, 11)),
            ("[PC] create chip: ALU8 with one component extracted computes the same", () => Combinational(lib, chips, "ALU8", 0.01, 5)),
            ("[PC] create chip: Registre8 with some flip-flops extracted behaves the same", () => Register(lib, chips)),
            ("[PC] create chip: CPU with RAM256 + Tampon8 extracted runs the micro-program", () => Cpu(lib, chips)),
            ("[PC] create chip: a bus without its terminus is refused, both together are accepted", () => Bus(lib, chips)),
            ("[PC] create chip: applied IN PLACE to the loaded CPU (what the app does), it still runs the micro-program", () => InPlace(lib, chips)),
            ("[PC] create chip: the new chip and the new parent load without losing an element or a wire", () => ReloadClean(lib, chips)),
        };

        static ChipLibrary With(ChipDescription[] chips, params ChipDescription[] extra) =>
            new(chips.Concat(extra).ToArray(), BuiltinChipCreator.CreateAllBuiltinChipDescriptions());

        // A, B -> n1 = NAND(A, B); G2 = NAND(n1, n1) -> Y; G3 = NAND(n1, B) -> Z
        static (ChipDescription parent, ChipLibrary lib, int n1, int g2, int g3) Hand()
        {
            ChipLibrary lib = BenchProject.BuiltinsOnly();
            ChipDescription nand = lib.GetChipDescription("NAND");
            int next = 1;
            PinDescription[] ins = ChipEmitHelper.MakePins(new[] { "A", "B" }, true, ref next);
            PinDescription[] outs = ChipEmitHelper.MakePins(new[] { "Y", "Z" }, false, ref next);
            var subs = new List<SubChipDescription>();
            int n1 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            int g2 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            int g3 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            subs[1] = new SubChipDescription("NAND", g2, "G2", subs[1].Position + new UnityEngine.Vector2(0, 2), null);
            subs[2] = new SubChipDescription("NAND", g3, "G3", subs[2].Position + new UnityEngine.Vector2(0, -2), null);
            int ia = nand.InputPins[0].ID, ib = nand.InputPins[1].ID, o = nand.OutputPins[0].ID;
            var wires = new List<WireDescription>
            {
                ChipEmitHelper.Wire(new PinAddress(ins[0].ID, 0), new PinAddress(n1, ia)),
                ChipEmitHelper.Wire(new PinAddress(ins[1].ID, 0), new PinAddress(n1, ib)),
                ChipEmitHelper.Wire(new PinAddress(n1, o), new PinAddress(g2, ia)),
                ChipEmitHelper.Wire(new PinAddress(n1, o), new PinAddress(g2, ib)),
                ChipEmitHelper.Wire(new PinAddress(n1, o), new PinAddress(g3, ia)),
                ChipEmitHelper.Wire(new PinAddress(ins[1].ID, 0), new PinAddress(g3, ib)),
                ChipEmitHelper.Wire(new PinAddress(g2, o), new PinAddress(outs[0].ID, 0)),
                ChipEmitHelper.Wire(new PinAddress(g3, o), new PinAddress(outs[1].ID, 0)),
            };
            return (ChipEmitHelper.Assemble("HAND", UnityEngine.Color.gray, NameDisplayLocation.Centre, UnityEngine.Vector2.zero, ins, outs, subs, wires), lib, n1, g2, g3);
        }

        static string Names()
        {
            var (parent, lib, n1, g2, g3) = Hand();
            string pinA = lib.GetChipDescription("NAND").InputPins[0].Name, pinB = lib.GetChipDescription("NAND").InputPins[1].Name;

            ChipExtractor.Result r = ChipExtractor.Extract(parent, new[] { n1 }, "FIRST", lib, out string e);
            if (r == null) return e;
            string ins = string.Join(",", r.NewChip.InputPins.Select(p => p.Name).OrderBy(x => x));
            if (ins != "A,B") return $"inputs of the extracted n1: {ins}, expected A,B";
            if (r.NewChip.OutputPins.Length != 1) return $"{r.NewChip.OutputPins.Length} outputs, expected 1 (one inside source)";
            string outName = r.NewChip.OutputPins[0].Name, expected = $"G2.{pinA}-G2.{pinB}-G3.{pinA}";
            if (outName != expected) return $"output named '{outName}', expected '{expected}'";
            string eq = SameTruthTable(parent, lib, r.NewParent, With(Array.Empty<ChipDescription>(), r.NewChip));
            if (eq != null) return "n1 extracted: " + eq;

            r = ChipExtractor.Extract(parent, new[] { g2, g3 }, "SECOND", lib, out e);
            if (r == null) return e;
            ins = string.Join(",", r.NewChip.InputPins.Select(p => p.Name).OrderBy(x => x));
            if (ins != "B,NAND") return $"inputs of the extracted G2 + G3: {ins}, expected B,NAND";
            string outs = string.Join(",", r.NewChip.OutputPins.Select(p => p.Name).OrderBy(x => x));
            if (outs != "Y,Z") return $"outputs of the extracted G2 + G3: {outs}, expected Y,Z";
            eq = SameTruthTable(parent, lib, r.NewParent, With(Array.Empty<ChipDescription>(), r.NewChip));
            if (eq != null) return "G2 + G3 extracted: " + eq;

            // two inputs with the same outside name get distinct names
            r = ChipExtractor.Extract(parent, new[] { n1, g2, g3 }, "ALL", lib, out e);
            if (r == null) return e;
            if (r.NewChip.SubChips.Length != 3 || r.NewParent.SubChips.Length != 1) return "select all: wrong split";
            return SameTruthTable(parent, lib, r.NewParent, With(Array.Empty<ChipDescription>(), r.NewChip));
        }

        // A -> n1 = NAND(A, VCC) -> n2 = NAND(n1, GND) -> Y; GND also feeds n3 = NAND(A, GND) -> Z (outside)
        static string Constants()
        {
            ChipLibrary lib = BenchProject.BuiltinsOnly();
            ChipDescription nand = lib.GetChipDescription("NAND");
            int next = 1;
            PinDescription[] ins = ChipEmitHelper.MakePins(new[] { "A" }, true, ref next);
            PinDescription[] outs = ChipEmitHelper.MakePins(new[] { "Y", "Z" }, false, ref next);
            var subs = new List<SubChipDescription>();
            int n1 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            int n2 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            int n3 = ChipEmitHelper.AddSubChip(subs, ref next, "NAND", ChipType.Nand);
            int vcc = ChipEmitHelper.AddSubChip(subs, ref next, ChipTypeHelper.GetName(ChipType.Vcc), ChipType.Vcc);
            int gnd = ChipEmitHelper.AddSubChip(subs, ref next, ChipTypeHelper.GetName(ChipType.Gnd), ChipType.Gnd);
            int ia = nand.InputPins[0].ID, ib = nand.InputPins[1].ID, o = nand.OutputPins[0].ID;
            int vo = lib.GetChipDescription(ChipTypeHelper.GetName(ChipType.Vcc)).OutputPins[0].ID, go = lib.GetChipDescription(ChipTypeHelper.GetName(ChipType.Gnd)).OutputPins[0].ID;
            var wires = new List<WireDescription>
            {
                ChipEmitHelper.Wire(new PinAddress(ins[0].ID, 0), new PinAddress(n1, ia)),
                ChipEmitHelper.Wire(new PinAddress(vcc, vo), new PinAddress(n1, ib)),
                ChipEmitHelper.Wire(new PinAddress(n1, o), new PinAddress(n2, ia)),
                ChipEmitHelper.Wire(new PinAddress(gnd, go), new PinAddress(n2, ib)),
                ChipEmitHelper.Wire(new PinAddress(n2, o), new PinAddress(outs[0].ID, 0)),
                ChipEmitHelper.Wire(new PinAddress(ins[0].ID, 0), new PinAddress(n3, ia)),
                ChipEmitHelper.Wire(new PinAddress(gnd, go), new PinAddress(n3, ib)),
                ChipEmitHelper.Wire(new PinAddress(n3, o), new PinAddress(outs[1].ID, 0)),
            };
            ChipDescription parent = ChipEmitHelper.Assemble("CONST", UnityEngine.Color.gray, NameDisplayLocation.Centre, UnityEngine.Vector2.zero, ins, outs, subs, wires);

            ChipExtractor.Result r = ChipExtractor.Extract(parent, new[] { n1, n2 }, "WITH CONSTANTS", lib, out string e);
            if (r == null) return e;
            string names = string.Join(",", r.NewChip.InputPins.Select(p => p.Name));
            if (names != "A") return $"inputs: {names}, expected only A (no VCC / GND input)";
            if (r.NewChip.SubChips.Count(s => s.Name == "VCC") != 1 || r.NewChip.SubChips.Count(s => s.Name == "GND") != 1) return "the new chip should hold the VCC and a GND";
            if (r.NewParent.SubChips.Any(s => s.ID == vcc)) return "the VCC that only fed the selection is still in the parent";
            if (!r.NewParent.SubChips.Any(s => s.ID == gnd)) return "the shared GND was removed from the parent (n3 still needs it)";
            if (!r.RemovedIDs.Contains(vcc) || r.RemovedIDs.Contains(gnd)) return "RemovedIDs wrong";
            return SameTruthTable(parent, lib, r.NewParent, With(Array.Empty<ChipDescription>(), r.NewChip));
        }

        static string SameTruthTable(ChipDescription a, ChipLibrary libA, ChipDescription b, ChipLibrary libB)
        {
            int n = a.InputPins.Length;
            for (int v = 0; v < 1 << n; v++)
            {
                using var fa = new DirectedCases.Fixture(a, libA);
                using var fb = new DirectedCases.Fixture(b, libB);
                for (int i = 0; i < n; i++) { fa.SetIndex(i, (v >> i) & 1); fb.SetIndex(i, (v >> i) & 1); }
                fa.Tick(10); fb.Tick(10);
                foreach (PinDescription o in a.OutputPins)
                    if (fa.Value(o.Name) != fb.Value(o.Name)) return $"input {v}: {o.Name} = {fb.Value(o.Name)}, was {fa.Value(o.Name)}";
            }
            return null;
        }

        static int[] Pick(ChipDescription d, double fraction, int seed)
        {
            var rnd = new Random(seed);
            int[] ids = d.SubChips.Where(s => !s.Name.StartsWith("BUS")).Select(s => s.ID).OrderBy(_ => rnd.Next()).ToArray();
            return ids.Take(Math.Max(1, (int)(ids.Length * fraction))).ToArray();
        }

        static string Combinational(ChipLibrary lib, ChipDescription[] chips, string chip, double fraction, int seed)
        {
            ChipDescription d = chips.First(c => c.Name == chip);
            ChipExtractor.Result r = ChipExtractor.Extract(d, Pick(d, fraction, seed), "EXTRACTED", lib, out string e);
            if (r == null) return e;
            ChipLibrary lib2 = With(chips, r.NewChip);
            var rnd = new Random(seed);
            using var fa = new DirectedCases.Fixture(d, lib);
            using var fb = new DirectedCases.Fixture(r.NewParent, lib2);
            for (int t = 0; t < 200; t++)
            {
                for (int i = 0; i < d.InputPins.Length; i++)
                {
                    int v = rnd.Next(1 << (int)d.InputPins[i].BitCount);
                    fa.SetIndex(i, v); fb.SetIndex(i, v);
                }
                fa.Tick(12); fb.Tick(12);
                foreach (PinDescription o in d.OutputPins)
                    if (fa.Value(o.Name) != fb.Value(o.Name)) return $"vector {t}: {o.Name} = {fb.Value(o.Name)}, was {fa.Value(o.Name)}";
            }
            return null;
        }

        static string Register(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = chips.First(c => c.Name == "Registre8");
            int[] flipFlops = d.SubChips.Where(s => s.Name == "Bascule D").Select(s => s.ID).Take(3).ToArray();
            ChipExtractor.Result r = ChipExtractor.Extract(d, flipFlops, "3 BASCULES", lib, out string e);
            if (r == null) return e;
            using var fa = new DirectedCases.Fixture(d, lib);
            using var fb = new DirectedCases.Fixture(r.NewParent, With(chips, r.NewChip));
            var rnd = new Random(8);
            foreach (var f in new[] { fa, fb }) { f.Set("LOAD", 0); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 1); f.Tick(10); f.Set("Reset", 0); f.Tick(10); }
            for (int t = 0; t < 30; t++)
            {
                int v = rnd.Next(256), load = rnd.Next(3) > 0 ? 1 : 0, oe = rnd.Next(4) > 0 ? 1 : 0;
                foreach (var f in new[] { fa, fb }) { f.Set("IN", v); f.Set("LOAD", load); f.Set("OE", oe); f.Tick(10); f.Cycle("Clock"); }
                if (oe == 1 && fa.Value("OUT") != fb.Value("OUT")) return $"cycle {t}: OUT = {fb.Value("OUT")}, was {fa.Value("OUT")}";
                if (fa.IsFloating("OUT") != fb.IsFloating("OUT")) return $"cycle {t}: OUT floating differs";
            }
            return null;
        }

        static string Cpu(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            int[] ids = cpu.SubChips.Where(s => s.Name == "RAM256" || s.Name == "Tampon8").Select(s => s.ID).ToArray();
            if (ids.Length != 2) return "RAM256 / Tampon8 not found in the CPU";
            ChipExtractor.Result r = ChipExtractor.Extract(cpu, ids, "MEMOIRE", lib, out string e);
            if (r == null) return e;
            if (!DirectedCases.Bodies.TryGetValue("CPU: micro-program: A=5, B=3, A+B on the bus, store to RAM[9], read back, A-B", out var program)) return "micro-program case not registered";
            using var f = new DirectedCases.Fixture(r.NewParent, With(chips, r.NewChip));
            string err = program(f);
            if (err != null) return "micro-program on the CPU with extracted memory: " + err;
            var constants = new HashSet<int>(r.NewParent.SubChips.Where(s => s.Name is "VCC" or "GND").Select(s => s.ID));
            return r.NewParent.Wires.Any(w => constants.Contains(w.SourcePinAddress.PinOwnerID) && w.TargetPinAddress.PinOwnerID == r.InstanceID) ? "a VCC / GND feeds an input of the new chip" : null;
        }

        static string InPlace(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            int[] ids = cpu.SubChips.Where(s => s.Name == "RAM256" || s.Name == "Tampon8").Select(s => s.ID).ToArray();
            ChipExtractor.Result r = ChipExtractor.Extract(cpu, ids, "MEMOIRE", lib, out string e);
            if (r == null) return e;
            ChipLibrary lib2 = With(chips, r.NewChip);
            (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(cpu, lib2);
            if (failed) return "CPU failed to load";
            ChipExtractor.ApplyInPlace(dev, r, lib2, noRunningSim: true);
            ChipDescription live = DescriptionCreator.CreateChipDescription(dev);
            string Net(ChipDescription d) => string.Join(";", d.Wires.Select(w => $"{w.SourcePinAddress.PinOwnerID}.{w.SourcePinAddress.PinID}>{w.TargetPinAddress.PinOwnerID}.{w.TargetPinAddress.PinID}").OrderBy(x => x));
            if (Net(live) != Net(r.NewParent)) return "the edited chip's wiring differs from the extracted parent";
            if (string.Join(",", live.SubChips.Select(s => s.ID).OrderBy(x => x)) != string.Join(",", r.NewParent.SubChips.Select(s => s.ID).OrderBy(x => x))) return "components differ";
            if (!DirectedCases.Bodies.TryGetValue("CPU: micro-program: A=5, B=3, A+B on the bus, store to RAM[9], read back, A-B", out var program)) return "micro-program case not registered";
            using var f = new DirectedCases.Fixture(live, lib2);
            string err = program(f);
            return err == null ? null : "micro-program on the CPU edited in place: " + err;
        }

        static string Bus(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            int bus = cpu.SubChips.First(s => s.Name == "BUS-8").ID, term = cpu.SubChips.First(s => s.Name == "BUS-TERMINUS-8").ID;
            if (ChipExtractor.Extract(cpu, new[] { bus }, "X", lib, out _) != null) return "a bus without its terminus was accepted";
            ChipExtractor.Result r = ChipExtractor.Extract(cpu, new[] { bus, term }, "X", lib, out string e);
            return r == null ? "bus + terminus refused: " + e : null;
        }

        static string ReloadClean(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription alu = chips.First(c => c.Name == "ALU8");
            ChipExtractor.Result r = ChipExtractor.Extract(alu, Pick(alu, 0.5, 3), "HALF ALU", lib, out string e);
            if (r == null) return e;
            // through the real serializer, as the save does
            ChipDescription newChip = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(r.NewChip));
            ChipLibrary lib2 = With(chips, newChip);
            foreach (ChipDescription d in new[] { newChip, r.NewParent })
            {
                (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(d, lib2);
                if (failed) return $"{d.Name}: some element failed to load";
                if (dev.Wires.Count != d.Wires.Length) return $"{d.Name}: {dev.Wires.Count} wires loaded of {d.Wires.Length}";
            }
            return null;
        }
    }
}
