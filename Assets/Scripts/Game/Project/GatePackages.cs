using System;
using System.Collections.Generic;

namespace DLS.Game
{
	public enum GateOp
	{
		Not,
		And,
		Or,
		Nand,
		Nor,
		Xor
	}

	// A gate function: an operator and how many inputs it takes.
	public readonly struct GateKind : IEquatable<GateKind>
	{
		public readonly GateOp Op;
		public readonly int Arity;

		public GateKind(GateOp op, int arity)
		{
			Op = op;
			Arity = arity;
		}

		public bool Equals(GateKind other) => Op == other.Op && Arity == other.Arity;
		public override bool Equals(object obj) => obj is GateKind other && Equals(other);
		public override int GetHashCode() => ((int)Op << 4) | Arity;

		public string ShortName => Op switch
		{
			GateOp.Not => "NOT",
			GateOp.And => "AND" + Arity,
			GateOp.Or => "OR" + Arity,
			GateOp.Nand => "NAND" + Arity,
			GateOp.Nor => "NOR" + Arity,
			GateOp.Xor => "XOR" + Arity,
			_ => "?"
		};

		public string Describe => Op == GateOp.Not ? "inverseur" : $"{Op.ToString().ToUpper()} a {Arity} entrees";
	}

	// One physical package the user may own: a number of identical gates in a single chip.
	// A package that is started counts as a whole package, which is what the mapper minimises.
	public class GatePackage
	{
		public readonly string Label;
		public readonly GateKind Kind;
		public readonly int GatesPerPackage;

		// Name of the DLS brick that stands for this package, e.g. "4xAND2", "6xNOT", "1xNOR2".
		public string BrickName => $"{GatesPerPackage}x{Kind.ShortName}";

		public GatePackage(int gatesPerPackage, GateOp op, int arity)
		{
			GatesPerPackage = gatesPerPackage;
			Kind = new GateKind(op, arity);
			Label = op == GateOp.Not
				? $"{gatesPerPackage} x inverseur"
				: $"{gatesPerPackage} x {op.ToString().ToUpper()} a {arity} entrees";
		}
	}

	public static class GatePackages
	{
		public static readonly GatePackage[] All =
		{
			new(4, GateOp.Nand, 2),
			new(4, GateOp.Nor, 2),
			new(6, GateOp.Not, 1),
			new(4, GateOp.And, 2),
			new(4, GateOp.Or, 2),
			new(4, GateOp.Xor, 2),
			new(3, GateOp.Nor, 3),
			new(3, GateOp.And, 3),
			new(2, GateOp.And, 4),
			new(1, GateOp.Nor, 2),

			// A package holding a single gate makes the objective degenerate into the plain gate count,
			// which is what "as few NAND as possible" means. Ticking only this line reproduces it.
			new(1, GateOp.Nand, 2)
		};

		// Index of that line, so the palette can offer it as a one-click preset.
		public const int UnitNandIndex = 10;

		// Which packages the user has. Static, so it must be reset with the rest of the app state.
		public static bool[] Selected = NewSelection();

		public static bool[] NewSelection()
		{
			bool[] selection = new bool[All.Length];
			for (int i = 0; i < selection.Length; i++) selection[i] = true;
			return selection;
		}

		public static void Reset() => Selected = NewSelection();

		public static bool AnySelected(bool[] selection)
		{
			foreach (bool b in selection)
			{
				if (b) return true;
			}

			return false;
		}

		// The distinct gate types the selection makes available. Two packages holding the same gate
		// (say 4 x NOR2 and 1 x NOR2) are one resource whose capacity is the larger of the two:
		// filling the biggest package first is always at least as good.
		public static List<(GateKind kind, int capacity)> Buckets(bool[] selection)
		{
			Dictionary<GateKind, int> capacity = new();
			for (int i = 0; i < All.Length; i++)
			{
				if (!selection[i]) continue;
				GatePackage p = All[i];
				capacity.TryGetValue(p.Kind, out int current);
				if (p.GatesPerPackage > current) capacity[p.Kind] = p.GatesPerPackage;
			}

			List<(GateKind, int)> buckets = new();
			foreach (KeyValuePair<GateKind, int> entry in capacity) buckets.Add((entry.Key, entry.Value));
			return buckets;
		}

		// How to actually buy `gateCount` gates of one kind: fill the biggest selected package as many
		// times as needed, then cover the remainder with the smallest package that still holds it.
		public static List<(GatePackage package, int count)> BillOfMaterials(bool[] selection, GateKind kind, int gateCount)
		{
			List<GatePackage> options = new();
			for (int i = 0; i < All.Length; i++)
			{
				if (selection[i] && All[i].Kind.Equals(kind)) options.Add(All[i]);
			}

			options.Sort((a, b) => b.GatesPerPackage.CompareTo(a.GatesPerPackage));
			List<(GatePackage, int)> bill = new();
			if (options.Count == 0 || gateCount <= 0) return bill;

			GatePackage biggest = options[0];
			int full = gateCount / biggest.GatesPerPackage;
			int remainder = gateCount - full * biggest.GatesPerPackage;

			if (remainder > 0)
			{
				// smallest package that still fits what is left (same package count either way)
				GatePackage fit = biggest;
				for (int i = options.Count - 1; i >= 0; i--)
				{
					if (options[i].GatesPerPackage >= remainder)
					{
						fit = options[i];
						break;
					}
				}

				if (fit == biggest) full++;
				else bill.Add((fit, 1));
			}

			if (full > 0) bill.Insert(0, (biggest, full));
			return bill;
		}
	}
}
