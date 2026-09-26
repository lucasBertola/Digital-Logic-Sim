using System.Collections.Generic;
using System.Text;
using DLS.Game;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Spotlight-style command box for Claude: Space (or right-click > ASK CLAUDE) opens it, you type a request
    // (multi-line, word-wrapped, everything stays visible), Enter fires it. Claude then works in the background:
    // a small busy indicator is shown meanwhile (BottomBarUI) and a "done" toast follows.
    // Keys: Enter = send, Shift+Enter = new line, Backspace, Ctrl+V = paste, Esc = close.
    public static class QuickAskBar
    {
        const int MaxChars = 2000;
        static readonly StringBuilder text = new();
        static int openedFrame;

        public static void Open()
        {
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.QuickAsk);
            text.Clear();
            openedFrame = Time.frameCount;
        }

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();
            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
            FontType font = theme.FontRegular;
            float fontSize = theme.FontSizeRegular * 1.25f;
            float lineH = Draw.CalculateTextBoundsSize("Ag", fontSize, font).y * 1.45f;

            HandleTyping();

            // ---- layout ----
            float width = Mathf.Min(UI.Width * 0.62f, 80f);
            const float padX = 1.6f, padY = 1.2f;
            float textW = width - padX * 2;
            List<string> lines = Wrap(text.ToString(), textW, fontSize, font);
            int shownLines = Mathf.Max(3, lines.Count);
            float boxH = padY * 2 + shownLines * lineH;
            float top = UI.Height * 0.78f;
            Vector2 boxCentre = new(UI.Centre.x, top - boxH / 2f);

            // box: border, body, header line
            Color border = new(0.4f, 0.7f, 1f);
            UI.DrawPanel(boxCentre, new Vector2(width + 0.16f, boxH + 0.16f), border);
            UI.DrawPanel(boxCentre, new Vector2(width, boxH), ColHelper.MakeCol255(26, 26, 30));

            Vector2 headerPos = new(UI.Centre.x - width / 2f, top + 0.5f);
            UI.DrawText("ASK CLAUDE", theme.FontBold, theme.FontSizeRegular, headerPos, Anchor.TextCentreLeft, Color.white * 0.85f);
            UI.DrawText("Enter: send   Shift+Enter: new line   Esc: close", font, theme.FontSizeRegular * 0.8f, new Vector2(UI.Centre.x + width / 2f, top + 0.5f), Anchor.TextCentreRight, Color.white * 0.5f);

            // text (or placeholder), top-left aligned, word-wrapped
            float x = UI.Centre.x - width / 2f + padX;
            float y = top - padY - lineH / 2f;
            if (text.Length == 0)
            {
                UI.DrawText("What should Claude do on this chip?", font, fontSize, new Vector2(x, y), Anchor.TextCentreLeft, ColHelper.MakeCol255(120));
            }
            else
            {
                foreach (string line in lines)
                {
                    UI.DrawText(line, font, fontSize, new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
                    y -= lineH;
                }
                y += lineH;
            }

            // caret at the end of the last line
            if ((int)(Time.time * 2f) % 2 == 0)
            {
                string last = lines.Count > 0 ? lines[^1] : "";
                float caretX = x + (last.Length > 0 ? Draw.CalculateTextBoundsSize(last, fontSize, font).x : 0f) + 0.1f;
                UI.DrawPanel(new Vector2(caretX, y), new Vector2(0.12f, lineH * 0.7f), border);
            }

            UI.DrawText("Claude works in the background: a busy indicator shows at the top, a toast tells you when it's done. Ctrl+Z undoes everything it did.",
                font, theme.FontSizeRegular * 0.8f, new Vector2(UI.Centre.x, boxCentre.y - boxH / 2f - 0.9f), Anchor.TextCentre, Color.white * 0.5f);
        }

        static void HandleTyping()
        {
            if (KeyboardShortcuts.CancelShortcutTriggered) { Close(); return; }
            if (Time.frameCount == openedFrame) return; // the key that opened the box must not type into it

            // paste
            if (InputHelper.CtrlIsHeld && InputHelper.IsKeyDownThisFrame(KeyboardShortcuts.Physical(KeyCode.V)))
            {
                string clip = GUIUtility.systemCopyBuffer ?? "";
                foreach (char c in clip) if (c == '\n' || !char.IsControl(c)) Append(c);
            }

            foreach (char c in InputHelper.InputStringThisFrame)
            {
                if (c == '\b') { if (text.Length > 0) text.Length--; }
                else if (c == '\n' || c == '\r')
                {
                    if (InputHelper.ShiftIsHeld) Append('\n');
                    else { Send(); return; }
                }
                else if (!char.IsControl(c)) Append(c);
            }
        }

        static void Append(char c)
        {
            if (text.Length < MaxChars) text.Append(c);
        }

        static void Send()
        {
            string request = text.ToString().Trim();
            if (request.Length == 0) return;
            AskClaude.Send(request, quick: true);
            text.Clear();
            Close();
        }

        static void Close() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);

        // Word-wraps each paragraph to maxWidth (a word longer than the width is cut).
        static List<string> Wrap(string s, float maxWidth, float fontSize, FontType font)
        {
            var result = new List<string>();
            foreach (string paragraph in s.Split('\n'))
            {
                string current = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    if (Draw.CalculateTextBoundsSize(candidate, fontSize, font).x <= maxWidth || current.Length == 0)
                    {
                        current = candidate;
                        // a single word wider than the box: cut it
                        while (Draw.CalculateTextBoundsSize(current, fontSize, font).x > maxWidth && current.Length > 1)
                        {
                            int cut = current.Length - 1;
                            while (cut > 1 && Draw.CalculateTextBoundsSize(current.Substring(0, cut), fontSize, font).x > maxWidth) cut--;
                            result.Add(current.Substring(0, cut));
                            current = current.Substring(cut);
                        }
                    }
                    else
                    {
                        result.Add(current);
                        current = word;
                    }
                }
                result.Add(current);
            }
            return result;
        }
    }
}
