using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // The ST7920 128x64 builtin (user, 2026-10-04: "it must react like the real one"): driven through its own pins
    // exactly as a microcontroller or a CPU would — 8-bit and 4-bit parallel (writes on the falling edge of E, reads on
    // DB OUT) and serial (PSB = 0: CS / SID / SCLK on RS / R/W / E) — and checked on the pixels it shows.
    public static class St7920Cases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("ST7920: off at power on; 8-bit init, clear, text on line 1 draws the ROM characters, the address advances one word per 2 bytes", Text8),
            ("ST7920: text lines are 80h, 90h, 88h, 98h (lines 1 to 4)", TextLines),
            ("ST7920: graphics (extended set, G on): vertical then horizontal address, 16 pixels per word, x 8-15 = rows 32-63, x advances", Graphics),
            ("ST7920: CLEAR leaves the graphics; the display is text XOR graphics", ClearKeepsGraphics),
            ("ST7920: 4-bit mode (20h): bytes as two transfers on DB7-4, writes and status read", FourBit),
            ("ST7920: serial (PSB = 0): start byte + nibble bytes on SCLK rising edges, several bytes per start byte, CS low resets", Serial),
            ("ST7920: status read = address counter, dummy read then data bytes, DB OUT floats when not reading", Reads),
            ("ST7920: user characters (CGRAM, codes 0000h-0006h) and GB2312 16x16 characters", CgramAndChinese),
            ("ST7920: display off blanks, reverse line inverts its band, cursor underline and blink, vertical scroll", Modes),
            ("ST7920: RST low resets; PSB and RST left unconnected = parallel and running (the module's pull-ups)", ResetAndPullUps),
            ("ST7920: a write is taken on the FALLING edge of E (data changed while E is high: the last value counts)", FallingEdge),
            ("ST7920: text, graphics and registers are saved with the chip (memory state) and restored", SavedState),
        };

        static readonly string[] Pins = { "DB", "RS", "RW", "E", "PSB", "RST" };

        static UnitCases.Circuit Lcd(bool wireAll = true)
        {
            int n = wireAll ? 6 : 4;
            var ins = new[] { "DB:8", "RS", "RW", "E", "PSB", "RST" }.Take(n).ToArray();
            var c = UnitCases.Build("t_st7920", ins, new[] { "OUT:8" }, b =>
            {
                int d = b.Add(ChipType.LcdSt7920);
                for (int i = 0; i < n; i++) b.Wire(b.Input(Pins[i]), b.In(d, i));
                b.Wire(b.Out(d, 0), b.Output("OUT"));
            });
            for (int i = 0; i < n; i++) c.Set(Pins[i], 0);
            if (wireAll) { c.Set("PSB", 1); c.Set("RST", 1); }
            c.Step(2);
            return c;
        }

        static uint[] S(UnitCases.Circuit c) => c.target.SubChips.First(s => s.ChipType == ChipType.LcdSt7920).InternalState;
        static bool Px(UnitCases.Circuit c, int x, int y, bool blink = false) => LcdSt7920.Pixel(S(c), x, y, blink);

        // one parallel bus cycle: RS / R/W / DB, E high, E low (the transfer happens on the fall)
        static void Send(UnitCases.Circuit c, int rs, int v)
        {
            c.Set("RW", 0); c.Set("RS", rs); c.Set("DB", v); c.Step(2);
            c.Set("E", 1); c.Step(2); c.Set("E", 0); c.Step(2);
        }
        static void Cmd(UnitCases.Circuit c, params int[] v) { foreach (int x in v) Send(c, 0, x); }
        static void Data(UnitCases.Circuit c, params int[] v) { foreach (int x in v) Send(c, 1, x); }
        static void Text(UnitCases.Circuit c, string t) { foreach (char ch in t) Send(c, 1, ch); }

        static int Read(UnitCases.Circuit c, int rs)
        {
            c.Set("RW", 1); c.Set("RS", rs); c.Step(2);
            c.Set("E", 1); c.Step(2);
            int v = c.OutValue("OUT");
            c.Set("E", 0); c.Step(2);
            c.Set("RW", 0); c.Step(2);
            return v;
        }

        static UnitCases.Circuit Ready()
        {
            var c = Lcd();
            Cmd(c, 0x30, 0x0C, 0x01, 0x06); // 8-bit basic, display on, clear, entry: increment
            return c;
        }

        static void ClearGraphics(UnitCases.Circuit c)
        {
            Cmd(c, 0x34);
            for (int y = 0; y < 32; y++) { Cmd(c, 0x80 | y, 0x80); for (int i = 0; i < 32; i++) Data(c, 0); }
            Cmd(c, 0x30);
        }

        // the 8x16 cell at (x0, y0) must show ROM character ch
        static string CellIs(UnitCases.Circuit c, int x0, int y0, char ch)
        {
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < 8; x++)
                {
                    bool want = ((St7920Font.Half[ch * 16 + r] >> (7 - x)) & 1) != 0;
                    if (Px(c, x0 + x, y0 + r) != want) return $"'{ch}' at ({x0}, {y0}): pixel ({x0 + x}, {y0 + r}) is {(want ? "off" : "on")}";
                }
            return null;
        }

        static bool Any(UnitCases.Circuit c, int x0, int y0, int w, int h) =>
            Enumerable.Range(x0, w).Any(x => Enumerable.Range(y0, h).Any(y => Px(c, x, y)));

        static string Text8()
        {
            var c = Lcd();
            if (Any(c, 0, 0, 128, 64)) return "pixels lit at power on (the display must start OFF)";
            Cmd(c, 0x30, 0x0C, 0x01, 0x06);
            if (Any(c, 0, 0, 128, 64)) return "pixels lit after CLEAR with nothing written (garbage graphics are not shown while G is off)";
            Cmd(c, 0x80);
            Text(c, "HIg!");
            if (S(c)[LcdSt7920.AC] != 2) return $"AC = {S(c)[LcdSt7920.AC]} after 4 bytes, expected 2 (one word per 2 bytes)";
            return CellIs(c, 0, 0, 'H') ?? CellIs(c, 8, 0, 'I') ?? CellIs(c, 16, 0, 'g') ?? CellIs(c, 24, 0, '!') ?? CellIs(c, 32, 0, ' ');
        }

        static string TextLines()
        {
            var c = Ready();
            Cmd(c, 0x80); Text(c, "A1");
            Cmd(c, 0x90); Text(c, "B2");
            Cmd(c, 0x88); Text(c, "C3");
            Cmd(c, 0x98); Text(c, "D4");
            Cmd(c, 0x83); Text(c, "Z");
            return CellIs(c, 0, 0, 'A') ?? CellIs(c, 0, 16, 'B') ?? CellIs(c, 0, 32, 'C') ?? CellIs(c, 0, 48, 'D') ?? CellIs(c, 48, 0, 'Z')
                ?? (string.Join("|", LcdSt7920.TextLines(S(c))).StartsWith("A1    Z ") ? null : "TextLines for Claude: " + string.Join("|", LcdSt7920.TextLines(S(c))));
        }

        static string Graphics()
        {
            var c = Ready();
            ClearGraphics(c);
            Cmd(c, 0x34, 0x36);                 // extended, graphics on
            Cmd(c, 0x80 | 5, 0x80 | 0);         // y = 5, x word 0
            Data(c, 0x80, 0x01);                // pixels 0 and 15 of row 5
            Data(c, 0xFF, 0x00);                // x advanced: pixels 16..23
            Cmd(c, 0x80 | 3, 0x80 | 8);         // y = 3, x word 8 = row 35, x 0..15
            Data(c, 0x00, 0x03);                // pixels 14, 15 of row 35
            bool[] want = new bool[128 * 64];
            want[5 * 128 + 0] = want[5 * 128 + 15] = true;
            for (int x = 16; x < 24; x++) want[5 * 128 + x] = true;
            want[35 * 128 + 14] = want[35 * 128 + 15] = true;
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 128; x++)
                    if (Px(c, x, y) != want[y * 128 + x]) return $"pixel ({x}, {y}) is {(want[y * 128 + x] ? "off" : "on")}";
            Cmd(c, 0x34);                       // G off
            return Any(c, 0, 0, 128, 64) ? "graphics still shown with G off" : null;
        }

        static string ClearKeepsGraphics()
        {
            var c = Ready();
            ClearGraphics(c);
            Cmd(c, 0x34, 0x36, 0x80, 0x80);
            for (int i = 0; i < 16; i++) Data(c, 0xFF); // rows 0: x 0..127 all on
            Cmd(c, 0x30, 0x80);
            Text(c, "H");
            // row 0 of 'H' is blank in the ROM, row 4 has pixels: text XOR graphics
            for (int x = 0; x < 8; x++)
            {
                bool hRow0 = ((St7920Font.Half['H' * 16] >> (7 - x)) & 1) != 0;
                if (Px(c, x, 0) == hRow0) return $"pixel ({x}, 0) is not graphics XOR text";
            }
            Cmd(c, 0x01); // clear: text gone, graphics stay
            for (int x = 0; x < 128; x++) if (!Px(c, x, 0)) return $"CLEAR erased the graphics (pixel ({x}, 0) off)";
            return CellIs(c, 0, 16, ' ');
        }

        static void Send4(UnitCases.Circuit c, int rs, int v)
        {
            Send(c, rs, v & 0xF0);
            Send(c, rs, (v << 4) & 0xF0);
        }

        static string FourBit()
        {
            var c = Lcd();
            Send(c, 0, 0x20);                   // still 8-bit: DB7-4 = 0010 -> 4-bit
            Send4(c, 0, 0x20); Send4(c, 0, 0x0C); Send4(c, 0, 0x01); Send4(c, 0, 0x06);
            Send4(c, 0, 0x80);
            foreach (char ch in "Ok") Send4(c, 1, ch);
            string e = CellIs(c, 0, 0, 'O') ?? CellIs(c, 8, 0, 'k');
            if (e != null) return e;
            // status in 4-bit: high nibble then low nibble, both on DB7-4; AC = 1
            int hi = Read(c, 0), lo = Read(c, 0);
            return hi == 0x00 && lo == 0x10 ? null : $"4-bit status read gave {hi:X2} then {lo:X2}, expected 00 then 10 (AC = 1)";
        }

        // serial: CS = RS, SID = R/W, SCLK = E
        static void Bits(UnitCases.Circuit c, int b)
        {
            for (int i = 7; i >= 0; i--)
            {
                c.Set("RW", (b >> i) & 1); c.Step(1);
                c.Set("E", 1); c.Step(1); c.Set("E", 0); c.Step(1);
            }
        }
        static void SerialByte(UnitCases.Circuit c, int start, int v, bool withStart = true)
        {
            if (withStart) Bits(c, start);
            Bits(c, v & 0xF0); Bits(c, (v << 4) & 0xF0);
        }

        static string Serial()
        {
            var c = Lcd();
            c.Set("PSB", 0); c.Set("RS", 1); c.Step(2); // serial, CS high
            foreach (int cmd in new[] { 0x30, 0x0C, 0x01, 0x06, 0x80 }) SerialByte(c, 0xF8, cmd);
            SerialByte(c, 0xFA, 'S');
            SerialByte(c, 0xFA, 'P', withStart: false);  // a second data byte under the same start byte
            SerialByte(c, 0xFA, 'I');
            string e = CellIs(c, 0, 0, 'S') ?? CellIs(c, 8, 0, 'P') ?? CellIs(c, 16, 0, 'I');
            if (e != null) return "serial: " + e;
            // CS low in the middle of a frame drops it
            Bits(c, 0xFA); Bits(c, 'X' & 0xF0);
            c.Set("RS", 0); c.Step(2); c.Set("RS", 1); c.Step(2);
            SerialByte(c, 0xFA, 'Y');
            if (c.OutValue("OUT") != -1) return "DB OUT driven in serial mode";
            return CellIs(c, 24, 0, 'Y');
        }

        static string Reads()
        {
            var c = Ready();
            if (c.OutValue("OUT") != -1) return "DB OUT is driven while nothing reads";
            Cmd(c, 0x80); Text(c, "ABCD");
            int st = Read(c, 0);
            if (st != 2) return $"status read {st:X2}, expected 02 (BF 0, AC 2)";
            Cmd(c, 0x80);
            int dummy = Read(c, 1), a = Read(c, 1), b = Read(c, 1), cc = Read(c, 1);
            if (a != 'A' || b != 'B' || cc != 'C') return $"data reads after the dummy gave {a:X2} {b:X2} {cc:X2}, expected 41 42 43";
            if (c.OutValue("OUT") != -1) return "DB OUT still driven after the read";
            return dummy == 'A' ? "the first read after the address already returned data: the real chip needs a dummy read" : null;
        }

        static string CgramAndChinese()
        {
            var c = Ready();
            Cmd(c, 0x40 | 16);                  // user character 1, row 0
            for (int r = 0; r < 16; r++) Data(c, r % 2 == 0 ? 0xAA : 0x55, 0xFF); // checkerboard left half, right half full
            Cmd(c, 0x80); Data(c, 0x00, 0x02);  // word 0002h = user character 1
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < 16; x++)
                {
                    bool want = x >= 8 || (((r % 2 == 0 ? 0xAA : 0x55) >> (7 - x)) & 1) != 0;
                    if (Px(c, x, r) != want) return $"user character: pixel ({x}, {r}) wrong";
                }
            Cmd(c, 0x90); Data(c, 0xD6, 0xD0);  // GB2312 D6D0
            int lit = 0;
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < 16; x++)
                {
                    bool p = Px(c, x, 16 + r);
                    if (p != LcdSt7920.CharPixel(S(c), 0xD6D0, x, r)) return $"GB2312 character drawn wrong at ({x}, {16 + r})";
                    if (p) lit++;
                }
            if (lit < 20) return $"the GB2312 character D6D0 shows only {lit} pixels: the 16x16 ROM is missing";
            // its centre column is a full vertical stroke in any font (the character is a box crossed by a vertical bar)
            return Enumerable.Range(0, 16).Count(r => Px(c, 7, 16 + r)) >= 12 ? null : "D6D0 does not look like its character (no central vertical stroke)";
        }

        static string Modes()
        {
            var c = Ready();
            ClearGraphics(c);
            Cmd(c, 0x80); Text(c, "MM");
            Cmd(c, 0x08);
            if (Any(c, 0, 0, 128, 64)) return "display OFF still shows pixels";
            Cmd(c, 0x0C);
            Cmd(c, 0x34, 0x04, 0x30);          // reverse line 1
            if (!Px(c, 100, 0) || !Px(c, 100, 15) || Px(c, 100, 16)) return "reverse line 1 did not invert exactly rows 0..15";
            Cmd(c, 0x34, 0x04, 0x30);          // again: back to normal
            if (Px(c, 100, 0)) return "a second reverse did not restore line 1";
            Cmd(c, 0x0E, 0x80);                // cursor on, at word 0
            if (!Enumerable.Range(0, 16).All(x => Px(c, x, 15))) return "no cursor underline on row 15 of the cursor's cell";
            Cmd(c, 0x0D);                      // blink on, cursor off
            bool same = Enumerable.Range(0, 16).All(x => Enumerable.Range(0, 16).All(y => Px(c, x, y, false) != Px(c, x, y, true)));
            if (!same) return "blink does not invert the cursor's cell";
            Cmd(c, 0x0C, 0x90); Text(c, "W");  // W on line 2 (row 16)
            Cmd(c, 0x34, 0x03, 0x40 | 16, 0x30); // vertical scroll by 16 rows
            return CellIs(c, 0, 0, 'W');
        }

        static string ResetAndPullUps()
        {
            var c = Ready();
            Cmd(c, 0x20);                       // 4-bit
            c.Set("RST", 0); c.Step(2); c.Set("RST", 1); c.Step(2);
            uint[] s = S(c);
            if (s[LcdSt7920.D] != 0 || s[LcdSt7920.DL] != 1 || s[LcdSt7920.AC] != 0) return "RST did not reset (display off, 8-bit, AC 0)";
            var u = Lcd(wireAll: false);         // PSB and RST not wired
            Cmd(u, 0x30, 0x0C, 0x01, 0x80); Text(u, "P");
            return CellIs(u, 0, 0, 'P') is string e ? "with PSB / RST unconnected: " + e : null;
        }

        static string FallingEdge()
        {
            var c = Ready();
            Cmd(c, 0x80);
            c.Set("RW", 0); c.Set("RS", 1); c.Set("DB", 'a'); c.Step(2);
            c.Set("E", 1); c.Step(2);
            c.Set("DB", 'Q'); c.Step(2);       // the data changes while E is high
            c.Set("E", 0); c.Step(2);
            return CellIs(c, 0, 0, 'Q') is string e ? "the value at the rising edge was taken, the real chip latches at the falling edge: " + e : null;
        }

        static string SavedState()
        {
            var c = Ready();
            Cmd(c, 0x80); Text(c, "SV");
            Cmd(c, 0x34, 0x36, 0x80 | 2, 0x80); Data(c, 0xF0, 0x0F);
            ChipDescription saved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(c.desc));
            saved.MemoryState = MemorySnapshot.Capture(c.target);
            var r = UnitCases.Rebuild(c, Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(saved)));
            r.Set("PSB", 1); r.Set("RST", 1); // the circuit's levels (RST low would reset the registers, as on the module)
            r.Step(2);
            uint[] s = r.target.SubChips.First(x => x.ChipType == ChipType.LcdSt7920).InternalState;
            bool ok = (s[LcdSt7920.DdRam] & 0xFFFF) == ('S' << 8 | 'V') && (s[LcdSt7920.GdRam + 2 * 16] & 0xFFFF) == 0xF00F && s[LcdSt7920.D] == 1 && s[LcdSt7920.G] == 1;
            return ok ? null : "the ST7920's text / graphics / registers were not restored from the saved memory state";
        }
    }
}
