using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // An input left unconnected is a quiet 0 for EVERYTHING downstream, not only for logic (user, 2026-10-03, ALU4:
    // the shifters' unconnected C_in went through a MERGE 1->4 and their 4-bit OUT flickered as floating). The slot of
    // an undriven pin held "floating low": a NAND read it as 0, but a merge / split / enabled buffer copied the
    // floating flag along.
    public static class UnconnectedCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("unconnected input through a MERGE 1->4 (the user's shifter C_in) is a driven 0, never floating", () => Through(ChipType.Merge_1To4Bit)),
            ("unconnected 4-bit input through a SPLIT 4->1 gives driven 0s", () => Through(ChipType.Split_4To1Bit)),
            ("unconnected data input of an ENABLED 3-state buffer outputs a driven 0", () => Through(ChipType.TriStateBuffer)),
            ("unconnected input passed straight to an output pin reads a driven 0", () => Through(ChipType.Custom)),
        };

        static ChipLibrary Lib(ChipType kind)
        {
            // the user's RightShifter shape: C_in + A3..A1 merged into a 4-bit OUT; or a split / buffer / pass-through
            ChipLibrary lib = BenchProject.BuiltinsOnly();
            var c = UnitCases.Build("SHIFT", new[] { "C_in", "A3", "A2", "A1", "W:4" }, new[] { "OUT:4", "B3", "B0", "Y" }, b =>
            {
                if (kind == ChipType.Merge_1To4Bit)
                {
                    int m = b.Add(ChipType.Merge_1To4Bit);
                    b.Wire(b.Input("C_in"), b.In(m, 0)); b.Wire(b.Input("A3"), b.In(m, 1)); b.Wire(b.Input("A2"), b.In(m, 2)); b.Wire(b.Input("A1"), b.In(m, 3));
                    b.Wire(b.Out(m, 0), b.Output("OUT"));
                }
                else if (kind == ChipType.Split_4To1Bit)
                {
                    int s = b.Add(ChipType.Split_4To1Bit);
                    b.Wire(b.Input("W"), b.In(s, 0));
                    b.Wire(b.Out(s, 0), b.Output("B3")); b.Wire(b.Out(s, 3), b.Output("B0"));
                }
                else if (kind == ChipType.TriStateBuffer)
                {
                    int t = b.Add(ChipType.TriStateBuffer);
                    int vcc = b.Add(ChipType.Vcc);
                    // buffer pins: data, enable (enable held high by VCC); data = the unconnected C_in
                    b.Wire(b.Input("C_in"), b.In(t, 0)); b.Wire(b.Out(vcc, 0), b.In(t, 1));
                    b.Wire(b.Out(t, 0), b.Output("Y"));
                }
                else b.Wire(b.Input("C_in"), b.Output("Y"));
            });
            return new ChipLibrary(new[] { c.desc }, BuiltinChipCreator.CreateAllBuiltinChipDescriptions());
        }

        static string Through(ChipType kind)
        {
            ChipLibrary lib = Lib(kind);
            // the parent leaves C_in and W unconnected, drives A3..A1 = 1, 0, 1
            var p = UnitCases.Build("PARENT", new[] { "A3", "A2", "A1" }, new[] { "OUT:4", "B3", "B0", "Y" }, b =>
            {
                int s = b.AddCustom("SHIFT");
                b.Wire(b.Input("A3"), b.In(s, 1)); b.Wire(b.Input("A2"), b.In(s, 2)); b.Wire(b.Input("A1"), b.In(s, 3));
                b.Wire(b.Out(s, 0), b.Output("OUT")); b.Wire(b.Out(s, 1), b.Output("B3")); b.Wire(b.Out(s, 2), b.Output("B0")); b.Wire(b.Out(s, 3), b.Output("Y"));
            }, lib);
            p.Set("A3", 1); p.Set("A2", 0); p.Set("A1", 1);
            // long enough for noise re-draws (a floating line read by logic changes "about once every 1024 steps")
            for (int step = 0; step < 4000; step += 50)
            {
                p.Step(50);
                switch (kind)
                {
                    case ChipType.Merge_1To4Bit:
                        if (p.OutValue("OUT") < 0) return $"step {step}: OUT is floating (an unconnected C_in made it flicker)";
                        if (BitCountOf(p.OutValue("OUT")) != 2) return $"step {step}: OUT = {Convert.ToString(p.OutValue("OUT"), 2)}, expected the 2 ones of A3, A1 and a 0 for C_in";
                        break;
                    case ChipType.Split_4To1Bit:
                        if (p.OutValue("B3") != 0 || p.OutValue("B0") != 0) return $"step {step}: split of an unconnected input gives B3 = {p.OutValue("B3")}, B0 = {p.OutValue("B0")} (-1 = floating), expected 0";
                        break;
                    default:
                        if (p.OutValue("Y") != 0) return $"step {step}: Y = {p.OutValue("Y")} (-1 = floating), expected a driven 0";
                        break;
                }
            }
            return null;
        }

        static int BitCountOf(int v) { int n = 0; for (; v > 0; v >>= 1) n += v & 1; return n; }
    }
}
