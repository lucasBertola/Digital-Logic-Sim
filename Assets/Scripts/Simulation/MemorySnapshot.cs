using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using DLS.Description;

namespace DLS.Simulation
{
	// Capture / restore the memory state of a simulated chip (see ChipMemoryState). Generic: no knowledge of what
	// the circuit is — every NAND output is recorded, so any latch, flip-flop or feedback loop keeps its value, and
	// the stateful builtins keep their contents. Children are walked in ID order, so a tree built again from the
	// same description gives the same order even after runtime edits reordered the live one.
	public static class MemorySnapshot
	{
		// (the ROM too: its contents can be edited from the memory editor of an enclosing chip, which only writes the
		// simulation — its own InternalData is the chip's default, the saved state carries the edit)
		static bool IsStateful(ChipType t) => t is ChipType.dev_Ram_8Bit or ChipType.Rom_256x16 or ChipType.DisplayRGB or ChipType.DisplayDot or ChipType.Pulse or ChipType.LcdDem122032;

		static void Walk(SimChip chip, StringBuilder sig, List<SimChip> nands, List<SimChip> stateful)
		{
			SimChip[] children = (SimChip[])chip.SubChips.Clone();
			Array.Sort(children, (a, b) => a.ID.CompareTo(b.ID));
			foreach (SimChip c in children)
			{
				if (c == null) continue;
				if (c.IsBuiltin)
				{
					if (c.ChipType == ChipType.Nand && c.OutputPins.Length > 0) { nands.Add(c); sig.Append('N').Append(c.ID).Append(';'); }
					else if (IsStateful(c.ChipType)) { stateful.Add(c); sig.Append('S').Append((int)c.ChipType).Append(':').Append(c.ID).Append(':').Append(c.InternalState.Length).Append(';'); }
				}
				else
				{
					sig.Append('(').Append(c.ID).Append(';');
					Walk(c, sig, nands, stateful);
					sig.Append(')');
				}
			}
		}

		static string Sha(string s)
		{
			using SHA1 sha = SHA1.Create();
			return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
		}

		public static ChipMemoryState Capture(SimChip chip)
		{
			var sig = new StringBuilder();
			var nands = new List<SimChip>();
			var stateful = new List<SimChip>();
			Walk(chip, sig, nands, stateful);
			if (nands.Count == 0 && stateful.Count == 0) return null;

			byte[] bits = new byte[(nands.Count + 7) / 8];
			for (int i = 0; i < nands.Count; i++)
				if ((nands[i].OutputPins[0].State & 1) != 0) bits[i >> 3] |= (byte)(1 << (i & 7));

			var mems = new string[stateful.Count];
			for (int i = 0; i < stateful.Count; i++)
			{
				var sb = new StringBuilder(stateful[i].InternalState.Length * 8);
				foreach (uint w in stateful[i].InternalState) sb.Append(w.ToString("x8"));
				mems[i] = sb.ToString();
			}

			return new ChipMemoryState { Hash = Sha(sig.ToString()), Gates = ToHex(bits), Memories = mems };
		}

		// Returns false (and changes nothing) when the chip's structure is not the one the state was captured from.
		public static bool Apply(SimChip chip, ChipMemoryState state)
		{
			if (state == null || string.IsNullOrEmpty(state.Hash)) return false;
			var sig = new StringBuilder();
			var nands = new List<SimChip>();
			var stateful = new List<SimChip>();
			Walk(chip, sig, nands, stateful);
			if (Sha(sig.ToString()) != state.Hash) return false;

			byte[] bits = FromHex(state.Gates ?? "");
			if (bits.Length * 8 < nands.Count) return false;
			string[] mems = state.Memories ?? Array.Empty<string>();
			if (mems.Length != stateful.Count) return false;

			for (int i = 0; i < nands.Count; i++)
				nands[i].OutputPins[0].State = (uint)((bits[i >> 3] >> (i & 7)) & 1); // driven 0 / 1
			for (int i = 0; i < stateful.Count; i++)
			{
				uint[] words = stateful[i].InternalState;
				string hex = mems[i];
				if (hex.Length != words.Length * 8) continue;
				for (int w = 0; w < words.Length; w++) words[w] = Convert.ToUInt32(hex.Substring(w * 8, 8), 16);
			}
			return true;
		}

		static string ToHex(byte[] b)
		{
			var sb = new StringBuilder(b.Length * 2);
			foreach (byte x in b) sb.Append(x.ToString("x2"));
			return sb.ToString();
		}

		static byte[] FromHex(string s)
		{
			if (s.Length % 2 != 0) return Array.Empty<byte>();
			var b = new byte[s.Length / 2];
			for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
			return b;
		}
	}
}
