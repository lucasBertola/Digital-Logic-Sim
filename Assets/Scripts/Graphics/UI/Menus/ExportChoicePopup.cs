using System;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // EXPORT (LLM): choose what goes to the clipboard — the current chip (with its sub-circuits) or every
    // custom chip of the project.
    public static class ExportChoicePopup
    {
        static readonly bool[] interactStates = { true, true, true };
        static Action<string> showToast;

        public static void Open(Action<string> toast)
        {
            showToast = toast;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.ExportChoice);
        }

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();
            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
            Project p = Project.ActiveProject;
            string chipName = p.ViewedChip.ChipName;
            string chipLabel = string.IsNullOrEmpty(chipName) ? "CURRENT CHIP" : chipName.ToUpperInvariant();

            using (UI.BeginBoundsScope(true))
            {
                Draw.ID panelID = UI.ReservePanel();
                Vector2 textPos = UI.Centre + Vector2.up * 5;
                UI.DrawText("Export for an LLM: copy to the clipboard", theme.FontRegular, theme.FontSizeRegular, textPos, Anchor.TextCentre, Color.white);

                float width = Mathf.Max(30f, Draw.CalculateTextBoundsSize(chipLabel, theme.ButtonTheme.fontSize, theme.ButtonTheme.font).x + 22f);
                Vector2 topLeft = new(UI.Centre.x - width / 2f, UI.PrevBounds.Bottom - 1.2f);
                string[] names = { "CANCEL", "ALL CHIPS", chipLabel };
                int pressed = UI.HorizontalButtonGroup(names, interactStates, theme.ButtonTheme, topLeft, width, DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);

                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (KeyboardShortcuts.CancelShortcutTriggered || pressed == 0) Close();
                else if (pressed == 1) { Export(true); Close(); }
                else if (pressed == 2 || KeyboardShortcuts.ConfirmShortcutTriggered) { Export(false); Close(); }
            }
        }

        static void Export(bool all)
        {
            Project p = Project.ActiveProject;
            try
            {
                string text;
                string label;
                if (all)
                {
                    // Every custom chip of the project, each with the live (possibly unsaved) description
                    var sb = new StringBuilder();
                    sb.AppendLine($"# Projet \"{p.description.ProjectName}\" : tous les circuits");
                    sb.AppendLine();
                    string[] names = p.chipLibrary.GetAllCustomChipNames();
                    Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                    foreach (string name in names)
                    {
                        ChipDescription d = p.chipLibrary.GetChipDescriptionForSim(name);
                        if (d == null) continue;
                        sb.Append(CircuitExporter.ExportChip(d, p.chipLibrary));
                    }
                    text = sb.ToString();
                    label = $"{names.Length} circuits du projet";
                }
                else
                {
                    ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
                    text = CircuitExporter.ExportChipWithDeps(desc, p.chipLibrary, p.description.ProjectName);
                    label = (string.IsNullOrEmpty(desc.Name) ? "le circuit courant" : $"\"{desc.Name}\"") + " + ses sous-circuits";
                }

                InputHelper.CopyToClipboard(text);
                showToast?.Invoke($"Copied to clipboard: {label} ({text.Length} chars)");
            }
            catch (Exception e)
            {
                showToast?.Invoke("Export failed: " + e.Message);
            }
        }

        static void Close() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
    }
}
