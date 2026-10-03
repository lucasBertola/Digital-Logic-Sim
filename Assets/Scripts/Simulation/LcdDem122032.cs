namespace DLS.Simulation
{
	// The DEM122032A graphic LCD (122 x 32, black and white): two SED1520 controllers, E1 = left half (61 columns),
	// E2 = right half. 68-series bus as on the real module: DB0-7, A0 (0 = command / status, 1 = display data),
	// R/W (1 = read), E1 / E2 enables: a write is taken on the FALLING edge of E, a read drives DB OUT while E is high
	// and its side effects (output latch loaded, column + 1) happen on the falling edge — so the first data read after
	// setting an address returns the old latch (the "dummy read" of the real chip). RES = 1 resets (here: a level).
	//
	// State (uint[]), per controller c at c * Block: display RAM 4 pages x 80 columns (bit 0 = top pixel of the page),
	// then the registers below.
	public static class LcdDem122032
	{
		public const int Width = 122, Height = 32, HalfWidth = 61, Columns = 80, Pages = 4;
		public const int Ram = Pages * Columns;
		public const int Page = Ram, Column = Ram + 1, StartLine = Ram + 2, On = Ram + 3, AdcNormal = Ram + 4, StaticDrive = Ram + 5,
			Rmw = Ram + 6, RmwColumn = Ram + 7, Latch = Ram + 8, LastE = Ram + 9;
		public const int Block = Ram + 16;
		public const int StateSize = Block * 2;

		// power on: display OFF, page 0, column 0, start line 0, ADC normal; the display RAM holds garbage (filled by the
		// caller with the simulator's seeded random bytes), as on the real module: clear it before turning it on
		public static void PowerOn(uint[] s)
		{
			for (int c = 0; c < 2; c++)
			{
				int b = c * Block;
				for (int r = Ram; r < Block; r++) s[b + r] = 0;
				s[b + AdcNormal] = 1;
			}
		}

		static void Reset(uint[] s, int b)
		{
			s[b + StartLine] = 0;
			s[b + Column] = 0;
			s[b + Page] = 3;
			s[b + Rmw] = 0;
		}

		public static void Command(uint[] s, int c, uint d)
		{
			int b = c * Block;
			d &= 0xFF;
			if (d == 0xAE || d == 0xAF) s[b + On] = d & 1;
			else if (d >= 0xC0 && d <= 0xDF) s[b + StartLine] = d & 31;
			else if (d >= 0xB8 && d <= 0xBB) s[b + Page] = d & 3;
			else if (d < Columns) s[b + Column] = d;
			else if (d == 0xA0 || d == 0xA1) s[b + AdcNormal] = (d & 1) == 0 ? 1u : 0u;
			else if (d == 0xA4 || d == 0xA5) s[b + StaticDrive] = d & 1;
			else if (d == 0xE0) { s[b + Rmw] = 1; s[b + RmwColumn] = s[b + Column]; }
			else if (d == 0xEE) { s[b + Rmw] = 0; s[b + Column] = s[b + RmwColumn]; }
			else if (d == 0xE2) Reset(s, b);
			// 0xA8 / 0xA9 (duty 1/16, 1/32): the module is 1/32, nothing to do
		}

		public static void WriteData(uint[] s, int c, uint d)
		{
			int b = c * Block;
			s[b + (int)(s[b + Page] & 3) * Columns + (int)s[b + Column]] = d & 0xFF;
			s[b + Column] = (s[b + Column] + 1) % Columns; // a write always advances, also in read-modify-write
		}

		// the falling edge of a data read: the output latch takes the addressed byte, the column advances (not in RMW)
		public static void ReadDataEdge(uint[] s, int c)
		{
			int b = c * Block;
			s[b + Latch] = s[b + (int)(s[b + Page] & 3) * Columns + (int)s[b + Column]];
			if (s[b + Rmw] == 0) s[b + Column] = (s[b + Column] + 1) % Columns;
		}

		// status byte: D7 busy (never), D6 ADC (1 = normal), D5 ON/OFF (1 = display OFF), D4 reset (never)
		public static uint Status(uint[] s, int c)
		{
			int b = c * Block;
			return (s[b + AdcNormal] & 1) << 6 | (s[b + On] == 0 ? 1u : 0u) << 5;
		}

		public static void ResetPin(uint[] s)
		{
			Reset(s, 0);
			Reset(s, Block);
		}

		// The pixel shown at (x, y), y = 0 the TOP row: which controller, which segment, the start line scroll
		public static bool Pixel(uint[] s, int x, int y)
		{
			int c = x < HalfWidth ? 0 : 1;
			int b = c * Block;
			if (s[b + On] == 0) return false;
			if (s[b + StaticDrive] != 0) return true;
			int seg = x - c * HalfWidth;
			int col = s[b + AdcNormal] != 0 ? seg : Columns - 1 - seg;
			int line = (y + (int)s[b + StartLine]) % Height;
			return ((s[b + (line >> 3) * Columns + col] >> (line & 7)) & 1) != 0;
		}

		// One run of the gate: inputs in, the DB OUT value (driven while a read is enabled, else floating) out
		public static uint Run(uint[] s, uint db, bool a0, bool rw, bool e1, bool e2, bool res)
		{
			if (res)
			{
				ResetPin(s);
				s[LastE] = e1 ? 1u : 0u;
				s[Block + LastE] = e2 ? 1u : 0u;
				return PinState.FloatingLow;
			}
			for (int c = 0; c < 2; c++)
			{
				int b = c * Block;
				bool e = c == 0 ? e1 : e2;
				if (s[b + LastE] != 0 && !e) // falling edge: the transfer happens
				{
					if (!rw) { if (a0) WriteData(s, c, db); else Command(s, c, db); }
					else if (a0) ReadDataEdge(s, c);
				}
				s[b + LastE] = e ? 1u : 0u;
			}
			if (rw && (e1 || e2))
			{
				int c = e1 ? 0 : 1;
				return a0 ? s[c * Block + Latch] & 0xFF : Status(s, c);
			}
			return PinState.FloatingLow;
		}
	}
}
