using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // Directed tests of the fixture chips: hand-written sequences with KNOWN expected values (a register is
    // loaded then held, a RAM is written then read back, the CPU runs a micro-program...), and exhaustive
    // checks of the combinational chips against a model. Unlike the goldens these assert the FUNCTION, after a
    // generous settle, not the exact tick latency.
    public static class DirectedCases
    {
        const int Settle = 6; // ticks after every input change on a sequential chip (observed latency: <= 3)

        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var cases = new List<(string, Func<string>)>();
            void Add(string chip, string title, Func<Fixture, string> body)
            {
                ChipDescription d = chips.FirstOrDefault(c => ChipDescription.NameMatch(c.Name, chip));
                cases.Add(($"[PC] {chip}: {title}", () =>
                {
                    if (d == null) return "fixture chip missing";
                    Fixture f = new(d, lib);
                    try { return body(f); }
                    finally { f.Dispose(); }
                }));
            }

            // ---------------- sequential ----------------

            Add("Registre8", "load on clock, hold, output enable, reset", f =>
            {
                f.Set("IN", 0x5A); f.Set("LOAD", 1); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 0); f.Tick(Settle);
                f.Cycle("Clock");
                if (f.Value("OUT") != 0x5A) return f.Fail("OUT after loading 0x5A", "OUT", 0x5A);
                f.Set("IN", 0x11); f.Set("LOAD", 0); f.Tick(Settle); f.Cycle("Clock");
                if (f.Value("OUT") != 0x5A) return f.Fail("OUT must hold with LOAD=0", "OUT", 0x5A);
                f.Set("IN", 0x11); f.Set("LOAD", 1); f.Tick(Settle);
                if (f.Value("OUT") != 0x5A) return f.Fail("OUT must not change without a clock", "OUT", 0x5A);
                f.Cycle("Clock");
                if (f.Value("OUT") != 0x11) return f.Fail("OUT after loading 0x11", "OUT", 0x11);
                f.Set("OE", 0); f.Tick(Settle);
                if (!f.IsFloating("OUT")) return f.Fail("OUT must float with OE=0", "OUT", null);
                f.Set("OE", 1); f.Set("Reset", 1); f.Tick(Settle);
                if (f.Value("OUT") != 0) return f.Fail("OUT after reset", "OUT", 0);
                f.Set("Reset", 0); f.Tick(Settle);
                if (f.Value("OUT") != 0) return f.Fail("OUT stays 0 after reset released", "OUT", 0);
                return null;
            });

            Add("Tampon8", "output enable", f =>
            {
                f.Set("IN", 0xA5); f.Set("OE", 1); f.Tick(Settle);
                if (f.Value("OUT") != 0xA5) return f.Fail("pass-through", "OUT", 0xA5);
                f.Set("OE", 0); f.Tick(Settle);
                if (!f.IsFloating("OUT")) return f.Fail("OE=0 must float", "OUT", null);
                f.Set("IN", 0x0F); f.Set("OE", 1); f.Tick(Settle);
                if (f.Value("OUT") != 0x0F) return f.Fail("pass-through again", "OUT", 0x0F);
                return null;
            });

            Add("MOT8", "write (level), read, hold", f =>
            {
                f.Set("D", 0x3C); f.Set("W", 1); f.Set("R", 0); f.Tick(Settle);
                f.Set("W", 0); f.Tick(Settle);
                if (!f.IsFloating("OUT")) return f.Fail("R=0 must float", "OUT", null);
                f.Set("R", 1); f.Tick(Settle);
                if (f.Value("OUT") != 0x3C) return f.Fail("read back", "OUT", 0x3C);
                f.Set("D", 0x00); f.Tick(Settle);
                if (f.Value("OUT") != 0x3C) return f.Fail("D change without W must not write", "OUT", 0x3C);
                f.Set("D", 0xC3); f.Set("W", 1); f.Tick(Settle); f.Set("W", 0); f.Tick(Settle);
                if (f.Value("OUT") != 0xC3) return f.Fail("second write", "OUT", 0xC3);
                return null;
            });

            foreach ((string ram, string[] addrPins, string oe) in new[]
            {
                ("RAM4", new[] { "Adress1", "Adress0" }, "OE"),
                ("RAM16", new[] { "Adress3", "Adress2", "Adress1", "Adress0" }, "Oe"),
                ("RAM64", new[] { "Adresse5", "Adresse4", "Adress3", "Adress2", "Adress1", "Adress0" }, "Oe"),
                ("RAM256", new[] { "Adresses" }, "Oe"),
            })
            {
                Add(ram, "write 3 addresses, read back, chip select", f =>
                {
                    int addrBits = addrPins.Length == 1 ? 8 : addrPins.Length;
                    int maxAddr = (1 << addrBits) - 1;
                    var writes = new (int addr, int val)[] { (0, 0x21), (maxAddr, 0x77), (maxAddr / 2, 0xE4) }; // 3 distinct addresses even on RAM4
                    f.Set("Cs", 1); f.Set(oe, 0); f.Set("We", 0); f.Tick(Settle);
                    foreach ((int a, int v) in writes)
                    {
                        f.SetWord(addrPins, a); f.Set("D_in", v); f.Tick(Settle);
                        f.Set("We", 1); f.Tick(Settle); f.Set("We", 0); f.Tick(Settle);
                    }
                    f.Set(oe, 1);
                    foreach ((int a, int v) in writes)
                    {
                        f.SetWord(addrPins, a); f.Tick(Settle);
                        if (f.Value("D_out") != v) return f.Fail($"read back address {a}", "D_out", v);
                    }
                    // a write with Cs=0 must not land
                    f.Set("Cs", 0); f.SetWord(addrPins, 0); f.Set("D_in", 0x00); f.Tick(Settle);
                    if (!f.IsFloating("D_out")) return f.Fail("Cs=0 must float the output", "D_out", null);
                    f.Set("We", 1); f.Tick(Settle); f.Set("We", 0); f.Tick(Settle);
                    f.Set("Cs", 1); f.Tick(Settle);
                    if (f.Value("D_out") != 0x21) return f.Fail("write with Cs=0 must be ignored", "D_out", 0x21);
                    return null;
                });
            }

            Add("PC", "reset then count 20 clock cycles (wraps at 16)", f =>
            {
                f.Set("Reset", 1); f.Set("Clock", 0); f.Tick(Settle); f.Set("Reset", 0); f.Tick(Settle);
                if (f.Word("Address3", "Address2", "Address1", "Address0") != 0) return f.Fail("after reset", "Address", 0);
                for (int i = 1; i <= 20; i++)
                {
                    f.Cycle("Clock");
                    int got = f.Word("Address3", "Address2", "Address1", "Address0");
                    if (got != (i & 15)) return $"after {i} cycles: Address = {got}, expected {i & 15}";
                }
                return null;
            });

            Add("Bascule D", "master/slave flip-flop: D sampled while high, shown after the fall", f =>
            {
                f.Set("D", 1); f.Set("Clock", 0); f.Set("Reset", 1); f.Tick(Settle); f.Set("Reset", 0); f.Tick(Settle);
                if (f.Value("Q") != 0 || f.Value("Q'") != 1) return f.Fail("after reset", "Q/Q'", "0/1");
                f.Set("Clock", 1); f.Tick(Settle);
                if (f.Value("Q") != 0) return f.Fail("Q must not change while clock is high", "Q", 0);
                f.Set("Clock", 0); f.Tick(Settle);
                if (f.Value("Q") != 1 || f.Value("Q'") != 0) return f.Fail("Q after the falling edge", "Q/Q'", "1/0");
                f.Set("D", 0); f.Tick(Settle);
                if (f.Value("Q") != 1) return f.Fail("Q must hold while clock is low", "Q", 1);
                f.Cycle("Clock");
                if (f.Value("Q") != 0 || f.Value("Q'") != 1) return f.Fail("Q after clocking D=0", "Q/Q'", "0/1");
                return null;
            });

            Add("VerrouD", "transparent latch: follows D while En=1, holds when En=0", f =>
            {
                f.Set("D", 0); f.Set("En", 0); f.Set("Reset", 1); f.Tick(Settle); f.Set("Reset", 0); f.Tick(Settle);
                if (f.Value("Q") != 0) return f.Fail("after reset", "Q", 0);
                f.Set("D", 1); f.Tick(Settle);
                if (f.Value("Q") != 0) return f.Fail("En=0 must hold", "Q", 0);
                f.Set("En", 1); f.Tick(Settle);
                if (f.Value("Q") != 1 || f.Value("Q'") != 0) return f.Fail("En=1 follows D=1", "Q/Q'", "1/0");
                f.Set("D", 0); f.Tick(Settle);
                if (f.Value("Q") != 0) return f.Fail("En=1 follows D=0", "Q", 0);
                f.Set("D", 1); f.Set("En", 0); f.Tick(Settle);
                if (f.Value("Q") != 0) return f.Fail("En=0 holds the last value", "Q", 0);
                return null;
            });

            Add("CPU", "micro-program: A=5, B=3, A+B on the bus, store to RAM[9], read back, A-B", f =>
            {
                foreach (string s in new[] { "OE_ALu", "OE_entree", "We_RAM", "Oe_ram", "Load_A", "Load_B", "Load_MAR", "OP2", "OP1", "OP0", "clock" }) f.Set(s, 0);
                f.Set("Reset_all", 1); f.Tick(Settle); f.Set("Reset_all", 0); f.Tick(Settle);
                if (f.Probe("Registre A", "OUT") != 0 || f.Probe("Registre B", "OUT") != 0 || f.Probe("MAR", "OUT") != 0) return "registers not 0 after Reset_all";

                // A <- 5 (from the input buffer, through the bus)
                f.Set("ENTREE", 5); f.Set("OE_entree", 1); f.Set("Load_A", 1); f.Tick(Settle);
                if (f.Probe("BUS-8", "BUS-8") != 5) return f.FailProbe("bus while OE_entree", "BUS-8", "BUS-8", 5);
                f.Cycle("clock"); f.Set("Load_A", 0);
                if (f.Probe("Registre A", "OUT") != 5) return f.FailProbe("A after load", "Registre A", "OUT", 5);
                // B <- 3
                f.Set("ENTREE", 3); f.Set("Load_B", 1); f.Tick(Settle); f.Cycle("clock"); f.Set("Load_B", 0);
                if (f.Probe("Registre B", "OUT") != 3) return f.FailProbe("B after load", "Registre B", "OUT", 3);
                // bus <- A + B
                f.Set("OE_entree", 0); f.Set("OE_ALu", 1); f.Tick(Settle);
                if (f.Probe("BUS-8", "BUS-8") != 8) return f.FailProbe("bus = A + B", "BUS-8", "BUS-8", 8);
                // MAR <- 9
                f.Set("OE_ALu", 0); f.Set("ENTREE", 9); f.Set("OE_entree", 1); f.Set("Load_MAR", 1); f.Tick(Settle); f.Cycle("clock"); f.Set("Load_MAR", 0);
                if (f.Probe("MAR", "OUT") != 9) return f.FailProbe("MAR after load", "MAR", "OUT", 9);
                // RAM[MAR] <- bus (A + B)
                f.Set("OE_entree", 0); f.Set("OE_ALu", 1); f.Tick(Settle); f.Set("We_RAM", 1); f.Tick(Settle); f.Set("We_RAM", 0); f.Set("OE_ALu", 0); f.Tick(Settle);
                if (!f.ProbeFloating("BUS-8", "BUS-8")) return "bus must float when nothing drives it";
                // bus <- RAM[MAR]
                f.Set("Oe_ram", 1); f.Tick(Settle);
                if (f.Probe("BUS-8", "BUS-8") != 8) return f.FailProbe("bus = RAM[9]", "BUS-8", "BUS-8", 8);
                // A <- RAM[9] (=8), then bus <- A - B = 5
                f.Set("Load_A", 1); f.Tick(Settle); f.Cycle("clock"); f.Set("Load_A", 0); f.Set("Oe_ram", 0);
                if (f.Probe("Registre A", "OUT") != 8) return f.FailProbe("A after loading from RAM", "Registre A", "OUT", 8);
                f.Set("OP0", 1); f.Set("OE_ALu", 1); f.Tick(Settle);
                if (f.Probe("BUS-8", "BUS-8") != 5) return f.FailProbe("bus = A - B", "BUS-8", "BUS-8", 5);
                if (f.Probe("ALU8", "Z") != 0 || f.Probe("ALU8", "N") != 0) return "ALU flags after 8 - 3";
                return null;
            });

            // ---------------- combinational, against a model ----------------

            Add("ALU8", "all 8 operations and flags on chosen operands", f =>
            {
                var pairs = new[] { (5, 3), (200, 100), (255, 1), (0, 0), (127, 1), (128, 1), (0, 1), (100, 200), (0x0F, 0xF0), (255, 255), (1, 255), (128, 128) };
                f.Set("OE", 1);
                foreach ((int a, int b) in pairs)
                    for (int op = 0; op < 8; op++)
                    {
                        f.Set("A", a); f.Set("B", b); f.Set("OP2", op >> 2 & 1); f.Set("OP1", op >> 1 & 1); f.Set("OP0", op & 1); f.Tick(4);
                        (int r, int c, int v) = Alu(a, b, op, 8);
                        int n = r >> 7 & 1, z = r == 0 ? 1 : 0;
                        string got = $"R={f.Value("R")} C={f.Value("C")} V={f.Value("V")} N={f.Value("N")} Z={f.Value("Z")}";
                        string exp = $"R={r} C={c} V={v} N={n} Z={z}";
                        if (got != exp) return $"A={a} B={b} OP={op} ({OpName(op)}): got {got}, expected {exp}";
                    }
                f.Set("OE", 0); f.Tick(4);
                if (!f.IsFloating("R")) return "R must float with OE=0";
                return null;
            });

            Add("ALU4", "all 8 operations exhaustively (unchained)", f =>
            {
                f.Set("CHAINED", 0); f.Set("Chain_in", 0);
                string[] aPins = { "A3", "A2", "A1", "A0" }, bPins = { "B3", "B2", "B1", "B0" };
                for (int a = 0; a < 16; a++)
                    for (int b = 0; b < 16; b++)
                        for (int op = 0; op < 8; op++)
                        {
                            f.SetWord(aPins, a); f.SetWord(bPins, b); f.Set("OP2", op >> 2 & 1); f.Set("OP1", op >> 1 & 1); f.Set("OP0", op & 1); f.Tick(2);
                            (int r, int c, int v) = Alu(a, b, op, 4);
                            int n = r >> 3 & 1, z = r == 0 ? 1 : 0;
                            string got = $"S={f.Word("S3", "S2", "S1", "S0")} C={f.Value("C")} V={f.Value("V")} N={f.Value("N")} Z={f.Value("Z")}";
                            string exp = $"S={r} C={c} V={v} N={n} Z={z}";
                            if (got != exp) return $"A={a} B={b} OP={op} ({OpName(op)}): got {got}, expected {exp}";
                        }
                return null;
            });

            Add("Add4", "4-bit adder exhaustive (sum, carry, overflow)", f =>
            {
                string[] aPins = { "A3", "A2", "A1", "A0" }, bPins = { "B3", "B2", "B1", "B0" };
                for (int a = 0; a < 16; a++)
                    for (int b = 0; b < 16; b++)
                        for (int cin = 0; cin < 2; cin++)
                        {
                            f.SetWord(aPins, a); f.SetWord(bPins, b); f.Set("C_in", cin); f.Tick(2);
                            int sum = a + b + cin, s = sum & 15, cout = sum >> 4, v = ((a ^ s) & (b ^ s) & 8) != 0 ? 1 : 0;
                            if (f.Word("S3", "S2", "S1", "S0") != s || f.Value("C_out") != cout || f.Value("V") != v)
                                return $"{a}+{b}+{cin}: S={f.Word("S3", "S2", "S1", "S0")} C_out={f.Value("C_out")} V={f.Value("V")}, expected S={s} C_out={cout} V={v}";
                        }
                return null;
            });

            Add("FullA", "full adder exhaustive", f =>
            {
                for (int i = 0; i < 8; i++)
                {
                    int a = i >> 2 & 1, b = i >> 1 & 1, cin = i & 1;
                    f.Set("A1", a); f.Set("A0", b); f.Set("C_in", cin); f.Tick(2);
                    int sum = a + b + cin;
                    if (f.Value("S") != (sum & 1) || f.Value("C_out") != sum >> 1) return $"{a}+{b}+{cin}: S={f.Value("S")} C_out={f.Value("C_out")}";
                }
                return null;
            });

            Add("HalfA", "half adder exhaustive", f =>
            {
                for (int i = 0; i < 4; i++)
                {
                    int a = i >> 1, b = i & 1;
                    f.SetIndex(0, a); f.SetIndex(1, b); f.Tick(2);
                    if (f.Value("S") != (a ^ b) || f.Value("C") != (a & b)) return $"{a}+{b}: S={f.Value("S")} C={f.Value("C")}";
                }
                return null;
            });

            foreach ((string chip, Func<int, int, int> fn) in new (string, Func<int, int, int>)[] { ("AND", (a, b) => a & b), ("OR", (a, b) => a | b), ("XOR", (a, b) => a ^ b) })
                Add(chip, "2-input gate exhaustive", f =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int a = i >> 1, b = i & 1;
                        f.SetIndex(0, a); f.SetIndex(1, b); f.Tick(2);
                        if (f.Value("OUT") != fn(a, b)) return $"{chip}({a},{b}) = {f.Value("OUT")}";
                    }
                    return null;
                });

            Add("NOT", "inverter", f =>
            {
                f.Set("IN", 0); f.Tick(2); if (f.Value("OUT") != 1) return "NOT(0) != 1";
                f.Set("IN", 1); f.Tick(2); if (f.Value("OUT") != 0) return "NOT(1) != 0";
                return null;
            });

            foreach ((string chip, Func<int, int, int> fn) in new (string, Func<int, int, int>)[] { ("And-4b", (a, b) => a & b), ("OR-4B", (a, b) => a | b), ("XOR-4B", (a, b) => a ^ b) })
                Add(chip, "4-bit bitwise exhaustive", f =>
                {
                    string[] aPins = { "A3", "A2", "A1", "A0" }, bPins = { "B3", "B2", "B1", "B0" };
                    for (int a = 0; a < 16; a++)
                        for (int b = 0; b < 16; b++)
                        {
                            f.SetWord(aPins, a); f.SetWord(bPins, b); f.Tick(2);
                            int got = f.Word("S3", "S2", "S1", "S0");
                            if (got != fn(a, b)) return $"{chip}({a},{b}) = {got}, expected {fn(a, b)}";
                        }
                    return null;
                });

            Add("Dec-2-4", "2-to-4 decoder with enable, exhaustive", f =>
            {
                for (int i = 0; i < 8; i++)
                {
                    int en = i >> 2 & 1, op = i & 3;
                    f.Set("EN", en); f.Set("OP1", op >> 1); f.Set("OP0", op & 1); f.Tick(2);
                    int got = f.Word("D3", "D2", "D1", "D0"), exp = en == 1 ? 1 << op : 0;
                    if (got != exp) return $"EN={en} OP={op}: D3..D0 = {Convert.ToString(got, 2).PadLeft(4, '0')}, expected {Convert.ToString(exp, 2).PadLeft(4, '0')}";
                }
                return null;
            });

            Add("Mux2-1", "exhaustive", f =>
            {
                for (int i = 0; i < 8; i++)
                {
                    int c = i >> 2 & 1, d1 = i >> 1 & 1, d0 = i & 1;
                    f.Set("C0", c); f.Set("D1", d1); f.Set("D0", d0); f.Tick(2);
                    int exp = c == 1 ? d1 : d0;
                    if (f.Value("Y") != exp) return $"C0={c} D1={d1} D0={d0}: Y={f.Value("Y")}, expected {exp}";
                }
                return null;
            });

            Add("Mux4-1", "exhaustive", f =>
            {
                string[] data = { "A3", "A2", "A1", "A0" };
                for (int d = 0; d < 16; d++)
                    for (int c = 0; c < 4; c++)
                    {
                        f.SetWord(data, d); f.Set("C1", c >> 1); f.Set("C0", c & 1); f.Tick(2);
                        int exp = d >> c & 1;
                        if (f.Value("OUT") != exp) return $"A3..A0={Convert.ToString(d, 2).PadLeft(4, '0')} C={c}: OUT={f.Value("OUT")}, expected {exp}";
                    }
                return null;
            });

            Add("MUX8-1", "exhaustive", f =>
            {
                string[] data = { "A7", "A6", "A5", "A4", "A3", "A2", "A1", "A0" };
                for (int d = 0; d < 256; d++)
                    for (int s = 0; s < 8; s++)
                    {
                        f.SetWord(data, d); f.Set("S2", s >> 2 & 1); f.Set("S1", s >> 1 & 1); f.Set("S0", s & 1); f.Tick(2);
                        int exp = d >> s & 1;
                        if (f.Value("D") != exp) return $"A={Convert.ToString(d, 2).PadLeft(8, '0')} S={s}: D={f.Value("D")}, expected {exp}";
                    }
                return null;
            });

            Add("4MUX4-1", "selects one of four 4-bit words (sampled)", f =>
            {
                string[][] words = { new[] { "A3", "A2", "A1", "A0" }, new[] { "B3", "B2", "B1", "B0" }, new[] { "C3", "C2", "C1", "C0" }, new[] { "D3", "D2", "D1", "D0" } };
                var rnd = new Random(4);
                for (int t = 0; t < 64; t++)
                {
                    int[] w = { rnd.Next(16), rnd.Next(16), rnd.Next(16), rnd.Next(16) };
                    for (int k = 0; k < 4; k++) f.SetWord(words[k], w[k]);
                    for (int sel = 0; sel < 4; sel++)
                    {
                        f.Set("SEL1", sel >> 1); f.Set("SEL0", sel & 1); f.Tick(2);
                        int got = f.Word("Y3", "Y2", "Y1", "Y0");
                        if (got != w[sel]) return $"A={w[0]} B={w[1]} C={w[2]} D={w[3]} SEL={sel}: Y={got}, expected {w[sel]}";
                    }
                }
                return null;
            });

            // generated gate packages: k-th output = gate of the k-th inputs
            foreach ((string chip, int gates, int fanIn, Func<int[], int> fn) in new (string, int, int, Func<int[], int>)[]
            {
                ("3xAND3", 3, 3, x => x[0] & x[1] & x[2]), ("3xNOR3", 3, 3, x => 1 ^ (x[0] | x[1] | x[2])),
                ("4xAND2", 4, 2, x => x[0] & x[1]), ("4xNAND2", 4, 2, x => 1 ^ (x[0] & x[1])), ("4xNOR2", 4, 2, x => 1 ^ (x[0] | x[1])),
                ("4xOR2", 4, 2, x => x[0] | x[1]), ("4xXOR2", 4, 2, x => x[0] ^ x[1]), ("6xNOT", 6, 1, x => 1 ^ x[0]),
            })
                Add(chip, "package: every gate exhaustive", f =>
                {
                    int combos = 1 << fanIn;
                    for (int c = 0; c < combos; c++)
                    {
                        // every gate of the package gets the same input pattern, then a different one per gate
                        for (int g = 0; g < gates; g++)
                            for (int i = 0; i < fanIn; i++) f.Set($"{g + 1}{(char)('A' + i)}", (c + g) >> i & 1);
                        f.Tick(2);
                        for (int g = 0; g < gates; g++)
                        {
                            int[] x = Enumerable.Range(0, fanIn).Select(i => (c + g) >> i & 1).ToArray();
                            int got = f.Value($"{g + 1}Y"), exp = fn(x);
                            if (got != exp) return $"gate {g + 1} inputs {string.Join("", x)}: {got}, expected {exp}";
                        }
                    }
                    return null;
                });

            return cases;
        }

        // ---------------- models ----------------

        // The user's ALU: 0 ADD, 1 SUB, 2 INC, 3 DEC, 4 A, 5 AND, 6 OR, 7 XOR. C = carry out of the adder
        // (for SUB/DEC: no borrow), V = signed overflow of the adder, 0 for the logic ops.
        static (int r, int c, int v) Alu(int a, int b, int op, int width)
        {
            int mask = (1 << width) - 1, sign = 1 << (width - 1);
            int Add(int x, int y, int cin)
            {
                int sum = x + y + cin;
                return sum;
            }
            int x = a, y, cin;
            switch (op)
            {
                case 0: y = b; cin = 0; break;
                case 1: y = ~b & mask; cin = 1; break;
                case 2: y = 0; cin = 1; break;
                case 3: y = mask; cin = 0; break;
                case 4: return (a, 0, 0);
                case 5: return (a & b, 0, 0);
                case 6: return (a | b, 0, 0);
                default: return (a ^ b, 0, 0);
            }
            int full = Add(x, y, cin), r = full & mask;
            int c = full >> width & 1;
            int v = ((x ^ r) & (y ^ r) & sign) != 0 ? 1 : 0;
            return (r, c, v);
        }

        static string OpName(int op) => new[] { "ADD", "SUB", "INC", "DEC", "A", "AND", "OR", "XOR" }[op];

        // ---------------- harness ----------------

        public class Fixture : IDisposable
        {
            static readonly object buildLock = new();
            readonly ChipDescription desc;
            readonly SimChip root, target;
            readonly SimAudio audio = new();
            readonly Dictionary<string, int> inIdx = new(), outIdx = new();
            readonly Dictionary<string, (SimChip chip, ChipDescription desc)> probes = new();

            public Fixture(ChipDescription desc, ChipLibrary lib)
            {
                this.desc = desc;
                Simulator.ResetForTests(GoldenRunner.StableHash(desc.Name) + 7);
                Simulator.stepsPerClockTransition = 250;
                lock (buildLock) root = CircuitTester.BuildIsolatedSim(desc, lib);
                target = CircuitTester.TargetOf(root);
                for (int i = 0; i < desc.InputPins.Length; i++) inIdx.TryAdd(desc.InputPins[i].Name, i);
                for (int i = 0; i < desc.OutputPins.Length; i++) outIdx.TryAdd(desc.OutputPins[i].Name, i);
                foreach (SubChipDescription sd in desc.SubChips)
                {
                    if (!lib.TryGetChipDescription(sd.Name, out ChipDescription d)) continue;
                    (bool ok, SimChip sc) = target.TryGetSubChipFromID(sd.ID);
                    if (!ok) continue;
                    if (!string.IsNullOrEmpty(sd.Label)) probes[sd.Label] = (sc, d);
                    probes.TryAdd(sd.Name, (sc, d)); // by type name: the first instance
                }
            }

            public void Dispose() { Simulator.forcedClockState = -1; Simulator.ClearTestSeed(); }

            public void Set(string input, int value)
            {
                if (!inIdx.TryGetValue(input, out int i)) throw new Exception($"no input pin '{input}' on {desc.Name}");
                PinState.Set(ref root.InputPins[i].State, (ushort)value, 0);
            }
            public void SetIndex(int i, int value) => PinState.Set(ref root.InputPins[i].State, (ushort)value, 0);
            public void SetWord(string[] pinsMsbFirst, int value)
            {
                if (pinsMsbFirst.Length == 1) { Set(pinsMsbFirst[0], value); return; }
                for (int i = 0; i < pinsMsbFirst.Length; i++) Set(pinsMsbFirst[i], value >> (pinsMsbFirst.Length - 1 - i) & 1);
            }
            public void Tick(int n) { for (int i = 0; i < n; i++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio); }
            public void Cycle(string clockPin) { Set(clockPin, 1); Tick(Settle); Set(clockPin, 0); Tick(Settle); }

            uint OutState(string output)
            {
                if (!outIdx.TryGetValue(output, out int i)) throw new Exception($"no output pin '{output}' on {desc.Name}");
                return target.OutputPins[i].State;
            }
            int Width(string output) => (int)desc.OutputPins[outIdx[output]].BitCount;
            // -1 when any bit of the pin floats
            public int Value(string output) => Decode(OutState(output), Width(output));
            public bool IsFloating(string output) => (PinState.GetTristateFlags(OutState(output)) & ((1 << Width(output)) - 1)) != 0;
            public int Word(params string[] pinsMsbFirst)
            {
                int w = 0;
                foreach (string p in pinsMsbFirst) { int v = Value(p); if (v < 0) return -1; w = w << 1 | v; }
                return w;
            }

            (SimPin pin, int width) ProbePin(string chipLabelOrName, string pinName)
            {
                if (!probes.TryGetValue(chipLabelOrName, out var p)) throw new Exception($"no sub-chip '{chipLabelOrName}' in {desc.Name}");
                int j = Array.FindIndex(p.desc.OutputPins, x => x.Name == pinName);
                if (j < 0) throw new Exception($"no output pin '{pinName}' on {chipLabelOrName}");
                return (p.chip.OutputPins[j], (int)p.desc.OutputPins[j].BitCount);
            }
            public int Probe(string chipLabelOrName, string pinName) { (SimPin pin, int w) = ProbePin(chipLabelOrName, pinName); return Decode(pin.State, w); }
            public bool ProbeFloating(string chipLabelOrName, string pinName) { (SimPin pin, int w) = ProbePin(chipLabelOrName, pinName); return (PinState.GetTristateFlags(pin.State) & ((1 << w) - 1)) != 0; }

            static int Decode(uint state, int width)
            {
                int mask = (1 << width) - 1;
                if ((PinState.GetTristateFlags(state) & mask) != 0) return -1;
                return PinState.GetBitStates(state) & mask;
            }

            public string Fail(string what, string pin, object expected)
            {
                string got = pin.Contains('/') ? "" : (IsFloating(pin) ? "Z" : Value(pin).ToString());
                return $"{what}: {pin} = {got}, expected {expected ?? "Z"}";
            }
            public string FailProbe(string what, string chip, string pin, int expected) =>
                $"{what}: {chip}.{pin} = {(ProbeFloating(chip, pin) ? "Z" : Probe(chip, pin).ToString())}, expected {expected}";
        }
    }
}
