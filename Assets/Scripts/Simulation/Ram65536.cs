namespace DLS.Simulation
{
	// Native 64 KB static RAM (user, 2026-10-04: "a NATIVE RAM, super fast, RAM65536, working like the existing ones").
	// Same behaviour as the user's RAMs built from latches (RAM4 ... RAM16384): while Cs = 1 and We = 1 the byte on D_in is
	// written at the address (level, not an edge: the latches are transparent), D_out shows the addressed byte while
	// Cs = 1 and Oe = 1 and floats otherwise (8 floating bits, like their 3-state output buffers), so it can sit on a bus.
	// With We and Oe both on, D_out shows what is being written, as through their transparent latches.
	// State: 65 536 bytes packed 4 per uint (byte a = word a >> 2, bits (a & 3) * 8) — a quarter of the size in the saved
	// memory state.
	public static class Ram65536
	{
		public const int Size = 65536, Words = Size / 4;
		const uint Floating8 = 0xFFu << 16;

		public static uint Read(uint[] mem, int a) => (mem[(a >> 2) & (Words - 1)] >> ((a & 3) * 8)) & 0xFF;

		public static void WriteByte(uint[] mem, int a, uint value)
		{
			int w = (a >> 2) & (Words - 1), sh = (a & 3) * 8;
			mem[w] = (mem[w] & ~(0xFFu << sh)) | ((value & 0xFF) << sh);
		}

		// pins in (as the gate reads them), D_out out
		public static uint Run(uint[] mem, uint dIn, uint adrHigh, uint adrLow, uint we, uint oe, uint cs)
		{
			if (!PinState.FirstBitHigh(cs)) return Floating8;
			int a = (int)(((adrHigh & 0xFF) << 8) | (adrLow & 0xFF));
			if (PinState.FirstBitHigh(we)) WriteByte(mem, a, dIn);
			return PinState.FirstBitHigh(oe) ? Read(mem, a) : Floating8;
		}
	}
}
