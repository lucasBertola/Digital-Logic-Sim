using System.Linq;
using DLS.Description;

namespace DLS.Game
{
	public static class BuiltinCollectionCreator
	{
		public static StarredItem[] GetDefaultStarredList()
		{
			return new StarredItem[]
			{
				new("IN/OUT", true),
				new(ChipTypeHelper.GetName(ChipType.Nand), false)
			};
		}

		public static ChipCollection[] CreateDefaultChipCollections()
		{
			return new[]
			{
				CreateChipCollection("BASIC",
					ChipType.Nand,
					ChipType.Clock,
					ChipType.Pulse,
					ChipType.Key,
					ChipType.TriStateBuffer,
					ChipType.Vcc,
					ChipType.Gnd
				),
				CreateChipCollection("IN/OUT",
					ChipType.In_1Bit,
					ChipType.In_4Bit,
					ChipType.In_8Bit,
					ChipType.Out_1Bit,
					ChipType.Out_4Bit,
					ChipType.Out_8Bit
				),
				CreateChipCollection("MERGE/SPLIT",
					ChipType.Merge_1To4Bit,
					ChipType.Merge_1To8Bit,
					ChipType.Merge_4To8Bit,
					ChipType.Split_4To1Bit,
					ChipType.Split_8To4Bit,
					ChipType.Split_8To1Bit
				),
				CreateChipCollection("BUS",
					ChipType.Bus_1Bit,
					ChipType.Bus_4Bit,
					ChipType.Bus_8Bit
				),
				CreateChipCollection("DISPLAY",
					ChipType.SevenSegmentDisplay,
					ChipType.DisplayDot,
					ChipType.DisplayRGB,
					ChipType.DisplayLED
				),
				CreateChipCollection("MEMORY",
					ChipType.Rom_256x16
				)
			};
		}

		// Builtin chips added to the fork *after* the default collections were authored. Existing projects
		// store their own collection list in ProjectDescription, so a new builtin would otherwise never
		// appear in their bottom bar / library. Each entry is added to the named collection (created if
		// missing) only when the chip is absent from *every* collection, so a chip the user deliberately
		// removed is never resurrected.
		static readonly (ChipType type, string collection)[] LateAddedBuiltins =
		{
			(ChipType.Vcc, "BASIC"),
			(ChipType.Gnd, "BASIC")
		};

		// ProjectDescription is a struct, but ChipCollections is a reference type: mutating the list (and the
		// collections in it) through a copy still affects the caller's description. Returns true if modified.
		public static bool AddMissingLateBuiltins(ProjectDescription description)
		{
			if (description.ChipCollections == null) return false;
			bool modified = false;

			foreach ((ChipType type, string collectionName) in LateAddedBuiltins)
			{
				string chipName = ChipTypeHelper.GetName(type);
				bool present = description.ChipCollections.Any(c => c.Chips != null && c.Chips.Any(n => ChipDescription.NameMatch(n, chipName)));
				if (present) continue;

				ChipCollection collection = description.ChipCollections.FirstOrDefault(c => ChipDescription.NameMatch(c.Name, collectionName));
				if (collection == null)
				{
					collection = new ChipCollection(collectionName);
					description.ChipCollections.Add(collection);
				}

				collection.Chips.Add(chipName);
				modified = true;
			}

			return modified;
		}

		static ChipCollection CreateChipCollection(string name, params ChipType[] chipTypes)
		{
			return new ChipCollection(name, chipTypes.Select(t => ChipTypeHelper.GetName(t)).ToArray());
		}
	}
}