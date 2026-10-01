using System;
using DLS.Description;
using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class PreferencesMenu
	{
		const float entrySpacing = 0.5f;
		const float menuWidth = 55;
		const float verticalOffset = 22;

		public const int DisplayMode_Always = 0;
		public const int DisplayMode_OnHover = 1;
		public const int DisplayMode_TabToggle = 2;

		static readonly string[] PinDisplayOptions =
		{
			"Always",
			"On Hover",
			"Tab to Toggle",
		};

		static readonly string[] GridDisplayOptions =
		{
			"Off",
			"On"
		};

		static readonly string[] SnappingOptions =
		{
			"Hold Ctrl",
			"If Grid Shown",
			"Always"
		};

		static readonly string[] StraightWireOptions =
		{
			"Hold Shift",
			"If Grid Shown",
			"Always"
		};

		static readonly string[] SimSpeedOptions =
		{
			"Limited",
			"Max"
		};

		static readonly string[] SimulationStatusOptions =
		{
			"Active",
			"Paused"
		};

		static readonly Vector2 entrySize = new(menuWidth, DrawSettings.SelectorWheelHeight);
		public static readonly Vector2 settingFieldSize = new(entrySize.x / 3, entrySize.y);

		// ---- State ----
		static readonly UIHandle ID_MainPinNames = new("PREFS_MainPinNames");
		static readonly UIHandle ID_ChipPinNames = new("PREFS_ChipPinNames");
		static readonly UIHandle ID_GridDisplay = new("PREFS_GridDisplay");
		static readonly UIHandle ID_Snapping = new("PREFS_Snapping");
		static readonly UIHandle ID_StraightWires = new("PREFS_StraightWires");
		static readonly UIHandle ID_SimStatus = new("PREFS_SimStatus");
		static readonly UIHandle ID_SimFrequencyField = new("PREFS_SimTickTarget");
		static readonly UIHandle ID_SimSpeedMode = new("PREFS_SimSpeedMode");
		static readonly UIHandle ID_ClockSpeedInput = new("PREFS_ClockSpeed");

		static readonly string showGridLabel = "Show grid" + CreateShortcutString("Ctrl+G");
		static readonly string simStatusLabel = "Sim Status" + CreateShortcutString("Ctrl+Space");
		static readonly Func<string, bool> integerInputValidator = ValidateIntegerInput;

		static double simAvgTicksPerSec_delayedRefreshForUI;
		static float lastSimAvgTicksPerSecRefreshTime;
		static float lastSimTickRateSetTime;
		static ProjectDescription originalProjectDesc;
		static string currentSimSpeedString = string.Empty;
		static Color currentSimSpeedStringColour;


		public static void DrawMenu(Project project)
		{
			//HandleKeyboardShortcuts();

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();
			UpdateSimSpeedString(project);

			const int inputTextPad = 1;
			const float headerSpacing = 1.5f;
			Color labelCol = Color.white;
			Color headerCol = new(0.46f, 1, 0.54f);
			Vector2 topLeft = UI.Centre + new Vector2(-menuWidth / 2, verticalOffset);
			Vector2 labelPosCurr = topLeft;

			using (UI.BeginBoundsScope(true))
			{
				// ---- Draw settings ----
				DrawHeader("DISPLAY:");
				int mainPinNamesMode = DrawNextWheel("Show I/O pin names", PinDisplayOptions, ID_MainPinNames);
				int chipPinNamesMode = DrawNextWheel("Show chip pin names", PinDisplayOptions, ID_ChipPinNames);
				int gridDisplayMode = DrawNextWheel(showGridLabel, GridDisplayOptions, ID_GridDisplay);
				DrawHeader("EDITING:");
				int snappingMode = DrawNextWheel("Snap to grid", SnappingOptions, ID_Snapping);
				int straightWireMode = DrawNextWheel("Straight wires", StraightWireOptions, ID_StraightWires);

				DrawHeader("SIMULATION:");
				bool pauseSim = MenuHelper.LabeledOptionsWheel(simStatusLabel, labelCol, labelPosCurr, entrySize, ID_SimStatus, SimulationStatusOptions, settingFieldSize.x, true) == 1;
				AddSpacing();
				InputFieldState clockSpeedInputFieldState = MenuHelper.LabeledInputField("Steps per clock tick", labelCol, labelPosCurr, entrySize, ID_ClockSpeedInput, integerInputValidator, settingFieldSize.x, true);
				AddSpacing();
				bool maxSpeed = MenuHelper.LabeledOptionsWheel("Speed", labelCol, labelPosCurr, entrySize, ID_SimSpeedMode, SimSpeedOptions, settingFieldSize.x, true) == 1;
				AddSpacing();
				InputFieldState freqState = MenuHelper.LabeledInputField(maxSpeed ? "Steps per second (target, unused at Max)" : "Steps per second (target)", labelCol, labelPosCurr, entrySize, ID_SimFrequencyField, integerInputValidator, settingFieldSize.x, true);
				AddSpacing();
				// Draw current simulation speed
				Vector2 tickLabelRight = MenuHelper.DrawLabelSectionOfLabelInputPair(labelPosCurr, entrySize, "Steps per second (current)", labelCol * 0.75f, true);
				UI.DrawPanel(tickLabelRight, settingFieldSize, new Color(0.18f, 0.18f, 0.18f), Anchor.CentreRight);
				UI.DrawText(currentSimSpeedString, theme.FontBold, theme.FontSizeRegular, tickLabelRight + new Vector2(inputTextPad - settingFieldSize.x, 0), Anchor.TextCentreLeft, currentSimSpeedStringColour);
				AddSpacing();
				// Real clock frequency: a clock period is two transitions of "steps per clock tick" steps each
				int.TryParse(clockSpeedInputFieldState.text, out int stepsPerTickForFreq);
				double clockHz = stepsPerTickForFreq > 0 && !project.simPaused ? simAvgTicksPerSec_delayedRefreshForUI / (2.0 * stepsPerTickForFreq) : 0;
				string clockHzString = FormatHz(clockHz);
				Vector2 freqLabelRight = MenuHelper.DrawLabelSectionOfLabelInputPair(labelPosCurr, entrySize, "Clock frequency (current)", labelCol * 0.75f, true);
				UI.DrawPanel(freqLabelRight, settingFieldSize, new Color(0.18f, 0.18f, 0.18f), Anchor.CentreRight);
				UI.DrawText(clockHzString, theme.FontBold, theme.FontSizeRegular, freqLabelRight + new Vector2(inputTextPad - settingFieldSize.x, 0), Anchor.TextCentreLeft, currentSimSpeedStringColour);

				// Draw cancel/confirm buttons
				Vector2 buttonTopLeft = new(labelPosCurr.x, UI.PrevBounds.Bottom);
				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(buttonTopLeft, menuWidth, true);

				// Draw menu background
				Bounds2D menuBounds = UI.GetCurrentBoundsScope();
				MenuHelper.DrawReservedMenuPanel(panelID, menuBounds);

				// ---- Handle changes ----
				int.TryParse(clockSpeedInputFieldState.text, out int clockSpeed);

				// Parse target sim tick rate
				int.TryParse(freqState.text, out int targetSimTicksPerSecond);
				targetSimTicksPerSecond = Mathf.Max(1, targetSimTicksPerSecond);
				if (project.description.Prefs_SimTargetStepsPerSecond != targetSimTicksPerSecond || project.description.Prefs_SimMaxSpeed != maxSpeed || project.simPaused != pauseSim) lastSimTickRateSetTime = Time.time;

				// Assign changes immediately so can see them take effect in background
				project.description.Prefs_MainPinNamesDisplayMode = mainPinNamesMode;
				project.description.Prefs_ChipPinNamesDisplayMode = chipPinNamesMode;
				project.description.Prefs_GridDisplayMode = gridDisplayMode;
				project.description.Prefs_Snapping = snappingMode;
				project.description.Prefs_StraightWires = straightWireMode;
				project.description.Prefs_SimTargetStepsPerSecond = targetSimTicksPerSecond;
				project.description.Prefs_SimMaxSpeed = maxSpeed;
				project.description.Prefs_SimStepsPerClockTick = clockSpeed;
				project.description.Prefs_SimPaused = pauseSim;

				// Cancel / Confirm
				if (result == MenuHelper.CancelConfirmResult.Cancel)
				{
					// Restore original description
					project.description = originalProjectDesc;
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				else if (result == MenuHelper.CancelConfirmResult.Confirm)
				{
					// Save changes
					project.UpdateAndSaveProjectDescription(project.description);
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
			}

			return;

			int DrawNextWheel(string label, string[] options, UIHandle id)
			{
				int index = MenuHelper.LabeledOptionsWheel(label, labelCol, labelPosCurr, entrySize, id, options, settingFieldSize.x, true);
				AddSpacing();
				return index;
			}

			void DrawHeader(string text)
			{
				AddHeaderSpacing();
				UI.DrawText(text, theme.FontBold, theme.FontSizeRegular, labelPosCurr, Anchor.TextCentreLeft, headerCol);
				AddHeaderSpacing();
			}

			void AddSpacing()
			{
				labelPosCurr.y -= entrySize.y + entrySpacing;
			}

			void AddHeaderSpacing()
			{
				labelPosCurr.y -= headerSpacing;
			}
		}

		public static void OnMenuOpened()
		{
			originalProjectDesc = Project.ActiveProject.description;

			UpdateUIFromDescription();

			simAvgTicksPerSec_delayedRefreshForUI = Project.ActiveProject.simAvgTicksPerSec;
			lastSimAvgTicksPerSecRefreshTime = float.MinValue;
		}

		static void UpdateUIFromDescription()
		{
			// No need to update if not in menu
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.Preferences) return;

			ProjectDescription projDesc = Project.ActiveProject.description;

			// -- Wheels
			UI.GetWheelSelectorState(ID_MainPinNames).index = projDesc.Prefs_MainPinNamesDisplayMode;
			UI.GetWheelSelectorState(ID_ChipPinNames).index = projDesc.Prefs_ChipPinNamesDisplayMode;
			UI.GetWheelSelectorState(ID_GridDisplay).index = projDesc.Prefs_GridDisplayMode;
			UI.GetWheelSelectorState(ID_Snapping).index = projDesc.Prefs_Snapping;
			UI.GetWheelSelectorState(ID_StraightWires).index = projDesc.Prefs_StraightWires;
			UI.GetWheelSelectorState(ID_SimStatus).index = projDesc.Prefs_SimPaused ? 1 : 0;
			UI.GetWheelSelectorState(ID_SimSpeedMode).index = projDesc.Prefs_SimMaxSpeed ? 1 : 0;
			// -- Input fields
			UI.GetInputFieldState(ID_SimFrequencyField).SetText(projDesc.Prefs_SimTargetStepsPerSecond + "", false);
			UI.GetInputFieldState(ID_ClockSpeedInput).SetText(projDesc.Prefs_SimStepsPerClockTick + "", false);
		}

		public static void HandleKeyboardShortcuts()
		{
			bool inPrefsMenu = UIDrawer.ActiveMenu == UIDrawer.MenuType.Preferences;
			bool anyChange = false;

			if (KeyboardShortcuts.ToggleGridShortcutTriggered)
			{
				Project.ActiveProject.ToggleGridDisplay();
				anyChange = true;
			}

			if (KeyboardShortcuts.SimPauseToggleShortcutTriggered)
			{
				Project.ActiveProject.description.Prefs_SimPaused = !Project.ActiveProject.description.Prefs_SimPaused;
				anyChange = true;
			}

			if (anyChange)
			{
				if (inPrefsMenu) UpdateUIFromDescription();
				else Project.ActiveProject.SaveCurrentProjectDescription();
			}
		}

		static void UpdateSimSpeedString(Project project)
		{
			// Annoying if sim tick rate value flickers too much, so use slower refresh rate for ui
			// (but if sim target rate has been recently changed, update fast so doesn't feel laggy)
			bool slowModeSimUI = Time.time - lastSimTickRateSetTime > Project.SimulationPerformanceTimeWindowSec;
			const float slowModeRefreshDelay = 0.35f;
			const float fastModeRefreshDelay = 0.05f;
			float refreshDelay = slowModeSimUI ? slowModeRefreshDelay : fastModeRefreshDelay;

			if (Time.time > lastSimAvgTicksPerSecRefreshTime + refreshDelay || Project.ActiveProject.simPaused)
			{
				simAvgTicksPerSec_delayedRefreshForUI = project.simAvgTicksPerSec;
				lastSimAvgTicksPerSecRefreshTime = Time.time;
				currentSimSpeedString = project.simPaused ? "0" : FormatCount(simAvgTicksPerSec_delayedRefreshForUI);
				currentSimSpeedStringColour = GetSimFrequencyErrorCol();
			}
		}

		public static bool ValidateIntegerInput(string s)
		{
			if (string.IsNullOrEmpty(s)) return true;
			if (s.Contains(" ")) return false;
			return int.TryParse(s, out _);
		}

		public static Color GetSimFrequencyErrorCol()
		{
			Color frequencyErrorCol = new(0.3f, 0.92f, 0.32f);

			if (Project.ActiveProject.simPaused)
			{
				frequencyErrorCol = new Color(1, 1, 1, 0.35f);
			}
			else if (Project.ActiveProject.description.Prefs_SimMaxSpeed)
			{
				// no target to miss
			}
			else
			{
				int simFreqError = Mathf.RoundToInt(Project.ActiveProject.targetTicksPerSecond - (float)Project.ActiveProject.simAvgTicksPerSec);
				if (simFreqError > 10) frequencyErrorCol = new Color(0.95f, 0.25f, 0.13f);
				else if (simFreqError > 5) frequencyErrorCol = new Color(1, 0.38f, 0.27f);
				else if (simFreqError > 2) frequencyErrorCol = new Color(1, 0.7f, 0.27f);
			}

			return frequencyErrorCol;
		}

		// 999 Hz, 1.5 kHz, 2.36 MHz, 1.2 GHz: the unit changes at each thousand
		public static string FormatHz(double hz)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			string[] units = { "Hz", "kHz", "MHz", "GHz" };
			int u = 0;
			double v = Math.Max(0, hz);
			while (u < units.Length - 1 && Math.Round(v, 2) >= 1000) { v /= 1000; u++; } // (999 999 Hz shows "1 MHz", not "1000 kHz")
			return (u == 0 && v >= 10 ? v.ToString("0.#", inv) : v.ToString("0.##", inv)) + " " + units[u];
		}

		// 61043200 -> "61 043 200" (readable at a glance)
		public static string FormatCount(double v)
		{
			string s = Math.Round(Math.Max(0, v)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < s.Length; i++)
			{
				if (i > 0 && (s.Length - i) % 3 == 0) sb.Append(' ');
				sb.Append(s[i]);
			}
			return sb.ToString();
		}

		static string CreateShortcutString(string s) => UI.CreateColouredText("  " +s, new Color(1, 1, 1, 0.3f));
	}
}