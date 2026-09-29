using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.SaveSystem;
using UnityEngine;

namespace DLS.Game
{
	// CREATE CHIP (right-click on selected sub-chips): the selected components become a new custom chip, and the
	// selection is replaced by one instance of it. Pure netlist operation on descriptions (a wire is only
	// "source pin -> target pin"; ConnectionType is routing), so it is testable by simulation.
	//  - a wire entering the selection becomes an INPUT pin, one per outside source, named after that source
	//    (an input pin: its name; a component: its label or type name, plus ".pin" when it has several pins on
	//    that side);
	//  - a wire leaving the selection becomes an OUTPUT pin, one per inside source, named after everything it fed
	//    outside, joined with "-" when there are several;
	//  - wires inside the selection stay inside, the others are untouched;
	//  - VCC / GND never become inputs (user, 2026-09-29): a constant that only feeds the selection moves into the
	//    new chip, one that also feeds something outside is copied in (and stays outside for the rest).
	public static class ChipExtractor
	{
		public class Result
		{
			public ChipDescription NewChip;
			public ChipDescription NewParent;
			public int InstanceID;
			public int[] RemovedIDs; // components removed from the parent: the selection + the constants that only fed it
		}

		// The whole user action: extract, register the new chip (file, library, starred, project list), then change
		// the edited chip IN PLACE (only the selection is removed, so every other component keeps its running
		// state), as one undo step. Returns an error message, or null.
		public static string CreateFromSelection(Project p, ICollection<int> selectedIDs, string newName)
		{
			DevChipInstance dev = p.ViewedChip;
			ChipDescription parent = DescriptionCreator.CreateChipDescription(dev);
			Result r = Extract(parent, selectedIDs, newName, p.chipLibrary, out string error);
			if (r == null) return error;

			Saver.SaveChip(r.NewChip, p.description.ProjectName);
			p.chipLibrary.NotifyChipSaved(r.NewChip);
			p.SetStarred(r.NewChip.Name, true, false, false);
			p.UpdateAndSaveProjectDescription();

			var before = new UndoController.ChipSnapshot(dev);
			foreach (IMoveable e in p.controller.SelectedElements) e.IsSelected = false;
			p.controller.SelectedElements.Clear();
			ApplyInPlace(dev, r, p.chipLibrary);
			dev.UndoController.RecordClaudeTurn(before, new UndoController.ChipSnapshot(dev));
			p.LoadDevChipOrCreateNewIfDoesntExist(r.NewChip.Name); // straight into the new chip (the edited chip stays open in memory, unsaved edits and undo kept)
			return null;
		}

		// The edited chip becomes the new parent without being rebuilt: the selection is deleted, the instance added,
		// and the wires it lacks are loaded. (The new chip must already be in the library.)
		public static void ApplyInPlace(DevChipInstance dev, Result r, ChipLibrary lib, bool noRunningSim = false)
		{
			foreach (int id in r.RemovedIDs) dev.TryDeleteSubChipByID(id); // the selection + the constants that only fed it
			SubChipDescription inst = r.NewParent.SubChips.First(s => s.ID == r.InstanceID);
			dev.AddNewSubChip(new SubChipInstance(lib.GetChipDescription(r.NewChip.Name), inst), noRunningSim); // (a test has no running sim to sync)
			// wires of the new parent that the edited chip does not have yet (to and from the new chip, and the
			// branches of removed wires that became direct wires)
			WireDescription[] wires = r.NewParent.Wires;
			for (int i = 0; i < wires.Length; i++)
			{
				WireDescription w = wires[i];
				bool present = dev.Wires.Any(x => PinAddress.Equals(x.SourcePin.Address, w.SourcePinAddress) && PinAddress.Equals(x.TargetPin.Address, w.TargetPinAddress));
				if (present) continue;
				w.ConnectionType = WireConnectionType.ToPins;
				w.ConnectedWireIndex = -1;
				w.ConnectedWireSegmentIndex = -1;
				w.Points = new Vector2[2];
				(WireInstance loaded, bool failed) = DevChipInstance.TryLoadWireFromDescription(w, i, dev, dev.Wires);
				if (loaded != null && !failed) dev.AddWire(loaded, noRunningSim);
			}
		}

		public static Result Extract(ChipDescription parent, ICollection<int> selectedSubChipIDs, string newName, ChipLibrary lib, out string error)
		{
			error = null;
			var sel = new HashSet<int>(selectedSubChipIDs);
			SubChipDescription[] subs = parent.SubChips ?? Array.Empty<SubChipDescription>();
			PinDescription[] ins = parent.InputPins ?? Array.Empty<PinDescription>();
			PinDescription[] outs = parent.OutputPins ?? Array.Empty<PinDescription>();
			WireDescription[] wires = parent.Wires ?? Array.Empty<WireDescription>();
			sel.RemoveWhere(id => subs.All(s => s.ID != id));
			if (sel.Count == 0) { error = "Select at least one component."; return null; }

			var subByID = subs.ToDictionary(s => s.ID);
			var descOf = new Dictionary<int, ChipDescription>();
			foreach (SubChipDescription s in subs)
			{
				ChipDescription d = lib.GetChipDescriptionForSim(s.Name);
				if (d == null && !lib.TryGetChipDescription(s.Name, out d)) { error = $"Unknown chip \"{s.Name}\"."; return null; }
				descOf[s.ID] = d;
			}
			// a bus origin and its terminus go together
			foreach (int id in sel)
			{
				SubChipDescription s = subByID[id];
				if (!ChipTypeHelper.IsBusType(descOf[id].ChipType) || s.InternalData == null || s.InternalData.Length == 0) continue;
				int linked = (int)s.InternalData[0];
				if (subByID.ContainsKey(linked) && !sel.Contains(linked)) { error = "A bus and its terminus must be selected together."; return null; }
			}

			// ---- VCC / GND feeding the selection: moved in when they feed nothing else, copied in otherwise
			var copiedConstants = new HashSet<int>();
			foreach (SubChipDescription s in subs)
			{
				if (sel.Contains(s.ID) || !ChipTypeHelper.IsConstantType(descOf[s.ID].ChipType)) continue;
				var targets = wires.Where(w => w.SourcePinAddress.PinOwnerID == s.ID).Select(w => w.TargetPinAddress.PinOwnerID).ToList();
				if (!targets.Any(t => sel.Contains(t))) continue;
				if (targets.All(t => sel.Contains(t))) sel.Add(s.ID);
				else copiedConstants.Add(s.ID);
			}

			// ---- names and widths of the things at the other end of a wire
			var parentPins = new Dictionary<int, PinDescription>();
			foreach (PinDescription p in ins) parentPins[p.ID] = p;
			foreach (PinDescription p in outs) parentPins[p.ID] = p;

			PinDescription? SubPin(PinAddress a, bool output)
			{
				if (!descOf.TryGetValue(a.PinOwnerID, out ChipDescription d)) return null;
				foreach (PinDescription p in (output ? d.OutputPins : d.InputPins) ?? Array.Empty<PinDescription>())
					if (p.ID == a.PinID) return p;
				return null;
			}

			string NameOf(PinAddress a, bool output)
			{
				if (parentPins.TryGetValue(a.PinOwnerID, out PinDescription dp)) return dp.Name;
				SubChipDescription s = subByID[a.PinOwnerID];
				string baseName = string.IsNullOrWhiteSpace(s.Label) ? s.Name : s.Label;
				PinDescription[] side = (output ? descOf[s.ID].OutputPins : descOf[s.ID].InputPins) ?? Array.Empty<PinDescription>();
				PinDescription? p = SubPin(a, output);
				return side.Length > 1 && p != null ? $"{baseName}.{p.Value.Name}" : baseName;
			}

			PinBitCount BitsOf(PinAddress a, bool output)
			{
				if (parentPins.TryGetValue(a.PinOwnerID, out PinDescription dp)) return dp.BitCount;
				return SubPin(a, output)?.BitCount ?? PinBitCount.Bit1;
			}

			bool Inside(PinAddress a) => sel.Contains(a.PinOwnerID);

			// ---- classify the wires
			var inside = new List<int>();
			var entering = new List<int>();
			var leaving = new List<int>();
			var kept = new List<int>();
			for (int i = 0; i < wires.Length; i++)
			{
				bool s = Inside(wires[i].SourcePinAddress), t = Inside(wires[i].TargetPinAddress);
				if (s && t) inside.Add(i);
				else if (t) entering.Add(i);
				else if (s) leaving.Add(i);
				else kept.Add(i);
			}

			// ---- the new chip
			var usedInNew = new HashSet<int>(sel);
			var rnd = new System.Random(newName.GetHashCode() ^ sel.Count);
			int NewID(HashSet<int> used)
			{
				int id;
				do id = rnd.Next(1, int.MaxValue); while (!used.Add(id));
				return id;
			}

			Vector2 centre = Vector2.zero;
			foreach (int id in sel) centre += subByID[id].Position;
			centre /= sel.Count;
			float minX = sel.Min(id => subByID[id].Position.x) - centre.x - 6;
			float maxX = sel.Max(id => subByID[id].Position.x) - centre.x + 6;

			var newIns = new List<(PinDescription pin, PinAddress outsideSource, float y)>();
			var newOuts = new List<(PinDescription pin, PinAddress insideSource, List<PinAddress> outsideTargets, float y)>();
			var namesIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var namesOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string Unique(string name, HashSet<string> used)
			{
				string n = string.IsNullOrWhiteSpace(name) ? "PIN" : name.Trim();
				string c = n;
				for (int k = 2; !used.Add(c); k++) c = $"{n} {k}";
				return c;
			}

			var newWires = new List<WireDescription>();
			var constantCopies = new List<SubChipDescription>();
			foreach (IGrouping<(int, int), int> g in entering.GroupBy(i => (wires[i].SourcePinAddress.PinOwnerID, wires[i].SourcePinAddress.PinID)))
			{
				PinAddress src = wires[g.First()].SourcePinAddress;
				if (copiedConstants.Contains(src.PinOwnerID))
				{
					SubChipDescription copy = subByID[src.PinOwnerID];
					copy.ID = NewID(usedInNew);
					copy.Label = string.Empty;
					copy.Position = subByID[wires[g.First()].TargetPinAddress.PinOwnerID].Position - centre + new Vector2(-3, 0);
					constantCopies.Add(copy);
					foreach (int i in g) newWires.Add(ChipEmitHelper.Wire(new PinAddress(copy.ID, src.PinID), wires[i].TargetPinAddress));
					continue;
				}
				int id = NewID(usedInNew);
				float y = g.Average(i => subByID[wires[i].TargetPinAddress.PinOwnerID].Position.y) - centre.y;
				var pin = new PinDescription(Unique(NameOf(src, true), namesIn), id, Vector2.zero, BitsOf(src, true), PinColour.Red, PinValueDisplayMode.Off);
				newIns.Add((pin, src, y));
				foreach (int i in g) newWires.Add(ChipEmitHelper.Wire(new PinAddress(id, 0), wires[i].TargetPinAddress));
			}
			foreach (IGrouping<(int, int), int> g in leaving.GroupBy(i => (wires[i].SourcePinAddress.PinOwnerID, wires[i].SourcePinAddress.PinID)))
			{
				PinAddress src = wires[g.First()].SourcePinAddress;
				int id = NewID(usedInNew);
				List<PinAddress> targets = g.Select(i => wires[i].TargetPinAddress).ToList();
				string name = string.Join("-", targets.Select(t => NameOf(t, false)).Distinct(StringComparer.OrdinalIgnoreCase));
				float y = subByID[src.PinOwnerID].Position.y - centre.y;
				var pin = new PinDescription(Unique(name, namesOut), id, Vector2.zero, BitsOf(src, true), PinColour.Red, PinValueDisplayMode.Off);
				newOuts.Add((pin, src, targets, y));
				newWires.Add(ChipEmitHelper.Wire(src, new PinAddress(id, 0)));
			}

			// pin positions: inputs on the left, outputs on the right, in the height order of what they connect to
			PinDescription[] Place(List<PinDescription> pins, List<float> ys, float x)
			{
				int[] order = Enumerable.Range(0, pins.Count).OrderByDescending(i => ys[i]).ToArray();
				var result = new PinDescription[pins.Count];
				for (int k = 0; k < order.Length; k++)
				{
					PinDescription p = pins[order[k]];
					p.Position = new Vector2(x, (order.Length - 1) * 0.75f - k * 1.5f);
					result[k] = p;
				}
				return result;
			}
			PinDescription[] inPins = Place(newIns.Select(e => e.pin).ToList(), newIns.Select(e => e.y).ToList(), minX);
			PinDescription[] outPins = Place(newOuts.Select(e => e.pin).ToList(), newOuts.Select(e => e.y).ToList(), maxX);

			// inside components keep their layout (moved around the origin), inside wires keep their points
			var newSubs = sel.Select(id =>
			{
				SubChipDescription s = subByID[id];
				s.Position -= centre;
				return s;
			}).ToList();
			newSubs.AddRange(constantCopies);
			foreach (int i in inside)
			{
				WireDescription w = wires[i];
				w.ConnectionType = WireConnectionType.ToPins;
				w.ConnectedWireIndex = -1;
				w.ConnectedWireSegmentIndex = -1;
				w.Points = w.Points == null || w.Points.Length < 2 || wires[i].ConnectionType != WireConnectionType.ToPins
					? new Vector2[2]
					: w.Points.Select(p => p - centre).ToArray();
				newWires.Insert(0, w);
			}

			Color colour = Color.HSVToRGB(Mathf.Abs(newName.GetHashCode() % 1000) / 1000f, 0.5f, 0.75f);
			ChipDescription newChip = ChipEmitHelper.Assemble(newName, colour, NameDisplayLocation.Centre, Vector2.zero, inPins, outPins, newSubs, newWires);
			newChip.Displays = (parent.Displays ?? Array.Empty<DisplayDescription>()).Where(d => sel.Contains(d.SubChipID)).ToArray();
			if (newChip.Displays.Length == 0) newChip.Displays = null;

			// ---- the new parent: selection replaced by one instance
			var usedInParent = new HashSet<int>(subs.Select(s => s.ID).Concat(ins.Select(p => p.ID)).Concat(outs.Select(p => p.ID)));
			int instanceID = NewID(usedInParent);
			var colours = outPins.Select(p => new OutputPinColourInfo(PinColour.Red, p.ID)).ToArray();
			var parentSubs = subs.Where(s => !sel.Contains(s.ID)).ToList();
			parentSubs.Add(new SubChipDescription(newName, instanceID, string.Empty, centre, colours));

			var parentWires = new List<WireDescription>();
			var newIndex = new Dictionary<int, int>();
			foreach (int i in kept) { newIndex[i] = parentWires.Count; parentWires.Add(wires[i]); }
			for (int k = 0; k < parentWires.Count; k++)
			{
				WireDescription w = parentWires[k];
				if (w.ConnectionType == WireConnectionType.ToPins) continue;
				if (newIndex.TryGetValue(w.ConnectedWireIndex, out int ni)) w.ConnectedWireIndex = ni;
				else
				{
					// it branched off a wire that now ends on the new chip: a direct wire
					w.ConnectionType = WireConnectionType.ToPins;
					w.ConnectedWireIndex = -1;
					w.ConnectedWireSegmentIndex = -1;
					w.Points = new Vector2[2];
				}
				parentWires[k] = w;
			}
			foreach (var e in newIns) parentWires.Add(ChipEmitHelper.Wire(e.outsideSource, new PinAddress(instanceID, e.pin.ID)));
			foreach (var e in newOuts)
				foreach (PinAddress t in e.outsideTargets)
					parentWires.Add(ChipEmitHelper.Wire(new PinAddress(instanceID, e.pin.ID), t));

			ChipDescription newParent = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(parent));
			newParent.SubChips = parentSubs.ToArray();
			newParent.Wires = parentWires.ToArray();
			if (newParent.Displays != null)
			{
				newParent.Displays = newParent.Displays.Where(d => !sel.Contains(d.SubChipID)).ToArray();
				if (newParent.Displays.Length == 0) newParent.Displays = null;
			}
			newParent.MemoryState = null; // its structure changed; the live state is captured again at the next save

			return new Result { NewChip = newChip, NewParent = newParent, InstanceID = instanceID, RemovedIDs = sel.ToArray() };
		}
	}
}
