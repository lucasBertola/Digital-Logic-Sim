using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using UnityEngine;

namespace DLS.Bench
{
    // ChipInterfaceUpdate on hand-built descriptions: what a chip using a re-saved chip becomes.
    public static class ChipInterfaceCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("interface update: removed pin unwired, same-name pin re-wired, new pin colour added, junction off a removed wire becomes direct", Basics),
            ("interface update: two old pins with the same name are not guessed (unwired), an untouched chip is returned as is", Ambiguous),
        };

        static PinDescription Pin(string name, int id) => new(name, id, Vector2.zero, PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off);

        static ChipDescription Child(PinDescription[] ins, PinDescription[] outs) => new() { Name = "CHILD", InputPins = ins, OutputPins = outs, SubChips = Array.Empty<SubChipDescription>(), Wires = Array.Empty<WireDescription>() };

        // parent: input X -> CHILD.A (id 11), X -> CHILD.B (id 12); CHILD.Q (id 21) -> output Y; a junction wire off the X->B wire to CHILD.C (13)
        static ChipDescription Parent()
        {
            var w0 = ChipEmitHelper.Wire(new PinAddress(1, 0), new PinAddress(100, 11));
            var w1 = ChipEmitHelper.Wire(new PinAddress(1, 0), new PinAddress(100, 12));
            var w2 = ChipEmitHelper.Wire(new PinAddress(100, 21), new PinAddress(2, 0));
            var w3 = ChipEmitHelper.Wire(new PinAddress(1, 0), new PinAddress(100, 13));
            w3.ConnectionType = WireConnectionType.ToWireSource; w3.ConnectedWireIndex = 1; w3.Points = new[] { Vector2.one, Vector2.zero };
            return new ChipDescription
            {
                Name = "PARENT", InputPins = new[] { Pin("X", 1) }, OutputPins = new[] { Pin("Y", 2) },
                SubChips = new[] { new SubChipDescription("CHILD", 100, "", Vector2.zero, new[] { new OutputPinColourInfo(PinColour.Blue, 21) }) },
                Wires = new[] { w0, w1, w2, w3 },
            };
        }

        static string Basics()
        {
            ChipDescription oldChild = Child(new[] { Pin("A", 11), Pin("B", 12), Pin("C", 13) }, new[] { Pin("Q", 21) });
            // B removed, A deleted + re-created (new id 31), C kept, new input D (32), new output R (33)
            ChipDescription newChild = Child(new[] { Pin("A", 31), Pin("C", 13), Pin("D", 32) }, new[] { Pin("Q", 21), Pin("R", 33) });
            ChipDescription r = ChipInterfaceUpdate.Apply(Parent(), "CHILD", oldChild, newChild, out bool changed);
            if (!changed) return "not reported as changed";
            string Wires() => string.Join(" ", r.Wires.Select(w => $"{w.SourcePinAddress.PinOwnerID}.{w.SourcePinAddress.PinID}>{w.TargetPinAddress.PinOwnerID}.{w.TargetPinAddress.PinID}"));
            if (r.Wires.Length != 3) return "wires: " + Wires() + " (expected the X->B wire gone, 3 left)";
            if (!r.Wires.Any(w => w.TargetPinAddress.PinID == 31)) return "the re-created A (same name, new id) lost its wire: " + Wires();
            if (r.Wires.Any(w => w.TargetPinAddress.PinID == 12)) return "a wire to the removed B is still there";
            WireDescription j = r.Wires.First(w => w.TargetPinAddress.PinID == 13);
            if (j.ConnectionType != WireConnectionType.ToPins || j.ConnectedWireIndex != -1) return "the junction off the removed X->B wire must become a direct wire";
            var colours = r.SubChips[0].OutputPinColourInfo;
            if (colours.Length != 2 || colours[0].PinColour != PinColour.Blue || colours[1].PinID != 33) return "output colours: Q must keep Blue, R must get an entry";
            return null;
        }

        static string Ambiguous()
        {
            ChipDescription oldChild = Child(new[] { Pin("IN", 11), Pin("IN", 12), Pin("C", 13) }, new[] { Pin("Q", 21) });
            ChipDescription newChild = Child(new[] { Pin("IN", 41), Pin("C", 13) }, new[] { Pin("Q", 21) });
            ChipDescription r = ChipInterfaceUpdate.Apply(Parent(), "CHILD", oldChild, newChild, out _);
            if (r.Wires.Any(w => w.TargetPinAddress.PinID == 41)) return "a pin named like two old ones was guessed";
            ChipDescription same = ChipInterfaceUpdate.Apply(Parent(), "OTHER", oldChild, newChild, out bool changed);
            return changed ? "a chip not using it was changed" : null;
        }
    }
}
