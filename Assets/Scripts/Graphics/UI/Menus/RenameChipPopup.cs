using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Rename a chip from the bottom-bar right-click menu.
    public static class RenameChipPopup
    {
        const string MaxLen = "MY LONG CHIP NAME 1234";
        static string oldName;
        static readonly UIHandle ID_NameField = new("RenameChip_NameField");
        static readonly string[] CancelConfirmButtonNames = { "CANCEL", "CONFIRM" };
        static readonly bool[] interactStates = { true, true };

        public static void Open(string chipName)
        {
            oldName = chipName;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.RenameChip);
            InputFieldState s = UI.GetInputFieldState(ID_NameField);
            s.SetText(chipName);
            s.SelectAll();
        }

        public static void DrawMenu()
        {
            UI.DrawFullscreenPanel(DrawSettings.ActiveUITheme.MenuBackgroundOverlayCol);
            const float spacing = 0.8f;

            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
            InputFieldTheme inputTheme = theme.ChipNameInputField;
            Draw.ID panelID = UI.ReservePanel();

            using (UI.BeginBoundsScope(true))
            {
                Vector2 unpaddedSize = Draw.CalculateTextBoundsSize(MaxLen, inputTheme.fontSize, inputTheme.font);
                const float padX = 2.25f;
                Vector2 inputFieldSize = unpaddedSize + new Vector2(padX, 2.25f);
                Vector2 pos = UI.Centre + Vector2.up * 5;

                UI.DrawText("Renommer la brique", theme.FontRegular, theme.FontSizeRegular, pos + Vector2.up * 3.2f, Anchor.TextCentre, Color.white);

                InputFieldState field = UI.InputField(ID_NameField, inputTheme, pos, inputFieldSize, oldName, Anchor.Centre, padX / 2, ValidateNameInput, true);
                Bounds2D inputFieldBounds = UI.PrevBounds;
                string newName = field.text.Trim();

                interactStates[1] = CanConfirm(newName);
                Vector2 buttonsTopLeft = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
                int buttonIndex = UI.HorizontalButtonGroup(CancelConfirmButtonNames, interactStates, theme.ButtonTheme, buttonsTopLeft, inputFieldBounds.Width, DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (KeyboardShortcuts.CancelShortcutTriggered || buttonIndex == 0)
                {
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
                else if ((KeyboardShortcuts.ConfirmShortcutTriggered || buttonIndex == 1) && CanConfirm(newName))
                {
                    Project.ActiveProject.RenameChip(oldName, newName);
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
            }
        }

        static bool CanConfirm(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !SaveUtils.ValidFileName(name)) return false;
            if (ChipDescription.NameMatch(name, oldName)) return false;
            return !Project.ActiveProject.chipLibrary.HasChip(name);
        }

        static bool ValidateNameInput(string name) => name.Length <= MaxLen.Length;
    }
}
