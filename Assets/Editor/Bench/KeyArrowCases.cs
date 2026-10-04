using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;

namespace DLS.Bench
{
    // KEY chips bound to the arrow keys (user, 2026-10-04): the arrow's Unicode character is the binding (saved like a
    // letter), the sim reacts to that arrow only, and the names Claude may use map to it.
    public static class KeyArrowCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("KEY bound to an arrow: high while that arrow is held, not for another arrow nor a letter", Reacts),
            ("KEY arrows: UP / DOWN / LEFT / RIGHT (and HAUT / BAS / GAUCHE / DROITE, and the arrow characters) name the arrows; letters are not arrows", Names),
            ("KEY arrows: the binding survives the chip's save / load and still reacts", Saved),
            ("KEY nested 3 levels deep (a chip using a chip using a chip with KEYs): the top circuit reacts to its keys", Nested),
            ("KEY bindings of a custom chip are listed for its parents: every depth, duplicates merged, letters then arrows then space; none when no KEY", Listed),
            ("export for Claude: each KEY shows its key ([touche: A], UP / SPACE...), a custom component the keys of the KEYs inside it (any depth); the names round-trip through bind_keys' parser", Exported),
            ("KEY on the space bar: high while space is held (not for a letter), labelled SPACE, named SPACE / ESPACE; the circuit is seen as using it (the Ask Claude bar then leaves Space alone)", SpaceKey),
        };

        static UnitCases.Circuit Keys()
        {
            var c = UnitCases.Build("t_key_arrows", new string[0], new[] { "U", "L", "R", "D" }, b =>
            {
                foreach ((string o, char a) in new[] { ("U", SimKeyboardHelper.Up), ("L", SimKeyboardHelper.Left), ("R", SimKeyboardHelper.Right), ("D", SimKeyboardHelper.Down) })
                {
                    int k = b.Add(ChipType.Key);
                    b.Data(k)[0] = a;
                    b.Wire(b.Out(k, 0), b.Output(o));
                }
            });
            return c;
        }

        static string Pressed(UnitCases.Circuit c, HashSet<char> held, string expectHigh)
        {
            SimKeyboardHelper.SetVirtualKeys(held);
            c.Step(2);
            foreach (string o in new[] { "U", "L", "R", "D" })
                if ((c.OutBit(o) == 1) != (o == expectHigh)) return $"holding [{string.Join(",", held.Select(h => ((int)h).ToString("X")))}]: KEY {o} reads {c.OutBit(o)}";
            return null;
        }

        static string Reacts()
        {
            var c = Keys();
            try
            {
                return Pressed(c, new HashSet<char>(), null)
                    ?? Pressed(c, new HashSet<char> { SimKeyboardHelper.Up }, "U")
                    ?? Pressed(c, new HashSet<char> { SimKeyboardHelper.Left }, "L")
                    ?? Pressed(c, new HashSet<char> { SimKeyboardHelper.Right }, "R")
                    ?? Pressed(c, new HashSet<char> { SimKeyboardHelper.Down }, "D")
                    ?? Pressed(c, new HashSet<char> { 'A', 'U', 'D' }, null);
            }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
        }

        static string Names()
        {
            var cases = new (string name, char want)[]
            {
                ("UP", SimKeyboardHelper.Up), ("down", SimKeyboardHelper.Down), ("Left", SimKeyboardHelper.Left), ("RIGHT", SimKeyboardHelper.Right),
                ("HAUT", SimKeyboardHelper.Up), ("bas", SimKeyboardHelper.Down), ("GAUCHE", SimKeyboardHelper.Left), ("droite", SimKeyboardHelper.Right),
                ("↑", SimKeyboardHelper.Up), ("A", '\0'), ("U", '\0'), ("", '\0'),
            };
            foreach ((string name, char want) in cases)
                if (SimKeyboardHelper.ArrowFromName(name) != want) return $"\"{name}\" mapped to {(int)SimKeyboardHelper.ArrowFromName(name):X}";
            if (SimKeyboardHelper.IsArrow('A') || SimKeyboardHelper.IsArrow('9') || !SimKeyboardHelper.IsArrow(SimKeyboardHelper.Down)) return "IsArrow wrong";
            return SimKeyboardHelper.ArrowDirection(SimKeyboardHelper.Up) == UnityEngine.Vector2.up && SimKeyboardHelper.ArrowDirection('A') == UnityEngine.Vector2.zero ? null : "ArrowDirection wrong";
        }

        static string SpaceKey()
        {
            var c = UnitCases.Build("t_key_space", new string[0], new[] { "S" }, b =>
            {
                int k = b.Add(ChipType.Key);
                b.Data(k)[0] = SimKeyboardHelper.Space;
                b.Wire(b.Out(k, 0), b.Output("S"));
            });
            try
            {
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char>()); c.Step(2);
                if (c.OutBit("S") != 0) return "space KEY high while nothing is held";
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char> { ' ' }); c.Step(2);
                if (c.OutBit("S") != 1) return "space KEY low while space is held";
                SimKeyboardHelper.SetVirtualKeys(new HashSet<char> { 'A', SimKeyboardHelper.Up }); c.Step(2);
                if (c.OutBit("S") != 0) return "space KEY high for A / up";
            }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
            if (SimKeyboardHelper.KeyLabel(' ') != "SPACE" || SimKeyboardHelper.KeyLabel('H') != "H") return "labels wrong";
            if (SimKeyboardHelper.SpecialKeyFromName("space") != ' ' || SimKeyboardHelper.SpecialKeyFromName("ESPACE") != ' ' || SimKeyboardHelper.SpecialKeyFromName("UP") != SimKeyboardHelper.Up || SimKeyboardHelper.SpecialKeyFromName("S") != '\0') return "SpecialKeyFromName wrong";
            if (!Project.TreeUsesKey(c.root, ' ')) return "a circuit with a space KEY is not seen as using the space bar";
            return Project.TreeUsesKey(Keys().root, ' ') ? "a circuit without a space KEY is seen as using it" : null;
        }

        // INNER: KEY A -> Q, KEY UP -> R ; MIDDLE: INNER + its own KEY A (duplicate) and KEY SPACE ; TOP: MIDDLE
        static (UnitCases.Circuit top, ChipLibrary lib, ChipDescription middle) NestedChips()
        {
            var builtins = BuiltinChipCreator.CreateAllBuiltinChipDescriptions();
            var inner = UnitCases.Build("INNER", new string[0], new[] { "Q", "R" }, b =>
            {
                int a = b.Add(ChipType.Key); b.Data(a)[0] = 'A'; b.Wire(b.Out(a, 0), b.Output("Q"));
                int u = b.Add(ChipType.Key); b.Data(u)[0] = SimKeyboardHelper.Up; b.Wire(b.Out(u, 0), b.Output("R"));
            });
            var lib1 = new ChipLibrary(new[] { inner.desc }, builtins);
            var middle = UnitCases.Build("MIDDLE", new string[0], new[] { "Q", "R", "S" }, b =>
            {
                int i = b.AddCustom("INNER"); b.Wire(b.Out(i, 0), b.Output("Q")); b.Wire(b.Out(i, 1), b.Output("R"));
                int a = b.Add(ChipType.Key); b.Data(a)[0] = 'A';
                int s = b.Add(ChipType.Key); b.Data(s)[0] = SimKeyboardHelper.Space; b.Wire(b.Out(s, 0), b.Output("S"));
            }, lib1);
            var lib2 = new ChipLibrary(new[] { inner.desc, middle.desc }, builtins);
            var top = UnitCases.Build("TOP", new string[0], new[] { "Q", "R", "S" }, b =>
            {
                int m = b.AddCustom("MIDDLE"); b.Wire(b.Out(m, 0), b.Output("Q")); b.Wire(b.Out(m, 1), b.Output("R")); b.Wire(b.Out(m, 2), b.Output("S"));
            }, lib2);
            return (top, lib2, middle.desc);
        }

        static string Nested()
        {
            var (c, _, _) = NestedChips();
            try
            {
                foreach ((HashSet<char> held, string want) in new[]
                {
                    (new HashSet<char>(), "000"), (new HashSet<char> { 'A' }, "100"), (new HashSet<char> { SimKeyboardHelper.Up }, "010"),
                    (new HashSet<char> { ' ' }, "001"), (new HashSet<char> { 'A', ' ' }, "101"), (new HashSet<char> { 'B' }, "000"),
                })
                {
                    SimKeyboardHelper.SetVirtualKeys(held); c.Step(2);
                    string got = $"{c.OutBit("Q")}{c.OutBit("R")}{c.OutBit("S")}";
                    if (got != want) return $"holding [{string.Join(",", held.Select(h => ((int)h).ToString("X")))}]: Q R S = {got}, expected {want}";
                }
                return null;
            }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
        }

        static string Listed()
        {
            var (c, lib, middle) = NestedChips();
            string Show(List<char> k) => string.Join(",", k.Select(x => ((int)x).ToString("X")));
            List<char> top = ChipKeyBindings.Collect(c.desc, n => lib.TryGetChipDescription(n, out ChipDescription d) ? d : null);
            var want = new List<char> { 'A', SimKeyboardHelper.Up, ' ' };
            if (!top.SequenceEqual(want)) return $"TOP lists [{Show(top)}], expected [{Show(want)}]";
            List<char> mid = ChipKeyBindings.Collect(middle, n => lib.TryGetChipDescription(n, out ChipDescription d) ? d : null);
            if (!mid.SequenceEqual(want)) return $"MIDDLE lists [{Show(mid)}]";
            var plain = UnitCases.Build("PLAIN", new[] { "A" }, new[] { "Y" }, b => b.Wire(b.Input("A"), b.Output("Y")));
            return ChipKeyBindings.Collect(plain.desc, n => lib.TryGetChipDescription(n, out ChipDescription d) ? d : null).Count == 0 ? null : "a chip without KEY lists keys";
        }

        static string Exported()
        {
            var (top, lib, middle) = NestedChips();
            string mid = SaveSystem.CircuitExporter.ExportChip(middle, lib);
            foreach (string want in new[] { "[touche: A]", "[touche: SPACE]", "[touches clavier internes: A, UP]" })
                if (!mid.Contains(want)) return $"export of MIDDLE lacks \"{want}\":\n{mid}";
            string t = SaveSystem.CircuitExporter.ExportChip(top.desc, lib);
            if (!t.Contains("[touches clavier internes: A, UP, SPACE]")) return "export of TOP lacks the keys of its sub-chip:\n" + t;
            foreach (char c in new[] { 'A', '7', SimKeyboardHelper.Up, SimKeyboardHelper.Down, SimKeyboardHelper.Left, SimKeyboardHelper.Right, SimKeyboardHelper.Space })
            {
                string name = SimKeyboardHelper.KeyName(c);
                char back = SimKeyboardHelper.SpecialKeyFromName(name);
                if (back == '\0') back = name.Length == 1 ? name[0] : '\0';
                if (back != c) return $"KeyName({(int)c:X}) = \"{name}\" does not map back";
            }
            return null;
        }

        static string Saved()
        {
            var c = Keys();
            ChipDescription back = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(c.desc));
            var keys = back.SubChips.Where(s => s.Name == "KEY").Select(s => (char)s.InternalData[0]).ToList();
            if (!keys.Contains(SimKeyboardHelper.Up) || !keys.Contains(SimKeyboardHelper.Down)) return "the arrow bindings were not saved";
            var r = UnitCases.Rebuild(c, back);
            try { return Pressed(r, new HashSet<char> { SimKeyboardHelper.Right }, "R"); }
            finally { SimKeyboardHelper.SetVirtualKeys(null); }
        }
    }
}
