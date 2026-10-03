using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using UnityEngine;

namespace DLS.Game
{
	// A chip's interface changed (pins added / removed) and it was SAVED: a chip using it is updated (user,
	// 2026-10-03): a pin that still exists keeps its wires — also when it was deleted and re-created with the same name
	// (a new ID) —, a removed pin's wires go, a new pin simply appears (nothing wired to it). Pure description
	// transform, used for the chips using it on disk and the ones open in memory.
	public static class ChipInterfaceUpdate
	{
		public static ChipDescription Apply(ChipDescription parent, string childName, ChipDescription oldChild, ChipDescription newChild, out bool changed)
		{
			changed = false;
			SubChipDescription[] subs = parent.SubChips ?? Array.Empty<SubChipDescription>();
			var instances = new HashSet<int>(subs.Where(s => ChipDescription.NameMatch(s.Name, childName)).Select(s => s.ID));
			if (instances.Count == 0) return parent;

			// old pin ID -> new pin ID (same ID, or same name + direction when the ID changed); -1 = gone
			var map = new Dictionary<int, int>();
			void Map(PinDescription[] oldPins, PinDescription[] newPins)
			{
				oldPins ??= Array.Empty<PinDescription>();
				newPins ??= Array.Empty<PinDescription>();
				var newIDs = new HashSet<int>(newPins.Select(p => p.ID));
				var oldIDs = new HashSet<int>(oldPins.Select(p => p.ID));
				foreach (PinDescription o in oldPins)
				{
					if (newIDs.Contains(o.ID)) { map[o.ID] = o.ID; continue; }
					// a re-created pin: same name, an ID the old interface did not have, and no ambiguity
					var same = newPins.Where(n => n.Name == o.Name && !oldIDs.Contains(n.ID)).ToList();
					map[o.ID] = same.Count == 1 && oldPins.Count(x => x.Name == o.Name) == 1 ? same[0].ID : -1;
				}
			}
			Map(oldChild?.InputPins, newChild.InputPins);
			Map(oldChild?.OutputPins, newChild.OutputPins);

			bool Touches(PinAddress a) => instances.Contains(a.PinOwnerID);
			int Target(PinAddress a) => map.TryGetValue(a.PinID, out int n) ? n : a.PinID; // (unknown to the old interface: left as is)

			WireDescription[] wires = parent.Wires ?? Array.Empty<WireDescription>();
			var kept = new List<WireDescription>();
			var newIndex = new Dictionary<int, int>();
			for (int i = 0; i < wires.Length; i++)
			{
				WireDescription w = wires[i];
				bool drop = false;
				if (Touches(w.SourcePinAddress)) { int n = Target(w.SourcePinAddress); if (n < 0) drop = true; else if (n != w.SourcePinAddress.PinID) { w.SourcePinAddress.PinID = n; changed = true; } }
				if (!drop && Touches(w.TargetPinAddress)) { int n = Target(w.TargetPinAddress); if (n < 0) drop = true; else if (n != w.TargetPinAddress.PinID) { w.TargetPinAddress.PinID = n; changed = true; } }
				if (drop) { changed = true; continue; }
				newIndex[i] = kept.Count;
				kept.Add(w);
			}
			for (int k = 0; k < kept.Count; k++)
			{
				WireDescription w = kept[k];
				if (w.ConnectionType == WireConnectionType.ToPins) continue;
				if (newIndex.TryGetValue(w.ConnectedWireIndex, out int ni)) { if (ni != w.ConnectedWireIndex) { w.ConnectedWireIndex = ni; kept[k] = w; } continue; }
				// it branched off a wire that went: a direct wire now
				w.ConnectionType = WireConnectionType.ToPins;
				w.ConnectedWireIndex = -1;
				w.ConnectedWireSegmentIndex = -1;
				w.Points = new Vector2[2];
				kept[k] = w;
				changed = true;
			}

			// output pin colours of the instances: one entry per output pin of the NEW interface (kept colours follow the map)
			var newSubs = new SubChipDescription[subs.Length];
			for (int i = 0; i < subs.Length; i++)
			{
				SubChipDescription s = subs[i];
				if (instances.Contains(s.ID) && s.OutputPinColourInfo != null)
				{
					var byNewID = new Dictionary<int, PinColour>();
					foreach (OutputPinColourInfo c in s.OutputPinColourInfo)
						if (map.TryGetValue(c.PinID, out int n) ? n >= 0 : true) byNewID[map.TryGetValue(c.PinID, out int m) ? m : c.PinID] = c.PinColour;
					OutputPinColourInfo[] colours = (newChild.OutputPins ?? Array.Empty<PinDescription>())
						.Select(p => new OutputPinColourInfo(byNewID.TryGetValue(p.ID, out PinColour col) ? col : p.Colour, p.ID)).ToArray();
					if (!colours.Select(c => (c.PinID, c.PinColour)).SequenceEqual(s.OutputPinColourInfo.Select(c => (c.PinID, c.PinColour)))) changed = true;
					s.OutputPinColourInfo = colours;
				}
				newSubs[i] = s;
			}
			if (!changed) return parent;

			ChipDescription result = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(parent));
			result.SubChips = newSubs;
			result.Wires = kept.ToArray();
			return result;
		}
	}
}
