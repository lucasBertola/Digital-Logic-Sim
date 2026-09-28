using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // Saved memory state (ChipDescription.MemoryState, MemorySnapshot): what a chip remembers must come back after
    // save + reload — for every kind of memory: a bare SR latch, the builtin RAM, a register (master/slave
    // flip-flops), a counter, an addressed RAM made of latches, a whole CPU — through the real JSON serializer.
    public static class MemoryCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips) => new()
        {
            ("memory state: SR latch set / reset survives save + reload", SrLatch),
            ("memory state: builtin RAM contents survive save + reload", BuiltinRam),
            ("memory state: JSON round trip of the state", JsonRoundTrip),
            ("memory state: structure changed -> state ignored, nothing breaks", StructureChanged),
            ("memory state: a child's own saved state applies to its instances, the parent's state wins", Nesting),
            ("[PC] memory state: Registre8 value survives reload and the register still loads", () => Registre8(lib, chips)),
            ("[PC] memory state: PC counter value survives reload and it keeps counting", () => Counter(lib, chips)),
            ("[PC] memory state: RAM256 contents survive reload", () => Ram256(lib, chips)),
            ("[PC] memory state: CPU registers and RAM survive reload", () => Cpu(lib, chips)),
            ("[PC] memory state: kept by the editor round trip (no false unsaved star, not lost on the next save)", () => EditorRoundTrip(lib, chips)),
        };

        // ---- helpers ----

        static ChipDescription Clone(ChipDescription d) => Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(d));

        // "save": capture into a copy of the description, through the JSON serializer
        static ChipDescription SaveWithState(ChipDescription desc, SimChip live)
        {
            ChipDescription copy = Clone(desc);
            copy.MemoryState = MemorySnapshot.Capture(live);
            return Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(copy));
        }

        static ChipDescription Find(ChipDescription[] chips, string name) => chips.First(c => ChipDescription.NameMatch(c.Name, name));

        // ---- builtins-only circuits ----

        static UnitCases.Circuit Latch(ChipLibrary lib = null) => UnitCases.Build("t_mem_latch", new[] { "S", "R" }, new[] { "Q" }, b =>
        {
            // active-low SR latch: Q = NAND(S, Qn), Qn = NAND(R, Q)
            int q = b.Add(ChipType.Nand), qn = b.Add(ChipType.Nand);
            b.Wire(b.Input("S"), b.In(q, 0)); b.Wire(b.Out(qn, 0), b.In(q, 1));
            b.Wire(b.Input("R"), b.In(qn, 0)); b.Wire(b.Out(q, 0), b.In(qn, 1));
            b.Wire(b.Out(q, 0), b.Output("Q"));
        }, lib);

        static string SrLatch()
        {
            foreach (int v in new[] { 1, 0 })
            {
                var c = Latch();
                c.Set("S", 1); c.Set("R", 1); c.Step(3);
                if (v == 1) c.Set("S", 0); else c.Set("R", 0);
                c.Step(3);
                c.Set("S", 1); c.Set("R", 1); c.Step(3);
                if (c.OutBit("Q") != v) return $"latch did not hold {v} before saving";
                ChipDescription saved = SaveWithState(c.desc, c.target);
                if (saved.MemoryState == null) return "nothing captured";
                var r = UnitCases.Rebuild(c, saved);
                r.Set("S", 1); r.Set("R", 1); r.Step(5);
                if (r.OutBit("Q") != v) return $"latch holding {v} came back as {r.OutBit("Q")}";
            }
            return null;
        }

        static UnitCases.Circuit Ram() => UnitCases.Build("t_mem_ram", new[] { "ADDR:8", "DATA:8", "WE", "RST", "CLK" }, new[] { "OUT:8" }, b =>
        {
            int r = b.Add(ChipType.dev_Ram_8Bit);
            b.Wire(b.Input("ADDR"), b.In(r, 0)); b.Wire(b.Input("DATA"), b.In(r, 1)); b.Wire(b.Input("WE"), b.In(r, 2));
            b.Wire(b.Input("RST"), b.In(r, 3)); b.Wire(b.Input("CLK"), b.In(r, 4));
            b.Wire(b.Out(r, 0), b.Output("OUT"));
        });

        static string BuiltinRam()
        {
            var c = Ram();
            void Clock(UnitCases.Circuit x) { x.Set("CLK", 1); x.Step(2); x.Set("CLK", 0); x.Step(2); }
            var writes = new (int a, int v)[] { (0, 0x11), (7, 0xA5), (255, 0x3C) };
            c.Set("RST", 0); c.Set("CLK", 0); c.Set("WE", 0); c.Step(2); // every input driven (an unset input floats: noise)
            foreach ((int a, int v) in writes) { c.Set("ADDR", a); c.Set("DATA", v); c.Set("WE", 1); c.Step(2); Clock(c); }
            c.Set("WE", 0); c.Step(2);
            foreach ((int a, int v) in writes)
            {
                c.Set("ADDR", a); c.Step(2);
                if (c.OutValue("OUT") != v) return $"RAM[{a}] = {c.OutValue("OUT")} BEFORE saving, expected {v} (the write itself failed)";
            }
            ChipDescription saved = SaveWithState(c.desc, c.target);
            if (saved.MemoryState == null || saved.MemoryState.Memories == null || saved.MemoryState.Memories.Length != 1) return "RAM contents not captured";
            var r = UnitCases.Rebuild(c, saved);
            r.Set("RST", 0); r.Set("CLK", 0); r.Set("WE", 0);
            foreach ((int a, int v) in writes)
            {
                r.Set("ADDR", a); r.Step(2);
                if (r.OutValue("OUT") != v) return $"RAM[{a}] = {r.OutValue("OUT")} after reload, expected {v}";
            }
            return null;
        }

        static string JsonRoundTrip()
        {
            var c = Latch();
            c.Set("S", 0); c.Set("R", 1); c.Step(3);
            ChipMemoryState st = MemorySnapshot.Capture(c.target);
            ChipDescription d = Clone(c.desc);
            d.MemoryState = st;
            ChipDescription back = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(d));
            if (back.MemoryState == null) return "state lost by the serializer";
            if (back.MemoryState.Hash != st.Hash || back.MemoryState.Gates != st.Gates) return "state changed by the serializer";
            if ((back.MemoryState.Memories ?? Array.Empty<string>()).Length != (st.Memories ?? Array.Empty<string>()).Length) return "memories changed by the serializer";
            return null;
        }

        static string StructureChanged()
        {
            var c = Latch();
            c.Set("S", 0); c.Set("R", 1); c.Step(3);
            ChipDescription saved = SaveWithState(c.desc, c.target);
            // the chip is edited after the save: an extra NAND appears
            var list = saved.SubChips.ToList();
            SubChipDescription extra = list[0];
            list.Add(new SubChipDescription(extra.Name, 9999, "", extra.Position, null, null));
            saved.SubChips = list.ToArray();
            try
            {
                var r = UnitCases.Rebuild(c, saved);
                r.Set("S", 1); r.Set("R", 1); r.Step(5); // must simply run
                ChipMemoryState st = MemorySnapshot.Capture(r.target);
                if (st != null && st.Hash == c.desc.MemoryState?.Hash) return "hash did not change with the structure";
            }
            catch (Exception e) { return "rebuild with a stale state threw: " + e.Message; }
            // Apply must refuse a state captured from another structure
            var other = Ram();
            if (MemorySnapshot.Apply(other.target, saved.MemoryState)) return "a state was applied to a different structure";
            return null;
        }

        static string Nesting()
        {
            // a library with a custom latch chip whose OWN saved state holds 1
            ChipLibrary lib = BenchProject.BuiltinsOnly();
            var inner = Latch(lib);
            inner.Set("S", 0); inner.Set("R", 1); inner.Step(3); inner.Set("S", 1); inner.Step(3);
            ChipDescription latchChip = SaveWithState(inner.desc, inner.target);
            latchChip.Name = "MemLatch";
            lib = new ChipLibrary(new[] { latchChip }, BuiltinChipCreator.CreateAllBuiltinChipDescriptions());

            UnitCases.Circuit Parent() => UnitCases.Build("t_mem_parent", new[] { "S", "R" }, new[] { "Q" }, b =>
            {
                int l = b.AddCustom("MemLatch");
                b.Wire(b.Input("S"), b.In(l, 0)); b.Wire(b.Input("R"), b.In(l, 1));
                b.Wire(b.Out(l, 0), b.Output("Q"));
            }, lib);

            // no parent state: the child's own saved state (1) applies
            var p = Parent();
            p.Set("S", 1); p.Set("R", 1); p.Step(5);
            if (p.OutBit("Q") != 1) return "the latch chip's own saved state (1) was not applied to its instance";
            // the parent resets it and is saved: its state (0) wins over the child's
            p.Set("R", 0); p.Step(3); p.Set("R", 1); p.Step(3);
            if (p.OutBit("Q") != 0) return "reset did not work";
            var r = UnitCases.Rebuild(p, SaveWithState(p.desc, p.target));
            r.Set("S", 1); r.Set("R", 1); r.Step(5);
            if (r.OutBit("Q") != 0) return "the parent's saved state (0) did not override the child's (1)";
            return null;
        }

        // ---- the user's chips ----

        static DirectedCases.Fixture Reload(DirectedCases.Fixture f, ChipDescription desc, ChipLibrary lib)
        {
            ChipDescription saved = SaveWithState(desc, f.Target);
            f.Dispose();
            return new DirectedCases.Fixture(saved, lib);
        }

        static string Registre8(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = Find(chips, "Registre8");
            var f = new DirectedCases.Fixture(d, lib);
            f.Set("IN", 0xB7); f.Set("LOAD", 1); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 0); f.Tick(6);
            f.Cycle("Clock"); f.Set("LOAD", 0); f.Tick(6);
            if (f.Value("OUT") != 0xB7) { f.Dispose(); return "register did not load before saving"; }
            var r = Reload(f, d, lib);
            try
            {
                r.Set("IN", 0x00); r.Set("LOAD", 0); r.Set("OE", 1); r.Set("Clock", 0); r.Set("Reset", 0); r.Tick(6);
                if (r.Value("OUT") != 0xB7) return $"register came back as {r.Value("OUT")}, expected {0xB7}";
                r.Cycle("Clock");
                if (r.Value("OUT") != 0xB7) return "register lost its value on a clock without LOAD after reload";
                r.Set("IN", 0x42); r.Set("LOAD", 1); r.Tick(6); r.Cycle("Clock");
                if (r.Value("OUT") != 0x42) return "register no longer loads after reload";
                return null;
            }
            finally { r.Dispose(); }
        }

        static string Counter(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = Find(chips, "PC");
            var f = new DirectedCases.Fixture(d, lib);
            f.Set("Jump", 0); f.Set("AdressJump", 0); f.Set("clck", 0); f.Set("Reset", 1); f.Tick(6); f.Set("Reset", 0); f.Tick(6);
            for (int i = 0; i < 5; i++) f.Cycle("clck");
            int before = f.Value("OUT");
            if (before != 5) { f.Dispose(); return $"counter at {before} after 5 cycles (expected 5) before saving"; }
            var r = Reload(f, d, lib);
            try
            {
                r.Set("Jump", 0); r.Set("AdressJump", 0); r.Set("clck", 0); r.Set("Reset", 0); r.Tick(6);
                if (r.Value("OUT") != 5) return $"counter came back as {r.Value("OUT")}, expected 5";
                r.Cycle("clck");
                if (r.Value("OUT") != 6) return $"counter at {r.Value("OUT")} after one more cycle, expected 6";
                return null;
            }
            finally { r.Dispose(); }
        }

        static string Ram256(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = Find(chips, "RAM256");
            var f = new DirectedCases.Fixture(d, lib);
            var writes = new (int a, int v)[] { (0, 0x21), (255, 0x77), (128, 0xE4), (9, 0x08) };
            f.Set("Cs", 1); f.Set("Oe", 0); f.Set("We", 0); f.Tick(6);
            foreach ((int a, int v) in writes) { f.Set("Adresses", a); f.Set("D_in", v); f.Tick(6); f.Set("We", 1); f.Tick(6); f.Set("We", 0); f.Tick(6); }
            var r = Reload(f, d, lib);
            try
            {
                r.Set("Cs", 1); r.Set("Oe", 1); r.Set("We", 0);
                foreach ((int a, int v) in writes)
                {
                    r.Set("Adresses", a); r.Tick(6);
                    if (r.Value("D_out") != v) return $"RAM256[{a}] = {r.Value("D_out")} after reload, expected {v}";
                }
                return null;
            }
            finally { r.Dispose(); }
        }

        static string EditorRoundTrip(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = Find(chips, "Registre8");
            var f = new DirectedCases.Fixture(d, lib);
            f.Set("IN", 0x3C); f.Set("LOAD", 1); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 0); f.Tick(6); f.Cycle("Clock");
            ChipDescription saved = SaveWithState(d, f.Target);
            f.Dispose();
            (DevChipInstance dev, bool failed) = DevChipInstance.LoadFromDescriptionTest(saved, lib);
            if (failed) return "load failed";
            ChipDescription again = DLS.SaveSystem.DescriptionCreator.CreateChipDescription(dev);
            if (again.MemoryState == null || again.MemoryState.Hash != saved.MemoryState.Hash || again.MemoryState.Gates != saved.MemoryState.Gates)
                return "the editor dropped the saved memory state";
            string a = DLS.SaveSystem.Saver.CreateSerializedChipDescription(saved), b = DLS.SaveSystem.Saver.CreateSerializedChipDescription(again);
            if (!UnsavedChangeDetector.IsEquivalentJson(a, b)) return "a freshly opened chip with a memory state looks modified";
            return null;
        }

        static string Cpu(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = Find(chips, "CPU");
            var f = new DirectedCases.Fixture(d, lib);
            foreach (string s in new[] { "OE_ALu", "OE_entree", "We_RAM", "Oe_ram", "Load_A", "Load_B", "Load_MAR", "OP2", "OP1", "OP0" }) f.Set(s, 0);
            f.ClockLevel(0);
            f.Set("Reset_all", 1); f.Tick(6); f.Set("Reset_all", 0); f.Tick(6);
            // A <- 5, B <- 3, MAR <- 9, RAM[9] <- 0x5A
            f.Set("ENTREE", 5); f.Set("OE_entree", 1); f.Set("Load_A", 1); f.Tick(6); f.ClockCycle(); f.Set("Load_A", 0);
            f.Set("ENTREE", 3); f.Set("Load_B", 1); f.Tick(6); f.ClockCycle(); f.Set("Load_B", 0);
            f.Set("ENTREE", 9); f.Set("Load_MAR", 1); f.Tick(6); f.ClockCycle(); f.Set("Load_MAR", 0);
            f.Set("ENTREE", 0x5A); f.Tick(6); f.Set("We_RAM", 1); f.Tick(6); f.Set("We_RAM", 0); f.Set("OE_entree", 0); f.Tick(6);
            if (f.Probe("Registre A", "OUT") != 5 || f.Probe("Registre B", "OUT") != 3 || f.Probe("MAR", "OUT") != 9) { f.Dispose(); return "CPU registers not set before saving"; }
            var r = Reload(f, d, lib);
            try
            {
                foreach (string s in new[] { "OE_ALu", "OE_entree", "We_RAM", "Oe_ram", "Load_A", "Load_B", "Load_MAR", "OP2", "OP1", "OP0", "Reset_all" }) r.Set(s, 0);
                r.ClockLevel(0);
                r.Tick(6);
                if (r.Probe("Registre A", "OUT") != 5) return r.FailProbe("A after reload", "Registre A", "OUT", 5);
                if (r.Probe("Registre B", "OUT") != 3) return r.FailProbe("B after reload", "Registre B", "OUT", 3);
                if (r.Probe("MAR", "OUT") != 9) return r.FailProbe("MAR after reload", "MAR", "OUT", 9);
                r.Set("Oe_ram", 1); r.Tick(6);
                if (r.Probe("BUS-8", "BUS-8") != 0x5A) return r.FailProbe("RAM[9] on the bus after reload", "BUS-8", "BUS-8", 0x5A);
                r.Set("Oe_ram", 0); r.Set("OE_ALu", 1); r.Tick(6); // A + B
                if (r.Probe("BUS-8", "BUS-8") != 8) return r.FailProbe("the CPU still computes A + B after reload", "BUS-8", "BUS-8", 8);
                return null;
            }
            finally { r.Dispose(); }
        }
    }
}
