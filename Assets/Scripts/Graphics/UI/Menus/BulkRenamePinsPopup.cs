using System.Collections.Generic;
using System.Linq;
using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Rename several selected elements at once: a prefix, numbered from the top down (prefix "D" on 5
    // elements -> D4, D3, D2, D1, D0, the highest one getting the biggest number). Works on input/output
    // pins (their name) and on sub-chips (their label). Opened from the right-click menu of a selected
    // element when several of the same kind are selected.
    public static class BulkRenamePinsPopup
    {
        const string MaxLen = "MY LONG PIN NAME"; // same limit as the single-pin edit menu
        static readonly UIHandle ID_PrefixField = new("BulkRenamePins_PrefixField");
        static readonly string[] CancelConfirmButtonNames = { "CANCEL", "CONFIRM" };
        static readonly bool[] interactStates = { true, true };
        static List<IMoveable> pins = new(); // the elements being renamed (pins or sub-chips), top first
        static bool renamingChips;

        static string NameOf(IMoveable e) => e is DevPinInstance p ? p.Pin.Name : e is SubChipInstance c ? c.Label : "";
        static void SetName(IMoveable e, string name)
        {
            if (e is DevPinInstance p) p.Pin.Name = name;
            else if (e is SubChipInstance c) c.Label = name;
        }

        public static void Open(IEnumerable<DevPinInstance> selectedPins) => OpenFor(selectedPins, false);
        public static void OpenChips(IEnumerable<SubChipInstance> selectedChips) => OpenFor(selectedChips, true);

        static void OpenFor(IEnumerable<IMoveable> selected, bool chips)
        {
            // Top element first: it gets the highest number
            pins = selected.OrderByDescending(p => p.Position.y).ToList();
            renamingChips = chips;
            if (pins.Count == 0) return;

            UIDrawer.SetActiveMenu(UIDrawer.MenuType.BulkRenamePins);
            InputFieldState s = UI.GetInputFieldState(ID_PrefixField);
            s.SetText(CommonPrefix(pins.Select(NameOf)));
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

                UI.DrawText($"Rename {pins.Count} {(renamingChips ? "chips (label)" : "pins")}: prefix", theme.FontRegular, theme.FontSizeRegular, pos + Vector2.up * 3.2f, Anchor.TextCentre, Color.white);

                InputFieldState field = UI.InputField(ID_PrefixField, inputTheme, pos, inputFieldSize, "", Anchor.Centre, padX / 2, ValidateInput, true);
                Bounds2D inputFieldBounds = UI.PrevBounds;
                string prefix = field.text.Trim();

                // Preview: "D4, D3, D2, D1, D0"
                string preview = string.IsNullOrEmpty(prefix) ? "" : string.Join(", ", Enumerable.Range(0, pins.Count).Select(i => NameFor(prefix, i)));
                UI.DrawText(preview, theme.FontRegular, theme.FontSizeRegular * 0.9f, inputFieldBounds.CentreBottom + Vector2.down * 0.9f, Anchor.TextCentre, Color.white * 0.75f);

                interactStates[1] = CanConfirm(prefix);
                Vector2 buttonsTopLeft = inputFieldBounds.BottomLeft + Vector2.down * (spacing + 1.2f);
                int buttonIndex = UI.HorizontalButtonGroup(CancelConfirmButtonNames, interactStates, theme.ButtonTheme, buttonsTopLeft, inputFieldBounds.Width, DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (KeyboardShortcuts.CancelShortcutTriggered || buttonIndex == 0)
                {
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
                else if ((KeyboardShortcuts.ConfirmShortcutTriggered || buttonIndex == 1) && CanConfirm(prefix))
                {
                    var renames = new List<(int, string, string)>();
                    for (int i = 0; i < pins.Count; i++)
                    {
                        string newName = NameFor(prefix, i);
                        renames.Add((pins[i].ID, NameOf(pins[i]), newName));
                        SetName(pins[i], newName);
                    }
                    Project.ActiveProject.ViewedChip.UndoController.RecordPinRenames(renames);
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
            }
        }

        // i = 0 is the top pin -> highest number
        static string NameFor(string prefix, int i) => prefix + (pins.Count - 1 - i);

        static bool CanConfirm(string prefix) => !string.IsNullOrWhiteSpace(prefix) && NameFor(prefix, 0).Length <= MaxLen.Length;

        static bool ValidateInput(string s) => s.Length <= MaxLen.Length;

        // Proposed prefix: the letters the current names start with, if they share them (e.g. "D" for D0..D3)
        static string CommonPrefix(IEnumerable<string> names)
        {
            string[] arr = names.Select(n => n ?? "").ToArray();
            if (arr.Length == 0) return "";
            string first = arr[0].TrimEnd("0123456789".ToCharArray());
            return arr.All(n => n.StartsWith(first)) ? first : "";
        }
    }
}
