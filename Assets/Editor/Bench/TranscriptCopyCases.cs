using System;
using System.Collections.Generic;
using DLS.Game;
using DLS.Graphics;

namespace DLS.Bench
{
    // Ask Claude panel: a selection copies the text as it was written (wrapped lines joined back, display indents
    // dropped), and the per-answer COPY copies the answer exactly.
    public static class TranscriptCopyCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("Ask Claude panel: a selection copies the text as written (wrap undone, indent dropped, new lines kept)", Selection),
            ("Ask Claude panel: lines know their answer (for COPY) and a word cut by the wrap is joined back whole", MessageIndexAndHardCut),
        };

        static List<AskClaudeMenu.Line> Lines(params (string role, string text)[] msgs)
        {
            var list = new List<AskClaude.Msg>();
            foreach (var (role, text) in msgs) list.Add(new AskClaude.Msg { role = role, text = text });
            return AskClaudeMenu.BuildLines(list, 20);
        }

        static int Find(List<AskClaudeMenu.Line> lines, string start) => lines.FindIndex(l => l.text.TrimStart().StartsWith(start));

        static string Selection()
        {
            string answer = "Le registre A charge la valeur du bus au front montant.\nPuis le bus passe a l'ALU.";
            var lines = Lines(("user", "Explique"), ("assistant", answer));
            int first = Find(lines, "Le registre");
            int last = lines.FindLastIndex(l => l.msg == 1 && l.text.Trim().Length > 0); // the answer's last non-empty line
            if (first < 0 || last < 0 || Find(lines, "Puis") < 0) return "answer lines not found";
            if (last - first < 2) return "test setup: the answer should wrap over several lines at 20 columns";
            // whole answer: from the start of its first line to the end of its last line
            string all = AskClaudeMenu.SelectedText(lines, (first, 0), (last, lines[last].text.Length));
            if (all != answer) return $"whole answer copied as '{all}', expected '{answer}'";
            // reversed drag gives the same
            if (AskClaudeMenu.SelectedText(lines, (last, lines[last].text.Length), (first, 0)) != answer) return "a selection dragged upwards differs";
            // a passage inside one line
            string l0 = lines[first].text; // "  Le registre A"
            int s = l0.IndexOf("registre");
            string part = AskClaudeMenu.SelectedText(lines, (first, s), (first, s + 8));
            if (part != "registre") return $"passage copied as '{part}', expected 'registre'";
            // across the head line: the head is copied, the body indent is not
            int head = Find(lines, "Claude :");
            string withHead = AskClaudeMenu.SelectedText(lines, (head, 0), (first, lines[first].text.Length));
            if (!withHead.StartsWith("Claude :\nLe registre")) return $"head + first line copied as '{withHead}'";
            return null;
        }

        static string MessageIndexAndHardCut()
        {
            string longWord = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"; // longer than 20 columns: cut inside the word
            var lines = Lines(("user", "q"), ("assistant", "mot " + longWord + " fin"));
            int head = Find(lines, "Claude :");
            if (head < 0 || lines[head].msg != 1) return "the answer's head line does not point at its message (COPY would copy the wrong one)";
            int first = Find(lines, "mot");
            int last = lines.FindLastIndex(l => l.text.Contains("fin"));
            string all = AskClaudeMenu.SelectedText(lines, (first, 0), (last, lines[last].text.Length));
            return all == "mot " + longWord + " fin" ? null : $"copied as '{all}' (a word cut by the wrap must come back whole)";
        }
    }
}
