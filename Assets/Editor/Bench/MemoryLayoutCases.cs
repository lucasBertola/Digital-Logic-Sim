using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // The memory editor (MemoryLayout): rules grouping cells into words — the kind Claude returns — are verified by
    // simulation, rejected when wrong, and used to write words that the circuit then really uses. The rules below
    // are hand-written reference rules for the user's chips (four kinds of memory: gated-latch words in an
    // addressed RAM, master/slave-flip-flop registers, a counter, the builtin ROM).
    public static class MemoryLayoutCases
    {
        public const string CpuRulesJson = @"{
  ""banks"": [
    { ""name"": ""Registre A"", ""path"": [""Registre A""] },
    { ""name"": ""Registre B"", ""path"": [""Registre B""] },
    { ""name"": ""MAR"", ""path"": [""MAR""] },
    { ""name"": ""PC"", ""path"": [""PC""] },
    { ""name"": ""RAM"", ""path"": [""RAM256""] }
  ],
  ""types"": [
    { ""type"": ""Registre8"", ""kind"": ""word"", ""children"": [""BD0"",""BD1"",""BD2"",""BD3"",""BD4"",""BD5"",""BD6"",""BD7""] },
    { ""type"": ""PC"", ""kind"": ""word"", ""children"": [""Bascule0"",""Bascule1"",""Bascule2"",""Bascule3"",""Bascule4"",""Bascule5"",""Bascule6"",""Bascule7""] },
    { ""type"": ""RAM256"", ""kind"": ""array"", ""children"": [""RAM64#4"",""RAM64#3"",""RAM64#2"",""RAM64#1""] },
    { ""type"": ""RAM64"", ""kind"": ""array"", ""children"": [""RAM16#4"",""RAM16#3"",""RAM16#2"",""RAM16#1""] },
    { ""type"": ""RAM16"", ""kind"": ""array"", ""children"": [""RAM4#4"",""RAM4#3"",""RAM4#2"",""RAM4#1""] },
    { ""type"": ""RAM4"", ""kind"": ""array"", ""children"": [""MOT8#4"",""MOT8#3"",""MOT8#2"",""MOT8#1""] },
    { ""type"": ""MOT8"", ""kind"": ""word"", ""children"": [""VerrouD 2#8"",""VerrouD 2#7"",""VerrouD 2#6"",""VerrouD 2#5"",""VerrouD 2#4"",""VerrouD 2#3"",""VerrouD 2#2"",""VerrouD 2#1""] }
  ],
  ""reads"": [
    { ""type"": ""Registre8"", ""inputs"": [ { ""pin"": ""OE"", ""value"": ""1"" } ], ""output"": [""OUT""] },
    { ""type"": ""PC"", ""inputs"": [], ""output"": [""OUT""] },
    { ""type"": ""RAM256"", ""inputs"": [ { ""pin"": ""Adresses"", ""value"": ""a"" }, { ""pin"": ""Oe"", ""value"": ""1"" }, { ""pin"": ""Cs"", ""value"": ""1"" } ], ""output"": [""D_out""] },
    { ""type"": ""RAM64"", ""inputs"": [ { ""pin"": ""Adresse5"", ""value"": ""(a>>5)&1"" }, { ""pin"": ""Adresse4"", ""value"": ""(a>>4)&1"" }, { ""pin"": ""Adress3"", ""value"": ""(a>>3)&1"" }, { ""pin"": ""Adress2"", ""value"": ""(a>>2)&1"" }, { ""pin"": ""Adress1"", ""value"": ""(a>>1)&1"" }, { ""pin"": ""Adress0"", ""value"": ""a&1"" }, { ""pin"": ""Oe"", ""value"": ""1"" }, { ""pin"": ""Cs"", ""value"": ""1"" } ], ""output"": [""D_out""] },
    { ""type"": ""RAM16"", ""inputs"": [ { ""pin"": ""Adress3"", ""value"": ""(a>>3)&1"" }, { ""pin"": ""Adress2"", ""value"": ""(a>>2)&1"" }, { ""pin"": ""Adress1"", ""value"": ""(a>>1)&1"" }, { ""pin"": ""Adress0"", ""value"": ""a&1"" }, { ""pin"": ""Oe"", ""value"": ""1"" }, { ""pin"": ""Cs"", ""value"": ""1"" } ], ""output"": [""D_out""] },
    { ""type"": ""RAM4"", ""inputs"": [ { ""pin"": ""Adress1"", ""value"": ""(a>>1)&1"" }, { ""pin"": ""Adress0"", ""value"": ""a&1"" }, { ""pin"": ""OE"", ""value"": ""1"" }, { ""pin"": ""Cs"", ""value"": ""1"" } ], ""output"": [""D_out""] },
    { ""type"": ""MOT8"", ""inputs"": [ { ""pin"": ""R"", ""value"": ""1"" } ], ""output"": [""OUT""] }
  ]
}";

        // Two real answers from Claude for the CPU (2026-09-28): the first had the register bits reversed and must be
        // rejected by the check; the second (after the rejection was fed back) is correct and must pass.
        public const string ClaudeCpuWrong = @"{""banks"":[{""name"":""Registre A"",""path"":[""Registre8#1""]},{""name"":""Registre B"",""path"":[""Registre8#2""]},{""name"":""MAR"",""path"":[""Registre8#3""]},{""name"":""PC"",""path"":[""PC""]},{""name"":""RAM256"",""path"":[""RAM256""]}],""types"":[{""type"":""Registre8"",""kind"":""word"",""children"":[""Bascule D#1"",""Bascule D#2"",""Bascule D#3"",""Bascule D#4"",""Bascule D#5"",""Bascule D#6"",""Bascule D#7"",""Bascule D#8""]},{""type"":""PC"",""kind"":""word"",""children"":[""Bascule D#5"",""Bascule D#4"",""Bascule D#3"",""Bascule D#2"",""Bascule D#6"",""Bascule D#7"",""Bascule D#8"",""Bascule D#1""]},{""type"":""RAM256"",""kind"":""array"",""children"":[""RAM64#4"",""RAM64#3"",""RAM64#2"",""RAM64#1""]},{""type"":""RAM64"",""kind"":""array"",""children"":[""RAM16#4"",""RAM16#3"",""RAM16#2"",""RAM16#1""]},{""type"":""RAM16"",""kind"":""array"",""children"":[""RAM4#4"",""RAM4#3"",""RAM4#2"",""RAM4#1""]},{""type"":""RAM4"",""kind"":""array"",""children"":[""MOT8#4"",""MOT8#3"",""MOT8#2"",""MOT8#1""]},{""type"":""MOT8"",""kind"":""word"",""children"":[""VerrouD 2#1"",""VerrouD 2#2"",""VerrouD 2#3"",""VerrouD 2#4"",""VerrouD 2#5"",""VerrouD 2#6"",""VerrouD 2#7"",""VerrouD 2#8""]}],""reads"":[{""type"":""Registre8"",""inputs"":[{""pin"":""OE"",""value"":""1""}],""output"":[""OUT""]},{""type"":""PC"",""inputs"":[],""output"":[""OUT""]},{""type"":""RAM256"",""inputs"":[{""pin"":""Adresses"",""value"":""((a>>7)&1)|(((a>>6)&1)<<1)|(((a>>5)&1)<<2)|(((a>>4)&1)<<3)|(((a>>3)&1)<<4)|(((a>>2)&1)<<5)|(((a>>1)&1)<<6)|((a&1)<<7)""},{""pin"":""Oe"",""value"":""1""},{""pin"":""Cs"",""value"":""1""}],""output"":[""D_out""]}]}";
        public const string ClaudeCpuRight = @"{""banks"":[{""name"":""Registre A"",""path"":[""Registre8#1""]},{""name"":""Registre B"",""path"":[""Registre8#2""]},{""name"":""MAR"",""path"":[""Registre8#3""]},{""name"":""PC"",""path"":[""PC""]},{""name"":""RAM256"",""path"":[""RAM256""]}],""types"":[{""type"":""Registre8"",""kind"":""word"",""children"":[""Bascule D#8"",""Bascule D#7"",""Bascule D#6"",""Bascule D#5"",""Bascule D#4"",""Bascule D#3"",""Bascule D#2"",""Bascule D#1""]},{""type"":""PC"",""kind"":""word"",""children"":[""Bascule D#1"",""Bascule D#8"",""Bascule D#7"",""Bascule D#6"",""Bascule D#2"",""Bascule D#3"",""Bascule D#4"",""Bascule D#5""]},{""type"":""RAM256"",""kind"":""array"",""children"":[""RAM64#4"",""RAM64#3"",""RAM64#2"",""RAM64#1""]},{""type"":""RAM64"",""kind"":""array"",""children"":[""RAM16#4"",""RAM16#3"",""RAM16#2"",""RAM16#1""]},{""type"":""RAM16"",""kind"":""array"",""children"":[""RAM4#4"",""RAM4#3"",""RAM4#2"",""RAM4#1""]},{""type"":""RAM4"",""kind"":""array"",""children"":[""MOT8#4"",""MOT8#3"",""MOT8#2"",""MOT8#1""]},{""type"":""MOT8"",""kind"":""word"",""children"":[""VerrouD 2#8"",""VerrouD 2#7"",""VerrouD 2#6"",""VerrouD 2#5"",""VerrouD 2#4"",""VerrouD 2#3"",""VerrouD 2#2"",""VerrouD 2#1""]}],""reads"":[{""type"":""Registre8"",""inputs"":[{""pin"":""OE"",""value"":""1""}],""output"":[""OUT""]},{""type"":""PC"",""inputs"":[],""output"":[""OUT""]},{""type"":""RAM256"",""inputs"":[{""pin"":""Adresses"",""value"":""a""},{""pin"":""Oe"",""value"":""1""},{""pin"":""Cs"",""value"":""1""}],""output"":[""D_out""]}]}";

        public static List<(string name, Func<string> run)> All(ChipLibrary lib, ChipDescription[] chips)
        {
            var cases = new List<(string, Func<string>)>
            {
                ("memory editor: expression evaluator", ExprCases),
                ("memory editor: rules parsed from Claude's JSON answer", () => Parse() == null ? "rules not parsed" : null),
                ("memory editor: a chip without memory has no cell", () => NoMemory(lib, chips)),
                ("memory editor: structure hash ignores positions, sees wiring", () => StructureHash(lib, chips)),
                ("memory editor: cache entry JSON round trip", CacheRoundTrip),
                ("memory editor: no reflection-based JSON in the memory editor (the player build strips it)", NoReflectionJson),
            };
            foreach (string type in new[] { "MOT8", "RAM4", "RAM16", "RAM64", "RAM256", "Registre8", "PC" })
            {
                string t = type;
                cases.Add(($"[PC] memory editor: correct rules for {t} are verified", () => Verify(lib, t, Parse())));
            }
            // Behaviour change 2026-09-28: a wrong BIT ORDER is no longer rejected but corrected by simulation (Claude
            // reversed the register bits on its first try every time); the corrected rule must be exactly the right one.
            cases.Add(("[PC] memory editor: register bits reversed -> corrected by simulation", () => MustCorrect(lib, "Registre8", r => Rule(r, "Registre8").children = Rule(r, "Registre8").children.Reverse().ToArray())));
            cases.Add(("[PC] memory editor: counter bits scrambled -> corrected by simulation", () => MustCorrect(lib, "PC", r => { var c = Rule(r, "PC").children; Rule(r, "PC").children = new[] { c[3], c[0], c[7], c[1], c[6], c[2], c[5], c[4] }; })));
            cases.Add(("[PC] memory editor: RAM word bits reversed (deep inside the RAM) -> corrected by simulation", () => MustCorrect(lib, "RAM4", r => Rule(r, "MOT8").children = Rule(r, "MOT8").children.Reverse().ToArray())));
            cases.Add(("[PC] memory editor: a known type is reused for another chip of that type without Claude", () => Reuse(lib, chips)));
            cases.Add(("memory editor: long messages are wrapped", WrapCase));
            cases.Add(("memory editor: a field only accepts values of the word's width", FitsWordCase));
            cases.Add(("memory editor: changing the base shows the typed value in the new base", ConvertCase));
            cases.Add(("[PC] memory editor: words follow the circuit live (clock edge), edited words are kept", () => FollowCase(lib, chips)));
            cases.Add(("[PC] memory editor: RAM blocks in the wrong order -> rejected", () => MustReject(lib, "RAM4", r => Rule(r, "RAM4").children = new[] { "MOT8#1", "MOT8#2", "MOT8#3", "MOT8#4" })));
            cases.Add(("[PC] memory editor: wrong read pin -> rejected", () => MustReject(lib, "Registre8", r => r.reads.First(x => x.type == "Registre8").output = new[] { "NOPE" })));
            cases.Add(("[PC] memory editor: read procedure that does not select the word -> rejected", () => MustReject(lib, "RAM4", r => r.reads.First(x => x.type == "RAM4").inputs.RemoveAll(p => p.pin == "Adress1"))));
            cases.Add(("[PC] memory editor: missing read procedure -> rejected", () => MustReject(lib, "PC", r => r.reads.RemoveAll(x => x.type == "PC"))));
            cases.Add(("[PC] memory editor: unknown component / kind / cell-less bit -> rejected", () => BadRules(lib)));
            cases.Add(("[PC] memory editor: CPU banks found (4 latch kinds + builtin ROM) and edited words are used by the circuit", () => CpuBanks(lib, chips)));
            cases.Add(("[PC] memory editor: Claude's real wrong answer is rejected, its corrected answer verifies", () => RealAnswers(lib, chips)));
            cases.Add(("[PC] memory editor: edited words survive save + reload", () => EditThenReload(lib, chips)));
            return cases;
        }

        static MemoryRules Parse() => MemoryLayout.ParseRules(CpuRulesJson, out _);
        static MemoryTypeRule Rule(MemoryRules r, string type) => r.types.First(t => t.type == type);

        static string ExprCases()
        {
            (string e, long a, long v)[] table =
            {
                ("a", 5, 5), ("1", 0, 1), ("0x1F", 0, 31), ("0b101", 0, 5), ("(a>>1)&1", 2, 1), ("(a>>1)&1", 1, 0),
                ("a & 1", 3, 1), ("a*4+2", 3, 14), ("~a & 0xFF", 0, 255), ("a<<2 | 1", 3, 13), ("a ^ 0xF", 5, 10), ("-a+10", 3, 7), ("a % 4", 7, 3), ("a/2", 9, 4),
            };
            foreach (var t in table)
            {
                if (!Expr.TryEval(t.e, t.a, out long v, out string err)) return $"\"{t.e}\" failed: {err}";
                if (v != t.v) return $"\"{t.e}\" with a={t.a} = {v}, expected {t.v}";
            }
            foreach (string bad in new[] { "", "a+", "(a", "b", "1/0", "0x" })
                if (Expr.TryEval(bad, 1, out _, out _)) return $"\"{bad}\" should be rejected";
            return null;
        }

        static string NoMemory(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription alu = chips.First(c => c.Name == "ALU8");
            SimChip target = CircuitTester.TargetOf(CircuitTester.BuildIsolatedSim(alu, lib));
            if (MemoryLayout.FindCells(target).Count != 0) return "cells found in a combinational ALU";
            ChipDescription reg = chips.First(c => c.Name == "Registre8");
            SimChip r = CircuitTester.TargetOf(CircuitTester.BuildIsolatedSim(reg, lib));
            int n = MemoryLayout.FindCells(r).Count;
            return n == 16 ? null : $"Registre8 should hold 16 cells (8 master/slave flip-flops), found {n}";
        }

        static string StructureHash(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription d = chips.First(c => c.Name == "MOT8");
            string h = MemoryLayout.StructureHash(d, lib);
            ChipDescription moved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(d));
            moved.SubChips[0].Position += new UnityEngine.Vector2(3, 3);
            moved.MemoryState = new ChipMemoryState { Hash = "x", Gates = "00" };
            if (MemoryLayout.StructureHash(moved, lib) != h) return "moving a component / a memory state changed the structure hash";
            ChipDescription rewired = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(d));
            rewired.Wires = rewired.Wires.Skip(1).ToArray();
            if (MemoryLayout.StructureHash(rewired, lib) == h) return "removing a wire did not change the structure hash";
            return null;
        }

        static string CacheRoundTrip()
        {
            var e = new MemoryLayout.CacheEntry { hash = "h", rules = Parse() };
            e.polarity["Bascule D"] = new[] { 1, 0 };
            // the code path the app uses (hand-written JSON: the player build strips reflection constructors)
            string json = MemoryLayout.CacheToJson(new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = e });
            var back = MemoryLayout.CacheFromJson(json);
            MemoryLayout.CacheEntry b = back["CPU"];
            if (b.hash != "h" || b.rules.types.Count != e.rules.types.Count || b.rules.banks.Count != 5) return "cache entry changed by the round trip";
            if (!b.polarity.TryGetValue("Bascule D", out int[] p) || p.Length != 2 || p[0] != 1) return "polarity lost by the round trip";
            return null;
        }

        // The player build strips the constructors Newtonsoft's reflection needs; the editor does not, so a
        // JsonConvert.DeserializeObject on our own classes passes every test here and fails in the app (it did).
        static string NoReflectionJson()
        {
            string dir = System.IO.Path.Combine(BenchProject.RepoRoot, "Assets", "Scripts", "Game", "Project");
            foreach (string f in new[] { "MemoryLayout.cs", "MemoryLayoutClaude.cs" })
            {
                string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, f));
                if (src.Contains("JsonConvert.DeserializeObject") || src.Contains("JsonConvert.SerializeObject")) return f + " uses reflection-based JSON";
            }
            return null;
        }

        static string Verify(ChipLibrary lib, string type, MemoryRules rules)
        {
            var pol = new Dictionary<string, int[]>();
            string err = MemoryLayout.Verify(type, rules, lib, pol);
            return err;
        }

        static string MustCorrect(ChipLibrary lib, string type, Action<MemoryRules> spoil)
        {
            MemoryRules good = Parse(), r = Parse();
            spoil(r);
            string err = MemoryLayout.Verify(type, r, lib, new Dictionary<string, int[]>());
            if (err != null) return "not corrected: " + err;
            foreach (MemoryTypeRule t in good.types)
            {
                MemoryTypeRule c = r.types.First(x => x.type == t.type);
                if (!c.children.SequenceEqual(t.children)) return $"{t.type} corrected to [{string.Join(", ", c.children)}], expected [{string.Join(", ", t.children)}]";
            }
            return null;
        }

        static string Reuse(ChipLibrary lib, ChipDescription[] chips)
        {
            // the CPU was analysed: its cache entry knows Registre8 -> a Registre8 alone needs no new analysis
            var cache = new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = new MemoryLayout.CacheEntry { hash = "h", rules = Parse() } };
            ChipDescription reg = chips.First(c => c.Name == "Registre8");
            MemoryRules r = MemoryLayout.RulesFromCache(reg, cache, lib, out var pol);
            if (r == null) return "Registre8 not rebuilt from the CPU's cached rules";
            string err = MemoryLayout.Verify("Registre8", r, lib, pol);
            if (err != null) return "rules rebuilt from the cache fail: " + err;
            ChipDescription ram = chips.First(c => c.Name == "RAM256");
            MemoryRules rr = MemoryLayout.RulesFromCache(ram, cache, lib, out var pol2);
            if (rr == null || MemoryLayout.Verify("RAM256", rr, lib, pol2) != null) return "RAM256 (types nested 5 levels) not reusable from the cache";
            ChipDescription alu = chips.First(c => c.Name == "ALU8");
            if (MemoryLayout.RulesFromCache(alu, cache, lib, out _) != null) return "a type never analysed was 'found' in the cache";
            return null;
        }

        static string WrapCase()
        {
            string w = DLS.Graphics.MemoryEditMenu.Wrap(new string('x', 10) + " " + string.Join(" ", Enumerable.Repeat("word", 60)), 40);
            return w.Split('\n').All(l => l.Length <= 40) && w.Contains('\n') ? null : "a line is longer than the width";
        }

        static string FitsWordCase()
        {
            var ok = new (string s, int bits, int mode)[] { ("FF", 8, 0), ("0", 8, 0), ("", 8, 0), ("255", 8, 1), ("11111111", 8, 2), ("F", 4, 0), ("7", 3, 0), ("FFFF", 16, 0), ("65535", 16, 1), ("1", 1, 2), ("a5", 8, 0) };
            var bad = new (string s, int bits, int mode)[] { ("100", 8, 0), ("1FF", 8, 0), ("000", 8, 0), ("256", 8, 1), ("0255", 8, 1), ("111111111", 8, 2), ("10", 4, 0), ("8", 3, 0), ("10000", 16, 0), ("65536", 16, 1), ("2", 1, 2), ("G", 8, 0), ("12", 8, 2), ("A", 8, 1) };
            foreach (var c in ok) if (!DLS.Graphics.MemoryEditMenu.FitsWord(c.s, c.bits, c.mode)) return $"'{c.s}' refused for {c.bits} bits, mode {c.mode}";
            foreach (var c in bad) if (DLS.Graphics.MemoryEditMenu.FitsWord(c.s, c.bits, c.mode)) return $"'{c.s}' accepted for {c.bits} bits, mode {c.mode}";
            return null;
        }

        // Serial (main thread: the input field states are a shared dictionary). The real menu code paths:
        // - decimal 3 typed, then binary -> the field shows 00000011 (the converted text used to be overwritten by the
        //   old field text, the user saw no change)
        // - a chip opened after another with as many words shows ITS values (it showed the previous chip's)
        public static string MenuFieldsCase(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription reg = chips.First(c => c.Name == "Registre8");
            var cache = new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = new MemoryLayout.CacheEntry { hash = "h", rules = Parse() } };
            MemoryRules rules = MemoryLayout.RulesFromCache(reg, cache, lib, out var pol);
            string e = MemoryLayout.Verify("Registre8", rules, lib, pol);
            if (e != null) return e;
            List<MemoryBank> Load(DirectedCases.Fixture f, int value)
            {
                f.Set("IN", value); f.Set("LOAD", 1); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 0); f.Tick(6);
                f.Cycle("Clock");
                return MemoryLayout.Banks(f.Target, reg, rules, pol, lib, out _);
            }
            using (var fa = new DirectedCases.Fixture(reg, lib))
            {

                DLS.Graphics.MemoryEditMenu.OpenForTests(Load(fa, 0x5A));
                DLS.Graphics.MemoryEditMenu.SetModeForTests(0);
                if (DLS.Graphics.MemoryEditMenu.FieldText(0) != "5A") return $"register A opened shows {DLS.Graphics.MemoryEditMenu.FieldText(0)}";
                DLS.Graphics.MemoryEditMenu.SetModeForTests(1);
                if (DLS.Graphics.MemoryEditMenu.FieldText(0) != "90") return $"hex 5A shown in decimal as {DLS.Graphics.MemoryEditMenu.FieldText(0)}";
                DLS.Graphics.MemoryEditMenu.TypeForTests(0, "3");
                DLS.Graphics.MemoryEditMenu.SetModeForTests(2);
                if (DLS.Graphics.MemoryEditMenu.FieldText(0) != "00000011") return $"decimal 3 then binary shows {DLS.Graphics.MemoryEditMenu.FieldText(0)}";
                DLS.Graphics.MemoryEditMenu.SetModeForTests(0);
                if (DLS.Graphics.MemoryEditMenu.FieldText(0) != "03") return $"then hex shows {DLS.Graphics.MemoryEditMenu.FieldText(0)}";
            }
            using (var fb = new DirectedCases.Fixture(reg, lib))
            {
                DLS.Graphics.MemoryEditMenu.OpenForTests(Load(fb, 0x21));
                string t = DLS.Graphics.MemoryEditMenu.FieldText(0);
                DLS.Graphics.MemoryEditMenu.Reset();
                return t == "21" ? null : $"register B (21) opened after register A shows {t}";
            }
        }

        static string ConvertCase()
        {
            var cases = new (string text, int bits, int from, int to, string expect)[]
            {
                ("3", 8, 1, 2, "00000011"), ("3", 8, 1, 0, "03"), ("00000011", 8, 2, 1, "3"), ("FF", 8, 0, 1, "255"),
                ("255", 8, 1, 2, "11111111"), ("A", 4, 0, 2, "1010"), ("1234", 16, 1, 0, "04D2"), ("", 8, 1, 2, "00000000"),
            };
            foreach (var c in cases)
            {
                string got = DLS.Graphics.MemoryEditMenu.ConvertText(c.text, c.bits, c.from, c.to);
                if (got != c.expect) return $"'{c.text}' ({c.bits} bits) mode {c.from} -> {c.to} gave '{got}', expected '{c.expect}'";
            }
            return null;
        }

        static string FollowCase(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription reg = chips.First(c => c.Name == "Registre8");
            var cache = new Dictionary<string, MemoryLayout.CacheEntry> { ["CPU"] = new MemoryLayout.CacheEntry { hash = "h", rules = Parse() } };
            MemoryRules rules = MemoryLayout.RulesFromCache(reg, cache, lib, out var pol);
            string e = MemoryLayout.Verify("Registre8", rules, lib, pol);
            if (e != null) return e;
            using var f = new DirectedCases.Fixture(reg, lib);
            f.Set("IN", 0x5A); f.Set("LOAD", 1); f.Set("OE", 1); f.Set("Clock", 0); f.Set("Reset", 0); f.Tick(6);
            f.Cycle("Clock");
            List<MemoryBank> banks = MemoryLayout.Banks(f.Target, reg, rules, pol, lib, out string err);
            if (banks == null) return err;
            var original = new[] { new[] { banks[0].Read(0) } };
            var texts = new[] { new[] { "5A" } };
            if (original[0][0] != 0x5A) return $"register reads {original[0][0]:X}, expected 5A";
            if (DLS.Graphics.MemoryEditMenu.Follow(banks, original, texts, 0).Count != 0) return "a word changed with no clock edge";
            f.Set("IN", 0x11); f.Tick(6); f.Cycle("Clock");
            var ch = DLS.Graphics.MemoryEditMenu.Follow(banks, original, texts, 0);
            if (ch.Count != 1 || texts[0][0] != "11") return $"after a clock edge loading 11 the field says {texts[0][0]}";
            texts[0][0] = "33"; // the user types a value, then an edge loads 22
            f.Set("IN", 0x22); f.Tick(6); f.Cycle("Clock");
            DLS.Graphics.MemoryEditMenu.Follow(banks, original, texts, 0);
            if (texts[0][0] != "33") return "the user's edit was overwritten by the circuit";
            if (original[0][0] != 0x22) return "the circuit's value is not tracked under an edited word";
            texts[0][0] = "00100010"; // (fields in binary now) back to the circuit's value: the word follows again
            f.Set("IN", 0x44); f.Tick(6); f.Cycle("Clock");
            DLS.Graphics.MemoryEditMenu.Follow(banks, original, texts, 2);
            return texts[0][0] == "01000100" ? null : $"binary follow gave {texts[0][0]}";
        }

        static string MustReject(ChipLibrary lib, string type, Action<MemoryRules> spoil)
        {
            MemoryRules r = Parse();
            spoil(r);
            string err = MemoryLayout.Verify(type, r, lib, new Dictionary<string, int[]>());
            return err != null ? null : $"wrong rules for {type} were accepted";
        }

        static string BadRules(ChipLibrary lib)
        {
            var spoils = new (string type, Action<MemoryRules> spoil, string what)[]
            {
                ("MOT8", r => Rule(r, "MOT8").children[3] = "VerrouD 2#99", "unknown component"),
                ("MOT8", r => Rule(r, "MOT8").kind = "table", "unknown kind"),
                ("Registre8", r => Rule(r, "Registre8").children[0] = "Mux70", "bit holder without cell"),
                ("RAM4", r => r.types.RemoveAll(t => t.type == "MOT8"), "missing child rule"),
                ("RAM4", r => Rule(r, "RAM4").children = new[] { "MOT8#1", "MOT8#2" }, "half the words"),
            };
            foreach (var s in spoils)
            {
                MemoryRules r = Parse();
                s.spoil(r);
                if (MemoryLayout.Verify(s.type, r, lib, new Dictionary<string, int[]>()) == null) return $"{s.what}: accepted";
            }
            return null;
        }

        // Verify every bank type of the CPU, then resolve its banks on a running copy
        static (DirectedCases.Fixture f, List<MemoryBank> banks, string error) CpuWithBanks(ChipLibrary lib, ChipDescription cpu)
        {
            MemoryRules rules = Parse();
            var pol = new Dictionary<string, int[]>();
            foreach (string t in MemoryLayout.BankTypes(cpu, rules, lib))
            {
                string e = MemoryLayout.Verify(t, rules, lib, pol);
                if (e != null) return (null, null, "verify " + t + ": " + e);
            }
            var f = new DirectedCases.Fixture(cpu, lib);
            foreach (string s in new[] { "OE_ALu", "OE_entree", "We_RAM", "Oe_ram", "Load_A", "Load_B", "Load_MAR", "OP2", "OP1", "OP0", "Reset_all", "ENTREE" }) f.Set(s, 0);
            f.ClockLevel(0);
            f.Tick(6);
            List<MemoryBank> banks = MemoryLayout.Banks(f.Target, cpu, rules, pol, lib, out string err);
            if (banks == null) { f.Dispose(); return (null, null, err); }
            return (f, banks, null);
        }

        static string CpuBanks(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            (DirectedCases.Fixture f, List<MemoryBank> banks, string error) = CpuWithBanks(lib, cpu);
            if (error != null) return error;
            try
            {
                string Describe() => string.Join(", ", banks.Select(b => $"{b.Name} {b.WordCount}x{b.Bits}"));
                MemoryBank Bank(string name) => banks.FirstOrDefault(b => b.Name == name);
                foreach ((string name, int words, int bits) in new[] { ("Registre A", 1, 8), ("Registre B", 1, 8), ("MAR", 1, 8), ("PC", 1, 8), ("RAM", 256, 8) })
                {
                    MemoryBank b = Bank(name);
                    if (b == null || b.WordCount != words || b.Bits != bits) return $"bank {name} ({words}x{bits}) not found: {Describe()}";
                }
                MemoryBank rom = banks.FirstOrDefault(b => b.Builtin != null && b.Builtin.ChipType == ChipType.Rom_256x16);
                if (rom == null || rom.WordCount != 256 || rom.Bits != 16) return "builtin ROM bank not found: " + Describe();

                SimProgram prog = f.RootProgram;
                // write through the editor
                Bank("Registre A").Write(0, 0x12, prog);
                Bank("Registre B").Write(0, 0x05, prog);
                Bank("MAR").Write(0, 0x33, prog);
                Bank("PC").Write(0, 42, prog);
                Bank("RAM").Write(0x33, 0x99, prog);
                Bank("RAM").Write(0x00, 0x11, prog);
                rom.Write(0, 0xBEEF, prog);
                f.Tick(6);
                foreach ((string name, int w, uint v) in new[] { ("Registre A", 0, 0x12u), ("Registre B", 0, 0x05u), ("MAR", 0, 0x33u), ("PC", 0, 42u), ("RAM", 0x33, 0x99u), ("RAM", 0, 0x11u) })
                    if (Bank(name).Read(w) != v) return $"{name}[{w}] reads {Bank(name).Read(w)} after writing {v}";
                if (rom.Read(0) != 0xBEEF) return "ROM word 0 not written";

                // the circuit really uses them
                if (f.Probe("Registre A", "OUT") != 0x12) return f.FailProbe("register A output after editing", "Registre A", "OUT", 0x12);
                if (f.Probe("PC", "OUT") != 42) return f.FailProbe("PC output after editing", "PC", "OUT", 42);
                f.Set("OE_ALu", 1); f.Tick(6);
                if (f.Probe("BUS-8", "BUS-8") != 0x17) return f.FailProbe("bus = A + B with edited registers", "BUS-8", "BUS-8", 0x17);
                f.Set("OE_ALu", 0); f.Set("Oe_ram", 1); f.Tick(6);
                if (f.Probe("BUS-8", "BUS-8") != 0x99) return f.FailProbe("bus = RAM[MAR] with edited RAM and MAR", "BUS-8", "BUS-8", 0x99);
                uint romHi = rom.Builtin.OutputPins[0].State & 0xFF, romLo = rom.Builtin.OutputPins[1].State & 0xFF;
                if (romHi != 0xBE || romLo != 0xEF) return $"ROM outputs {romHi:X2}{romLo:X2} after editing word 0 (its address is 0), expected BEEF";
                // and keeps working: the counter counts from the edited value
                f.ClockCycle();
                if (f.Probe("PC", "OUT") != 43) return f.FailProbe("PC counts on from the edited value", "PC", "OUT", 43);
                return null;
            }
            finally { f.Dispose(); }
        }

        static string RealAnswers(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            MemoryRules wrong = MemoryLayout.ParseRules(ClaudeCpuWrong, out string e1), right = MemoryLayout.ParseRules(ClaudeCpuRight, out string e2);
            if (wrong == null || right == null) return "answers not parsed: " + (e1 ?? e2);
            bool anyRejected = MemoryLayout.BankTypes(cpu, wrong, lib).Any(t => MemoryLayout.Verify(t, wrong, lib, new Dictionary<string, int[]>()) != null);
            if (!anyRejected) return "the wrong answer (register bits reversed) was accepted";
            var pol = new Dictionary<string, int[]>();
            foreach (string t in MemoryLayout.BankTypes(cpu, right, lib))
            {
                string e = MemoryLayout.Verify(t, right, lib, pol);
                if (e != null) return $"the correct answer failed on {t}: {e}";
            }
            SimChip target = CircuitTester.TargetOf(CircuitTester.BuildIsolatedSim(cpu, lib));
            var banks = MemoryLayout.Banks(target, cpu, right, pol, lib, out string be);
            if (banks == null) return be;
            return banks.Count == 6 ? null : $"expected 6 banks (A, B, MAR, PC, RAM, ROM), got {banks.Count}";
        }

        static string EditThenReload(ChipLibrary lib, ChipDescription[] chips)
        {
            ChipDescription cpu = chips.First(c => c.Name == "CPU");
            (DirectedCases.Fixture f, List<MemoryBank> banks, string error) = CpuWithBanks(lib, cpu);
            if (error != null) return error;
            ChipDescription saved;
            try
            {
                banks.First(b => b.Name == "Registre A").Write(0, 0xA5, f.RootProgram);
                banks.First(b => b.Name == "RAM").Write(200, 0x3C, f.RootProgram);
                banks.First(b => b.Name == "PC").Write(0, 7, f.RootProgram);
                banks.First(b => b.Builtin != null && b.Builtin.ChipType == ChipType.Rom_256x16).Write(5, 0xF27C, f.RootProgram);
                f.Tick(6);
                saved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(cpu));
                saved.MemoryState = MemorySnapshot.Capture(f.Target);
                saved = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(saved));
            }
            finally { f.Dispose(); }

            (DirectedCases.Fixture r, List<MemoryBank> rb, string e2) = CpuWithBanks(lib, saved);
            if (e2 != null) return e2;
            try
            {
                if (rb.First(b => b.Name == "Registre A").Read(0) != 0xA5) return "register A lost its edited value after reload";
                if (rb.First(b => b.Name == "RAM").Read(200) != 0x3C) return "RAM[200] lost its edited value after reload";
                if (rb.First(b => b.Name == "PC").Read(0) != 7) return "PC lost its edited value after reload";
                MemoryBank rom = rb.First(b => b.Builtin != null && b.Builtin.ChipType == ChipType.Rom_256x16);
                if (rom.Read(5) != 0xF27C) return $"ROM word 5 edited from the CPU's memory editor came back as {rom.Read(5):X4} (found by the self-test in the built app)";
                return null;
            }
            finally { r.Dispose(); }
        }
    }
}
