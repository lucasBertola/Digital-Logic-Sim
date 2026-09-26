using DLS.Description;
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
        const float HorizontalGap = 1.5f;   // minimum gap between columns (widened when a column needs many wire channels)
        const float VerticalGap = 0.375f;   // between elements in a column
        const float ClearPad = 0.22f;       // clearance kept between a wire and a component
        const float Lead = 0.375f;          // horizontal lead out of / into a pin (= first track / channel)
        const float TrackStep = 0.25f;      // spacing between parallel tracks / channels
        const int MaxTracks = 4;
        const int TrunkFanOut = 3;          // a pin feeding at least this many wires gets a shared vertical channel (trunk)
        const float DevPinWidth = 1.0f;
        const float MinRowHeight = 0.375f;

        // recordUndo = false when the caller records a bigger undo step itself (a whole Claude request);
        // focusCamera = false when the chip is not the one on screen.
        public static void CleanUp(DevChipInstance chip, bool recordUndo = true, bool focusCamera = true)
        {
            var subchips = chip.Elements.OfType<SubChipInstance>().ToList();
            var inputPins = chip.Elements.OfType<DevPinInstance>().Where(d => d.IsInputPin).ToList();
            var outputPins = chip.Elements.OfType<DevPinInstance>().Where(d => !d.IsInputPin).ToList();
            if (subchips.Count == 0 && inputPins.Count == 0 && outputPins.Count == 0) return;

            UndoController.LayoutSnapshot layoutBefore = recordUndo ? new(chip) : null; // Ctrl+Z restores positions AND wire points

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

            var sortedCols = columns.Keys.OrderBy(c => c).ToList();
            var colOf = new Dictionary<IMoveable, int>();
            foreach (var kv in columns) foreach (IMoveable e in kv.Value) colOf[e] = kv.Key;

            // ---- 2b) Wires that will be routed, and how many vertical channels each column needs ----
            // A pin that feeds many wires (TrunkFanOut or more) gets its own vertical channel in the gap right
            // after its column: its wires share it as a trunk and branch off horizontally at their target's
            // height — a fan of diagonals from one pin to several stacked chips is unreadable, a trunk with
            // right-angle branches is not. Every other wire goes as directly as it can (see step 4). Gaps
            // widen to fit the channels. (Wires connected to another wire, or branched from, are left alone.)
            // A wire that starts ON another wire (a junction) is first turned into a direct pin-to-pin wire: the
            // routing below gives every fan-out its own trunk, so the junction has nothing to add — and left as
            // is, both it and the wire it hangs from would keep points that no longer mean anything once the
            // components have moved (that is what used to scramble chips like a D latch). A wire whose target
            // end is on another wire only exists for buses and is left alone.
            foreach (WireInstance w in chip.Wires)
            {
                int guard = 0;
                while (w.SourceConnectionInfo.IsConnectedAtWire && guard++ < 16) w.RemoveConnectionDependency();
            }

            var branchedWires = new HashSet<WireInstance>();
            foreach (WireInstance w in chip.Wires)
            {
                if (w.SourceConnectionInfo.connectedWire != null) branchedWires.Add(w.SourceConnectionInfo.connectedWire);
                if (w.TargetConnectionInfo.connectedWire != null) branchedWires.Add(w.TargetConnectionInfo.connectedWire);
            }

            var routable = new List<WireInstance>();
            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if (w.SourceConnectionInfo.IsConnectedAtWire || w.TargetConnectionInfo.IsConnectedAtWire) continue;
                if (branchedWires.Contains(w)) continue;
                if (!colOf.ContainsKey(w.SourcePin.parent) || !colOf.ContainsKey(w.TargetPin.parent)) continue;
                routable.Add(w);
            }

            var fanOut = new Dictionary<PinInstance, int>();
            foreach (WireInstance w in routable) fanOut[w.SourcePin] = fanOut.TryGetValue(w.SourcePin, out int n) ? n + 1 : 1;

            var trunkPins = new HashSet<PinInstance>(fanOut.Where(kv => kv.Value >= TrunkFanOut).Select(kv => kv.Key));
            var channelCount = new Dictionary<int, int>();
            foreach (int c in sortedCols) channelCount[c] = 0;
            foreach (PinInstance pin in trunkPins) channelCount[colOf[pin.parent]]++;
            float GapAfter(int c) => Mathf.Max(HorizontalGap, Lead * 2f + channelCount[c] * TrackStep + 0.5f);

            // ---- 3) Initial order of each column ----
            // Dev-pin columns (inputs / outputs) are ordered by name: groups alphabetically (A.. before D..),
            // and inside a numbered group the biggest number on top (D4, D3, D2, D1, D0 — the way a bus is
            // read); other columns start from their current top-to-bottom order.
            var placedCols = new Dictionary<int, List<IMoveable>>();
            foreach (int c in sortedCols)
            {
                var elems = new List<IMoveable>(columns[c]);
                if (elems.TrueForAll(e => e is DevPinInstance))
                    elems.Sort((x, y) => PinOrderCompare(((DevPinInstance)x).Pin.Name, ((DevPinInstance)y).Pin.Name));
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
            float totalWidth = colWidth.Values.Sum();
            for (int i = 0; i < sortedCols.Count - 1; i++) totalWidth += GapAfter(sortedCols[i]);
            float runningX = -totalWidth / 2f;
            var colCentreX = new Dictionary<int, float>();
            foreach (int c in sortedCols)
            {
                colCentreX[c] = runningX + colWidth[c] / 2f;
                runningX += colWidth[c] + GapAfter(c);
            }

            // Stacks a column's elements top→bottom (centred on y = 0), grid-snapped. Between two dev pins
            // that belong to different "groups" (same text, different number: D4..D1 vs A3..A0) an extra gap of
            // two pin heights is left, so that the groups read as separate buses.
            void PlaceColumn(int c, List<IMoveable> elems)
            {
                float[] gapAfter = new float[elems.Count];
                for (int i = 0; i < elems.Count - 1; i++)
                {
                    gapAfter[i] = VerticalGap;
                    if (elems[i] is DevPinInstance a && elems[i + 1] is DevPinInstance b && PinGroup(a) != PinGroup(b) && (GroupSize(elems, a) >= 2 || GroupSize(elems, b) >= 2))
                        gapAfter[i] += 2f * ElementHeight(a);
                }

                float colHeight = elems.Sum(ElementHeight) + gapAfter.Sum();
                float y = colHeight / 2f;
                for (int i = 0; i < elems.Count; i++)
                {
                    IMoveable e = elems[i];
                    float h = ElementHeight(e);
                    Vector2 target = new(colCentreX[c], y - h / 2f);
                    e.Position = new Vector2(GridHelper.SnapToGrid(target.x), GridHelper.SnapToGrid(target.y));
                    y -= h + gapAfter[i];
                }
            }

            static string PinGroup(DevPinInstance p) => (p.Pin.Name ?? "").TrimEnd("0123456789".ToCharArray()).Trim().ToUpperInvariant();
            static int GroupSize(List<IMoveable> elems, DevPinInstance p) { string g = PinGroup(p); int n = 0; foreach (IMoveable e in elems) if (e is DevPinInstance d && PinGroup(d) == g) n++; return n; }

            foreach (int c in sortedCols) PlaceColumn(c, placedCols[c]);

            // ---- 3a) Crossing reduction (barycenter sweeps) ----
            // Each subchip column is reordered so that every element sits at the average height of the pins
            // it is wired to in the columns already swept (left→right, then right→left, a few times). This is
            // the classic layered-graph heuristic: it removes most wire crossings and, just as importantly,
            // spreads the unavoidable ones out instead of piling them up in one spot. Dev-pin columns keep
            // their alphabetical order.
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

            // ---- 3d) Channel x of every trunk pin (positions are final now) ----
            // Within a column the topmost pin takes the channel farthest from the column, so the leads nest
            // like brackets instead of crossing each other.
            var channelX = new Dictionary<PinInstance, float>();
            foreach (int c in sortedCols)
            {
                List<PinInstance> pins = trunkPins.Where(pin => colOf[pin.parent] == c).OrderByDescending(pin => pin.GetWorldPos().y).ToList();
                float baseX = colCentreX[c] + colWidth[c] / 2f + Lead;
                for (int i = 0; i < pins.Count; i++) channelX[pins[i]] = baseX + (pins.Count - 1 - i) * TrackStep;
            }

            // ---- 4) Wire routing ----
            // As few bends as possible. A wire tries, in order: a straight line; one bend (diagonal, then a
            // horizontal lead into the target pin — or a lead out of the source pin, then the diagonal); two
            // bends (lead, diagonal, lead). Each candidate must clip no component and lie on no other wire. A
            // trunk pin's wires instead take lead → shared channel → horizontal branch at the target's height
            // (the trunk is the only overlap allowed). Whatever is left (a wire that would clip a component of
            // a column it spans over, or that runs backwards) gets a minimal orthogonal detour: lead, vertical
            // run, horizontal run just above/below the obstacles, then the mirror image into the target, on
            // the nearest free track. Wires may CROSS but never OVERLAP. Wires from the same column to the same
            // component are routed as a group: same side, nested tracks, so they travel together.
            var obstacles = moved.Select(e => (el: e, c: e.Position, half: new Vector2(ElementWidth(e) / 2f + ClearPad, ElementHeight(e) / 2f + ClearPad))).ToList();
            var used = new List<(Vector2 a, Vector2 b, PinInstance srcPin)>(); // wire segments already laid down

            bool ClipsAny(Vector2 a, Vector2 b, IMoveable ignore1, IMoveable ignore2)
            {
                foreach (var o in obstacles)
                    if (o.el != ignore1 && o.el != ignore2 && SegIntersectsBox(a, b, o.c, o.half)) return true;
                return false;
            }

            bool OverlapsUsed(Vector2 a, Vector2 b, PinInstance sharedPin)
            {
                foreach (var u in used)
                    if (u.srcPin != sharedPin && CollinearOverlap(a, b, u.a, u.b)) return true;
                return false;
            }

            bool PathOk(List<Vector2> pts, WireInstance w)
            {
                IMoveable srcEl = w.SourcePin.parent, tgtEl = w.TargetPin.parent;
                for (int i = 0; i < pts.Count - 1; i++)
                    if (ClipsAny(pts[i], pts[i + 1], srcEl, tgtEl) || OverlapsUsed(pts[i], pts[i + 1], w.SourcePin)) return false;
                return true;
            }

            void Apply(WireInstance w, List<Vector2> pts)
            {
                for (int i = 1; i < pts.Count - 1; i++) w.InsertPoint(pts[i], i - 1);
                for (int i = 0; i < pts.Count - 1; i++) used.Add((pts[i], pts[i + 1], w.SourcePin));
            }

            bool Backwards(Vector2 s, Vector2 t) => t.x - s.x < Lead * 2f + 0.05f; // an output pin faces right, an input pin faces left

            // Pass 1: straighten everything; wires with a clear direct path (or channel path) are final.
            var pending = new List<WireInstance>();
            foreach (WireInstance w in routable)
            {
                while (w.WirePointCount > 2) w.DeleteWirePoint(1); // straighten

                Vector2 s = w.GetWirePoint(0);
                Vector2 t = w.GetWirePoint(1);
                if (Backwards(s, t)) { pending.Add(w); continue; }

                var candidates = new List<List<Vector2>>();
                if (Mathf.Abs(s.y - t.y) < 0.001f) candidates.Add(new List<Vector2> { s, t });
                else if (trunkPins.Contains(w.SourcePin))
                {
                    float xk = channelX[w.SourcePin];
                    candidates.Add(new List<Vector2> { s, new(xk, s.y), new(xk, t.y), t });
                }
                else
                {
                    candidates.Add(new List<Vector2> { s, t });                                             // 0 bends
                    candidates.Add(new List<Vector2> { s, new(t.x - Lead, t.y), t });                       // 1 bend, lead into the target
                    candidates.Add(new List<Vector2> { s, new(s.x + Lead, s.y), t });                       // 1 bend, lead out of the source
                    candidates.Add(new List<Vector2> { s, new(s.x + Lead, s.y), new(t.x - Lead, t.y), t }); // 2 bends
                }

                List<Vector2> chosen = candidates.Find(path => PathOk(path, w));
                if (chosen != null) Apply(w, chosen);
                else pending.Add(w);
            }

            // Pass 2: detours. First the obstacle band of each wire and its cost above/below, then the wires
            // are grouped (source column → target component) so that a group goes the same way, nested.
            var route = new Dictionary<WireInstance, (float topBase, float botBase, float costAbove, float costBelow)>();
            foreach (WireInstance w in pending)
            {
                Vector2 s = w.GetWirePoint(0);
                Vector2 t = w.GetWirePoint(1);
                IMoveable srcEl = w.SourcePin.parent;
                IMoveable tgtEl = w.TargetPin.parent;
                bool backwards = Backwards(s, t);

                // Obstacle band across the wire's horizontal span (channel and leads included). A backwards
                // wire has to go around its own source/target, so those count too in that case.
                float spanMinX = Mathf.Min(s.x, t.x - Lead);
                float spanMaxX = Mathf.Max(channelX.TryGetValue(w.SourcePin, out float cx) ? cx : s.x + Lead, t.x);
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
                route[w] = (topBase, botBase, costAbove, costBelow);
            }

            var groups = pending
                .GroupBy(w => (col: colOf[w.SourcePin.parent], tgt: w.TargetPin.parent))
                .OrderBy(g => g.Key.col);

            foreach (var group in groups)
            {
                // One side for the whole group (whichever is cheaper overall), innermost track for the wire
                // closest to the obstacles.
                bool above = group.Sum(w => route[w].costAbove) <= group.Sum(w => route[w].costBelow);
                float Height(WireInstance w) => w.GetWirePoint(0).y + w.GetWirePoint(1).y;
                IEnumerable<WireInstance> ordered = above ? group.OrderBy(Height) : group.OrderByDescending(Height);

                foreach (WireInstance w in ordered)
                {
                    Vector2 s = w.GetWirePoint(0);
                    Vector2 t = w.GetWirePoint(1);
                    var r = route[w];
                    bool hasChannel = channelX.TryGetValue(w.SourcePin, out float xk); // a trunk pin's vertical run stays on its channel
                    bool[] sides = above ? new[] { true, false } : new[] { false, true };

                    List<Vector2> best = null;
                    foreach (bool side in sides)
                    {
                        // Nearest tracks first: iterate by total track index.
                        for (int sum = 0; sum <= MaxTracks * 3 && best == null; sum++)
                        for (int ky = 0; ky <= sum && best == null; ky++)
                        for (int kx = 0; kx <= sum - ky && best == null; kx++)
                        {
                            int ke = sum - ky - kx;
                            if (kx >= MaxTracks || ke >= MaxTracks || ky > MaxTracks * 2) continue;
                            if (hasChannel && kx > 0) continue;

                            float routeY = side ? r.topBase + ky * TrackStep : r.botBase - ky * TrackStep;
                            float exitX = hasChannel ? xk : s.x + Lead + kx * TrackStep;
                            float entryX = t.x - Lead - ke * TrackStep;

                            var path = new List<Vector2> { s, new(exitX, s.y), new(exitX, routeY), new(entryX, routeY), new(entryX, t.y), t };
                            if (PathOk(path, w)) { best = path; break; }
                        }
                        if (best != null) break;
                    }

                    // Nothing free within reach: take the nearest track anyway (may overlap).
                    if (best == null)
                    {
                        float routeY = above ? r.topBase : r.botBase;
                        float exitX = hasChannel ? xk : s.x + Lead;
                        best = new List<Vector2> { s, new(exitX, s.y), new(exitX, routeY), new(t.x - Lead, routeY), new(t.x - Lead, t.y), t };
                    }

                    Apply(w, best);
                }
            }

            // Diagnostics (Player.log): how many wires took a detour, and whether any overlap remains.
            int overlaps = 0;
            for (int i = 0; i < used.Count; i++)
                for (int j = i + 1; j < used.Count; j++)
                    if (used[i].srcPin != used[j].srcPin && CollinearOverlap(used[i].a, used[i].b, used[j].a, used[j].b)) overlaps++;
            Debug.Log($"CleanUp: {routable.Count} wires routed, {pending.Count} detours, {overlaps} overlapping segment pairs");

            // ---- 5) Record undo ----
            if (recordUndo) chip.UndoController.RecordLayoutChange(layoutBefore);

            // ---- 6) Re-frame the camera on the tidied contents ----
            if (focusCamera) CameraController.FocusChip(chip);
        }

        // Incremental placement: puts ONLY the given (newly added) elements at sensible spots, without moving
        // anything else — each in the column matching its signal depth (aligned on the existing components of
        // that depth, or a new column further right), stacked under the existing content of that column, in
        // the first free slot. Used at the end of a Claude request on a chip that already had a layout, so the
        // additions integrate instead of piling up at the origin.
        public static void PlaceNewElements(DevChipInstance chip, HashSet<int> newIDs)
        {
            var newElems = chip.Elements.Where(e => newIDs.Contains(e.ID)).ToList();
            if (newElems.Count == 0) return;
            var oldElems = chip.Elements.Where(e => !newIDs.Contains(e.ID)).ToList();
            var subchips = chip.Elements.OfType<SubChipInstance>().ToList();

            // Signal depth of every subchip (dev inputs = 0), as in CleanUp
            var inputSources = new Dictionary<int, List<IMoveable>>();
            foreach (var s in subchips) inputSources[s.ID] = new List<IMoveable>();
            foreach (WireInstance w in chip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if (w.TargetPin.parent is SubChipInstance ts && inputSources.ContainsKey(ts.ID)) inputSources[ts.ID].Add(w.SourcePin.parent);
            }
            var level = new Dictionary<int, int>();
            int Level(int id, HashSet<int> visiting)
            {
                if (level.TryGetValue(id, out int cached)) return cached;
                if (!visiting.Add(id)) return 0;
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

            int ColumnOf(IMoveable e) => e is DevPinInstance d ? (d.IsInputPin ? -1 : int.MaxValue) : level[((SubChipInstance)e).ID];

            // x of the existing columns
            var colX = new Dictionary<int, float>();
            foreach (var g in oldElems.GroupBy(ColumnOf)) colX[g.Key] = g.Average(e => e.Position.x);
            float oldMinX = oldElems.Count > 0 ? oldElems.Min(e => e.Position.x - ElementWidth(e) / 2f) : 0f;
            float oldMaxX = oldElems.Count > 0 ? oldElems.Max(e => e.Position.x + ElementWidth(e) / 2f) : 0f;

            float ColumnX(IMoveable e)
            {
                // Strongest cue: existing components of the SAME kind form the column (new MUXes go with the
                // MUX already there), whatever their computed depth.
                if (e is SubChipInstance sc)
                {
                    var sameKind = oldElems.OfType<SubChipInstance>().Where(o => ChipDescription.NameMatch(o.Description.Name, sc.Description.Name)).ToList();
                    if (sameKind.Count > 0) return sameKind.Average(o => o.Position.x);
                }
                if (e is DevPinInstance dp)
                {
                    var samePins = oldElems.OfType<DevPinInstance>().Where(o => o.IsInputPin == dp.IsInputPin).ToList();
                    if (samePins.Count > 0) return samePins.Average(o => o.Position.x);
                }

                int c = ColumnOf(e);
                if (colX.TryGetValue(c, out float x)) return x;
                if (c == -1) return oldMinX - HorizontalGap - DevPinWidth / 2f;          // new input column, left of everything
                if (c == int.MaxValue) return oldMaxX + HorizontalGap + DevPinWidth / 2f; // new output column, right of everything
                // subchip level with no existing column: right of the nearest existing lower level (or of the inputs)
                var lower = colX.Keys.Where(k => k != int.MaxValue && k < c).DefaultIfEmpty(-1).Max();
                float baseX = colX.TryGetValue(lower, out float lx) ? lx : oldMinX;
                x = baseX + (c - lower) * (3f + HorizontalGap);
                colX[c] = x;
                return x;
            }

            // free-slot search: below the existing content of the column, first spot that overlaps nothing
            var placed = new List<IMoveable>(oldElems);
            bool Overlaps(Vector2 pos, IMoveable e)
            {
                Vector2 half = new(ElementWidth(e) / 2f + VerticalGap / 2f, ElementHeight(e) / 2f + VerticalGap / 2f);
                foreach (IMoveable o in placed)
                {
                    Vector2 oh = new(ElementWidth(o) / 2f, ElementHeight(o) / 2f);
                    if (Mathf.Abs(pos.x - o.Position.x) < half.x + oh.x && Mathf.Abs(pos.y - o.Position.y) < half.y + oh.y) return true;
                }
                return false;
            }

            // Preferred height of a new element: the median height of the pins it is wired to on elements
            // already in place (a MUX feeding the top flip-flop from D7 lands next to them). Median, not mean,
            // so one far-away control signal (LOAD at the bottom) does not drag it down.
            float? TargetY(IMoveable e)
            {
                var ys = new List<float>();
                foreach (WireInstance w in chip.Wires)
                {
                    if (!w.IsFullyConnected) continue;
                    PinInstance other = w.SourcePin.parent == e ? w.TargetPin : w.TargetPin.parent == e ? w.SourcePin : null;
                    if (other == null || other.parent == e || !placed.Contains(other.parent)) continue;
                    ys.Add(other.GetWorldPos().y);
                }
                if (ys.Count == 0) return null;
                ys.Sort();
                return ys.Count % 2 == 1 ? ys[ys.Count / 2] : (ys[ys.Count / 2 - 1] + ys[ys.Count / 2]) / 2f;
            }

            // Top-most targets first, so a column fills downward in the same order as the pins it mirrors.
            var order = newElems.Select(e => (e, ty: TargetY(e))).OrderBy(t => ColumnOf(t.e)).ThenByDescending(t => t.ty ?? float.NegativeInfinity).Select(t => t.e).ToList();
            foreach (IMoveable e in order)
            {
                float x = GridHelper.SnapToGrid(ColumnX(e));
                float h = ElementHeight(e), w = ElementWidth(e);
                float? ty = TargetY(e);
                float y0;
                if (ty.HasValue) y0 = ty.Value;
                else
                {
                    var inCol = placed.Where(o => Mathf.Abs(o.Position.x - x) < (w + ElementWidth(o)) / 2f).ToList();
                    y0 = inCol.Count > 0 ? inCol.Min(o => o.Position.y - ElementHeight(o) / 2f) - VerticalGap - h / 2f : 0f;
                }
                y0 = GridHelper.SnapToGrid(y0);

                // nearest free slot: the target itself, then one grid step down, up, two down, two up...
                float y = y0;
                for (int k = 0; k < 400; k++)
                {
                    float dy = (k + 1) / 2 * DrawSettings.GridSize;
                    y = k == 0 ? y0 : (k % 2 == 1 ? y0 - dy : y0 + dy);
                    if (!Overlaps(new Vector2(x, y), e)) break;
                }

                e.MoveStartPosition = e.Position;
                e.Position = new Vector2(x, y);
                placed.Add(e);
            }
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

        // Order of dev pins in a column: by text part (natural, case-insensitive), and within one numbered
        // group the biggest number FIRST (= highest on screen): D4, D3, D2, D1, D0.
        static int PinOrderCompare(string a, string b)
        {
            SplitTrailingNumber(a, out string pa, out long na);
            SplitTrailingNumber(b, out string pb, out long nb);
            if (na >= 0 && nb >= 0 && string.Equals(pa, pb, System.StringComparison.OrdinalIgnoreCase)) return nb.CompareTo(na);
            return NaturalCompare(a, b);
        }

        // "D12" -> ("D", 12); "CLK" -> ("CLK", -1)
        static void SplitTrailingNumber(string name, out string prefix, out long number)
        {
            name ??= "";
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            prefix = name.Substring(0, i);
            number = i < name.Length && long.TryParse(name.Substring(i), out long n) ? n : -1;
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
