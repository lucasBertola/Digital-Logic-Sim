using System.Collections.Generic;
using System.Text;
using DLS.Description;

namespace DLS.Game
{
	// Counts how many NAND gates a chip is built from, by recursively expanding every custom sub-chip
	// down to the builtin primitives. Reads the LIVE description of each chip (ChipLibrary.GetChipDescriptionForSim)
	// so unsaved edits are taken into account, exactly like the simulation does.
	public static class NandCounter
	{
		public class Counts
		{
			public int Nand;
			public readonly Dictionary<ChipType, int> OtherBuiltins = new();

			public void Accumulate(Counts other)
			{
				Nand += other.Nand;
				foreach (KeyValuePair<ChipType, int> kv in other.OtherBuiltins)
				{
					OtherBuiltins.TryGetValue(kv.Key, out int n);
					OtherBuiltins[kv.Key] = n + kv.Value;
				}
			}

			public void AddBuiltin(ChipType type)
			{
				OtherBuiltins.TryGetValue(type, out int n);
				OtherBuiltins[type] = n + 1;
			}
		}

		public static Counts Count(string chipName, ChipLibrary library, out List<string> unresolvedChips)
		{
			unresolvedChips = new List<string>();
			Dictionary<string, Counts> cache = new(ChipDescription.NameComparer);
			HashSet<string> inProgress = new(ChipDescription.NameComparer);
			return CountRecursive(chipName, library, cache, inProgress, unresolvedChips);
		}

		static Counts CountRecursive(string chipName, ChipLibrary library, Dictionary<string, Counts> cache, HashSet<string> inProgress, List<string> unresolved)
		{
			if (cache.TryGetValue(chipName, out Counts cached)) return cached;
			if (!inProgress.Add(chipName)) return new Counts(); // cycle guard (shouldn't happen: a chip can't contain itself)

			Counts counts = new();
			ChipDescription desc = library.GetChipDescriptionForSim(chipName);

			if (desc == null)
			{
				if (!unresolved.Contains(chipName)) unresolved.Add(chipName);
			}
			else if (desc.ChipType == ChipType.Nand)
			{
				counts.Nand = 1;
			}
			else if (desc.ChipType != ChipType.Custom)
			{
				if (IsRealComponent(desc.ChipType)) counts.AddBuiltin(desc.ChipType);
			}
			else if (desc.SubChips != null)
			{
				foreach (SubChipDescription sub in desc.SubChips)
				{
					counts.Accumulate(CountRecursive(sub.Name, library, cache, inProgress, unresolved));
				}
			}

			inProgress.Remove(chipName);
			cache[chipName] = counts;
			return counts;
		}

		// Buses and merge/split are pure wiring: they cost no gate, so they're not reported as components.
		static bool IsRealComponent(ChipType type)
		{
			if (ChipTypeHelper.IsBusType(type)) return false;
			return type is not (ChipType.Merge_1To4Bit or ChipType.Merge_1To8Bit or ChipType.Merge_4To8Bit
				or ChipType.Split_4To1Bit or ChipType.Split_8To4Bit or ChipType.Split_8To1Bit);
		}

		public static string BuildReport(string chipName, ChipLibrary library)
		{
			Counts counts = Count(chipName, library, out List<string> unresolved);

			StringBuilder sb = new();
			sb.Append(chipName.ToUpper()).Append('\n').Append('\n');
			sb.Append("NAND au total : ").Append(counts.Nand);

			if (counts.OtherBuiltins.Count > 0)
			{
				sb.Append("\n\nAutres composants natifs :");
				List<ChipType> types = new(counts.OtherBuiltins.Keys);
				types.Sort((a, b) => counts.OtherBuiltins[b].CompareTo(counts.OtherBuiltins[a]));
				foreach (ChipType type in types)
				{
					sb.Append('\n').Append(ChipTypeHelper.GetName(type)).Append(" x ").Append(counts.OtherBuiltins[type]);
				}
			}

			if (unresolved.Count > 0)
			{
				sb.Append("\n\nBriques introuvables (non comptees) :");
				foreach (string name in unresolved) sb.Append('\n').Append(name);
			}

			return sb.ToString();
		}
	}
}
