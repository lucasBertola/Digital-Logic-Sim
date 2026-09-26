using System.Collections.Generic;
using DLS.Game;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
    // Right-side chat panel to ask Claude about the currently-viewed circuit. NON-blocking overlay: it
    // is drawn on top of the work area but the rest of the app stays usable (only the area under the
    // panel is inert, via UI mouse-over). Width is resizable by dragging the vertical divider on its
    // left edge. Keyboard is captured only while the input field is focused (so typing never leaks into
    // scene shortcuts). The panel sits above the bottom bar and never overlaps it.
    public static class AskClaudeMenu
    {
        static readonly UIHandle ID_Input = new("AskClaude_Input");

        static bool open;
        static bool focusNextFrame;
        static float panelWidth = 47f;
        static bool resizing;
        static float appliedViewportF; // fraction of screen width the panel currently reserves (for camera compensation)
        static float copiedFlashUntil;

        // Transcript scrolling
        static float scrollFromTop;
        static bool stickBottom = true;
        static bool draggingBar;
        static float barGrabOffset;
        const float BarW = 0.45f;

        const float Margin = 1f;
        const float Pad = 1f;
        const float MinWidth = 30f;
        const float MaxWidth = 74f;
        const float DividerGrab = 0.6f;

        public static bool IsOpen => open;

        // Force the panel-aware camera compensation to re-apply next frame (e.g. after a tool re-framed
        // the camera to full screen via Clean Up, while the panel still covers part of the view).
        public static void NotifyViewportDirty() => appliedViewportF = -1f;

        public static void Open()
        {
            open = true;
            focusNextFrame = true;
            UIDrawer.SetActiveMenu(UIDrawer.MenuType.None); // close the bottom-bar popup; overlay is non-blocking
        }

        public static void Toggle()
        {
            if (open) Close();
            else Open();
        }

        static void Close()
        {
            open = false;
            resizing = false;
            KeyboardShortcuts.TextInputActive = false;
        }

        public static void Reset()
        {
            open = false;
            resizing = false;
            focusNextFrame = false;
            appliedViewportF = 0f;
            scrollFromTop = 0f;
            stickBottom = true;
            draggingBar = false;
            KeyboardShortcuts.TextInputActive = false;
        }

        public static void Draw()
        {
            AskClaude.Poll();

            // Keep the camera framing the same content in the area not covered by the panel: when the
            // reserved fraction changes (open / resize / close), compensate zoom + pan.
            float targetF = open ? Mathf.Clamp01((Margin + Mathf.Clamp(panelWidth, MinWidth, MaxWidth)) / UI.Width) : 0f;
            if (Mathf.Abs(targetF - appliedViewportF) > 0.0005f)
            {
                CameraController.AdjustForViewportChange(appliedViewportF, targetF);
                appliedViewportF = targetF;
            }

            if (!open)
            {
                KeyboardShortcuts.TextInputActive = false;
                return;
            }

            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
            FontType font = theme.FontRegular;
            float fs = theme.FontSizeRegular * 0.85f;
            ButtonTheme btn = theme.MenuPopupButtonTheme;

            float panelRight = UI.Width - Margin;
            float panelTop = UI.Height;
            float panelBottom = BottomBarUI.barHeight; // flush above the horizontal bar, never overlapping it
            float panelH = panelTop - panelBottom;

            HandleResize(panelRight, panelBottom, panelH);
            panelWidth = Mathf.Clamp(panelWidth, MinWidth, MaxWidth);
            float panelLeft = panelRight - panelWidth;

            // Panel background + vertical resize divider on the left edge.
            UI.DrawPanel(new Vector2(panelLeft, panelTop), new Vector2(panelWidth, panelH), ColHelper.MakeCol255(26, 27, 33), Anchor.TopLeft);
            bool overDivider = MouseOverDivider(panelLeft, panelBottom, panelH);
            Color dividerCol = (resizing || overDivider) ? new Color(0.4f, 0.7f, 1f) : ColHelper.MakeCol255(70, 72, 82);
            UI.DrawPanel(new Vector2(panelLeft - 0.09f, panelBottom), new Vector2(0.18f, panelH), dividerCol, Anchor.BottomLeft);

            float contentLeft = panelLeft + Pad;
            float contentW = panelWidth - Pad * 2;

            // ---- Header: title + Effacer + Fermer ----
            float headerY = panelTop - Pad;
            UI.DrawText("ASK CLAUDE", theme.FontBold, fs * 1.15f, new Vector2(contentLeft, headerY), Anchor.TopLeft, new Color(0.6f, 0.8f, 1f));

            var closeSize = new Vector2(7.5f, 2.2f);
            var clearSize = new Vector2(9f, 2.2f);
            var copySize = new Vector2(8.5f, 2.2f);
            float btnRowTop = headerY + 0.4f;
            if (UI.Button("FERMER", btn, new Vector2(panelRight - Pad, btnRowTop), closeSize, true, false, false, Anchor.TopRight))
            {
                Close();
                return;
            }

            if (UI.Button("EFFACER", btn, new Vector2(panelRight - Pad - closeSize.x - 0.35f, btnRowTop), clearSize, AskClaude.Messages.Count > 0 || AskClaude.Waiting, false, false, Anchor.TopRight))
            {
                AskClaude.Clear();
            }

            string copyLabel = Time.time < copiedFlashUntil ? "COPIE !" : "COPIER";
            if (UI.Button(copyLabel, btn, new Vector2(panelRight - Pad - closeSize.x - clearSize.x - 0.7f, btnRowTop), copySize, AskClaude.Messages.Count > 0, false, false, Anchor.TopRight))
            {
                InputHelper.CopyToClipboard(AskClaude.BuildTranscript());
                copiedFlashUntil = Time.time + 1.5f;
            }

            // ---- Input row (bottom) ----
            var inputTheme = new InputFieldTheme
            {
                font = font,
                fontSize = fs,
                bgCol = ColHelper.MakeCol255(15),
                defaultTextCol = ColHelper.MakeCol255(95),
                textCol = Color.white,
                focusBorderCol = new Color(0.4f, 0.7f, 1f)
            };

            const float inputH = 2.4f;
            var sendSize = new Vector2(9.5f, inputH);
            float inputW = contentW - sendSize.x - 0.4f;
            float inputRowBottom = panelBottom + Pad;
            var inputPos = new Vector2(contentLeft, inputRowBottom);
            string placeholder = AskClaude.Waiting ? "Message pendant qu'il travaille (pris en compte des la fin du cycle)..." : "Pose ta question sur ce circuit...";
            InputFieldState inputState = UI.InputField(ID_Input, inputTheme, inputPos, new Vector2(inputW, inputH), placeholder, Anchor.BottomLeft, 0.5f, null, focusNextFrame);
            focusNextFrame = false;

            // A click anywhere outside the field drops its focus. The field itself only sees *unconsumed* mouse
            // downs, and a click in the scene is usually consumed by the interaction controller before the UI
            // draws, so without this the field would stay focused forever and keep every scene shortcut
            // (Ctrl+Z, Delete, Space, Ctrl+S...) blocked.
            if (inputState.focused && InputHelper.IsAnyMouseButtonDownThisFrame_IgnoreConsumed() && !UI.MouseInsideBounds(UI.PrevBounds))
            {
                inputState.SetFocus(false);
            }

            // Capture the keyboard only while the field is focused (so typing does not trigger scene shortcuts).
            KeyboardShortcuts.TextInputActive = inputState.focused;

            // Sending stays available while Claude works: the message is queued and injected at the end of
            // the current tool round (AskClaude keeps the thread intact).
            bool sendEnabled = !string.IsNullOrWhiteSpace(inputState.text);
            bool sendPressed = UI.Button(AskClaude.Waiting ? "EN FILE" : "ENVOYER", btn, new Vector2(panelRight - Pad, inputRowBottom), sendSize, sendEnabled, false, false, Anchor.BottomRight);
            bool enterPressed = inputState.focused && sendEnabled && InputHelper.IsKeyDownThisFrame(KeyCode.Return);

            if (sendPressed || enterPressed)
            {
                AskClaude.Send(inputState.text);
                inputState.ClearText();
                stickBottom = true; // jump to the bottom to follow the new exchange
            }

            // ---- Messages region (between header and input) ----
            float regionTop = headerY - 2f;
            float regionBottom = inputRowBottom + inputH + 0.6f;
            float regionH = regionTop - regionBottom;

            float charW = Mathf.Max(0.01f, UI.CalculateTextSize("M", fs, font).x);
            float lineH = UI.CalculateTextSize("M", fs, font).y * 1.5f;
            int cols = Mathf.Max(8, Mathf.FloorToInt((contentW - BarW - 0.3f) / charW)); // leave room for the scrollbar

            List<Line> lines = BuildLines(cols);

            // Live streaming answer (or "thinking" placeholder) at the bottom of the transcript.
            if (AskClaude.Waiting)
            {
                string partial = AskClaude.StreamingAnswer;
                if (!string.IsNullOrEmpty(partial))
                {
                    lines.Add(new Line("Claude :", LineKind.AssistantHead));
                    foreach (string raw in partial.Replace("\r", "").Split('\n'))
                        foreach (string wl in WrapLine(raw, cols))
                            lines.Add(new Line("  " + wl, LineKind.Body));
                    lines.Add(new Line("  _", LineKind.Status));
                }
                else
                {
                    lines.Add(new Line("Claude reflechit...", LineKind.Status));
                }
            }

            if (!string.IsNullOrEmpty(AskClaude.Error))
                foreach (string el in WrapLine(AskClaude.Error.Replace("\r", ""), cols))
                    lines.Add(new Line(el, LineKind.Error));

            if (lines.Count == 0)
                lines.Add(new Line("Demande par ex. : \"combien de NAND ?\" ou \"quel est le probleme ?\"", LineKind.Body));

            // ---- Scroll + render the transcript ----
            float totalH = lines.Count * lineH;
            float maxScroll = Mathf.Max(0f, totalH - regionH);

            // Mouse wheel (when hovering the transcript region).
            Vector2 mUI = UI.ScreenToUISpace(InputHelper.MousePos);
            bool overRegion = mUI.x >= panelLeft && mUI.x <= panelRight && mUI.y >= regionBottom && mUI.y <= regionTop;
            float wheel = InputHelper.MouseScrollDelta.y;
            if (overRegion && Mathf.Abs(wheel) > 0.0001f)
            {
                scrollFromTop -= wheel * lineH * 3f;
                stickBottom = false;
            }

            if (stickBottom) scrollFromTop = maxScroll;
            scrollFromTop = Mathf.Clamp(scrollFromTop, 0f, maxScroll);
            if (maxScroll - scrollFromTop < 0.05f) stickBottom = true;

            using (UI.CreateMaskScopeMinMax(new Vector2(panelLeft, regionBottom), new Vector2(panelRight, regionTop)))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    float topY = regionTop - (i * lineH - scrollFromTop);
                    if (topY < regionBottom - lineH || topY > regionTop + lineH) continue; // cull off-screen
                    UI.DrawText(lines[i].text, font, fs, new Vector2(contentLeft, topY), Anchor.TopLeft, ColourFor(lines[i].kind));
                }
            }

            HandleScrollbar(panelRight, regionTop, regionBottom, regionH, totalH, maxScroll);

            // Escape closes the panel.
            if (KeyboardShortcuts.CancelShortcutTriggered) Close();
        }

        static bool MouseOverDivider(float panelLeft, float panelBottom, float panelH)
        {
            Vector2 m = UI.ScreenToUISpace(InputHelper.MousePos);
            return m.x >= panelLeft - DividerGrab && m.x <= panelLeft + DividerGrab && m.y >= panelBottom && m.y <= panelBottom + panelH;
        }

        static void HandleResize(float panelRight, float panelBottom, float panelH)
        {
            float panelLeft = panelRight - Mathf.Clamp(panelWidth, MinWidth, MaxWidth);

            if (!resizing && MouseOverDivider(panelLeft, panelBottom, panelH) && InputHelper.IsMouseDownThisFrame(MouseButton.Left, consumeEvent: true))
                resizing = true;

            if (resizing)
            {
                Vector2 m = UI.ScreenToUISpace(InputHelper.MousePos);
                panelWidth = Mathf.Clamp(panelRight - m.x, MinWidth, MaxWidth);
                if (InputHelper.IsMouseUpThisFrame(MouseButton.Left) || !InputHelper.IsMouseHeld(MouseButton.Left)) resizing = false;
            }
        }

        static void HandleScrollbar(float panelRight, float regionTop, float regionBottom, float regionH, float totalH, float maxScroll)
        {
            if (maxScroll <= 0.001f) { draggingBar = false; return; } // nothing to scroll

            float barRight = panelRight - Pad;
            float barLeft = barRight - BarW;

            UI.DrawPanel(new Vector2(barLeft, regionBottom), new Vector2(BarW, regionH), ColHelper.MakeCol255(40, 41, 48), Anchor.BottomLeft);

            float thumbH = Mathf.Clamp(regionH * (regionH / totalH), 2f, regionH);
            float travel = regionH - thumbH;
            float t = maxScroll > 0f ? scrollFromTop / maxScroll : 0f;
            float thumbTop = regionTop - t * travel;

            Vector2 m = UI.ScreenToUISpace(InputHelper.MousePos);
            bool inBarX = m.x >= barLeft - 0.2f && m.x <= barRight + 0.2f;
            bool overThumb = inBarX && m.y <= thumbTop && m.y >= thumbTop - thumbH;
            bool overTrack = inBarX && m.y <= regionTop && m.y >= regionBottom;

            if (!draggingBar && overTrack && InputHelper.IsMouseDownThisFrame(MouseButton.Left, consumeEvent: true))
            {
                stickBottom = false;
                if (overThumb) { draggingBar = true; barGrabOffset = thumbTop - m.y; }
                else
                {
                    float newThumbTop = m.y + thumbH / 2f;
                    float nt = travel > 0f ? (regionTop - newThumbTop) / travel : 0f;
                    scrollFromTop = Mathf.Clamp(nt * maxScroll, 0f, maxScroll);
                }
            }

            if (draggingBar)
            {
                if (InputHelper.IsMouseHeld(MouseButton.Left))
                {
                    float newThumbTop = m.y + barGrabOffset;
                    float nt = travel > 0f ? (regionTop - newThumbTop) / travel : 0f;
                    scrollFromTop = Mathf.Clamp(nt * maxScroll, 0f, maxScroll);
                    stickBottom = false;
                }
                else draggingBar = false;
            }

            Color thumbCol = (draggingBar || overThumb) ? new Color(0.5f, 0.7f, 1f) : ColHelper.MakeCol255(95, 98, 110);
            UI.DrawPanel(new Vector2(barLeft, thumbTop), new Vector2(BarW, thumbH), thumbCol, Anchor.TopLeft);
        }

        enum LineKind { UserHead, AssistantHead, Body, Status, Error, Tool }

        struct Line
        {
            public string text;
            public LineKind kind;
            public Line(string t, LineKind k) { text = t; kind = k; }
        }

        static Color ColourFor(LineKind k) => k switch
        {
            LineKind.UserHead => new Color(0.55f, 0.85f, 1f),
            LineKind.AssistantHead => new Color(0.7f, 1f, 0.7f),
            LineKind.Status => new Color(0.8f, 0.8f, 0.5f),
            LineKind.Error => new Color(1f, 0.5f, 0.5f),
            LineKind.Tool => new Color(0.75f, 0.7f, 0.95f),
            _ => new Color(0.9f, 0.9f, 0.92f)
        };

        static List<Line> BuildLines(int cols)
        {
            var outLines = new List<Line>();
            foreach (AskClaude.Msg m in AskClaude.Messages)
            {
                if (m.role == "tool" || m.role == "error")
                {
                    // Tool call / result, or an error — no header. Errors in red.
                    LineKind k = m.role == "error" ? LineKind.Error : LineKind.Tool;
                    foreach (string raw in m.text.Replace("\r", "").Split('\n'))
                        foreach (string wl in WrapLine(raw, cols))
                            outLines.Add(new Line(wl, k));
                    if (m.role == "error") outLines.Add(new Line("", LineKind.Body));
                    continue;
                }

                bool isUser = m.role is "user" or "queued";
                string head = m.role switch
                {
                    "queued" => "Toi (en attente, pris en compte a la fin du cycle) :",
                    "user" => "Toi :",
                    _ => "Claude :"
                };
                outLines.Add(new Line(head, isUser ? LineKind.UserHead : LineKind.AssistantHead));

                foreach (string raw in m.text.Replace("\r", "").Split('\n'))
                    foreach (string wl in WrapLine(raw, cols))
                        outLines.Add(new Line("  " + wl, LineKind.Body));

                outLines.Add(new Line("", LineKind.Body)); // spacer between messages
            }

            return outLines;
        }

        // Word-wrap a single logical line to a max character count (hard-splits over-long words).
        static IEnumerable<string> WrapLine(string s, int cols)
        {
            if (string.IsNullOrEmpty(s)) { yield return ""; yield break; }

            string cur = "";
            foreach (string word in s.Split(' '))
            {
                string w = word;
                while (w.Length > cols)
                {
                    if (cur.Length > 0) { yield return cur; cur = ""; }
                    yield return w.Substring(0, cols);
                    w = w.Substring(cols);
                }

                if (cur.Length == 0) cur = w;
                else if (cur.Length + 1 + w.Length <= cols) cur += " " + w;
                else { yield return cur; cur = w; }
            }

            yield return cur;
        }
    }
}
