using System.Collections.Generic;
using System.Linq;
using DLS.Graphics;
using UnityEngine;

namespace DLS.Game
{
    // "Clean Up": tidies the current chip — subchips in columns by signal-propagation depth, rows
    // ordered to minimise wire crossings, grid-snapped, dev pins on the left/right, wires straight
    // (with minimal detours; wires may cross but never overlap). Records undo.
    public static class CircuitAutoLayout
    {
        const float HorizontalGap = 1.5f;   // between columns (room for wire routing tracks)
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

            // ---- 3) Initial order of each column ----
            // Dev-pin columns (inputs / outputs) are ordered alphabetically (natural order, so A1..A10 sort
            // correctly); other columns start from their current top-to-bottom order.
            var sortedCols = columns.Keys.OrderBy(c => c).ToList();
            var placedCols = new Dictionary<int, List<IMoveable>>();
            foreach (int c in sortedCols)
            {
                var elems = new List<IMoveable>(columns[c]);
                if (elems.TrueForAll(e => e is DevPinInstance))
                    elems.Sort((x, y) => NaturalCompare(((DevPinInstance)x).Pin.Name, ((DevPinInstance)y).Pin.Name));
                else
                    elems.Sort((x, y) => y.Position.y.CompareTo(x.Position.y));
                placedCols[c] = elems;
            }

            var moved = new List<IMoveable>();
            foreach (int c in sortedCols)
                foreach (IMoveable e in placedCols[c])
                {
                    e.MoveStartPosition = e.Position; // for undo
                    moved.Add(e);
                }

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

            // Stacks a column's elements top→bottom (centred on y = 0), grid-snapped.
            void PlaceColumn(int c, List<IMoveable> elems)
            {
                float colHeight = elems.Sum(ElementHeight) + VerticalGap * (elems.Count - 1);
                float y = colHeight / 2f;
                foreach (IMoveable e in elems)
                {
                    float h = ElementHeight(e);
                    Vector2 target = new(colCentreX[c], y - h / 2f);
                    e.Position = new Vector2(GridHelper.SnapToGrid(target.x), GridHelper.SnapToGrid(target.y));
                    y -= h + VerticalGap;
                }
            }

            foreach (int c in sortedCols) PlaceColumn(c, placedCols[c]);

            // ---- 3a) Crossing reduction (barycenter sweeps) ----
            // Each subchip column is reordered so that every element sits at the average height of the pins
            // it is wired to in the columns already swept (left→right, then right→left, a few times). This is
            // the classic layered-graph heuristic: it removes most wire crossings and, just as importantly,
            // spreads the unavoidable ones out instead of piling them up in one spot. Dev-pin columns keep
            // their alphabetical order.
            var colOf = new Dictionary<IMoveable, int>();
            foreach (int c in sortedCols) foreach (IMoveable e in placedCols[c]) colOf[e] = c;

            var links = new List<(IMoveable a, PinInstance pa, IMoveable b, PinInstance pb)>();
            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                IMoveable a = w.SourcePin.parent, b = w.TargetPin.parent;
                if (colOf.TryGetValue(a, out int ca) && colOf.TryGetValue(b, out int cb) && ca != cb) links.Add((a, w.SourcePin, b, w.TargetPin));
            }

            if (links.Count > 0)
            {
                var key = new Dictionary<IMoveable, float>();
                for (int iter = 0; iter < 4; iter++)
                {
                    bool forward = iter % 2 == 0;
                    IEnumerable<int> sweep = forward ? sortedCols : Enumerable.Reverse(sortedCols);
                    foreach (int c in sweep)
                    {
                        List<IMoveable> elems = placedCols[c];
                        if (elems.TrueForAll(e => e is DevPinInstance)) continue;

                        key.Clear();
                        foreach (IMoveable e in elems)
                        {
                            float sum = 0; int n = 0;
                            foreach (var l in links)
                            {
                                if (l.a == e && (forward ? colOf[l.b] < c : colOf[l.b] > c)) { sum += l.pb.GetWorldPos().y; n++; }
                                else if (l.b == e && (forward ? colOf[l.a] < c : colOf[l.a] > c)) { sum += l.pa.GetWorldPos().y; n++; }
                            }
                            key[e] = n > 0 ? sum / n : e.Position.y; // unconnected on that side: stay where it is
                        }

                        placedCols[c] = elems.OrderByDescending(e => key[e]).ToList(); // stable: ties keep their order
                        PlaceColumn(c, placedCols[c]);
                    }
                }
            }

            // ---- 3b) Explicit LayoutRow constraints (bigger = higher) override the computed order ----
            foreach (int c in sortedCols)
            {
                if (!placedCols[c].Exists(e => e.LayoutRow != 0)) continue;
                placedCols[c] = ApplyRowConstraints(placedCols[c]);
                PlaceColumn(c, placedCols[c]);
            }

            // ---- 3c) Same-line constraint: elements of different columns sharing a LayoutRow value must
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

            // ---- 4) Wire routing ----
            // Every wire stays a straight line unless it would clip a component (or run backwards, or lie on
            // top of another wire). Those get a minimal orthogonal detour: a short stub out of the pin, a
            // vertical run, a horizontal run just above/below the obstacles, and the mirror image into the
            // target. Wires may CROSS but never OVERLAP: each segment is checked against every segment already
            // laid down, and a detour picks the nearest free "track" (vertical tracks step away from the pin,
            // horizontal tracks step away from the obstacles). When a stub would lie on another wire leaving
            // the same pin, the stub is dropped and the wire leaves the pin diagonally instead.
            // Skip wires that connect to another wire, or that another wire branches from.
            var branchedWires = new HashSet<WireInstance>();
            foreach (WireInstance w in chip.Wires)
            {
                if (w.SourceConnectionInfo.connectedWire != null) branchedWires.Add(w.SourceConnectionInfo.connectedWire);
                if (w.TargetConnectionInfo.connectedWire != null) branchedWires.Add(w.TargetConnectionInfo.connectedWire);
            }

            const float ClearPad = 0.22f;  // clearance kept between a wire and a component
            const float StubLen = 0.375f;  // first track: distance from the pin
            const float TrackStep = 0.25f; // spacing between parallel tracks
            const int MaxTracks = 4;

            var obstacles = moved.Select(e => (el: e, c: e.Position, half: new Vector2(ElementWidth(e) / 2f + ClearPad, ElementHeight(e) / 2f + ClearPad))).ToList();
            var used = new List<(Vector2 a, Vector2 b)>(); // wire segments already laid down

            bool ClipsAny(Vector2 a, Vector2 b, IMoveable ignore1, IMoveable ignore2)
            {
                foreach (var o in obstacles)
                    if (o.el != ignore1 && o.el != ignore2 && SegIntersectsBox(a, b, o.c, o.half)) return true;
                return false;
            }

            bool OverlapsUsed(Vector2 a, Vector2 b)
            {
                foreach (var u in used)
                    if (CollinearOverlap(a, b, u.a, u.b)) return true;
                return false;
            }

            bool PathOk(List<Vector2> pts, IMoveable srcEl, IMoveable tgtEl)
            {
                for (int i = 0; i < pts.Count - 1; i++)
                    if (ClipsAny(pts[i], pts[i + 1], srcEl, tgtEl) || OverlapsUsed(pts[i], pts[i + 1])) return false;
                return true;
            }

            void Register(List<Vector2> pts)
            {
                for (int i = 0; i < pts.Count - 1; i++) used.Add((pts[i], pts[i + 1]));
            }

            // Pass 1: straighten everything; wires whose straight line is clear are final and claim their segment.
            var pending = new List<WireInstance>();
            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if (w.SourceConnectionInfo.IsConnectedAtWire || w.TargetConnectionInfo.IsConnectedAtWire) continue;
                if (branchedWires.Contains(w)) continue;

                while (w.WirePointCount > 2) w.DeleteWirePoint(1); // straighten

                Vector2 s = w.GetWirePoint(0);
                Vector2 t = w.GetWirePoint(1);
                bool backwards = t.x - s.x < StubLen * 2f; // an output pin faces right, an input pin faces left
                if (backwards || ClipsAny(s, t, w.SourcePin.parent, w.TargetPin.parent) || OverlapsUsed(s, t)) pending.Add(w);
                else used.Add((s, t));
            }

            // Pass 2: detours, on the nearest free tracks.
            foreach (WireInstance w in pending)
            {
                Vector2 s = w.GetWirePoint(0);
                Vector2 t = w.GetWirePoint(1);
                IMoveable srcEl = w.SourcePin.parent;
                IMoveable tgtEl = w.TargetPin.parent;
                bool backwards = t.x - s.x < StubLen * 2f;

                // Obstacle band across the wire's horizontal span (stubs included). A backwards wire has to go
                // around its own source/target, so those count too in that case.
                float spanMinX = Mathf.Min(s.x, t.x - StubLen);
                float spanMaxX = Mathf.Max(s.x + StubLen, t.x);
                float bandTop = float.NegativeInfinity, bandBot = float.PositiveInfinity;
                foreach (var o in obstacles)
                {
                    if (!backwards && (o.el == srcEl || o.el == tgtEl)) continue;
                    if (o.c.x + o.half.x < spanMinX || o.c.x - o.half.x > spanMaxX) continue;
                    bandTop = Mathf.Max(bandTop, o.c.y + o.half.y);
                    bandBot = Mathf.Min(bandBot, o.c.y - o.half.y);
                }
                if (float.IsInfinity(bandTop)) { bandTop = Mathf.Max(s.y, t.y); bandBot = Mathf.Min(s.y, t.y); }
                float topBase = Mathf.Ceil(bandTop / DrawSettings.GridSize) * DrawSettings.GridSize;
                float botBase = Mathf.Floor(bandBot / DrawSettings.GridSize) * DrawSettings.GridSize;

                float costAbove = Mathf.Abs(topBase - s.y) + Mathf.Abs(topBase - t.y);
                float costBelow = Mathf.Abs(botBase - s.y) + Mathf.Abs(botBase - t.y);
                bool[] sides = costAbove <= costBelow ? new[] { true, false } : new[] { false, true };

                List<Vector2> best = null;
                foreach (bool above in sides)
                {
                    // Nearest tracks first: iterate by total track index.
                    for (int sum = 0; sum <= MaxTracks * 3 && best == null; sum++)
                    for (int ky = 0; ky <= sum && best == null; ky++)
                    for (int kx = 0; kx <= sum - ky && best == null; kx++)
                    {
                        int ke = sum - ky - kx;
                        if (kx >= MaxTracks || ke >= MaxTracks || ky > MaxTracks * 2) continue;

                        float routeY = above ? topBase + ky * TrackStep : botBase - ky * TrackStep;
                        float exitX = s.x + StubLen + kx * TrackStep;
                        float entryX = t.x - StubLen - ke * TrackStep;

                        // Full detour, then the variants without the exit stub / entry stub (when a stub would
                        // lie on another wire leaving or reaching the same pin).
                        var full = new List<Vector2> { s, new(exitX, s.y), new(exitX, routeY), new(entryX, routeY), new(entryX, t.y), t };
                        if (PathOk(full, srcEl, tgtEl)) { best = full; break; }
                        var noExit = new List<Vector2> { s, new(exitX, routeY), new(entryX, routeY), new(entryX, t.y), t };
                        if (PathOk(noExit, srcEl, tgtEl)) { best = noExit; break; }
                        var noEntry = new List<Vector2> { s, new(exitX, s.y), new(exitX, routeY), new(entryX, routeY), t };
                        if (PathOk(noEntry, srcEl, tgtEl)) { best = noEntry; break; }
                        var neither = new List<Vector2> { s, new(exitX, routeY), new(entryX, routeY), t };
                        if (PathOk(neither, srcEl, tgtEl)) { best = neither; break; }
                    }
                    if (best != null) break;
                }

                // Nothing free within reach: take the nearest track anyway (may overlap — the crossing gaps
                // in the renderer still keep it readable).
                if (best == null)
                {
                    bool above = sides[0];
                    float routeY = above ? topBase : botBase;
                    best = new List<Vector2> { s, new(s.x + StubLen, s.y), new(s.x + StubLen, routeY), new(t.x - StubLen, routeY), new(t.x - StubLen, t.y), t };
                }

                for (int i = 1; i < best.Count - 1; i++) w.InsertPoint(best[i], i - 1);
                Register(best);
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

        // Do segments a1→a2 and b1→b2 lie on the same line and share more than a sliver of it?
        // (Crossing wires are fine — this only catches wires running on top of each other.)
        static bool CollinearOverlap(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            const float lineEps = 0.02f;   // max distance from the line to count as "on" it
            const float minShared = 0.05f; // shared length below this is just a touching corner

            Vector2 d = a2 - a1;
            float len = d.magnitude;
            if (len < 1e-4f) return false;
            d /= len;

            Vector2 r1 = b1 - a1, r2 = b2 - a1;
            if (Mathf.Abs(d.x * r1.y - d.y * r1.x) > lineEps) return false;
            if (Mathf.Abs(d.x * r2.y - d.y * r2.x) > lineEps) return false;

            float p1 = Vector2.Dot(r1, d), p2 = Vector2.Dot(r2, d);
            float lo = Mathf.Max(0f, Mathf.Min(p1, p2));
            float hi = Mathf.Min(len, Mathf.Max(p1, p2));
            return hi - lo > minShared;
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
