using UnityEngine;

namespace DLS.Description
{
	public struct SubChipDescription
	{
		public string Name;
		public int ID; // Unique within parent chip. ID > 0
		public string Label;
		public Vector2 Position;
		public OutputPinColourInfo[] OutputPinColourInfo;

		// Arbitrary data for specific chip types:
		// ROM: stores memory contents
		// BUS: stores id of linked bus pair (origin/terminus), and horizontal flip value (0 = no, 1 = yes)
		// KEY: stores bound key code
		// Otherwise is null
		public uint[] InternalData;

		// Optional layout constraints honoured by Clean Up (0 = automatic placement, the default):
		// LayoutCol = column, bigger means further right, equal means same column.
		// LayoutRow = row, bigger means higher up; elements sharing a row value are aligned on the same line.
		public int LayoutCol;
		public int LayoutRow;

		// DISPLAY NAME (right-click): the label is drawn ON the chip, in place of its type name, all the time.
		public bool ShowLabel;

		public SubChipDescription(string name, int id, string label, Vector2 position, OutputPinColourInfo[] outputPinColInfo, uint[] internalData = null, int layoutCol = 0, int layoutRow = 0, bool showLabel = false)
		{
			Name = name;
			ID = id;
			Label = label;
			Position = position;
			OutputPinColourInfo = outputPinColInfo;
			InternalData = internalData;
			LayoutCol = layoutCol;
			LayoutRow = layoutRow;
			ShowLabel = showLabel;
		}
	}

	public struct OutputPinColourInfo
	{
		public PinColour PinColour;
		public int PinID;

		public OutputPinColourInfo(PinColour pinColour, int pinID)
		{
			PinColour = pinColour;
			PinID = pinID;
		}
	}
}