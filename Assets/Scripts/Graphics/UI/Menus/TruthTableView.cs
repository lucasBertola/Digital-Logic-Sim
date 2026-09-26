using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Toggleable overlay showing the simulated truth table of the currently-viewed chip.
    public static class TruthTableView
    {
        static bool open;
        static TruthTableComputer.Result result;
        static string computedForChip;

        public static bool IsOpen => open;

        public static void Toggle()
        {
            open = !open;
            if (open) Recompute();
        }

        public static void Reset()
        {
            open = false;
            result = null;
            computedForChip = null;
        }

        static void Recompute()
        {
            Project p = Project.ActiveProject;
            if (p?.ViewedChip == null) return;

            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            computedForChip = p.ViewedChip.ChipName;
            bool seq = CircuitExporter.IsSequential(desc, p.chipLibrary);

            TruthTableComputer.Result res = null;
            p.RunWithSimulationPaused(() => res = TruthTableComputer.Compute(desc, p.chipLibrary, seq));
            result = res;
        }

        public static void Draw()
        {
            if (!open) return;
            Project p = Project.ActiveProject;
            if (p?.ViewedChip == null) return;

            // Recompute if the viewed chip changed since last computation.
            if (result == null || p.ViewedChip.ChipName != computedForChip) Recompute();
            if (result == null) return;

            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
            FontType font = theme.FontRegular;
            float fs = theme.FontSizeRegular * 0.9f;

            List<string> lines = BuildLines();

            float lineH = UI.CalculateTextSize("M", fs, font).y * 1.45f;
            float maxW = 0;
            foreach (string ln in lines) maxW = Mathf.Max(maxW, UI.CalculateTextSize(ln, fs, font).x);

            float pad = 1f;
            Vector2 panelSize = new(maxW + pad * 2, lineH * lines.Count + pad * 2);
            Vector2 panelTopLeft = new(UI.Width - panelSize.x - 1f, UI.Centre.y + panelSize.y / 2);

            UI.DrawPanel(panelTopLeft, panelSize, ColHelper.MakeCol255(28, 28, 34), Anchor.TopLeft);

            Vector2 textPos = panelTopLeft + new Vector2(pad, -pad);
            for (int i = 0; i < lines.Count; i++)
            {
                Color col = i == 0 ? new Color(0.6f, 0.8f, 1f) : Color.white;
                UI.DrawText(lines[i], font, fs, textPos, Anchor.TopLeft, col);
                textPos.y -= lineH;
            }
        }

        static List<string> BuildLines()
        {
            var r = result;
            var lines = new List<string> { "TRUTH TABLE — " + computedForChip };

            if (!r.ok)
            {
                lines.Add(r.message);
                return lines;
            }

            if (r.sequential) lines.Add("(sequential: depends on history — indicative)");

            int nIn = r.inputNames.Length, nOut = r.outputNames.Length;
            var wIn = new int[nIn];
            var wOut = new int[nOut];
            for (int i = 0; i < nIn; i++) wIn[i] = Math.Max(r.inputNames[i].Length, 3);
            for (int i = 0; i < nOut; i++) wOut[i] = Math.Max(r.outputNames[i].Length, 3);

            string header = "";
            for (int i = 0; i < nIn; i++) header += r.inputNames[i].PadLeft(wIn[i]) + " ";
            header += "|";
            for (int i = 0; i < nOut; i++) header += " " + r.outputNames[i].PadLeft(wOut[i]);
            lines.Add(header);
            lines.Add(new string('-', header.Length));

            for (int row = 0; row < r.inputRows.Count; row++)
            {
                string line = "";
                for (int i = 0; i < nIn; i++) line += r.inputRows[row][i].ToString().PadLeft(wIn[i]) + " ";
                line += "|";
                for (int i = 0; i < nOut; i++) line += " " + r.outputRows[row][i].ToString().PadLeft(wOut[i]);
                lines.Add(line);
            }

            return lines;
        }
    }
}
