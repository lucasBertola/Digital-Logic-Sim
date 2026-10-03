using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Right-click > EDIT MEMORY on a custom chip: its memory, word by word. The grouping of cells into words comes
	// from a cached layout, or from Claude (MemoryLayoutClaude) — always verified by simulation first (MemoryLayout).
	// Saved words are written into the running simulation; the chip's memory state is stored at the next save
	// (ChipDescription.MemoryState), and the chip is marked modified so the user knows to save.
	public static class MemoryEditMenu
	{
		enum Phase { Analysing, Verifying, Ready, Error }

		static Phase phase;
		static string message;
		static SubChipInstance chip;
		static ChipDescription chipDesc;
		static SimChip liveChip;
		static List<MemoryBank> banks = new();
		static int bankIndex;
		static string[][] texts;       // per bank, per word: the field text
		static uint[][] original;      // values in the circuit (followed live, see Follow)
		static float[][] changedAt;    // when the circuit last changed a word (the row flashes)
		static UIHandle[] rowIDs = Array.Empty<UIHandle>();
		static UIHandle ID_scroll, ID_mode;
		static int mode; // 0 hex, 1 decimal, 2 binary
		static readonly string[] ModeNames = { "Hexadecimal", "Decimal", "Binary" };
		static int attempts;
		static string lastJson;
		const int MaxAttempts = 3;
		static Bounds2D scrollBounds;
		static int lastFocusedRow;          // where PASTE starts (the word last clicked / typed in)
		static string lastRaw = "";         // focused field text last frame (to see a separator being deleted)
		static string pasteStatus;
		static float pasteStatusUntil;

		public static void OnMenuOpened()
		{
			banks = new List<MemoryBank>();
			bankIndex = 0;
			attempts = 0;
			lastJson = null;
			message = null;
			chip = ContextMenu.interactionContext as SubChipInstance;
			Project project = Project.ActiveProject;
			project?.StopFastMode(); // the memory edited is the gates
			if (chip == null || project == null) { Fail("Nothing to edit."); return; }
			chipDesc = project.chipLibrary.GetChipDescriptionForSim(chip.Description.Name) ?? chip.Description;
			liveChip = null;
			SimChip root = project.rootSimChip;
			if (root != null) { (bool ok, SimChip c) = root.TryGetSubChipFromID(chip.ID); if (ok) liveChip = c; }
			if (liveChip == null) { Fail("The simulation of this chip is not running."); return; }
			ID_scroll = new UIHandle("MEM_scroll", chip.ID);
			ID_mode = new UIHandle("MEM_mode", 0);

			bool hasCells = MemoryLayout.FindCells(liveChip).Count > 0;
			if (!hasCells)
			{
				banks = MemoryLayout.BuiltinBanks(liveChip, chipDesc, project.chipLibrary);
				if (banks.Count == 0) Fail($"\"{chipDesc.Name}\" holds no memory.");
				else Ready();
				return;
			}

			var cache = MemoryLayout.LoadCache(project.description.ProjectName);
			string hash = MemoryLayout.StructureHash(chipDesc, project.chipLibrary);
			if (cache.TryGetValue(chipDesc.Name, out var entry) && entry.hash == hash && entry.rules != null)
			{
				var b = MemoryLayout.Banks(liveChip, chipDesc, entry.rules, entry.polarity, project.chipLibrary, out string err);
				if (b != null) { banks = b; Ready(); return; }
			}
			// the types inside are already known from another analysis (Registre B after Registre A): no Claude call
			MemoryRules known = MemoryLayout.RulesFromCache(chipDesc, cache, project.chipLibrary, out var knownPol);
			if (known != null)
			{
				string err = null;
				var pol = new Dictionary<string, int[]>(knownPol);
				project.RunWithSimulationPaused(() => err = MemoryLayout.Verify(chipDesc.Name, known, project.chipLibrary, pol));
				if (err == null)
				{
					var b = MemoryLayout.Banks(liveChip, chipDesc, known, pol, project.chipLibrary, out string e2);
					if (b != null)
					{
						cache[chipDesc.Name] = new MemoryLayout.CacheEntry { hash = hash, rules = known, polarity = pol };
						MemoryLayout.SaveCache(project.description.ProjectName, cache);
						banks = b;
						Ready();
						return;
					}
				}
			}
			phase = Phase.Analysing;
			attempts = 1;
			MemoryLayoutClaude.Start(chipDesc, project.chipLibrary, project.description.ProjectName, null, null);
		}

		static void Fail(string msg) { phase = Phase.Error; message = msg; }

		// word-wraps a message so it never runs off the screen
		public static string Wrap(string text, int width)
		{
			var sb = new StringBuilder();
			foreach (string para in (text ?? "").Split('\n'))
			{
				int col = 0;
				foreach (string word in para.Split(' '))
				{
					if (col > 0 && col + 1 + word.Length > width) { sb.Append('\n'); col = 0; }
					else if (col > 0) { sb.Append(' '); col++; }
					sb.Append(word);
					col += word.Length;
				}
				sb.Append('\n');
			}
			return sb.ToString().TrimEnd('\n');
		}

		static void Ready()
		{
			phase = Phase.Ready;
			Project.ActiveProject.RunWithSimulationPaused(InitWords);
			SelectBank(0);
		}

		static void InitWords()
		{
			texts = new string[banks.Count][];
			original = new uint[banks.Count][];
			changedAt = new float[banks.Count][];
			bankIndex = 0;
			// the fields still hold the texts of the previous chip edited: forget them, or SelectBank's StoreFields
			// would copy them over this chip's values (register B opened after register A showed A's value)
			rowIDs = Array.Empty<UIHandle>();
			for (int b = 0; b < banks.Count; b++)
			{
				original[b] = new uint[banks[b].WordCount];
				changedAt[b] = Enumerable.Repeat(-10f, banks[b].WordCount).ToArray();
				texts[b] = new string[banks[b].WordCount];
				for (int w = 0; w < banks[b].WordCount; w++) { original[b][w] = banks[b].Read(w); texts[b][w] = Format(original[b][w], banks[b].Bits); }
			}
		}

		// test hooks: the real field / mode code paths without drawing (bench, main thread)
		public static void OpenForTests(List<MemoryBank> b) { banks = b; InitWords(); SelectBank(0); }
		public static void SetModeForTests(int m) => SetMode(m);
		public static string FieldText(int w) => UI.GetInputFieldState(rowIDs[w]).text;
		public static void TypeForTests(int w, string text) => UI.GetInputFieldState(rowIDs[w]).SetText(text, false);

		static void SetMode(int newMode)
		{
			if (newMode == mode) return;
			StoreFields();
			ConvertAll(mode, newMode);
			mode = newMode;
			LoadFields();
		}

		static void SelectBank(int index)
		{
			StoreFields();
			bankIndex = index;
			int n = banks[bankIndex].WordCount;
			rowIDs = new UIHandle[n];
			for (int i = 0; i < n; i++)
			{
				rowIDs[i] = new UIHandle("MEM_row_" + bankIndex, i);
				UI.GetInputFieldState(rowIDs[i]).SetText(texts[bankIndex][i], false);
			}
		}

		static void LoadFields()
		{
			for (int i = 0; i < rowIDs.Length; i++)
			{
				InputFieldState st = UI.GetInputFieldState(rowIDs[i]);
				st.SetText(texts[bankIndex][i], st.focused);
			}
		}

		static void StoreFields()
		{
			if (texts == null || bankIndex >= texts.Length || rowIDs.Length != texts[bankIndex].Length) return;
			for (int i = 0; i < rowIDs.Length; i++) texts[bankIndex][i] = UI.GetInputFieldState(rowIDs[i]).text;
		}

		// ---------------------------------------------------------------- analysis (Claude + verification)

		static void UpdateAnalysis()
		{
			if (phase != Phase.Analysing) return;
			MemoryLayoutClaude.Poll();
			if (MemoryLayoutClaude.State == MemoryLayoutClaude.Status.Failed) { Fail(MemoryLayoutClaude.Error); return; }
			if (MemoryLayoutClaude.State != MemoryLayoutClaude.Status.Done) return;

			Project project = Project.ActiveProject;
			ChipLibrary lib = project.chipLibrary;
			lastJson = MemoryLayoutClaude.ResultJson;
			MemoryLayoutClaude.Cancel();
			MemoryRules rules = MemoryLayout.ParseRules(lastJson, out string error);
			var polarity = new Dictionary<string, int[]>();
			if (rules != null)
			{
				if (rules.banks.Count == 0) error = "no bank listed";
				else
					project.RunWithSimulationPaused(() =>
					{
						foreach (string type in MemoryLayout.BankTypes(chipDesc, rules, lib))
						{
							error = MemoryLayout.Verify(type, rules, lib, polarity);
							if (error != null) break;
						}
					});
			}
			if (error == null)
			{
				var b = MemoryLayout.Banks(liveChip, chipDesc, rules, polarity, lib, out string err);
				if (b == null) error = err;
				else
				{
					var cache = MemoryLayout.LoadCache(project.description.ProjectName);
					cache[chipDesc.Name] = new MemoryLayout.CacheEntry { hash = MemoryLayout.StructureHash(chipDesc, lib), rules = rules, polarity = polarity };
					MemoryLayout.SaveCache(project.description.ProjectName, cache);
					banks = b;
					Ready();
					return;
				}
			}
			if (attempts >= MaxAttempts) { Fail($"Could not work out the memory layout of \"{chipDesc.Name}\" ({attempts} attempts). Last check: {error}"); return; }
			attempts++;
			message = error;
			MemoryLayoutClaude.Start(chipDesc, lib, project.description.ProjectName, lastJson, error);
		}

		// ---------------------------------------------------------------- drawing

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			UpdateAnalysis();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			if (phase != Phase.Ready)
			{
				string dots = new string('.', 1 + (int)(Time.time * 2) % 3);
				string text = phase switch
				{
					Phase.Analysing => $"Claude is working out how the memory of \"{chipDesc?.Name}\" is organised{dots}" + (attempts > 1 ? $"\nAttempt {attempts} of {MaxAttempts}: the previous proposal was rejected by the simulation check, Claude is correcting it." : ""),
					Phase.Verifying => "Checking the layout by simulation" + dots,
					_ => message ?? "Error"
				};
				using (UI.BeginBoundsScope(true))
				{
					Draw.ID panel = UI.ReservePanel();
					UI.DrawText(Wrap(text, 90), theme.FontRegular, theme.FontSizeRegular, UI.Centre + Vector2.up * 2, Anchor.Centre, phase == Phase.Error ? Color.yellow : Color.white);
					bool close = UI.Button(phase == Phase.Error ? "CLOSE" : "CANCEL", theme.MainMenuButtonTheme, UI.PrevBounds.CentreBottom + Vector2.down * 3, true, Anchor.CentreTop);
					MenuHelper.DrawReservedMenuPanel(panel, UI.GetCurrentBoundsScope());
					if (close || KeyboardShortcuts.CancelShortcutTriggered) { MemoryLayoutClaude.Cancel(); UIDrawer.SetActiveMenu(UIDrawer.MenuType.None); }
				}
				return;
			}

			FollowCircuit();
			MemoryBank bank = banks[bankIndex];
			scrollBounds = Bounds2D.CreateFromCentreAndSize(UI.Centre, new Vector2(UI.Width * 0.4f, UI.Height * 0.8f));
			UI.DrawScrollView(ID_scroll, scrollBounds.TopLeft, scrollBounds.Size, 0, Anchor.TopLeft, theme.ScrollTheme, DrawRow, bank.WordCount);

			Vector2 sideSize = new(UI.Width * 0.22f, UI.Height * 0.8f);
			Vector2 sideTopLeft = scrollBounds.TopRight + Vector2.right * (UI.Width * 0.04f);
			Draw.ID sidePanel = UI.ReservePanel();
			using (UI.BeginBoundsScope(true))
			{
				const float spacing = 0.75f;
				UI.DrawText($"{chipDesc.Name} — memory", theme.FontBold, theme.FontSizeRegular, sideTopLeft, Anchor.TopLeft, Color.white);
				Vector2 pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				string[] bankNames = banks.Select(b => $"{b.Name}  ({b.WordCount} x {b.Bits} bits)").ToArray();
				int newBank = UI.WheelSelector(bankIndex, bankNames, pos, new Vector2(sideSize.x, DrawSettings.SelectorWheelHeight), MenuHelper.Theme.OptionsWheel, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				int newMode = UI.WheelSelector(ID_mode, ModeNames, pos, new Vector2(sideSize.x, DrawSettings.SelectorWheelHeight), MenuHelper.Theme.OptionsWheel, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				int copyPaste = MenuHelper.DrawButtonPair("COPY ALL", "PASTE", pos, sideSize.x, false);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				bool clear = UI.Button("CLEAR ALL", MenuHelper.Theme.ButtonTheme, pos, new Vector2(sideSize.x, 0), true, false, true, Anchor.TopLeft);
				if (pasteStatus != null && Time.time < pasteStatusUntil)
				{
					pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
					UI.DrawText(pasteStatus, MenuHelper.Theme.FontRegular, MenuHelper.Theme.FontSizeRegular * 0.8f, pos, Anchor.TopLeft, new Color(0.6f, 0.9f, 0.6f));
				}
				pos = UI.PrevBounds.BottomLeft + Vector2.down * (spacing * 2);
				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(pos, sideSize.x, false, false);
				MenuHelper.DrawReservedMenuPanel(sidePanel, UI.GetCurrentBoundsScope());

				SetMode(newMode);
				if (newBank != bankIndex) SelectBank(newBank);
				if (copyPaste == 0) CopyAll();
				else if (copyPaste == 1) PasteFrom(lastFocusedRow);
				else if (clear) for (int i = 0; i < rowIDs.Length; i++) UI.GetInputFieldState(rowIDs[i]).SetText(Format(0, bank.Bits), false);

				if (result == MenuHelper.CancelConfirmResult.Cancel || KeyboardShortcuts.CancelShortcutTriggered) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				else if (result == MenuHelper.CancelConfirmResult.Confirm) { Save(); UIDrawer.SetActiveMenu(UIDrawer.MenuType.None); }
			}
		}

		static void DrawRow(Vector2 topLeft, float width, int index, bool isLayoutPass)
		{
			const float height = 2.5f;
			Vector2 size = new(width, height);
			Bounds2D bounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, size);
			if (bounds.Overlaps(scrollBounds) && !isLayoutPass && index < rowIDs.Length)
			{
				InputFieldState state = UI.GetInputFieldState(rowIDs[index]);
				InputFieldTheme t = MenuHelper.Theme.ChipNameInputField;
				t.fontSize = MenuHelper.Theme.FontSizeRegular;
				t.bgCol = state.focused ? new Color(0.33f, 0.55f, 0.34f) : index % 2 == 0 ? ColHelper.MakeCol(0.17f) : ColHelper.MakeCol(0.13f);
				float flash = 1 - (Time.time - changedAt[bankIndex][index]) / 0.6f; // the circuit just changed this word
				if (flash > 0) t.bgCol = Color.Lerp(t.bgCol, new Color(0.55f, 0.47f, 0.15f), flash);
				t.focusBorderCol = Color.clear;
				UI.InputField(rowIDs[index], t, topLeft, size, "0", Anchor.TopLeft, 6, Validate);
				if (state.focused)
				{
					if (lastFocusedRow != index) { lastFocusedRow = index; lastRaw = state.text; }
					// several lines in the clipboard: they fill this word and the next ones (the field itself refuses them)
					// (one line the field refuses — a comment after the value, binary while hex is shown — goes this way too)
					string clip = (InputHelper.GetClipboardContents() ?? "").Trim();
					if (InputHelper.CtrlIsHeld && InputHelper.IsKeyDownThisFrame(KeyboardShortcuts.Physical(KeyCode.V)) && (clip.Contains('\n') || !FitsWord(clip, banks[bankIndex].Bits, mode)))
						PasteFrom(index);
					// separators placed by themselves while typing (binary: every 8 bits)
					(string grouped, int caret) = Regroup(state.text, state.cursorBeforeCharIndex, lastRaw, banks[bankIndex].Bits, mode);
					if (grouped != state.text) { state.SetText(grouped, true); state.SetCursorIndex(caret); }
					lastRaw = state.text;
				}
				string label = index.ToString().PadLeft(banks[bankIndex].WordCount.ToString().Length, '0') + ":";
				UI.DrawText(label, MenuHelper.Theme.FontBold, MenuHelper.Theme.FontSizeRegular, bounds.CentreLeft + Vector2.right * 0.52f, Anchor.TextCentreLeft, ColHelper.MakeCol(0.4f));
				// Enter / Tab: next word, Shift: previous
				if (state.focused && (KeyboardShortcuts.ConfirmShortcutTriggered || InputHelper.IsKeyDownThisFrame(KeyCode.Tab)))
				{
					int next = index + (InputHelper.ShiftIsHeld ? -1 : 1);
					if (next >= 0 && next < rowIDs.Length) { state.SetFocus(false); UI.GetInputFieldState(rowIDs[next]).SetFocus(true); }
				}
			}
			UI.OverridePreviousBounds(bounds);
		}

		static bool Validate(string s) => FitsWord(s, banks[bankIndex].Bits, mode);

		// What a field of a word of this width accepts while typing: digits of the mode, no more digits than the
		// widest value, and a value that fits (8-bit word: FF / 255 / 11111111 at most).
		public static bool FitsWord(string s, int bits, int m)
		{
			s = (s ?? "").Replace(" ", ""); // separators are display only
			foreach (char c in s)
			{
				if (m == 0 && !Uri.IsHexDigit(c)) return false;
				if (m == 1 && !char.IsDigit(c)) return false;
				if (m == 2 && c is not ('0' or '1')) return false;
			}
			uint max = bits >= 32 ? uint.MaxValue : (uint)((1UL << bits) - 1);
			return s.Length <= Format(max, bits, m).Replace(" ", "").Length && TryParse(s, bits, m, out _);
		}

		// The words follow the circuit while the editor is open (a clock edge that loads a register shows up at
		// once). A word the user has edited (its text no longer says the circuit's old value) is left alone: SAVE
		// writes it. Returns the words whose text was updated.
		public static List<(int bank, int word)> Follow(List<MemoryBank> banks, uint[][] original, string[][] texts, int m)
		{
			var changed = new List<(int, int)>();
			for (int b = 0; b < banks.Count; b++)
				for (int w = 0; w < banks[b].WordCount; w++)
				{
					uint live = banks[b].Read(w);
					if (live == original[b][w]) continue;
					bool edited = !TryParse(texts[b][w], banks[b].Bits, m, out uint typed) || typed != original[b][w];
					original[b][w] = live;
					if (edited) continue;
					texts[b][w] = Format(live, banks[b].Bits, m);
					changed.Add((b, w));
				}
			return changed;
		}

		// (reads the live slots without pausing the simulation: at worst a value one step old, shown next frame)
		static void FollowCircuit()
		{
			StoreFields();
			foreach ((int b, int w) in Follow(banks, original, texts, mode))
			{
				changedAt[b][w] = Time.time;
				if (b != bankIndex || w >= rowIDs.Length) continue;
				InputFieldState st = UI.GetInputFieldState(rowIDs[w]);
				st.SetText(texts[b][w], st.focused);
			}
		}

		// ---------------------------------------------------------------- values

		static string Format(uint v, int bits) => Format(v, bits, mode);

		static string Format(uint v, int bits, int m) => m switch
		{
			0 => v.ToString("X").PadLeft((bits + 3) / 4, '0'),
			1 => v.ToString(),
			_ => Group(Convert.ToString(v, 2).PadLeft(bits, '0'), bits, m)
		};

		// A pretty separator every 8 bits in binary for words wider than a byte, counted from the right so it falls on
		// the byte boundaries ("00000001 00100011 01000101"). Display only: typing never needs it.
		public static string Group(string digits, int bits, int m)
		{
			digits = (digits ?? "").Replace(" ", "");
			if (m != 2 || bits <= 8 || digits.Length <= 8) return digits;
			var sb = new StringBuilder();
			for (int i = 0; i < digits.Length; i++)
			{
				if (i > 0 && (digits.Length - i) % 8 == 0) sb.Append(' ');
				sb.Append(digits[i]);
			}
			return sb.ToString();
		}

		// The field after a keystroke, separators put back: the caret stays after the same digit. Deleting a separator
		// (backspace / delete on it) deletes the digit next to it instead, otherwise it would come straight back.
		public static (string text, int caret) Regroup(string text, int caret, string previous, int bits, int m)
		{
			text ??= "";
			string digits = text.Replace(" ", "");
			int digitsBefore = 0;
			for (int i = 0; i < Math.Min(caret, text.Length); i++) if (text[i] != ' ') digitsBefore++;
			string prevDigits = (previous ?? "").Replace(" ", "");
			if (previous != null && text.Length < previous.Length && digits == prevDigits && digits.Length > 0)
			{
				int at = Math.Max(0, Math.Min(digitsBefore, digits.Length) - 1); // a separator went: the digit before it goes
				if (digitsBefore == 0) at = 0;
				digits = digits.Remove(at, 1);
				digitsBefore = at;
			}
			string grouped = Group(digits, bits, m);
			int newCaret = 0, seen = 0;
			while (newCaret < grouped.Length && seen < digitsBefore) { if (grouped[newCaret] != ' ') seen++; newCaret++; }
			return (grouped, newCaret);
		}

		public static bool TryParse(string s, int bits, int m, out uint v)
		{
			v = 0;
			s = (s ?? "").Replace(" ", "").Replace("\t", "").Trim();
			if (s.Length == 0) return true;
			try
			{
				ulong x = m switch { 0 => Convert.ToUInt64(s, 16), 1 => ulong.Parse(s), _ => Convert.ToUInt64(s, 2) };
				ulong mask = bits >= 32 ? uint.MaxValue : (1UL << bits) - 1;
				if (x > mask) return false;
				v = (uint)x;
				return true;
			}
			catch (Exception) { return false; }
		}

		static void ConvertAll(int from, int to)
		{
			for (int b = 0; b < banks.Count; b++)
				for (int w = 0; w < texts[b].Length; w++)
					texts[b][w] = ConvertText(texts[b][w], banks[b].Bits, from, to);
		}

		// a typed value shown in another base (decimal 3 -> binary 00000011 on an 8-bit word)
		public static string ConvertText(string text, int bits, int from, int to)
		{
			return TryParse(text, bits, from, out uint v) ? Format(v, bits, to) : text;
		}

		static void CopyAll()
		{
			var sb = new StringBuilder();
			for (int i = 0; i < rowIDs.Length; i++) sb.AppendLine(UI.GetInputFieldState(rowIDs[i]).text);
			InputHelper.CopyToClipboard(sb.ToString());
		}

		// The clipboard's lines, one word each (empty lines at the end ignored). Stops at the first line that is not a
		// value of this width in this base, and says which.
		public static List<uint> ParsePasted(string clipboard, int bits, int m, out string error)
		{
			error = null;
			var values = new List<uint>();
			var lines = (clipboard ?? "").Replace("\r", "").Split('\n').ToList();
			while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
			for (int i = 0; i < lines.Count; i++)
			{
				string l = StripComment(lines[i]);
				if (l.Length == 0) continue; // a blank or comment-only line is not a word
				if (!TryPastedValue(l, bits, m, out uint v)) { error = $"line {i + 1} (\"{l}\") is not a {bits}-bit {(m == 2 ? "binary" : ModeNames[m].ToLowerInvariant() + " or binary")} value"; break; }
				values.Add(v);
			}
			return values;
		}

		// "01010000 00000000 00000101 //range 5 dans A" -> the value part (comments: //, # and ;)
		static string StripComment(string line)
		{
			string l = line ?? "";
			foreach (string mark in new[] { "//", "#", ";" })
			{
				int k = l.IndexOf(mark, StringComparison.Ordinal);
				if (k >= 0) l = l.Substring(0, k);
			}
			return l.Trim();
		}

		// a pasted value: in the editor's base; 0x / 0b prefixes; and exactly a word of 0s and 1s is binary whatever
		// the base shown (a program written in binary pasted while the editor shows hex)
		static bool TryPastedValue(string l, int bits, int m, out uint v)
		{
			v = 0;
			string s = l.Replace(" ", "").Replace("\t", "").Replace("_", "");
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return FitsWord(s.Substring(2), bits, 0) && TryParse(s.Substring(2), bits, 0, out v);
			if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return FitsWord(s.Substring(2), bits, 2) && TryParse(s.Substring(2), bits, 2, out v);
			if (FitsWord(s, bits, m) && TryParse(s, bits, m, out v)) return true;
			if (s.Length == bits && s.All(c => c is '0' or '1')) return TryParse(s, bits, 2, out v);
			return false;
		}

		// Ctrl+V of several lines in a word, or PASTE: the lines fill that word and the following ones
		public static void PasteFrom(int start)
		{
			StoreFields();
			int bits = banks[bankIndex].Bits;
			List<uint> values = ParsePasted(InputHelper.GetClipboardContents(), bits, mode, out string error);
			int n = 0;
			for (; n < values.Count && start + n < rowIDs.Length; n++)
			{
				texts[bankIndex][start + n] = Format(values[n], bits);
				InputFieldState st = UI.GetInputFieldState(rowIDs[start + n]);
				st.SetText(texts[bankIndex][start + n], st.focused);
			}
			if (rowIDs.Length > 0 && start < rowIDs.Length && UI.GetInputFieldState(rowIDs[start]).focused) lastRaw = UI.GetInputFieldState(rowIDs[start]).text;
			pasteStatus = $"{n} word{(n == 1 ? "" : "s")} pasted from address {start}" + (n < values.Count ? $" ({values.Count - n} past the end ignored)" : "") + (error != null ? "; " + error : "");
			pasteStatusUntil = Time.time + 4f;
		}

		// test hooks
		public static void FocusForTests(int row) { lastFocusedRow = row; }
		public static void PasteForTests(string clipboard)
		{
			InputHelper.CopyToClipboard(clipboard);
			PasteFrom(lastFocusedRow);
		}

		static void Save()
		{
			StoreFields();
			Project project = Project.ActiveProject;
			SimProgram prog = project.rootSimChip?.Program;
			bool changed = false;
			project.RunWithSimulationPaused(() =>
			{
				for (int b = 0; b < banks.Count; b++)
					for (int w = 0; w < banks[b].WordCount; w++)
					{
						if (!TryParse(texts[b][w], banks[b].Bits, mode, out uint v) || v == original[b][w]) continue;
						banks[b].Write(w, v, prog);
						changed = true;
					}
			});
			if (changed && project.ViewedChip != null) project.ViewedChip.MemoryEdited = true; // the red star: save to keep it
		}

		public static void Reset()
		{
			banks = new List<MemoryBank>();
			texts = null;
			changedAt = null;
			rowIDs = Array.Empty<UIHandle>();
			MemoryLayoutClaude.Cancel();
		}
	}
}
