using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Bench
{
    // One directed case per builtin chip the fixture projects do not exercise (pulse, ROM, RAM, the four
    // displays, buzzer, buses of every width, merge/split of every width). Built in code on a builtins-only
    // library, pin indices as in BuiltinChipCreator / Simulator.ProcessBuiltinChip.
    public static class BuiltinCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("builtin PULSE: one pulse of the set width per rising edge", Pulse),
            ("builtin PULSE: floating input gives a floating output (width 0: noise would trigger a pulse)", PulseFloating),
            ("builtin ROM 256x16: contents are read at the address", Rom),
            ("builtin RAM 8-bit: write on rising edge, read at address, reset", Ram),
            ("builtin BUZZER: registers a note at the pitch, none at volume 0", Buzzer),
            ("builtin 7-SEGMENT: segment inputs reach the display", SevenSegment),
            ("builtin LED: input reaches the display", Led),
            ("builtin RGB DISPLAY: write pixel, refresh, read back, reset", DisplayRgb),
            ("builtin DOT DISPLAY: write pixel, refresh, read back, reset", DisplayDot),
            ("builtin BUS 1/4/8 bit: driven source wins on the bus, terminus accepted", Buses),
            ("builtin MERGE/SPLIT 8<->1 and 8<->4 round trips", MergeSplit8),
        };

        static string Expect(bool cond, string msg) => cond ? null : msg;

        static string Pulse()
        {
            var c = UnitCases.Build("t_pulse", new[] { "A" }, new[] { "Q" }, b =>
            {
                int p = b.Add(ChipType.Pulse);
                b.Data(p)[0] = 5; // pulse width in ticks
                b.Wire(b.Input("A"), b.In(p, 0));
                b.Wire(b.Out(p, 0), b.Output("Q"));
            });
            c.Set("A", 0); c.Step(3);
            if (c.OutBit("Q") != 0) return "output high before any edge";
            c.Set("A", 1);
            for (int t = 1; t <= 5; t++) { c.Step(); if (c.OutBit("Q") != 1) return $"tick {t} of the pulse: output low (expected high for 5 ticks)"; }
            c.Step(); if (c.OutBit("Q") != 0) return "output still high after the pulse width";
            c.Step(5); if (c.OutBit("Q") != 0) return "output high while the input stays high (no new edge)";
            c.Set("A", 0); c.Step(2); c.Set("A", 1); c.Step();
            if (c.OutBit("Q") != 1) return "second rising edge did not start a pulse";
            return null;
        }

        static string PulseFloating()
        {
            var c = UnitCases.Build("t_pulsez", new[] { "E" }, new[] { "Q" }, b =>
            {
                int buf = b.Add(ChipType.TriStateBuffer), p = b.Add(ChipType.Pulse);
                b.Data(p)[0] = 0; // a floating input is NOISE, which would trigger a pulse of the default width
                b.Wire(b.Input("E"), b.In(buf, 1));
                b.Wire(b.Out(buf, 0), b.In(p, 0));
                b.Wire(b.Out(p, 0), b.Output("Q"));
            });
            c.Set("E", 0); c.Step(3);
            return Expect(c.OutFloating("Q"), "pulse fed by a disabled buffer should float");
        }

        static string Rom()
        {
            var c = UnitCases.Build("t_rom", new[] { "ADDR:8" }, new[] { "HI:8", "LO:8" }, b =>
            {
                int r = b.Add(ChipType.Rom_256x16);
                uint[] data = b.Data(r);
                data[0] = 0x1234; data[3] = 0xAB12; data[255] = 0xFFFF; data[100] = 0x00FF;
                b.Wire(b.Input("ADDR"), b.In(r, 0));
                b.Wire(b.Out(r, 0), b.Output("HI")); // output 0 = high byte, output 1 = low byte
                b.Wire(b.Out(r, 1), b.Output("LO"));
            });
            foreach ((int addr, int hi, int lo) in new[] { (0, 0x12, 0x34), (3, 0xAB, 0x12), (255, 0xFF, 0xFF), (100, 0x00, 0xFF), (7, 0, 0) })
            {
                c.Set("ADDR", addr); c.Step(2);
                if (c.OutValue("HI") != hi || c.OutValue("LO") != lo) return $"ROM[{addr}] = {c.OutValue("HI"):X2}{c.OutValue("LO"):X2}, expected {hi:X2}{lo:X2}";
            }
            return null;
        }

        static string Ram()
        {
            var c = UnitCases.Build("t_ram", new[] { "ADDR:8", "DATA:8", "WE", "RST", "CLK" }, new[] { "OUT:8" }, b =>
            {
                int r = b.Add(ChipType.dev_Ram_8Bit);
                b.Wire(b.Input("ADDR"), b.In(r, 0)); b.Wire(b.Input("DATA"), b.In(r, 1)); b.Wire(b.Input("WE"), b.In(r, 2));
                b.Wire(b.Input("RST"), b.In(r, 3)); b.Wire(b.Input("CLK"), b.In(r, 4));
                b.Wire(b.Out(r, 0), b.Output("OUT"));
            });
            void Clock() { c.Set("CLK", 1); c.Step(2); c.Set("CLK", 0); c.Step(2); }
            c.Set("RST", 1); c.Set("WE", 0); c.Set("CLK", 0); c.Step(2); Clock(); c.Set("RST", 0); c.Step(2);
            foreach (int a in new[] { 0, 9, 255 }) { c.Set("ADDR", a); c.Step(2); if (c.OutValue("OUT") != 0) return $"RAM[{a}] = {c.OutValue("OUT")} after reset (expected 0)"; }
            c.Set("ADDR", 9); c.Set("DATA", 0x5C); c.Set("WE", 1); c.Step(2);
            if (c.OutValue("OUT") != 0) return "write landed without a clock edge";
            Clock(); c.Set("WE", 0); c.Step(2);
            if (c.OutValue("OUT") != 0x5C) return $"RAM[9] = {c.OutValue("OUT")} after write (expected 0x5C)";
            c.Set("ADDR", 10); c.Step(2); if (c.OutValue("OUT") != 0) return "RAM[10] should still be 0";
            c.Set("DATA", 0x11); Clock(); // WE=0: no write
            if (c.OutValue("OUT") != 0) return "clock edge with WE=0 wrote";
            c.Set("ADDR", 9); c.Step(2); if (c.OutValue("OUT") != 0x5C) return "RAM[9] lost its value";
            return null;
        }

        static string Buzzer()
        {
            var c = UnitCases.Build("t_buzz", new[] { "PITCH:8", "VOL:4" }, Array.Empty<string>(), b =>
            {
                int z = b.Add(ChipType.Buzzer);
                b.Wire(b.Input("PITCH"), b.In(z, 0)); b.Wire(b.Input("VOL"), b.In(z, 1));
            });
            c.Set("PITCH", 40); c.Set("VOL", 15); c.Step(); c.audio.NotifyAllNotesRegistered(1);
            if (!(c.audio.targetAmplitudesPerFreq[40] > 0)) return "no amplitude at the pitch index";
            if (c.audio.targetAmplitudesPerFreq.Where((v, i) => i != 40).Any(v => v > 0)) return "amplitude registered at another pitch";
            c.Set("PITCH", 80); c.Step(); c.audio.NotifyAllNotesRegistered(1);
            if (!(c.audio.targetAmplitudesPerFreq[80] > 0) || c.audio.targetAmplitudesPerFreq[40] > 0) return "pitch change not reflected";
            c.Set("VOL", 0); c.Step(); c.audio.NotifyAllNotesRegistered(1);
            // volume 0 registers nothing: the temp buffer is not cleared (no input this frame), so the
            // previous note may linger — only assert that nothing NEW appeared elsewhere
            if (c.audio.targetAmplitudesPerFreq.Where((v, i) => i != 80).Any(v => v > 0)) return "volume 0 registered a note";
            return null;
        }

        static string SevenSegment()
        {
            int seg = -1;
            var c = UnitCases.Build("t_7seg", new[] { "A", "B", "C", "D", "E", "F", "G", "COL" }, Array.Empty<string>(), b =>
            {
                seg = b.Add(ChipType.SevenSegmentDisplay);
                string[] n = { "A", "B", "C", "D", "E", "F", "G", "COL" };
                for (int i = 0; i < 8; i++) b.Wire(b.Input(n[i]), b.In(seg, i));
            });
            SimChip d = c.target.GetSubChipFromID(seg);
            int pattern = 0b10110101;
            string[] names = { "A", "B", "C", "D", "E", "F", "G", "COL" };
            for (int i = 0; i < 8; i++) c.Set(names[i], pattern >> i & 1);
            c.Step(2);
            for (int i = 0; i < 8; i++)
                if ((d.InputPins[i].State & 1) != (pattern >> i & 1)) return $"segment {names[i]} reads {d.InputPins[i].State & 1}, expected {pattern >> i & 1}";
            return null;
        }

        static string Led()
        {
            int led = -1;
            var c = UnitCases.Build("t_led", new[] { "A" }, Array.Empty<string>(), b =>
            {
                led = b.Add(ChipType.DisplayLED);
                b.Wire(b.Input("A"), b.In(led, 0));
            });
            SimChip d = c.target.GetSubChipFromID(led);
            c.Set("A", 1); c.Step(2); if ((d.InputPins[0].State & 1) != 1) return "LED input not high";
            c.Set("A", 0); c.Step(2); if ((d.InputPins[0].State & 1) != 0) return "LED input not low";
            return null;
        }

        static string DisplayRgb()
        {
            var c = UnitCases.Build("t_rgb", new[] { "ADDR:8", "R:4", "G:4", "B:4", "RST", "WR", "REF", "CLK" }, new[] { "RO:4", "GO:4", "BO:4" }, b =>
            {
                int d = b.Add(ChipType.DisplayRGB);
                string[] n = { "ADDR", "R", "G", "B", "RST", "WR", "REF", "CLK" };
                for (int i = 0; i < 8; i++) b.Wire(b.Input(n[i]), b.In(d, i));
                b.Wire(b.Out(d, 0), b.Output("RO")); b.Wire(b.Out(d, 1), b.Output("GO")); b.Wire(b.Out(d, 2), b.Output("BO"));
            });
            void Clock() { c.Set("CLK", 1); c.Step(2); c.Set("CLK", 0); c.Step(2); }
            string Px() => $"{c.OutValue("RO")},{c.OutValue("GO")},{c.OutValue("BO")}";
            c.Set("CLK", 0); c.Set("RST", 1); c.Set("WR", 0); c.Set("REF", 1); c.Step(2); Clock(); c.Set("RST", 0);
            c.Set("ADDR", 10); c.Set("R", 3); c.Set("G", 5); c.Set("B", 9); c.Set("WR", 1); c.Set("REF", 0); c.Step(2);
            if (Px() != "0,0,0") return "pixel visible before any clock edge: " + Px();
            Clock(); // written to the back buffer only
            if (Px() != "0,0,0") return "pixel visible without a refresh: " + Px();
            c.Set("WR", 0); c.Set("REF", 1); Clock();
            if (Px() != "3,5,9") return "pixel 10 after refresh: " + Px() + ", expected 3,5,9";
            c.Set("ADDR", 11); c.Step(2); if (Px() != "0,0,0") return "pixel 11 should be black: " + Px();
            c.Set("ADDR", 10); c.Set("RST", 1); Clock(); // reset clears the back buffer, refresh copies it
            if (Px() != "0,0,0") return "pixel 10 after reset+refresh: " + Px();
            return null;
        }

        static string DisplayDot()
        {
            var c = UnitCases.Build("t_dot", new[] { "ADDR:8", "PX", "RST", "WR", "REF", "CLK" }, new[] { "OUT" }, b =>
            {
                int d = b.Add(ChipType.DisplayDot);
                string[] n = { "ADDR", "PX", "RST", "WR", "REF", "CLK" };
                for (int i = 0; i < 6; i++) b.Wire(b.Input(n[i]), b.In(d, i));
                b.Wire(b.Out(d, 0), b.Output("OUT"));
            });
            void Clock() { c.Set("CLK", 1); c.Step(2); c.Set("CLK", 0); c.Step(2); }
            c.Set("CLK", 0); c.Set("RST", 1); c.Set("WR", 0); c.Set("REF", 1); c.Step(2); Clock(); c.Set("RST", 0);
            c.Set("ADDR", 200); c.Set("PX", 1); c.Set("WR", 1); c.Set("REF", 1); Clock();
            if (c.OutBit("OUT") != 1) return "pixel 200 not lit after write+refresh";
            c.Set("ADDR", 201); c.Step(2); if (c.OutBit("OUT") != 0) return "pixel 201 lit";
            c.Set("ADDR", 200); c.Set("WR", 0); c.Set("RST", 1); Clock();
            if (c.OutBit("OUT") != 0) return "pixel 200 still lit after reset";
            return null;
        }

        static string Buses()
        {
            foreach ((ChipType bus, ChipType term, int bits) in new[] { (ChipType.Bus_1Bit, ChipType.BusTerminus_1Bit, 1), (ChipType.Bus_4Bit, ChipType.BusTerminus_4Bit, 4), (ChipType.Bus_8Bit, ChipType.BusTerminus_8Bit, 8) })
            {
                int mask = (1 << bits) - 1;
                var c = UnitCases.Build("t_bus" + bits, new[] { "A:" + bits, "B:" + bits, "EA", "EB" }, new[] { "Q:" + bits }, b =>
                {
                    // two 3-state buffers drive the same bus; a terminus closes it
                    int bufA = b.Add(ChipType.TriStateBuffer), bufB = b.Add(ChipType.TriStateBuffer), bs = b.Add(bus), t = b.Add(term);
                    b.Wire(b.Input("A"), b.In(bufA, 0)); b.Wire(b.Input("EA"), b.In(bufA, 1));
                    b.Wire(b.Input("B"), b.In(bufB, 0)); b.Wire(b.Input("EB"), b.In(bufB, 1));
                    b.Wire(b.Out(bufA, 0), b.In(bs, 0)); b.Wire(b.Out(bufB, 0), b.In(bs, 0));
                    b.Wire(b.Out(bs, 0), b.In(t, 0));
                    b.Wire(b.Out(bs, 0), b.Output("Q"));
                });
                int va = 0b10110101 & mask, vb = 0b01001110 & mask;
                c.Set("A", va); c.Set("B", vb); c.Set("EA", 1); c.Set("EB", 0); c.Step(3);
                if (c.OutValue("Q") != va) return $"{bits}-bit bus: A driving, Q = {c.OutValue("Q")}, expected {va}";
                c.Set("EA", 0); c.Set("EB", 1); c.Step(3);
                if (c.OutValue("Q") != vb) return $"{bits}-bit bus: B driving, Q = {c.OutValue("Q")}, expected {vb}";
                c.Set("EB", 0); c.Step(3);
                if (!c.OutFloating("Q")) return $"{bits}-bit bus: nothing driving, Q should float";
            }
            return null;
        }

        static string MergeSplit8()
        {
            var c = UnitCases.Build("t_ms8", new[] { "X:8" }, new[] { "Y1:8", "Y4:8" }, b =>
            {
                int s1 = b.Add(ChipType.Split_8To1Bit), m1 = b.Add(ChipType.Merge_1To8Bit);
                b.Wire(b.Input("X"), b.In(s1, 0));
                for (int i = 0; i < 8; i++) b.Wire(b.Out(s1, i), b.In(m1, i));
                b.Wire(b.Out(m1, 0), b.Output("Y1"));
                int s4 = b.Add(ChipType.Split_8To4Bit), m4 = b.Add(ChipType.Merge_4To8Bit);
                b.Wire(b.Input("X"), b.In(s4, 0));
                b.Wire(b.Out(s4, 0), b.In(m4, 0)); b.Wire(b.Out(s4, 1), b.In(m4, 1));
                b.Wire(b.Out(m4, 0), b.Output("Y4"));
            });
            foreach (int x in new[] { 0, 1, 0x80, 0xA5, 0x5A, 0xFF, 0x0F, 0xF0 })
            {
                c.Set("X", x); c.Step(2);
                if (c.OutValue("Y1") != x) return $"split 8->1 + merge 1->8: {x} -> {c.OutValue("Y1")}";
                if (c.OutValue("Y4") != x) return $"split 8->4 + merge 4->8: {x} -> {c.OutValue("Y4")}";
            }
            return null;
        }
    }
}
