using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // The DEM122032A builtin (user, 2026-10-03: "the real component"): driven through its own pins exactly as a CPU
    // would — commands and data on DB with A0 / R/W, taken on the falling edge of E1 / E2, reads on DB OUT.
    public static class LcdCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("LCD DEM122032A: display on, page + column commands, data bytes draw 8 vertical pixels, the column advances", Draw),
            ("LCD DEM122032A: E2 drives the right half (x 61..121), E1 the left", Halves),
            ("LCD DEM122032A: display off blanks, A5 lights everything, the start line scrolls, ADC reverse mirrors", Modes),
            ("LCD DEM122032A: status read, dummy read then data read on DB OUT, DB OUT floats when not reading", Reads),
            ("LCD DEM122032A: read-modify-write keeps the column on reads and restores it at END; RES resets the addresses", RmwAndReset),
            ("LCD DEM122032A: the display RAM is saved with the chip (memory state) and restored", SavedState),
            ("LCD DEM122032A: a write is taken on the FALLING edge of E (data changed while E is high: the last value counts)", FallingEdge),
        };

        static UnitCases.Circuit Lcd()
        {
            var c = UnitCases.Build("t_lcd", new[] { "DB:8", "A0", "RW", "E1", "E2", "RES" }, new[] { "OUT:8" }, b =>
            {
                int d = b.Add(ChipType.LcdDem122032);
                string[] n = { "DB", "A0", "RW", "E1", "E2", "RES" };
                for (int i = 0; i < 6; i++) b.Wire(b.Input(n[i]), b.In(d, i));
                b.Wire(b.Out(d, 0), b.Output("OUT"));
            });
            foreach (string s in new[] { "DB", "A0", "RW", "E1", "E2", "RES" }) c.Set(s, 0);
            c.Step(2);
            return c;
        }

        static SimChip Chip(UnitCases.Circuit c) => c.target.SubChips.First(s => s.ChipType == ChipType.LcdDem122032);

        // one bus cycle on E1 (or E2): set A0 / R/W / DB, E high, E low (the transfer happens on the fall)
        static void Send(UnitCases.Circuit c, string e, int a0, int value)
        {
            c.Set("RW", 0); c.Set("A0", a0); c.Set("DB", value); c.Step(2);
            c.Set(e, 1); c.Step(2); c.Set(e, 0); c.Step(2);
        }

        static int Read(UnitCases.Circuit c, string e, int a0)
        {
            c.Set("RW", 1); c.Set("A0", a0); c.Step(2);
            c.Set(e, 1); c.Step(2);
            int v = c.OutValue("OUT");
            c.Set(e, 0); c.Step(2);
            c.Set("RW", 0); c.Step(2);
            return v;
        }

        static void Clear(UnitCases.Circuit c, string e)
        {
            for (int p = 0; p < 4; p++)
            {
                Send(c, e, 0, 0xB8 | p); Send(c, e, 0, 0x00);
                for (int col = 0; col < 80; col++) Send(c, e, 1, 0);
            }
        }

        static bool Px(UnitCases.Circuit c, int x, int y) => LcdDem122032.Pixel(Chip(c).InternalState, x, y);

        static string Draw()
        {
            var c = Lcd();
            if (Enumerable.Range(0, 122).Any(x => Enumerable.Range(0, 32).Any(y => Px(c, x, y)))) return "pixels lit while the display is OFF (power-on state)";
            Clear(c, "E1");
            Send(c, "E1", 0, 0xAF);            // display ON
            Send(c, "E1", 0, 0xB8 | 1);        // page 1 = rows 8..15
            Send(c, "E1", 0, 5);               // column 5
            Send(c, "E1", 1, 0xFF);            // x = 5: rows 8..15
            Send(c, "E1", 1, 0x81);            // x = 6 (column advanced): rows 8 and 15
            for (int y = 0; y < 32; y++)
            {
                bool e5 = y >= 8 && y <= 15, e6 = y == 8 || y == 15;
                if (Px(c, 5, y) != e5) return $"pixel (5, {y}) = {Px(c, 5, y)}, expected {e5}";
                if (Px(c, 6, y) != e6) return $"pixel (6, {y}) = {Px(c, 6, y)}, expected {e6} (bit 0 = top of the page)";
                if (Px(c, 7, y)) return $"pixel (7, {y}) lit";
            }
            int col = (int)Chip(c).InternalState[LcdDem122032.Column];
            return col == 7 ? null : $"column after two bytes from 5 = {col}, expected 7";
        }

        static string Halves()
        {
            var c = Lcd();
            Clear(c, "E1"); Clear(c, "E2");
            Send(c, "E1", 0, 0xAF); Send(c, "E2", 0, 0xAF);
            Send(c, "E2", 0, 0xB8); Send(c, "E2", 0, 0); Send(c, "E2", 1, 0x01); // right half, column 0, row 0
            Send(c, "E1", 0, 0xB8); Send(c, "E1", 0, 60); Send(c, "E1", 1, 0x01); // left half, last visible column
            if (!Px(c, 61, 0)) return "E2 column 0 is not x = 61";
            if (!Px(c, 60, 0)) return "E1 column 60 is not x = 60";
            if (Px(c, 0, 0) || Px(c, 121, 0)) return "a pixel lit at the wrong place";
            return null;
        }

        static string Modes()
        {
            var c = Lcd();
            Clear(c, "E1");
            Send(c, "E1", 0, 0xAF); Send(c, "E1", 0, 0xB8); Send(c, "E1", 0, 3); Send(c, "E1", 1, 0x01); // (3, 0)
            if (!Px(c, 3, 0)) return "test setup: (3, 0) should be lit";
            Send(c, "E1", 0, 0xC0 | 5); // start line 5: RAM line 0 shows at the row 27
            if (Px(c, 3, 0) || !Px(c, 3, 27)) return "start line 5 does not scroll line 0 to row 27";
            Send(c, "E1", 0, 0xC0);
            Send(c, "E1", 0, 0xA1); // ADC reverse: column 3 shows at segment 76 -> not visible; segment 3 shows column 76
            if (Px(c, 3, 0)) return "ADC reverse still shows column 3 at x = 3";
            Send(c, "E1", 0, 0xA0);
            Send(c, "E1", 0, 0xA5); // static drive: everything on
            if (!Px(c, 40, 20) || !Px(c, 10, 31)) return "A5 (static drive) does not light everything";
            Send(c, "E1", 0, 0xA4);
            Send(c, "E1", 0, 0xAE); // display off
            return Px(c, 3, 0) ? "display OFF still shows pixels" : null;
        }

        static string Reads()
        {
            var c = Lcd();
            if (c.OutValue("OUT") != -1) return "DB OUT is driven while nothing reads";
            int st = Read(c, "E1", 0);
            if (st != 0x60) return $"status after power on = {st:X2}, expected 60 (ADC normal, display OFF)";
            Send(c, "E1", 0, 0xAF);
            st = Read(c, "E1", 0);
            if (st != 0x40) return $"status with the display ON = {st:X2}, expected 40";
            Send(c, "E1", 0, 0xB8 | 2); Send(c, "E1", 0, 10); Send(c, "E1", 1, 0x5A); Send(c, "E1", 1, 0xC3); // page 2: col 10 = 5A, col 11 = C3
            Send(c, "E1", 0, 10);
            Read(c, "E1", 1);                    // dummy read
            int d1 = Read(c, "E1", 1), d2 = Read(c, "E1", 1);
            if (d1 != 0x5A || d2 != 0xC3) return $"data reads after a dummy read gave {d1:X2} {d2:X2}, expected 5A C3";
            return c.OutValue("OUT") == -1 ? null : "DB OUT still driven after the read";
        }

        static string RmwAndReset()
        {
            var c = Lcd();
            Send(c, "E1", 0, 0xB8); Send(c, "E1", 0, 20); Send(c, "E1", 1, 0x0F);
            Send(c, "E1", 0, 20);
            Send(c, "E1", 0, 0xE0);              // read-modify-write from column 20
            Read(c, "E1", 1);                    // dummy
            int v = Read(c, "E1", 1);
            if (v != 0x0F) return $"RMW read gave {v:X2}, expected 0F";
            if (Chip(c).InternalState[LcdDem122032.Column] != 20) return "a read advanced the column in read-modify-write";
            Send(c, "E1", 1, 0xF0);              // the write advances
            Send(c, "E1", 0, 0xEE);              // END: column back to 20
            if (Chip(c).InternalState[LcdDem122032.Column] != 20) return "END did not restore the column";
            if (Chip(c).InternalState[20] != 0xF0) return "the RMW write did not land at column 20";
            c.Set("RES", 1); c.Step(2); c.Set("RES", 0); c.Step(2);
            uint[] s = Chip(c).InternalState;
            return s[LcdDem122032.Column] == 0 && s[LcdDem122032.Page] == 3 && s[LcdDem122032.StartLine] == 0 ? null : "RES did not reset column 0 / page 3 / start line 0";
        }

        static string FallingEdge()
        {
            var c = Lcd();
            Send(c, "E1", 0, 0xB8); Send(c, "E1", 0, 0);
            c.Set("RW", 0); c.Set("A0", 1); c.Set("DB", 0x01); c.Step(2);
            c.Set("E1", 1); c.Step(2);
            c.Set("DB", 0xA5); c.Step(2);   // the data changes while E is high
            c.Set("E1", 0); c.Step(2);
            uint got = Chip(c).InternalState[0];
            return got == 0xA5 ? null : $"wrote {got:X2}: the value at the rising edge was taken, the real chip latches at the falling edge (A5)";
        }

        static string SavedState()
        {
            var c = Lcd();
            Send(c, "E1", 0, 0xAF); Send(c, "E1", 0, 0xB8); Send(c, "E1", 0, 0); Send(c, "E1", 1, 0x77);
            ChipDescription saved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(c.desc));
            saved.MemoryState = MemorySnapshot.Capture(c.target);
            var r = UnitCases.Rebuild(c, Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(saved)));
            r.Step(2);
            uint[] s = r.target.SubChips.First(x => x.ChipType == ChipType.LcdDem122032).InternalState;
            return s[0] == 0x77 && s[LcdDem122032.On] == 1 ? null : "the LCD's RAM / display state was not restored from the saved memory state";
        }
    }
}
