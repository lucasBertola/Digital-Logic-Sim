using System;
using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Everything that calls Claude goes through Require (user, 2026-10-06): with a key (ANTHROPIC_API_KEY, or one typed
	// here before) the action runs at once; without one, this popup asks for it instead. SAVE stores it for the next
	// times (AskClaude.SaveKey) and runs the action it was opened for; CANCEL (or Esc) does nothing at all.
	public static class ApiKeyPopup
	{
		static Action onReady;
		static string error;
		static readonly UIHandle ID_KeyField = new("ApiKey_Field");
		static readonly string[] ButtonNames = { "CANCEL", "SAVE" };
		static readonly bool[] interactStates = { true, true };
		const string FieldWidthText = "sk-ant-api03-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

		public static void Require(Action action)
		{
			if (AskClaude.HasKey()) { action?.Invoke(); return; }
			onReady = action;
			error = null;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.ApiKey);
			InputFieldState s = UI.GetInputFieldState(ID_KeyField);
			s.SetText("");
		}

		public static void Reset() => onReady = null;

		public static void DrawMenu()
		{
			UI.DrawFullscreenPanel(DrawSettings.ActiveUITheme.MenuBackgroundOverlayCol);
			const float spacing = 0.8f;
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			InputFieldTheme inputTheme = theme.ChipNameInputField;
			Draw.ID panelID = UI.ReservePanel();

			using (UI.BeginBoundsScope(true))
			{
				Vector2 unpaddedSize = Draw.CalculateTextBoundsSize(FieldWidthText, inputTheme.fontSize, inputTheme.font);
				const float padX = 2.25f;
				Vector2 inputFieldSize = unpaddedSize + new Vector2(padX, 2.25f);
				Vector2 pos = UI.Centre + Vector2.up * 5;

				UI.DrawText("Claude needs an Anthropic API key", theme.FontRegular, theme.FontSizeRegular, pos + Vector2.up * 5.6f, Anchor.TextCentre, Color.white);
				UI.DrawText("Paste it (Ctrl+V): it is saved for the next times.", theme.FontRegular, theme.FontSizeRegular * 0.85f, pos + Vector2.up * 3.4f, Anchor.TextCentre, new Color(0.75f, 0.8f, 0.9f));

				InputFieldState field = UI.InputField(ID_KeyField, inputTheme, pos, inputFieldSize, "sk-ant-...", Anchor.Centre, padX / 2, null, true);
				Bounds2D fieldBounds = UI.PrevBounds;
				string key = field.text.Trim();

				interactStates[1] = key.Length > 0;
				Vector2 buttonsTopLeft = fieldBounds.BottomLeft + Vector2.down * spacing;
				int buttonIndex = UI.HorizontalButtonGroup(ButtonNames, interactStates, theme.ButtonTheme, buttonsTopLeft, fieldBounds.Width, DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);
				if (error != null) UI.DrawText(error, theme.FontRegular, theme.FontSizeRegular * 0.85f, UI.PrevBounds.CentreBottom + Vector2.down * 1.5f, Anchor.TextCentre, new Color(1f, 0.5f, 0.45f));

				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (KeyboardShortcuts.CancelShortcutTriggered || buttonIndex == 0) Cancel();
				else if ((KeyboardShortcuts.ConfirmShortcutTriggered || buttonIndex == 1) && key.Length > 0) Confirm(key);
			}
		}

		// CANCEL: as if nothing had been asked
		public static void Cancel()
		{
			onReady = null;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		// SAVE: the key kept for the next times, then what the popup was opened for
		public static void Confirm(string key)
		{
			if (string.IsNullOrWhiteSpace(key)) return;
			if (!AskClaude.SaveKey(key)) { error = "Could not save the key."; return; }
			Action action = onReady;
			onReady = null;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			action?.Invoke();
		}
	}
}
