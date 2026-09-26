using DLS.Game;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Spotlight-style one-line command bar for Claude: Space (or right-click > ASK CLAUDE) opens it, you type a
    // request, Enter fires it. Claude then works in the background (no chat panel): a small busy indicator is
    // shown meanwhile (BottomBarUI) and a toast sums up the result when it is done.
    public static class QuickAskBar
    {
        static readonly UIHandle ID_Input = new("QuickAskBar_Input");
        static bool focusNextFrame;

        public static void Open()
        {
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.QuickAsk);
            UI.GetInputFieldState(ID_Input).ClearText();
            focusNextFrame = true;
        }

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();
            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

            InputFieldTheme inputTheme = theme.ChipNameInputField;
            inputTheme.fontSize *= 1.45f;
            inputTheme.bgCol = ColHelper.MakeCol255(30, 30, 34);
            inputTheme.textCol = Color.white;
            inputTheme.defaultTextCol = ColHelper.MakeCol255(120);
            inputTheme.focusBorderCol = new Color(0.4f, 0.7f, 1f);

            float width = Mathf.Min(UI.Width * 0.6f, 70f);
            const float height = 4.4f;
            Vector2 centre = new(UI.Centre.x, UI.Height * 0.68f);

            Draw.ID panelID = UI.ReservePanel();
            using (UI.BeginBoundsScope(true))
            {
                InputFieldState field = UI.InputField(ID_Input, inputTheme, centre, new Vector2(width, height), "Demande a Claude...", Anchor.Centre, 1.2f, Validate, focusNextFrame);
                focusNextFrame = false;
                Bounds2D fieldBounds = UI.PrevBounds;

                string hint = "Entree : lancer   |   Echap : fermer   |   Claude travaille en arriere-plan, un bilan s'affiche a la fin";
                UI.DrawText(hint, theme.FontRegular, theme.FontSizeRegular * 0.85f, fieldBounds.CentreBottom + Vector2.down * 0.9f, Anchor.TextCentre, Color.white * 0.6f);

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                string text = field.text.Trim();
                if (KeyboardShortcuts.CancelShortcutTriggered)
                {
                    Close();
                }
                else if (KeyboardShortcuts.ConfirmShortcutTriggered && text.Length > 0)
                {
                    AskClaude.Send(text, quick: true);
                    field.ClearText();
                    Close();
                }
            }
        }

        static void Close() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);

        static bool Validate(string s) => s.Length <= 400;
    }
}
