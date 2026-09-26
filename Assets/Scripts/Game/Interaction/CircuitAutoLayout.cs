using System.Collections.Generic;
using System.Linq;
using DLS.Graphics;
using UnityEngine;

namespace DLS.Game
{
    // "Clean Up": tidies the current chip — subchips in columns by signal-propagation depth, rows
    // aligned and grid-snapped, dev pins on the left/right, wires straightened. Records undo.
    public static class CircuitAutoLayout
    {
        const float HorizontalGap = 1.0f;   // between columns (room for wire routing)
        const float VerticalGap = 0.375f;   // between elements in a column
        const float DevPinWidth = 1.0f;
        const float MinRowHeight = 0.375f;

        public static void CleanUp(DevChipInstance chip)
        {
            var subchips = chip.Elements.OfType<SubChipInstance>().ToList();
            var inputPins = chip.Elements.OfType<DevPinInstance>().Where(d => d.IsInputPin).ToList();
            var outputPins = chip.Elements.OfType<DevPinInstance>().Where(d => !d.IsInputPin).ToList();
            if (subchips.Count == 0 && inputPins.Count == 0 && outputPins.Count == 0) return;

            // ---- 1) Signal-propagation level of each subchip (dev inputs = level 0) ----
            var inputSources = new Dictionary<int, List<IMoveable>>();
            foreach (var s in subchips) inputSources[s.ID] = new List<IMoveable>();
            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                IMoveable src = w.SourcePin.parent;
                IMoveable tgt = w.TargetPin.parent;
                if (tgt is SubChipInstance ts && inputSources.ContainsKey(ts.ID)) inputSources[ts.ID].Add(src);
            }

            var level = new Dictionary<int, int>();
            int Level(int id, HashSet<int> visiting)
            {
                if (level.TryGetValue(id, out int cached)) return cached;
                if (!visiting.Add(id)) return 0; // feedback loop
                int m = -1;
                foreach (IMoveable src in inputSources[id])
                {
                    int srcLevel = src is SubChipInstance ss && inputSources.ContainsKey(ss.ID) ? Level(ss.ID, visiting) : 0;
                    if (srcLevel > m) m = srcLevel;
                }
                visiting.Remove(id);
                int result = m < 0 ? 0 : m + 1;
                level[id] = result;
                return result;
            }
            foreach (var s in subchips) Level(s.ID, new HashSet<int>());

            int maxLevel = subchips.Count > 0 ? subchips.Max(s => level[s.ID]) : 0;

            // ---- 2) Assign elements to columns ----
            // col 0 = dev inputs (+ level-0 subchips like clocks); col L = subchips of that level; last col = dev outputs.
            // An element with an explicit LayoutCol constraint (> 0) goes to that column instead.
            int maxHintCol = 0;
            foreach (IMoveable e in chip.Elements) maxHintCol = Mathf.Max(maxHintCol, e.LayoutCol);
            int outputCol = Mathf.Max(maxLevel, maxHintCol) + 1;

            var columns = new Dictionary<int, List<IMoveable>>();
            void AddToCol(int c, IMoveable e) { if (!columns.TryGetValue(c, out var l)) columns[c] = l = new List<IMoveable>(); l.Add(e); }

            foreach (var p in inputPins) AddToCol(p.LayoutCol > 0 ? p.LayoutCol : 0, p);
            foreach (var s in subchips) AddToCol(s.LayoutCol > 0 ? s.LayoutCol : level[s.ID], s);
            foreach (var p in outputPins) AddToCol(p.LayoutCol > 0 ? p.LayoutCol : outputCol, p);

            // ---- 3) Place columns left→right, elements top→bottom (keep relative vertical order) ----
            var moved = new List<IMoveable>();
            var sortedCols = columns.Keys.OrderBy(c => c).ToList();

            // Column x positions (centered around 0)
            var colWidth = new Dictionary<int, float>();
            foreach (int c in sortedCols) colWidth[c] = columns[c].Max(ElementWidth);
            float totalWidth = colWidth.Values.Sum() + HorizontalGap * (sortedCols.Count - 1);
            float runningX = -totalWidth / 2f;
            var colCentreX = new Dictionary<int, float>();
            foreach (int c in sortedCols)
            {
                colCentreX[c] = runningX + colWidth[c] / 2f;
                runningX += colWidth[c] + HorizontalGap;
            }

            var placedCols = new Dictionary<int, List<IMoveable>>();

            foreach (int c in sortedCols)
            {
                // Dev-pin columns (inputs / outputs) are ordered alphabetically (natural order, so A1..A10
                // sort correctly); other columns keep their top-to-bottom order.
                var elems = new List<IMoveable>(columns[c]);
                if (elems.TrueForAll(e => e is DevPinInstance))
                    elems.Sort((x, y) => NaturalCompare(((DevPinInstance)x).Pin.Name, ((DevPinInstance)y).Pin.Name));
                else
                    elems.Sort((x, y) => y.Position.y.CompareTo(x.Position.y));

                // Explicit LayoutRow constraints (bigger = higher) override that order.
                if (elems.Exists(e => e.LayoutRow != 0)) elems = ApplyRowConstraints(elems);
                placedCols[c] = elems;

                float colHeight = elems.Sum(ElementHeight) + VerticalGap * (elems.Count - 1);
                float y = colHeight / 2f;
                foreach (IMoveable e in elems)
                {
                    float h = ElementHeight(e);
                    Vector2 target = new(colCentreX[c], y - h / 2f);
                    target = new Vector2(GridHelper.SnapToGrid(target.x), GridHelper.SnapToGrid(target.y));

                    e.MoveStartPosition = e.Position; // for undo
                    e.Position = target;
                    moved.Add(e);
                    y -= h + VerticalGap;
                }
            }

            // ---- 3b) Same-line constraint: elements of different columns sharing a LayoutRow value must
            // end up on the same horizontal line. Columns are shifted as a whole (left→right, first match
            // wins) so within-column spacing — and therefore the absence of overlaps — is preserved.
            var rowLineY = new Dictionary<int, float>();
            foreach (int c in sortedCols)
            {
                List<IMoveable> elems = placedCols[c];
                float shift = 0f;
                foreach (IMoveable e in elems)
                {
                    if (e.LayoutRow == 0 || !rowLineY.TryGetValue(e.LayoutRow, out float lineY)) continue;
                    shift = lineY - e.Position.y;
                    break;
                }

                if (Mathf.Abs(shift) > 0.0001f)
                    foreach (IMoveable e in elems)
                        e.Position += new Vector2(0f, shift);

                foreach (IMoveable e in elems)
                    if (e.LayoutRow != 0 && !rowLineY.ContainsKey(e.LayoutRow)) rowLineY[e.LayoutRow] = e.Position.y;
            }

            // ---- 4) Wire routing: keep every wire straight, and ONLY for the few that would actually
            // clip a component, add a minimal orthogonal detour that hugs just above/below the obstacle
            // (whichever side is the shorter detour). Wires that don't hit anything stay perfectly straight.
            // Skip wires that connect to another wire, or that another wire branches from.
            var branchedWires = new HashSet<WireInstance>();
            foreach (WireInstance w in chip.Wires)
            {
                if (w.SourceConnectionInfo.connectedWire != null) branchedWires.Add(w.SourceConnectionInfo.connectedWire);
                if (w.TargetConnectionInfo.connectedWire != null) branchedWires.Add(w.TargetConnectionInfo.connectedWire);
            }

            const float ClearPad = 0.22f; // clearance kept between a wire and a component

            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if (w.SourceConnectionInfo.IsConnectedAtWire || w.TargetConnectionInfo.IsConnectedAtWire) continue;
                if (branchedWires.Contains(w)) continue;

                while (w.WirePointCount > 2) w.DeleteWirePoint(1); // straighten

                Vector2 s = w.GetWirePoint(0);
                Vector2 t = w.GetWirePoint(w.WirePointCount - 1);
                IMoveable srcEl = w.SourcePin.parent;
                IMoveable tgtEl = w.TargetPin.parent;

                float spanMinX = Mathf.Min(s.x, t.x);
                float spanMaxX = Mathf.Max(s.x, t.x);
                bool hit = false;
                float bandTop = float.NegativeInfinity, bandBot = float.PositiveInfinity;

                foreach (IMoveable e in moved)
                {
                    if (e == srcEl || e == tgtEl) continue;
                    Vector2 c = e.Position;
                    Vector2 half = new(ElementWidth(e) / 2f + ClearPad, ElementHeight(e) / 2f + ClearPad);

                    if (SegIntersectsBox(s, t, c, half)) hit = true;

                    // Track the obstacle band across the wire's horizontal span (for a clean over/under route).
                    if (c.x + half.x >= spanMinX && c.x - half.x <= spanMaxX)
                    {
                        bandTop = Mathf.Max(bandTop, c.y + half.y);
                        bandBot = Mathf.Min(bandBot, c.y - half.y);
                    }
                }

                if (!hit) continue; // straight line is already clear — leave it perfectly straight

                float dir = Mathf.Sign(t.x - s.x);
                if (dir == 0) dir = 1;
                float exitX = s.x + dir * 0.45f;
                float entryX = t.x - dir * 0.45f;

                float costAbove = Mathf.Abs(bandTop - s.y) + Mathf.Abs(bandTop - t.y);
                float costBelow = Mathf.Abs(bandBot - s.y) + Mathf.Abs(bandBot - t.y);
                float routeY = costAbove <= costBelow ? bandTop : bandBot;

                // source → (exitX, s.y) → (exitX, routeY) → (entryX, routeY) → (entryX, t.y) → target
                w.InsertPoint(new Vector2(exitX, s.y), 0);
                w.InsertPoint(new Vector2(exitX, routeY), 1);
                w.InsertPoint(new Vector2(entryX, routeY), 2);
                w.InsertPoint(new Vector2(entryX, t.y), 3);
            }

            // ---- 5) Record undo ----
            if (moved.Count > 0) chip.UndoController.RecordMoveElements(moved);

            // ---- 6) Re-frame the camera on the tidied contents ----
            CameraController.FocusChip(chip);
        }

        // Reorders a column so that elements carrying a LayoutRow constraint appear in constraint order
        // (bigger = higher), while unconstrained elements keep their relative order and stay where they
        // were relative to their constrained neighbours (their keys are interpolated between them).
        static List<IMoveable> ApplyRowConstraints(List<IMoveable> natural)
        {
            int n = natural.Count;
            var keys = new double[n];
            for (int i = 0; i < n; i++) keys[i] = natural[i].LayoutRow;

            int a = 0;
            while (a < n)
            {
                if (natural[a].LayoutRow != 0) { a++; continue; }

                int b = a;
                while (b < n && natural[b].LayoutRow == 0) b++;
                int count = b - a;

                double upper = a > 0 ? keys[a - 1] : (b < n ? keys[b] + count + 1 : count + 1);
                double lower = b < n ? keys[b] : upper - count - 1;
                for (int k = a; k < b; k++) keys[k] = upper - (upper - lower) * (k - a + 1) / (count + 1);

                a = b;
            }

            // OrderByDescending is stable, so equal keys keep the natural order.
            return natural.Select((e, i) => (element: e, key: keys[i])).OrderByDescending(t => t.key).Select(t => t.element).ToList();
        }

        // Does segment a→b intersect the axis-aligned box centred at c with half-extents h? (Liang–Barsky)
        static bool SegIntersectsBox(Vector2 a, Vector2 b, Vector2 c, Vector2 h)
        {
            float t0 = 0f, t1 = 1f;
            float dx = b.x - a.x, dy = b.y - a.y;
            return Clip(-dx, a.x - (c.x - h.x), ref t0, ref t1)
                && Clip(dx, (c.x + h.x) - a.x, ref t0, ref t1)
                && Clip(-dy, a.y - (c.y - h.y), ref t0, ref t1)
                && Clip(dy, (c.y + h.y) - a.y, ref t0, ref t1);
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-6f) return q >= 0f; // parallel to this slab: inside only if q >= 0
            float r = q / p;
            if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }

        // Natural (human) string comparison: "A2" < "A10", case-insensitive.
        static int NaturalCompare(string a, string b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int cmp = string.CompareOrdinal(na, nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }

        static float ElementWidth(IMoveable e) => e switch
        {
            SubChipInstance s => s.Size.x,
            _ => DevPinWidth
        };

        static float ElementHeight(IMoveable e) => e switch
        {
            SubChipInstance s => s.Size.y,
            DevPinInstance d => Mathf.Max(MinRowHeight, d.BoundsHeight()),
            _ => MinRowHeight
        };
    }
}
