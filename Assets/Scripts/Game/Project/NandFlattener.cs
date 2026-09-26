using System.Collections.Generic;
using System.Text;
using DLS.Description;
using UnityEngine;

namespace DLS.Game
{
	// Rebuilds a chip as a single FLAT circuit that contains no custom sub-chip: every custom sub-chip is
	// inlined recursively, so what remains are only builtin primitives (NAND, plus the few builtins that
	// cannot be expressed with NANDs: clock, memory, displays, key...).
	//
	// The rewiring is a pure netlist operation: a WireDescription is just "source pin -> target pin"
	// (see Simulator.BuildSimChipRecursive, which ignores ConnectionType - that is only wire routing).
	// Each inlined chip interface pin becomes a "virtual" node that is resolved away afterwards, so a
	// signal crossing several nesting levels ends up as one direct wire.
	//
	// Buses vanish too: a bus origin is a pass-through (its sim behaviour is out = in) and a terminus is a
	// dead end, so both become virtual nodes and their taps turn into direct wires.
	public static class NandFlattener
	{
		public const int MaxComponents = 20000;

		public class Report
		{
			public int NandCount;
			public int ComponentCount;
			public readonly Dictionary<ChipType, int> OtherBuiltins = new();
			public readonly List<string> UnresolvedChips = new();
			public int DroppedWires;
			public bool AbortedTooLarge;
		}

		// A node of the flattening graph. Real = an actual pin of the flat chip (dev pin or primitive pin);
		// virtual = an inlined chip interface pin / a bus, which gets resolved away.
		class Net
		{
			public readonly PinAddress Addr;
			public readonly bool IsReal;
			public List<Net> Next;

			public Net(bool isReal, PinAddress addr)
			{
				IsReal = isReal;
				Addr = addr;
			}

			public void Link(Net other) => (Next ??= new List<Net>()).Add(other);
		}

		class Ctx
		{
			public readonly ChipLibrary Library;
			public readonly Report Report = new();
			public readonly List<SubChipDescription> SubChips = new();
			public readonly List<Net> Reals = new();

			// (scopeID, pinOwnerID, pinID) -> node. A scope is one inlined chip instance.
			public readonly Dictionary<(int, int, int), Net> Map = new();
			public readonly Dictionary<int, int> TopLevelIdRemap = new(); // for the chip's Displays
			public int nextElementID = 1;
			public int nextScopeID = 1;

			public Ctx(ChipLibrary library) => Library = library;

			public int NextID() => nextElementID++;

			public Net NewReal(PinAddress addr)
			{
				Net net = new(true, addr);
				Reals.Add(net);
				return net;
			}
		}

		public static ChipDescription Flatten(string sourceChipName, ChipLibrary library, string newName, out Report report)
		{
			report = null;
			ChipDescription src = library.GetChipDescriptionForSim(sourceChipName);
			if (src == null || src.ChipType != ChipType.Custom) return null;

			Ctx ctx = new(library);
			report = ctx.Report;

			// ---- The chip keeps its own interface (fresh IDs so nothing can collide) ----
			PinDescription[] inputPins = CopyInterface(src.InputPins, ctx);
			PinDescription[] outputPins = CopyInterface(src.OutputPins, ctx);

			InlineBody(src, 0, ctx);
			if (ctx.Report.AbortedTooLarge) return null;

			// ---- Resolve every signal down to direct pin-to-pin wires ----
			List<WireDescription> wires = new();
			HashSet<(int, int, int, int)> emitted = new();
			HashSet<Net> visited = new();

			foreach (Net source in ctx.Reals)
			{
				if (source.Next == null) continue;
				visited.Clear();
				CollectRealTargets(source, source, visited, wires, emitted);
			}

			ChipDescription flat = new()
			{
				DLSVersion = Main.DLSVersion.ToString(),
				Name = newName,
				NameLocation = src.NameLocation,
				ChipType = ChipType.Custom,
				Colour = src.Colour,
				InputPins = inputPins,
				OutputPins = outputPins,
				SubChips = ctx.SubChips.ToArray(),
				Wires = wires.ToArray(),
				Displays = RemapDisplays(src.Displays, ctx)
			};
			flat.Size = Vector2.Max(SubChipInstance.CalculateMinChipSize(inputPins, outputPins, newName), src.Size);

			return flat;
		}

		static PinDescription[] CopyInterface(PinDescription[] pins, Ctx ctx)
		{
			if (pins == null) return System.Array.Empty<PinDescription>();
			PinDescription[] copy = new PinDescription[pins.Length];

			for (int i = 0; i < pins.Length; i++)
			{
				PinDescription p = pins[i];
				int id = ctx.NextID();
				copy[i] = new PinDescription(p.Name, id, p.Position, p.BitCount, p.Colour, p.ValueDisplayMode, p.LayoutCol, p.LayoutRow);
				// A dev pin is addressed as (ownerID = the pin own ID, pinID = 0).
				ctx.Map[(0, p.ID, 0)] = ctx.NewReal(new PinAddress(id, 0));
			}

			return copy;
		}

		// Walks one chip level: instantiates its primitives, inlines its custom sub-chips, records its wires.
		// The interface pins of desc must already be registered in scope by the caller.
		static void InlineBody(ChipDescription desc, int scope, Ctx ctx)
		{
			if (desc.SubChips != null)
			{
				foreach (SubChipDescription sub in desc.SubChips)
				{
					if (ctx.Report.AbortedTooLarge) return;

					ChipDescription subDesc = ctx.Library.GetChipDescriptionForSim(sub.Name);
					if (subDesc == null)
					{
						if (!ctx.Report.UnresolvedChips.Contains(sub.Name)) ctx.Report.UnresolvedChips.Add(sub.Name);
						continue;
					}

					if (subDesc.ChipType == ChipType.Custom) InlineCustom(subDesc, sub, scope, ctx);
					else if (ChipTypeHelper.IsBusType(subDesc.ChipType)) InlineBus(subDesc, sub, scope, ctx);
					else PlacePrimitive(subDesc, sub, scope, ctx);
				}
			}

			if (desc.Wires == null) return;

			foreach (WireDescription wire in desc.Wires)
			{
				Net source = Lookup(scope, wire.SourcePinAddress, ctx);
				Net target = Lookup(scope, wire.TargetPinAddress, ctx);
				if (source == null || target == null) ctx.Report.DroppedWires++;
				else source.Link(target);
			}
		}

		static void InlineCustom(ChipDescription subDesc, SubChipDescription sub, int scope, Ctx ctx)
		{
			int childScope = ctx.nextScopeID++;

			// The instance interface pins are virtual: the parent sees them as (sub.ID, pinID), the child
			// as its own dev pins (pinID, 0). Both views share one node, which is what welds the two levels.
			RegisterInterface(subDesc.InputPins);
			RegisterInterface(subDesc.OutputPins);
			InlineBody(subDesc, childScope, ctx);

			void RegisterInterface(PinDescription[] pins)
			{
				if (pins == null) return;
				foreach (PinDescription p in pins)
				{
					Net net = new(false, default);
					ctx.Map[(childScope, p.ID, 0)] = net;
					ctx.Map[(scope, sub.ID, p.ID)] = net;
				}
			}
		}

		// Bus origin: out = in, so both pins are the same node. Terminus: a dead end (no outgoing link).
		static void InlineBus(ChipDescription subDesc, SubChipDescription sub, int scope, Ctx ctx)
		{
			Net net = new(false, default);
			if (subDesc.InputPins != null)
			{
				foreach (PinDescription p in subDesc.InputPins) ctx.Map[(scope, sub.ID, p.ID)] = net;
			}

			if (subDesc.OutputPins != null)
			{
				foreach (PinDescription p in subDesc.OutputPins) ctx.Map[(scope, sub.ID, p.ID)] = net;
			}
		}

		static void PlacePrimitive(ChipDescription subDesc, SubChipDescription sub, int scope, Ctx ctx)
		{
			if (ctx.SubChips.Count >= MaxComponents)
			{
				ctx.Report.AbortedTooLarge = true;
				return;
			}

			int id = ctx.NextID();
			int index = ctx.SubChips.Count;

			ctx.SubChips.Add(new SubChipDescription(
				sub.Name,
				id,
				sub.Label,
				// Rough spread so nothing is stacked at the origin; Clean Up lays it out properly afterwards.
				new Vector2(index % 24 * 3f, index / 24 * -2f),
				sub.OutputPinColourInfo == null ? null : (OutputPinColourInfo[])sub.OutputPinColourInfo.Clone(),
				(uint[])sub.InternalData?.Clone()
			));

			RegisterPins(subDesc.InputPins);
			RegisterPins(subDesc.OutputPins);

			if (scope == 0) ctx.TopLevelIdRemap[sub.ID] = id;

			ctx.Report.ComponentCount++;
			if (subDesc.ChipType == ChipType.Nand) ctx.Report.NandCount++;
			else
			{
				ctx.Report.OtherBuiltins.TryGetValue(subDesc.ChipType, out int n);
				ctx.Report.OtherBuiltins[subDesc.ChipType] = n + 1;
			}

			void RegisterPins(PinDescription[] pins)
			{
				if (pins == null) return;
				foreach (PinDescription p in pins) ctx.Map[(scope, sub.ID, p.ID)] = ctx.NewReal(new PinAddress(id, p.ID));
			}
		}

		static Net Lookup(int scope, PinAddress addr, Ctx ctx)
		{
			// A chip own (dev) pins are addressed as (ownerID = pin ID, pinID = 0), a sub-chip pin as
			// (ownerID = sub-chip ID, pinID = pin ID), so one exact lookup covers both.
			return ctx.Map.TryGetValue((scope, addr.PinOwnerID, addr.PinID), out Net net) ? net : null;
		}

		// Walks forward through the virtual nodes until real pins are reached, emitting one direct wire each.
		static void CollectRealTargets(Net source, Net current, HashSet<Net> visited, List<WireDescription> wires, HashSet<(int, int, int, int)> emitted)
		{
			if (current.Next == null) return;

			foreach (Net next in current.Next)
			{
				if (!visited.Add(next)) continue;

				if (next.IsReal)
				{
					if (emitted.Add((source.Addr.PinOwnerID, source.Addr.PinID, next.Addr.PinOwnerID, next.Addr.PinID)))
					{
						wires.Add(new WireDescription
						{
							SourcePinAddress = source.Addr,
							TargetPinAddress = next.Addr,
							ConnectionType = WireConnectionType.ToPins,
							ConnectedWireIndex = -1,
							ConnectedWireSegmentIndex = -1,
							Points = new Vector2[2]
						});
					}
				}
				else CollectRealTargets(source, next, visited, wires, emitted);
			}
		}

		static DisplayDescription[] RemapDisplays(DisplayDescription[] displays, Ctx ctx)
		{
			if (displays == null || displays.Length == 0) return null;

			List<DisplayDescription> kept = new();
			foreach (DisplayDescription d in displays)
			{
				// Only a display sitting directly on the chip survives: one exposed through a custom sub-chip
				// no longer has an owner once that sub-chip is inlined away.
				if (ctx.TopLevelIdRemap.TryGetValue(d.SubChipID, out int id)) kept.Add(new DisplayDescription(id, d.Position, d.Scale));
			}

			return kept.Count == 0 ? null : kept.ToArray();
		}

		public static string DescribeResult(string newName, Report report)
		{
			StringBuilder sb = new();
			sb.Append("Module \"").Append(newName).Append("\" cree.\n\n");
			sb.Append("NAND : ").Append(report.NandCount);

			if (report.OtherBuiltins.Count > 0)
			{
				sb.Append("\n\nAutres composants natifs (non reductibles) :");
				foreach (KeyValuePair<ChipType, int> kv in report.OtherBuiltins)
				{
					sb.Append('\n').Append(ChipTypeHelper.GetName(kv.Key)).Append(" x ").Append(kv.Value);
				}
			}

			if (report.UnresolvedChips.Count > 0)
			{
				sb.Append("\n\nBriques introuvables (ignorees) :");
				foreach (string name in report.UnresolvedChips) sb.Append('\n').Append(name);
			}

			if (report.DroppedWires > 0) sb.Append("\n\nFils ignores (pin introuvable) : ").Append(report.DroppedWires);

			return sb.ToString();
		}
	}
}
