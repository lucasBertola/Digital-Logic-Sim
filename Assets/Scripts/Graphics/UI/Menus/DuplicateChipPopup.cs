using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // DUPLICATE (right-click on a custom chip, in the bottom bar or in the scene): asks for the name of the
    // copy, then saves an identical chip under that name, stars it and opens it.
    public static class DuplicateChipPopup
    {
        const string MaxLen = "MY LONG CHIP NAME 1234";
        static string sourceName;
        static readonly UIHandle ID_NameField = new("DuplicateChip_NameField");
        static readonly string[] CancelConfirmButtonNames = { "CANCEL", "DUPLICATE" };
        static readonly bool[] interactStates = { true, true };

        public static void Open(string chipName)
        {
            sourceName = chipName;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.DuplicateChip);
            InputFieldState s = UI.GetInputFieldState(ID_NameField);
            s.SetText(ProposeName(chipName));
            s.SelectAll();
        }

        // "ALU4" -> "ALU4 2", then "ALU4 3"... (first free)
        static string ProposeName(string name)
        {
            ChipLibrary lib = Project.ActiveProject.chipLibrary;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{name} {i}";
                if (candidate.Length > MaxLen.Length) candidate = candidate.Substring(0, MaxLen.Length);
                if (!lib.HasChip(candidate)) return candidate;
            }
            return name + " copy";
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

                UI.DrawText($"Duplicate \"{sourceName}\" as", theme.FontRegular, theme.FontSizeRegular, pos + Vector2.up * 3.2f, Anchor.TextCentre, Color.white);

                InputFieldState field = UI.InputField(ID_NameField, inputTheme, pos, inputFieldSize, "", Anchor.Centre, padX / 2, ValidateNameInput, true);
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
                    Duplicate(sourceName, newName);
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
            }
        }

        static void Duplicate(string source, string newName)
        {
            Project p = Project.ActiveProject;

            // Deep copy of the LIVE description (unsaved edits included), renamed
            ChipDescription src = p.chipLibrary.GetChipDescriptionForSim(source) ?? p.chipLibrary.GetChipDescription(source);
            ChipDescription copy = Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(src));
            copy.Name = newName;

            // Same registration as a generated chip: file, library, starred, AllCustomChipNames, then open it
            Saver.SaveChip(copy, p.description.ProjectName);
            p.chipLibrary.NotifyChipSaved(copy);
            p.SetStarred(copy.Name, true, false, false);
            p.UpdateAndSaveProjectDescription();
            p.LoadDevChipOrCreateNewIfDoesntExist(copy.Name);
        }

        static bool CanConfirm(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !SaveUtils.ValidFileName(name)) return false;
            return !Project.ActiveProject.chipLibrary.HasChip(name);
        }

        static bool ValidateNameInput(string name) => name.Length <= MaxLen.Length;
    }
}
