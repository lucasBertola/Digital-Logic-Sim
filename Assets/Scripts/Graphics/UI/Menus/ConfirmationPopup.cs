using System;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Generic "are you sure?" popup: a message + a confirm callback (Cancel / Confirm buttons).
    public static class ConfirmationPopup
    {
        static string message;
        static Action onConfirm;

        public static void Open(string msg, Action confirm)
        {
            message = msg;
            onConfirm = confirm;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.Confirmation);
        }

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();

            Color textCol = new(1, 0.6f, 0.45f);
            Vector2 textPos = UI.Centre + Vector2.up * 5;

            using (UI.BeginBoundsScope(true))
            {
                Draw.ID panelID = UI.ReservePanel();
                Draw.ID textBGPanelID = UI.ReservePanel();
                UI.DrawText(message, DrawSettings.ActiveUITheme.FontRegular, DrawSettings.ActiveUITheme.FontSizeRegular, textPos, Anchor.TextCentre, textCol);
                UI.ModifyPanel(textBGPanelID, Bounds2D.Grow(UI.PrevBounds, 1.5f), ColHelper.MakeCol(0.11f));

                Vector2 topLeft = UI.PrevBounds.BottomLeft + Vector2.down * 1;
                MenuHelper.CancelConfirmResult button = MenuHelper.DrawCancelConfirmButtons(topLeft, UI.PrevBounds.Width, false);

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (button == MenuHelper.CancelConfirmResult.Cancel)
                {
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
                else if (button == MenuHelper.CancelConfirmResult.Confirm)
                {
                    Action confirm = onConfirm;
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                    confirm?.Invoke();
                }
            }
        }
    }
}
