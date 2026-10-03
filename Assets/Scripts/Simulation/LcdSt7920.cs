using System;

namespace DLS.Simulation
{
	// The ST7920 128 x 64 LCD module (user, 2026-10-04: "a Hailege ST7920 12864 I bought, it must react like the real
	// one"). The real module's logic pins: DB0-7, RS, R/W, E, PSB (1 = parallel, 0 = serial), RST (active LOW). The
	// bidirectional DB is an input DB + a 3-state output DB OUT, driven only while a parallel read is enabled.
	//  - Parallel (PSB = 1), 6800-style: a write is taken on the FALLING edge of E; a read drives DB OUT while E is high
	//    and its side effects (output latch loaded, address advanced) happen on the fall, so the first data read after
	//    setting an address returns the old latch (the real chip's "dummy read"). DL = 0 (function set 20h): 4-bit, the
	//    byte travels as two transfers on DB7-DB4, high nibble first (reads too).
	//  - Serial (PSB = 0): RS = CS, R/W = SID, E = SCLK, SID sampled on the RISING edge of SCLK while CS is high: a start
	//    byte 11111 RW RS 0, then each byte as two bytes (D7-D4 0000, D3-D0 0000). Bytes may follow one start byte or
	//    each carry their own (a nibble byte never starts with five 1s). CS low resets the transfer. Serial is write-only
	//    on the real chip: RW = 1 frames are ignored.
	//  - Instructions: basic (RE = 0) clear (fills DDRAM with spaces, the GRAPHICS are NOT cleared), home, entry mode,
	//    display on / cursor / blink, cursor or display shift, function set (DL, RE), CGRAM address, DDRAM address;
	//    extended (RE = 1) standby, scroll / IRAM select, reverse a line, sleep, function set (G = graphics on), scroll
	//    or IRAM address, GDRAM address (vertical then horizontal). Status read = BF (always 0: the simulation has no
	//    instruction time; the real chip is busy 72 us per instruction, 1.6 ms for a clear) + AC.
	//  - Memories (16-bit words, written / read as high byte then low byte): DDRAM text (line 1 = 00h-07h, line 2 =
	//    10h-17h, line 3 = 08h-0Fh, line 4 = 18h-1Fh; a word = two 8x16 half-width characters, or one 16x16 GB2312
	//    character when its high byte is A1h-F7h, or user character 0-3 when it is 0000h / 0002h / 0004h / 0006h), CGRAM
	//    (4 user characters of 16 rows of 16 bits), GDRAM (vertical 0-31 x horizontal 0-15 words: words 0-7 = rows 0-31,
	//    8-15 = rows 32-63; bit 15 = left), IRAM (kept, this module has no icons).
	//  - The display is the text layer XOR the graphics layer (G = 1). Power on: display OFF, RAMs hold garbage (seeded
	//    random, filled by the caller) — clear them before turning it on, as on the real module.
	//  - Unverified details, modelled the plain way: the display shift moves the text by one 16-pixel word over each
	//    32-character controller line (screen lines 1 + 3, 2 + 4); the vertical scroll moves all 64 rows; "reverse" inverts
	//    the 16-pixel band of the chosen text line.
	public static class LcdSt7920
	{
		public const int Width = 128, Height = 64;
		public const int DdRam = 0, CgRam = 64, GdRam = 128, IRam = GdRam + 64 * 16, Regs = IRam + 16;
		public const int AC = Regs, Target = Regs + 1, GdY = Regs + 2, GdX = Regs + 3, GdStage = Regs + 4, WriteHigh = Regs + 5,
			Latch = Regs + 6, ReadHigh = Regs + 7, DL = Regs + 8, RE = Regs + 9, G = Regs + 10, D = Regs + 11, C = Regs + 12,
			B = Regs + 13, ID = Regs + 14, S = Regs + 15, Shift = Regs + 16, SR = Regs + 17, Scroll = Regs + 18, Reverse = Regs + 19,
			Standby = Regs + 20, Sleep = Regs + 21, LastE = Regs + 22, NibbleHeld = Regs + 23, NibbleValue = Regs + 24,
			ReadNibble = Regs + 25, SerState = Regs + 26, SerCount = Regs + 27, SerBits = Regs + 28, SerRW = Regs + 29,
			SerRS = Regs + 30, SerHigh = Regs + 31, LastSclk = Regs + 32;
		public const int StateSize = Regs + 40;
		public const int RamWords = Regs; // what power-on fills with garbage
		public const uint TargetDd = 0, TargetCg = 1, TargetGd = 2, TargetI = 3;
		const int SerSync = 0, SerHighByte = 1, SerLowByte = 2;

		static byte[] gb;
		static byte[] Gb => gb ??= Convert.FromBase64String(St7920Font.GbBase64);

		public static void PowerOn(uint[] s) => Reset(s);

		// the RST pin (and power on): registers to their reset values; the RAMs keep their contents
		public static void Reset(uint[] s)
		{
			for (int r = Regs; r < StateSize; r++) s[r] = 0;
			s[DL] = 1; s[ID] = 1; s[WriteHigh] = 1; s[ReadHigh] = 1; s[Target] = TargetDd;
		}

		public static void Instruction(uint[] s, uint d)
		{
			d &= 0xFF;
			s[Standby] = 0; // any instruction leaves standby (the extended 01h below sets it again)
			if ((d & 0xE0) == 0x20) { FunctionSet(s, d); return; }
			if (s[RE] == 0)
			{
				if (d == 0x01)
				{
					for (int i = 0; i < 64; i++) s[DdRam + i] = 0x2020;
					s[AC] = 0; s[Target] = TargetDd; s[ID] = 1; s[Shift] = 0; s[WriteHigh] = 1; s[ReadHigh] = 1;
				}
				else if ((d & 0xFE) == 0x02) { s[AC] = 0; s[Target] = TargetDd; s[Shift] = 0; s[WriteHigh] = 1; s[ReadHigh] = 1; }
				else if ((d & 0xFC) == 0x04) { s[ID] = (d >> 1) & 1; s[S] = d & 1; }
				else if ((d & 0xF8) == 0x08) { s[D] = (d >> 2) & 1; s[C] = (d >> 1) & 1; s[B] = d & 1; }
				else if ((d & 0xF0) == 0x10)
				{
					bool right = (d & 4) != 0;
					if ((d & 8) != 0) s[Shift] = (uint)((int)s[Shift] + (right ? 1 : -1)) & 15; // display shift
					else { s[AC] = (uint)((int)s[AC] + (right ? 1 : -1)) & 0x3F; s[WriteHigh] = 1; } // cursor move
				}
				else if ((d & 0xC0) == 0x40) { s[AC] = d & 0x3F; s[Target] = TargetCg; s[WriteHigh] = 1; s[ReadHigh] = 1; }
				else if ((d & 0x80) != 0) { s[AC] = d & 0x3F; s[Target] = TargetDd; s[WriteHigh] = 1; s[ReadHigh] = 1; }
			}
			else
			{
				if (d == 0x01) s[Standby] = 1;
				else if ((d & 0xFE) == 0x02) s[SR] = d & 1;
				else if ((d & 0xFC) == 0x04) s[Reverse] ^= 1u << (int)(d & 3);
				else if ((d & 0xF8) == 0x08) s[Sleep] = (d & 4) == 0 ? 1u : 0u;
				else if ((d & 0xC0) == 0x40)
				{
					if (s[SR] != 0) s[Scroll] = d & 0x3F;
					else { s[AC] = d & 0x0F; s[Target] = TargetI; s[WriteHigh] = 1; s[ReadHigh] = 1; }
				}
				else if ((d & 0x80) != 0)
				{
					if (s[GdStage] == 0) { s[GdY] = d & 0x3F; s[GdStage] = 1; }
					else { s[GdX] = d & 0x0F; s[GdStage] = 0; s[Target] = TargetGd; s[WriteHigh] = 1; s[ReadHigh] = 1; }
				}
			}
		}

		static void FunctionSet(uint[] s, uint d)
		{
			s[DL] = (d >> 4) & 1;
			uint re = (d >> 2) & 1;
			if (re == 1) s[G] = (d >> 1) & 1; // the extended function set carries G; the basic one (RE = 0) leaves it
			if (re != s[RE]) s[GdStage] = 0;
			s[RE] = re;
			s[NibbleHeld] = 0; s[ReadNibble] = 0;
		}

		// index of the word the address counter points at, for the current target
		static int WordIndex(uint[] s) => s[Target] switch
		{
			TargetCg => CgRam + (int)(s[AC] & 0x3F),
			TargetGd => GdRam + (int)(s[GdY] & 63) * 16 + (int)(s[GdX] & 15),
			TargetI => IRam + (int)(s[AC] & 15),
			_ => DdRam + (int)(s[AC] & 0x3F)
		};

		// after the low byte of a word: the next word
		static void Advance(uint[] s)
		{
			switch (s[Target])
			{
				case TargetGd: s[GdX] = (s[GdX] + 1) & 15; break;
				case TargetI: s[AC] = (s[AC] + 1) & 15; break;
				case TargetCg: s[AC] = (s[AC] + 1) & 0x3F; break;
				default:
					s[AC] = (uint)((int)s[AC] + (s[ID] != 0 ? 1 : -1)) & 0x3F;
					if (s[S] != 0) s[Shift] = (uint)((int)s[Shift] + (s[ID] != 0 ? -1 : 1)) & 15; // entry mode S: the display follows
					break;
			}
		}

		public static void WriteData(uint[] s, uint b)
		{
			b &= 0xFF;
			int w = WordIndex(s);
			if (s[WriteHigh] != 0) { s[w] = (s[w] & 0x00FF) | (b << 8); s[WriteHigh] = 0; }
			else { s[w] = (s[w] & 0xFF00) | b; s[WriteHigh] = 1; Advance(s); }
		}

		// the falling edge of a data read: the output latch takes the next byte (high, then low and the address advances)
		public static void ReadDataEdge(uint[] s)
		{
			int w = WordIndex(s);
			if (s[ReadHigh] != 0) { s[Latch] = (s[w] >> 8) & 0xFF; s[ReadHigh] = 0; }
			else { s[Latch] = s[w] & 0xFF; s[ReadHigh] = 1; Advance(s); }
		}

		// status: D7 = busy flag (never: no instruction time in the simulation), D6-D0 = address counter
		public static uint Status(uint[] s) => s[AC] & 0x7F;

		static void Execute(uint[] s, bool rs, uint b)
		{
			if (rs) WriteData(s, b);
			else Instruction(s, b);
		}

		// One run of the gate: pins in, the DB OUT value (driven during a parallel read, else floating) out.
		// psb / rstLow already include the module's pull-ups (an unconnected PSB reads 1, an unconnected RST is not low).
		public static uint Run(uint[] s, uint db, bool rs, bool rw, bool e, bool psb, bool rstLow)
		{
			if (rstLow)
			{
				Reset(s);
				s[LastE] = e ? 1u : 0u; s[LastSclk] = e ? 1u : 0u;
				return PinState.FloatingLow;
			}
			if (!psb) { Serial(s, rs, rw, e); return PinState.FloatingLow; }

			bool fall = s[LastE] != 0 && !e;
			s[LastE] = e ? 1u : 0u;
			s[LastSclk] = s[LastE];
			bool eightBit = s[DL] != 0;
			if (fall)
			{
				if (!rw)
				{
					if (eightBit) Execute(s, rs, db);
					else if (s[NibbleHeld] == 0) { s[NibbleValue] = (db >> 4) & 0xF; s[NibbleHeld] = 1; }
					else { s[NibbleHeld] = 0; Execute(s, rs, s[NibbleValue] << 4 | ((db >> 4) & 0xF)); }
				}
				else if (eightBit) { if (rs) ReadDataEdge(s); }
				else if (s[ReadNibble] == 0) s[ReadNibble] = 1;
				else { s[ReadNibble] = 0; if (rs) ReadDataEdge(s); }
			}
			if (rw && e)
			{
				uint v = rs ? s[Latch] & 0xFF : Status(s);
				if (!eightBit) v = (s[ReadNibble] == 0 ? v >> 4 : v) & 0xF;
				return eightBit ? v : v << 4;
			}
			return PinState.FloatingLow;
		}

		// serial: CS = RS, SID = R/W, SCLK = E
		static void Serial(uint[] s, bool cs, bool sid, bool sclk)
		{
			bool rise = s[LastSclk] == 0 && sclk;
			s[LastSclk] = sclk ? 1u : 0u;
			s[LastE] = s[LastSclk];
			if (!cs) { s[SerState] = SerSync; s[SerCount] = 0; s[SerBits] = 0; return; }
			if (!rise) return;
			uint bit = sid ? 1u : 0u;
			if (s[SerState] == SerSync)
			{
				// five 1s, then RW, RS, 0
				uint n = s[SerCount];
				if (n < 5) { s[SerCount] = bit == 1 ? n + 1 : 0; return; }
				if (n == 5) { s[SerRW] = bit; s[SerCount] = 6; return; }
				if (n == 6) { s[SerRS] = bit; s[SerCount] = 7; return; }
				s[SerState] = SerHighByte; s[SerCount] = 0; s[SerBits] = 0; // the 8th bit (0)
				return;
			}
			s[SerBits] = (s[SerBits] << 1 | bit) & 0xFF;
			if (++s[SerCount] < 8) return;
			uint b = s[SerBits];
			s[SerCount] = 0; s[SerBits] = 0;
			if (s[SerState] == SerHighByte)
			{
				if ((b & 0xF8) == 0xF8) { s[SerRW] = (b >> 2) & 1; s[SerRS] = (b >> 1) & 1; return; } // a new start byte
				s[SerHigh] = b; s[SerState] = SerLowByte;
				return;
			}
			s[SerState] = SerHighByte;
			if (s[SerRW] == 0) Execute(s, s[SerRS] != 0, (s[SerHigh] & 0xF0) | (b >> 4));
		}

		// ---- the display ----

		// The pixel shown at (x, y), y = 0 the top row. blinkOn: the phase of the cursor blink (the caller's clock).
		public static bool Pixel(uint[] s, int x, int y, bool blinkOn = false)
		{
			if (s[D] == 0 || s[Standby] != 0 || s[Sleep] != 0) return false;
			int row = s[SR] != 0 ? (y + (int)s[Scroll]) & 63 : y;
			return TextPixel(s, x, row, blinkOn) ^ GraphicPixel(s, x, row);
		}

		public static bool GraphicPixel(uint[] s, int x, int row)
		{
			if (s[G] == 0) return false;
			int word = (x >> 4) + (row >= 32 ? 8 : 0);
			return ((s[GdRam + (row & 31) * 16 + word] >> (15 - (x & 15))) & 1) != 0;
		}

		// DDRAM word shown at text line 0-3, word column 0-7 (with the display shift)
		public static int TextWordIndex(uint[] s, int line, int col)
		{
			int logical = line & 1, offset = (line >> 1) * 8;
			return DdRam + logical * 16 + ((col + offset - (int)s[Shift]) & 15);
		}

		static bool TextPixel(uint[] s, int x, int row, bool blinkOn)
		{
			int line = row >> 4, r = row & 15;
			int idx = TextWordIndex(s, line, x >> 4);
			bool on = CharPixel(s, s[idx] & 0xFFFF, x & 15, r);
			bool atCursor = s[Target] == TargetDd && DdRam + (int)(s[AC] & 0x3F) == idx;
			if (atCursor && s[C] != 0 && r == 15) on = true;
			if (atCursor && s[B] != 0 && blinkOn) on = !on;
			if (((s[Reverse] >> line) & 1) != 0) on = !on;
			return on;
		}

		// pixel (cx 0-15, r 0-15) of the 16 x 16 cell of a DDRAM word
		public static bool CharPixel(uint[] s, uint word, int cx, int r)
		{
			uint hi = word >> 8, lo = word & 0xFF;
			if (word == 0 || word == 2 || word == 4 || word == 6)
				return ((s[CgRam + (int)(word >> 1) * 16 + r] >> (15 - cx)) & 1) != 0;
			if (hi >= 0xA1 && hi <= 0xF7 && lo >= 0xA1 && lo <= 0xFE)
			{
				int g = ((int)(hi - 0xA1) * St7920Font.GbColumns + (int)(lo - 0xA1)) * 32 + r * 2 + (cx >> 3);
				return ((Gb[g] >> (7 - (cx & 7))) & 1) != 0;
			}
			if (hi >= 0x80) return false; // not a character of the ROMs
			uint c = cx < 8 ? hi : lo;
			if (c >= 0x80) return false;
			return ((St7920Font.Half[c * 16 + r] >> (7 - (cx & 7))) & 1) != 0;
		}

		// what the text layer says, for Claude (half-width ASCII as is, a GB2312 character as its Unicode character
		// when the runtime knows the encoding, else as <hex>, user characters as <CGn>)
		public static string[] TextLines(uint[] s)
		{
			var lines = new string[4];
			for (int line = 0; line < 4; line++)
			{
				var sb = new System.Text.StringBuilder();
				for (int col = 0; col < 8; col++)
				{
					uint w = s[TextWordIndex(s, line, col)] & 0xFFFF;
					uint hi = w >> 8, lo = w & 0xFF;
					if (w == 0 || w == 2 || w == 4 || w == 6) sb.Append("<CG").Append(w >> 1).Append('>');
					else if (hi >= 0xA1 && hi <= 0xF7 && lo >= 0xA1 && lo <= 0xFE) sb.Append('<').Append(w.ToString("X4")).Append('>');
					else
					{
						sb.Append(hi >= 0x20 && hi < 0x7F ? (char)hi : '?');
						sb.Append(lo >= 0x20 && lo < 0x7F ? (char)lo : '?');
					}
				}
				lines[line] = sb.ToString();
			}
			return lines;
		}
	}
}
