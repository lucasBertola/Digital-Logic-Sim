using System;
using System.Collections.Generic;
using DLS.Description;

namespace DLS.Game
{
	// The keys a custom chip listens to (user, 2026-10-04): every KEY inside it, at any depth (a chip using a chip using
	// a chip... with KEYs). The simulation already runs nested KEYs (each is a gate of the compiled tree); this lists
	// their bindings so the parent can show them on the sub-chip.
	public static class ChipKeyBindings
	{
		// distinct bound keys in a stable order (letters / digits, then the arrows, then space)
		public static List<char> Collect(ChipDescription chip, Func<string, ChipDescription> resolve)
		{
			var found = new SortedSet<char>(Comparer<char>.Create(Order));
			var visiting = new HashSet<string>(ChipDescription.NameComparer);
			Walk(chip, resolve, found, visiting);
			return new List<char>(found);
		}

		static int Order(char a, char b)
		{
			int Rank(char c) => c == ' ' ? 2 : c >= '←' && c <= '↓' ? 1 : 0;
			int r = Rank(a).CompareTo(Rank(b));
			return r != 0 ? r : a.CompareTo(b);
		}

		static void Walk(ChipDescription d, Func<string, ChipDescription> resolve, SortedSet<char> found, HashSet<string> visiting)
		{
			if (d?.SubChips == null || !visiting.Add(d.Name)) return; // (a chip cannot contain itself; guard anyway)
			foreach (SubChipDescription s in d.SubChips)
			{
				if (s.Name == null) continue;
				ChipDescription child = resolve(s.Name);
				if (child == null) continue;
				if (child.ChipType == ChipType.Key)
				{
					if (s.InternalData != null && s.InternalData.Length > 0) found.Add((char)s.InternalData[0]);
				}
				else if (child.ChipType == ChipType.Custom) Walk(child, resolve, found, visiting);
			}
			visiting.Remove(d.Name);
		}

		// per frame, per chip name (the drawer asks for every sub-chip in view every frame)
		static int cachedFrame = -1;
		static readonly Dictionary<string, List<char>> cache = new(ChipDescription.NameComparer);

		public static List<char> ForDrawing(string chipName, ChipLibrary lib, int frame)
		{
			if (frame != cachedFrame) { cache.Clear(); cachedFrame = frame; }
			if (cache.TryGetValue(chipName, out List<char> keys)) return keys;
			ChipDescription Resolve(string n)
			{
				try { return lib.GetChipDescriptionForSim(n); }
				catch (Exception) { return null; }
			}
			keys = Collect(Resolve(chipName), Resolve);
			cache[chipName] = keys;
			return keys;
		}

		public static void Reset() { cache.Clear(); cachedFrame = -1; }
	}
}
