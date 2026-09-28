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
		static uint[][] original;      // values when opened
		static UIHandle[] rowIDs = Array.Empty<UIHandle>();
		static UIHandle ID_scroll, ID_mode;
		static int mode; // 0 hex, 1 decimal, 2 binary
		static readonly string[] ModeNames = { "Hexadecimal", "Decimal", "Binary" };
		static int attempts;
		static string lastJson;
		const int MaxAttempts = 3;
		static Bounds2D scrollBounds;

		public static void OnMenuOpened()
		{
			banks = new List<MemoryBank>();
			bankIndex = 0;
			attempts = 0;
			lastJson = null;
			message = null;
			chip = ContextMenu.interactionContext as SubChipInstance;
			Project project = Project.ActiveProject;
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
			phase = Phase.Analysing;
			attempts = 1;
			MemoryLayoutClaude.Start(chipDesc, project.chipLibrary, project.description.ProjectName, null, null);
		}

		static void Fail(string msg) { phase = Phase.Error; message = msg; }

		static void Ready()
		{
			phase = Phase.Ready;
			texts = new string[banks.Count][];
			original = new uint[banks.Count][];
			Project.ActiveProject.RunWithSimulationPaused(() =>
			{
				for (int b = 0; b < banks.Count; b++)
				{
					original[b] = new uint[banks[b].WordCount];
					texts[b] = new string[banks[b].WordCount];
					for (int w = 0; w < banks[b].WordCount; w++) { original[b][w] = banks[b].Read(w); texts[b][w] = Format(original[b][w], banks[b].Bits); }
				}
			});
			SelectBank(0);
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
					Phase.Analysing => $"Claude is working out how the memory of \"{chipDesc?.Name}\" is organised{dots}" + (attempts > 1 ? $"\n(attempt {attempts}: the previous rules failed the check: {message})" : ""),
					Phase.Verifying => "Checking the layout by simulation" + dots,
					_ => message ?? "Error"
				};
				using (UI.BeginBoundsScope(true))
				{
					Draw.ID panel = UI.ReservePanel();
					UI.DrawText(text, theme.FontRegular, theme.FontSizeRegular, UI.Centre + Vector2.up * 2, Anchor.Centre, phase == Phase.Error ? Color.yellow : Color.white);
					bool close = UI.Button(phase == Phase.Error ? "CLOSE" : "CANCEL", theme.MainMenuButtonTheme, UI.PrevBounds.CentreBottom + Vector2.down * 3, true, Anchor.CentreTop);
					MenuHelper.DrawReservedMenuPanel(panel, UI.GetCurrentBoundsScope());
					if (close || KeyboardShortcuts.CancelShortcutTriggered) { MemoryLayoutClaude.Cancel(); UIDrawer.SetActiveMenu(UIDrawer.MenuType.None); }
				}
				return;
			}

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
				int copyPaste = MenuHelper.DrawButtonPair("COPY ALL", "PASTE ALL", pos, sideSize.x, false);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				bool clear = UI.Button("CLEAR ALL", MenuHelper.Theme.ButtonTheme, pos, new Vector2(sideSize.x, 0), true, false, true, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * (spacing * 2);
				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(pos, sideSize.x, false, false);
				MenuHelper.DrawReservedMenuPanel(sidePanel, UI.GetCurrentBoundsScope());

				if (newMode != mode) { StoreFields(); ConvertAll(mode, newMode); mode = newMode; SelectBank(bankIndex); }
				if (newBank != bankIndex) SelectBank(newBank);
				if (copyPaste == 0) CopyAll();
				else if (copyPaste == 1) PasteAll();
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
				t.focusBorderCol = Color.clear;
				UI.InputField(rowIDs[index], t, topLeft, size, "0", Anchor.TopLeft, 6, Validate);
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

		static bool Validate(string s)
		{
			foreach (char c in s)
			{
				if (mode == 0 && !Uri.IsHexDigit(c)) return false;
				if (mode == 1 && !char.IsDigit(c)) return false;
				if (mode == 2 && c is not ('0' or '1')) return false;
			}
			return true;
		}

		// ---------------------------------------------------------------- values

		static string Format(uint v, int bits) => Format(v, bits, mode);

		static string Format(uint v, int bits, int m) => m switch
		{
			0 => v.ToString("X").PadLeft((bits + 3) / 4, '0'),
			1 => v.ToString(),
			_ => Convert.ToString(v, 2).PadLeft(bits, '0')
		};

		public static bool TryParse(string s, int bits, int m, out uint v)
		{
			v = 0;
			s = (s ?? "").Trim();
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
					texts[b][w] = TryParse(texts[b][w], banks[b].Bits, from, out uint v) ? Format(v, banks[b].Bits, to) : texts[b][w];
		}

		static void CopyAll()
		{
			var sb = new StringBuilder();
			for (int i = 0; i < rowIDs.Length; i++) sb.AppendLine(UI.GetInputFieldState(rowIDs[i]).text);
			InputHelper.CopyToClipboard(sb.ToString());
		}

		static void PasteAll()
		{
			string[] lines = StringHelper.SplitByLine(InputHelper.GetClipboardContents());
			int bits = banks[bankIndex].Bits;
			for (int i = 0; i < Mathf.Min(rowIDs.Length, lines.Length); i++)
				if (TryParse(lines[i], bits, mode, out uint v)) UI.GetInputFieldState(rowIDs[i]).SetText(Format(v, bits), false);
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
			rowIDs = Array.Empty<UIHandle>();
			MemoryLayoutClaude.Cancel();
		}
	}
}
