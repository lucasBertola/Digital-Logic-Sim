using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // The native RAM65536 (user, 2026-10-04: "a NATIVE RAM, super fast, working like the existing ones"): the interface
    // and behaviour of the user's latch RAMs, checked against their RAM256 on the same sequence.
    public static class Ram65536Cases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary fixtures, ChipDescription[] chips)
        {
            var list = new List<(string, Func<string>)>
            {
                ("RAM65536: written while Cs and We are 1 (a level), D_out = the addressed byte with Cs and Oe, floating otherwise; 16-bit address, bytes of one state word independent", Basic),
                ("RAM65536: the memory is saved with the chip (packed, 16384 state words) and restored", Saved),
                ("RAM65536: EDIT MEMORY sees it, inside a chip and placed directly (65536 words of 8 bits); an edited word is read by the circuit", MemoryEditor),
            };
            if (chips.Any(c => c.Name == "RAM256"))
                list.Add(("[PC] RAM65536 behaves like the user's RAM256 (latches) on the same random writes and reads", () => LikeRam256(fixtures)));
            return list;
        }

        static readonly string[] Pins = { "D_in", "Adr_high", "Adr_low", "We", "Oe", "Cs" };

        static UnitCases.Circuit Ram()
        {
            var c = UnitCases.Build("t_ram64k", new[] { "D_in:8", "Adr_high:8", "Adr_low:8", "We", "Oe", "Cs" }, new[] { "D_out:8" }, b =>
            {
                int r = b.Add(ChipType.Ram65536);
                for (int i = 0; i < 6; i++) b.Wire(b.Input(Pins[i]), b.In(r, i));
                b.Wire(b.Out(r, 0), b.Output("D_out"));
            });
            foreach (string p in Pins) c.Set(p, 0);
            c.Step(2);
            return c;
        }

        static void At(UnitCases.Circuit c, int a) { c.Set("Adr_high", (a >> 8) & 0xFF); c.Set("Adr_low", a & 0xFF); }

        static void Write(UnitCases.Circuit c, int a, int v)
        {
            At(c, a); c.Set("D_in", v); c.Set("Cs", 1); c.Set("Oe", 0); c.Step(1);
            c.Set("We", 1); c.Step(1); c.Set("We", 0); c.Step(1);
        }

        static int Read(UnitCases.Circuit c, int a)
        {
            At(c, a); c.Set("We", 0); c.Set("Cs", 1); c.Set("Oe", 1); c.Step(1);
            int v = c.OutValue("D_out");
            c.Set("Oe", 0); c.Step(1);
            return v;
        }

        static string Basic()
        {
            var c = Ram();
            if (c.OutValue("D_out") != -1) return "D_out driven while Cs = 0";
            var data = new Dictionary<int, int> { [0x0000] = 0x11, [0x1234] = 0x5A, [0x1235] = 0xA5, [0x1236] = 0x01, [0x1237] = 0xFE, [0xFFFF] = 0x77, [0x00FF] = 0x42, [0xFF00] = 0x24 };
            foreach (var kv in data) Write(c, kv.Key, kv.Value);
            foreach (var kv in data) { int v = Read(c, kv.Key); if (v != kv.Value) return $"read {v:X2} at {kv.Key:X4}, wrote {kv.Value:X2}"; }
            // a level: while We stays 1 the stored byte follows D_in (the last value counts)
            At(c, 0x2000); c.Set("Cs", 1); c.Set("D_in", 0x10); c.Set("We", 1); c.Step(1);
            c.Set("D_in", 0x20); c.Step(1); c.Set("We", 0); c.Step(1);
            if (Read(c, 0x2000) != 0x20) return "a write is not a level (the value present while We was 1 last must stay)";
            // We with Cs = 0 writes nothing; Oe with Cs = 0 drives nothing
            At(c, 0x1234); c.Set("Cs", 0); c.Set("D_in", 0x99); c.Set("We", 1); c.Set("Oe", 1); c.Step(1);
            if (c.OutValue("D_out") != -1) return "D_out driven with Cs = 0";
            c.Set("We", 0); c.Step(1);
            if (Read(c, 0x1234) != 0x5A) return "We wrote with Cs = 0";
            // Cs and We without Oe: no output; We and Oe together: the byte being written shows
            At(c, 0x3000); c.Set("Cs", 1); c.Set("Oe", 0); c.Set("We", 1); c.Set("D_in", 0x3C); c.Step(1);
            if (c.OutValue("D_out") != -1) return "D_out driven with Oe = 0";
            c.Set("Oe", 1); c.Step(1);
            if (c.OutValue("D_out") != 0x3C) return "with We and Oe both on, D_out does not show the byte being written";
            c.Set("We", 0); c.Set("Oe", 0); c.Step(1);
            return null;
        }

        static string Saved()
        {
            var c = Ram();
            Write(c, 0xBEEF, 0x5C); Write(c, 0x0001, 0x01);
            uint[] s = c.target.SubChips.First(x => x.ChipType == ChipType.Ram65536).InternalState;
            if (s.Length != Ram65536.Words) return $"state of {s.Length} words, expected {Ram65536.Words} (4 bytes per word)";
            ChipDescription saved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(c.desc));
            saved.MemoryState = MemorySnapshot.Capture(c.target);
            var r = UnitCases.Rebuild(c, Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(saved)));
            foreach (string p in Pins) r.Set(p, 0);
            r.Step(2);
            return Read(r, 0xBEEF) == 0x5C && Read(r, 0x0001) == 0x01 ? null : "the RAM's contents were not restored from the saved memory state";
        }

        static string MemoryEditor()
        {
            var c = Ram();
            List<MemoryBank> banks = MemoryLayout.BuiltinBanks(c.target, c.desc, c.lib);
            if (banks.Count != 1 || banks[0].WordCount != 65536 || banks[0].Bits != 8) return $"inside a chip: {banks.Count} banks, {(banks.Count > 0 ? banks[0].WordCount : 0)} words";
            Write(c, 0x4242, 0x10);
            if (banks[0].Read(0x4242) != 0x10) return "the editor does not read what the circuit wrote";
            banks[0].Write(0x4242, 0x99, c.target.Program);
            banks[0].Write(0x4243, 0x66, c.target.Program);
            if (Read(c, 0x4242) != 0x99 || Read(c, 0x4243) != 0x66) return "a word edited in the editor is not what the circuit reads";
            SimChip direct = c.target.SubChips.First(x => x.ChipType == ChipType.Ram65536);
            MemoryBank own = MemoryLayout.BuiltinBank(direct, "RAM65536");
            return own != null && own.WordCount == 65536 && own.Read(0x4243) == 0x66 ? null : "placed directly: not seen as a memory";
        }

        // the same sequence on the user's RAM256 (address = Adresses, high byte 0) and on RAM65536
        static string LikeRam256(ChipLibrary fixtures)
        {
            var latch = UnitCases.Build("t_ram256", new[] { "D_in:8", "A:8", "We", "Oe", "Cs" }, new[] { "D_out:8" }, b =>
            {
                int r = b.AddCustom("RAM256");
                string[] n = { "D_in", "A", "We", "Oe", "Cs" };
                for (int i = 0; i < 5; i++) b.Wire(b.Input(n[i]), b.In(r, i));
                b.Wire(b.Out(r, 0), b.Output("D_out"));
            }, fixtures);
            var native = Ram();
            const int settle = 6;
            void Both(string pin, int v) { native.Set(pin == "A" ? "Adr_low" : pin, v); latch.Set(pin, v); }
            void Step() { native.Step(settle); latch.Step(settle); }
            foreach (string p in new[] { "D_in", "A", "We", "Oe", "Cs" }) Both(p, 0);
            Step();
            // fill both on 16 addresses (the latch RAM powers up random; simulating it is slow, so the sequence stays short)
            Both("Cs", 1);
            for (int a = 0; a < 16; a++) { Both("A", a * 17); Both("D_in", (a * 37 + 11) & 0xFF); Step(); Both("We", 1); Step(); Both("We", 0); Step(); }
            var rnd = new Random(3);
            for (int op = 0; op < 120; op++)
            {
                int kind = rnd.Next(4);
                Both("We", 0); Both("Oe", 0); Step();
                Both("A", rnd.Next(16) * 17); // the 16 filled addresses (spread over the 8 address bits) Both("D_in", rnd.Next(256)); Both("Cs", rnd.Next(5) == 0 ? 0 : 1); Step();
                if (kind == 0) { Both("We", 1); Step(); Both("We", 0); Step(); }           // write
                else if (kind == 3) { Both("We", 1); Both("Oe", 1); Step(); }             // write while reading
                else { Both("Oe", 1); Step(); }                                           // read
                int n = native.OutValue("D_out"), l = latch.OutValue("D_out");
                if (n != l) return $"op {op} (kind {kind}): RAM65536 D_out = {n}, RAM256 D_out = {l} (-1 = floating)";
            }
            return null;
        }
    }
}
