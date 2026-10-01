using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Graphics;
using DLS.Simulation;

namespace DLS.Bench
{
    // EDIT MEMORY (user, 2026-10-02): "a memory is N addresses of words, full stop". RAMs on the same address are ONE
    // memory with a wider word (the user's prog_ram: 3 RAM256 = 256 x 24 bits), typed in one go, a separator every
    // 8 bits in binary for the eye only; several lines pasted fill the following words.
    public static class MemoryMergeCases
    {
        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips) => new()
        {
            ("[PC] memory editor: RAMs on the same address are ONE memory (word = the parts side by side, ram0 = low byte), the circuit reads it", () => Parallel(lib, chips)),
            ("memory editor: binary words wider than a byte show a separator every 8 bits; typing and parsing ignore it", Separators),
            ("memory editor: typing regroups the digits and keeps the caret; deleting a separator deletes the digit next to it", Regroup),
            ("memory editor: pasted lines are parsed one word each (CRLF, trailing empty lines, a bad line stops)", ParsePaste),
        };

        public static (string name, Func<string> run) Serial(ChipLibrary lib, ChipDescription[] chips) =>
            ("[PC] memory editor: Ctrl+V / PASTE of several lines fills the selected word and the next ones", () => PasteInMenu(lib, chips));

        // 3 RAM4 sharing the address pins (ram_prog0..2) + one RAM4 on other address pins ("other")
        static (UnitCases.Circuit c, MemoryRules rules, Dictionary<string, int[]> pol, string error) Build(ChipLibrary lib)
        {
            MemoryRules rules = MemoryLayout.ParseRules(MemoryLayoutCases.CpuRulesJson, out _);
            rules.banks = new List<MemoryBankRef>
            {
                new() { name = "ram_prog1", path = new[] { "ram_prog1" } },
                new() { name = "other", path = new[] { "other" } },
                new() { name = "ram_prog2", path = new[] { "ram_prog2" } },
                new() { name = "ram_prog0", path = new[] { "ram_prog0" } },
            };
            var pol = new Dictionary<string, int[]>();
            string e = MemoryLayout.Verify("RAM4", rules, lib, pol);
            if (e != null) return (null, null, null, "RAM4 layout: " + e);
            var c = UnitCases.Build("PARALLEL", new[] { "A1", "A0", "B1", "B0", "D0:8", "D1:8", "D2:8", "W" }, new[] { "O0:8", "O1:8", "O2:8" }, b =>
            {
                int vcc = b.Add(ChipType.Vcc);
                int Ram(string label)
                {
                    int id = b.AddCustom("RAM4");
                    int i = b.subChips.FindIndex(s => s.ID == id);
                    SubChipDescription s = b.subChips[i]; s.Label = label; b.subChips[i] = s;
                    b.Wire(b.Out(vcc, 0), b.In(id, 4)); // OE
                    b.Wire(b.Out(vcc, 0), b.In(id, 5)); // Cs
                    b.Wire(b.Input("W"), b.In(id, 3));
                    return id;
                }
                string[] data = { "D0", "D1", "D2" };
                for (int k = 0; k < 3; k++)
                {
                    int r = Ram("ram_prog" + k);
                    b.Wire(b.Input("A1"), b.In(r, 1)); b.Wire(b.Input("A0"), b.In(r, 2));
                    b.Wire(b.Input(data[k]), b.In(r, 0));
                    b.Wire(b.Out(r, 0), b.Output("O" + k));
                }
                int other = Ram("other");
                b.Wire(b.Input("B1"), b.In(other, 1)); b.Wire(b.Input("B0"), b.In(other, 2));
                b.Wire(b.Input("D0"), b.In(other, 0));
            }, lib);
            return (c, rules, pol, null);
        }

        static string Parallel(ChipLibrary lib, ChipDescription[] chips)
        {
            var (c, rules, pol, err) = Build(lib);
            if (err != null) return err;
            foreach (string i in new[] { "A1", "A0", "B1", "B0", "D0", "D1", "D2", "W" }) c.Set(i, 0);
            c.Step(10);
            List<MemoryBank> banks = MemoryLayout.Banks(c.target, c.desc, rules, pol, c.lib, out string e);
            if (banks == null) return e;
            string Describe() => string.Join(", ", banks.Select(b => $"{b.Name} {b.WordCount}x{b.Bits}"));
            MemoryBank merged = banks.FirstOrDefault(b => b.Parts != null);
            if (banks.Count != 2 || merged == null) return "expected 2 memories (the 3 parallel RAMs as one, and the other RAM): " + Describe();
            if (merged.WordCount != 4 || merged.Bits != 24) return "merged memory should be 4 x 24 bits: " + Describe();
            if (string.Join(",", merged.Parts.Select(p => p.Name)) != "ram_prog0,ram_prog1,ram_prog2") return "parts not in name order (ram_prog0 = low byte): " + string.Join(",", merged.Parts.Select(p => p.Name));
            if (banks.First(b => b.Parts == null).Name != "other") return "the RAM on other address pins was merged too";

            merged.Write(2, 0xABCDEF, c.root.Program);
            c.Step(10);
            if (merged.Read(2) != 0xABCDEF) return $"merged word 2 reads {merged.Read(2):X6}, wrote ABCDEF";
            if (merged.Parts[0].Read(2) != 0xEF || merged.Parts[1].Read(2) != 0xCD || merged.Parts[2].Read(2) != 0xAB) return "the bytes did not land in ram_prog0 (low), ram_prog1, ram_prog2 (high)";
            // and the circuit itself reads them at address 2
            c.Set("A1", 1); c.Set("A0", 0);
            c.Step(10);
            if (c.OutValue("O0") != 0xEF || c.OutValue("O1") != 0xCD || c.OutValue("O2") != 0xAB) return $"circuit at address 2 reads {c.OutValue("O2"):X2} {c.OutValue("O1"):X2} {c.OutValue("O0"):X2}, expected AB CD EF";
            return null;
        }

        static string Separators()
        {
            var cases = new (string text, int bits, int from, int to, string expect)[]
            {
                ("ABCDEF", 24, 0, 2, "10101011 11001101 11101111"),
                ("1", 24, 0, 2, "00000000 00000000 00000001"),
                ("FFF", 12, 0, 2, "1111 11111111"),
                ("FF", 8, 0, 2, "11111111"),          // a byte: no separator
                ("10101011 11001101 11101111", 24, 2, 0, "ABCDEF"),
                ("101010111100110111101111", 24, 2, 0, "ABCDEF"), // typed without separators
                ("ABCDEF", 24, 0, 0, "ABCDEF"),       // hex: no separator
            };
            foreach (var c in cases)
            {
                string got = MemoryEditMenu.ConvertText(c.text, c.bits, c.from, c.to);
                if (got != c.expect) return $"'{c.text}' ({c.bits} bits) {c.from}->{c.to} gave '{got}', expected '{c.expect}'";
            }
            if (!MemoryEditMenu.FitsWord("10101011 11001101 11101111", 24, 2)) return "a grouped 24-bit binary word refused";
            if (MemoryEditMenu.FitsWord("1 10101011 11001101 11101111", 24, 2)) return "25 bits accepted in a 24-bit word";
            return null;
        }

        static string Regroup()
        {
            // the 24th digit typed at the end
            var (t1, c1) = MemoryEditMenu.Regroup("000000000000000011110000", 24, null, 24, 2);
            if (t1 != "00000000 00000000 11110000" || c1 != t1.Length) return $"typed in one go: '{t1}' caret {c1}";
            // a digit typed in the middle: caret stays right after it
            var (t2, c2) = MemoryEditMenu.Regroup("000000001 11111111", 9, "00000000 11111111", 16, 2);
            if (t2 != "0 00000001 11111111" || t2.Substring(0, c2).Replace(" ", "") != "000000001") return $"typed in the middle: '{t2}' caret {c2}";
            // backspace on the separator: the digit before it goes (else the separator would just come back)
            var (t3, c3) = MemoryEditMenu.Regroup("0000000111111111", 8, "00000001 11111111", 16, 2);
            if (t3 != "0000000 11111111" || c3 != 7) return $"separator deleted: '{t3}' caret {c3}, expected '0000000 11111111' caret 7";
            return null;
        }

        static string ParsePaste()
        {
            var v = MemoryEditMenu.ParsePasted("AB\r\nCD\nEF\n\n", 8, 0, out string e);
            if (e != null || string.Join(",", v.Select(x => x.ToString("X2"))) != "AB,CD,EF") return $"hex lines: [{string.Join(",", v)}] {e}";
            v = MemoryEditMenu.ParsePasted("00000001 00000010 00000011\n000000010000001000000100", 24, 2, out e);
            if (e != null || v.Count != 2 || v[0] != 0x010203 || v[1] != 0x010204) return "binary 24-bit lines (with and without separators) misread";
            v = MemoryEditMenu.ParsePasted("11\nZZ\n33", 8, 0, out e);
            if (v.Count != 1 || e == null || !e.Contains("line 2")) return "a bad line must stop the paste and be reported";
            v = MemoryEditMenu.ParsePasted("1FF", 8, 0, out e);
            return v.Count == 0 && e != null ? null : "a value too wide for the word was pasted";
        }

        static string PasteInMenu(ChipLibrary lib, ChipDescription[] chips)
        {
            var (c, rules, pol, err) = Build(lib);
            if (err != null) return err;
            foreach (string i in new[] { "A1", "A0", "B1", "B0", "D0", "D1", "D2", "W" }) c.Set(i, 0);
            c.Step(10);
            List<MemoryBank> banks = MemoryLayout.Banks(c.target, c.desc, rules, pol, c.lib, out string e);
            if (banks == null) return e;
            MemoryBank merged = banks.First(b => b.Parts != null);
            try
            {
                MemoryEditMenu.OpenForTests(new List<MemoryBank> { merged });
                MemoryEditMenu.SetModeForTests(0);
                string before0 = MemoryEditMenu.FieldText(0);
                MemoryEditMenu.FocusForTests(1);
                MemoryEditMenu.PasteForTests("111111\r\n222222\n333333\n444444\n");
                if (MemoryEditMenu.FieldText(0) != before0) return "the word before the selected one was changed";
                if (MemoryEditMenu.FieldText(1) != "111111" || MemoryEditMenu.FieldText(2) != "222222" || MemoryEditMenu.FieldText(3) != "333333") return $"words 1..3 after the paste: {MemoryEditMenu.FieldText(1)} {MemoryEditMenu.FieldText(2)} {MemoryEditMenu.FieldText(3)}";
                MemoryEditMenu.SetModeForTests(2);
                return MemoryEditMenu.FieldText(1) == "00010001 00010001 00010001" ? null : $"word 1 in binary: '{MemoryEditMenu.FieldText(1)}'";
            }
            finally { MemoryEditMenu.Reset(); }
        }
    }
}
