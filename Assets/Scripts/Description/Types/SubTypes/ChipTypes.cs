namespace DLS.Description
{
	public enum ChipType
	{
		Custom,

		// ---- Basic Chips ----
		Nand,
		TriStateBuffer,
		Clock,
		Pulse,

		// ---- Memory ----
		dev_Ram_8Bit,
		Rom_256x16,

		// ---- Displays ----
		SevenSegmentDisplay,
		DisplayRGB,
		DisplayDot,
		DisplayLED,

		// ---- Merge / Split ----
		Merge_1To4Bit,
		Merge_1To8Bit,
		Merge_4To8Bit,
		Split_4To1Bit,
		Split_8To4Bit,
		Split_8To1Bit,

		// ---- In / Out Pins ----
		In_1Bit,
		In_4Bit,
		In_8Bit,
		Out_1Bit,
		Out_4Bit,
		Out_8Bit,

		Key,

		// ---- Buses ----
		Bus_1Bit,
		BusTerminus_1Bit,
		Bus_4Bit,
		BusTerminus_4Bit,
		Bus_8Bit,
		BusTerminus_8Bit,
		
		// ---- Audio ----
		Buzzer,

		// ---- Constants ----
		// Note: enum values are serialized (ChipDescription.ChipType), so only ever APPEND new types here.
		Vcc,
		Gnd,

		// ---- Graphic LCD ----
		LcdDem122032, // DEM122032A, 122 x 32 black and white, 2 x SED1520 (Simulation/LcdDem122032.cs)

		// ---- Internal: never saved, never placed ----
		FastModel, // a module simulated by its behaviour in RUN FAST (Simulation/FastModel.cs)

		// ---- Graphic LCD (continued) ----
		LcdSt7920 // ST7920 128 x 64 module, parallel 8 / 4 bit or serial, text + graphics (Simulation/LcdSt7920.cs)

	}
}