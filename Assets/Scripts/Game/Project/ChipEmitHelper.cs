using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.SaveSystem;
using UnityEngine;

namespace DLS.Game
{
	// Shared plumbing for the tools that generate a ChipDescription from scratch (NAND flattening,
	// NAND minimisation, gate/package mapping). Components are only given a rough grid position:
	// Clean Up does the real layout once the chip is opened.
	public static class ChipEmitHelper
	{
		public static PinDescription[] CopyPins(PinDescription[] pins, ref int nextID)
		{
			pins ??= Array.Empty<PinDescription>();
			PinDescription[] copy = new PinDescription[pins.Length];
			for (int i = 0; i < pins.Length; i++)
			{
				PinDescription p = pins[i];
				copy[i] = new PinDescription(p.Name, nextID++, p.Position, p.BitCount, p.Colour, p.ValueDisplayMode, p.LayoutCol, p.LayoutRow);
			}

			return copy;
		}

		public static PinDescription[] MakePins(string[] names, bool isInput, ref int nextID)
		{
			PinDescription[] pins = new PinDescription[names.Length];
			for (int i = 0; i < names.Length; i++)
			{
				Vector2 pos = new(isInput ? -5f : 5f, (names.Length - 1) * 0.5f - i);
				pins[i] = new PinDescription(names[i], nextID++, pos, PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off);
			}

			return pins;
		}

		// Note: OutputPinColourInfo is left null here (the caller has no library access); ChipLibrary fills
		// the default entries in when the chip is registered, so it never looks "modified" when opened.
		public static int AddSubChip(List<SubChipDescription> subChips, ref int nextID, string name, ChipType type)
		{
			int id = nextID++;
			int index = subChips.Count;
			subChips.Add(new SubChipDescription(name, id, string.Empty,
				new Vector2(index % 24 * 3f, index / 24 * -2f), null,
				DescriptionCreator.CreateDefaultInstanceData(type)));
			return id;
		}

		public static WireDescription Wire(PinAddress source, PinAddress target) =>
			new()
			{
				SourcePinAddress = source,
				TargetPinAddress = target,
				ConnectionType = WireConnectionType.ToPins,
				ConnectedWireIndex = -1,
				ConnectedWireSegmentIndex = -1,
				Points = new Vector2[2]
			};

		public static ChipDescription Assemble(string name, Color colour, NameDisplayLocation nameLocation, Vector2 minSize,
			PinDescription[] inputPins, PinDescription[] outputPins,
			List<SubChipDescription> subChips, List<WireDescription> wires)
		{
			ChipDescription chip = new()
			{
				DLSVersion = Main.DLSVersion.ToString(),
				Name = name,
				NameLocation = nameLocation,
				ChipType = ChipType.Custom,
				Colour = colour,
				InputPins = inputPins,
				OutputPins = outputPins,
				SubChips = subChips.ToArray(),
				Wires = wires.ToArray(),
				Displays = null
			};
			chip.Size = Vector2.Max(SubChipInstance.CalculateMinChipSize(inputPins, outputPins, name), minSize);
			return chip;
		}
	}
}
