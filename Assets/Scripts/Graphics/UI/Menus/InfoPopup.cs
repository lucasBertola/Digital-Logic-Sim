using DLS.Game;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Generic read-only popup: a message + a single OK button.
    public static class InfoPopup
    {
        static string message;

        public static void Open(string msg)
        {
            message = msg;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.Info);
        }

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();

            Color textCol = new(0.85f, 0.9f, 1f);
            Vector2 textPos = UI.Centre + Vector2.up * 5;

            using (UI.BeginBoundsScope(true))
            {
                Draw.ID panelID = UI.ReservePanel();
                Draw.ID textBGPanelID = UI.ReservePanel();
                UI.DrawText(message, DrawSettings.ActiveUITheme.FontRegular, DrawSettings.ActiveUITheme.FontSizeRegular, textPos, Anchor.TextCentre, textCol);
                UI.ModifyPanel(textBGPanelID, Bounds2D.Grow(UI.PrevBounds, 1.5f), ColHelper.MakeCol(0.11f));

                Vector2 topLeft = UI.PrevBounds.BottomLeft + Vector2.down * 1;
                float width = UI.PrevBounds.Width;
                bool close = UI.Button("OK", MenuHelper.Theme.ButtonTheme, topLeft, new Vector2(width, DrawSettings.ButtonHeight), true, false, false, Anchor.TopLeft);
                close |= KeyboardShortcuts.CancelShortcutTriggered || KeyboardShortcuts.ConfirmShortcutTriggered;

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (close) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
            }
        }
    }
}
