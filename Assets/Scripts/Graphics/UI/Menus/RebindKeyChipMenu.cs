using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class RebindKeyChipMenu
	{
		public const string allowedChars = "1234567890QWERTYUIOPASDFGHJKLZXCVBNM";
		static SubChipInstance keyChip;
		static char chosenKey; // the binding itself (a letter, a digit, an arrow character, ' ' for the space bar)

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			Vector2 pos = UI.Centre + Vector2.up * (UI.HalfHeight * 0.25f);

			using (UI.BeginBoundsScope(true))
			{
				if (InputHelper.AnyKeyOrMouseDownThisFrame && !string.IsNullOrEmpty(InputHelper.InputStringThisFrame))
				{
					char activeChar = char.ToUpper(InputHelper.InputStringThisFrame[0]);
					if (allowedChars.Contains(activeChar)) chosenKey = activeChar;
				}
				char arrow = SimKeyboardHelper.ArrowDownThisFrame(); // arrows type no character: polled by key
				if (arrow != '\0') chosenKey = arrow;
				if (InputHelper.IsKeyDownThisFrame(KeyCode.Space)) chosenKey = SimKeyboardHelper.Space;

				UI.DrawText("Press a key to rebind\n (letter, digit, arrow or space)", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentre, Color.white * 0.8f);

				bool word = chosenKey == SimKeyboardHelper.Space;
				UI.DrawPanel(UI.PrevBounds.CentreBottom + Vector2.down, word ? new Vector2(9f, 3.5f) : Vector2.one * 3.5f, new Color(0.1f, 0.1f, 0.1f), Anchor.CentreTop);
				Vector2 centre = UI.PrevBounds.Centre;
				if (SimKeyboardHelper.IsArrow(chosenKey)) DrawArrow(centre, SimKeyboardHelper.ArrowDirection(chosenKey), 1.8f, 0.3f, Color.white);
				else UI.DrawText(chosenKey == '\0' ? "" : SimKeyboardHelper.KeyLabel(chosenKey), theme.FontBold, theme.FontSizeRegular * (word ? 1.2f : 1.5f), centre, Anchor.TextCentre, Color.white);

				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(UI.GetCurrentBoundsScope().BottomLeft, UI.GetCurrentBoundsScope().Width, true);
				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (result == MenuHelper.CancelConfirmResult.Cancel)
				{
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				else if (result == MenuHelper.CancelConfirmResult.Confirm)
				{
					Project.ActiveProject.NotifyKeyChipBindingChanged(keyChip, chosenKey);
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
			}
		}

		// an arrow in UI space (the menu draws in UI coordinates, not the circuit's: Draw.Arrow there landed off the panel)
		static void DrawArrow(Vector2 centre, Vector2 dir, float length, float thickness, Color col)
		{
			Vector2 tip = centre + dir * (length / 2), tail = centre - dir * (length / 2);
			Vector2 side = new(-dir.y, dir.x);
			float head = length * 0.45f;
			UI.DrawLine(tail, tip, thickness, col);
			UI.DrawLine(tip, tip - dir * head + side * head * 0.75f, thickness, col);
			UI.DrawLine(tip, tip - dir * head - side * head * 0.75f, thickness, col);
		}

		public static void OnMenuOpened()
		{
			keyChip = (SubChipInstance)ContextMenu.interactionContext;
			chosenKey = keyChip.InternalData != null && keyChip.InternalData.Length > 0 ? (char)keyChip.InternalData[0] : 'A';
		}
	}
}
