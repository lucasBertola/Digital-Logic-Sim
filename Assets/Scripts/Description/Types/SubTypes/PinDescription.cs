using UnityEngine;

namespace DLS.Description
{
	public struct PinDescription
	{
		public string Name;
		public int ID;
		public Vector2 Position;
		public PinBitCount BitCount;
		public PinColour Colour;
		public PinValueDisplayMode ValueDisplayMode;

		// Optional layout constraints honoured by Clean Up (0 = automatic placement). See SubChipDescription.
		public int LayoutCol;
		public int LayoutRow;

		public PinDescription(string name, int id, Vector2 position, PinBitCount bitCount, PinColour colour, PinValueDisplayMode valueDisplayMode, int layoutCol = 0, int layoutRow = 0)
		{
			Name = name;
			ID = id;
			Position = position;
			BitCount = bitCount;
			Colour = colour;
			ValueDisplayMode = valueDisplayMode;
			LayoutCol = layoutCol;
			LayoutRow = layoutRow;
		}
	}

	public enum PinBitCount
	{
		Bit1 = 1,
		Bit4 = 4,
		Bit8 = 8
	}

	public enum PinColour
	{
		Red,
		Orange,
		Yellow,
		Green,
		Blue,
		Violet,
		Pink,
		White
	}

	public enum PinValueDisplayMode
	{
		Off,
		UnsignedDecimal,
		SignedDecimal,
		HEX
	}
}