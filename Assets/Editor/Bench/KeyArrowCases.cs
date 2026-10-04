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
