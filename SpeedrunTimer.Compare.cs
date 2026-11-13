using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace SpeedrunTimer;

/// <summary>
/// "Compare runs" section: pick any two runs (finished, unfinished, paused, running or imported) and compare the
/// total time spent in each zone (all visits summed), laid out in the comparison run's route order. Visit order
/// doesn't matter. Town/hideout time is one row per act.
/// </summary>
public partial class SpeedrunTimer
{
    private static readonly Vector4 ZoneCol = new(0.33f, 0.62f, 0.95f, 1f);
    private static readonly Vector4 TownCol = new(0.95f, 0.68f, 0.22f, 1f);
    private static readonly Vector4 PanelBg = new(1f, 1f, 1f, 0.04f);
    private static readonly Vector4 GridCol = new(1f, 1f, 1f, 0.10f);
    private static readonly Vector4 AxisCol = new(1f, 1f, 1f, 0.35f);

    private string _cmpAId; // null = auto (active run, else latest own run)
    private string _cmpBId; // null = auto (group comparison target)
    private int _rowMode;   // 0 losses, 1 gains, 2 route order
    private int _rowCount = 12;

    private enum ActState { None, Partial, Full }

    private sealed class ZoneRow
    {
        public string Key, Name;
        public int Act;          // progress act it belongs to
        public bool Town;        // the act's combined town/hideout row
        public double A, B;      // seconds
        public int VisitsA, VisitsB;
        public bool HasA, HasB;
        public bool Compared;    // both sides are final enough to compare
        public bool Live;        // the zone the running run is in right now
        public double Diff => A - B;
    }

    // ------------------------------------------------------------ entry point

    private void DrawCompareSection(RunDatabase db, RunGroup group)
    {
        var a = db.Runs.FirstOrDefault(r => r.Id == _cmpAId)
                ?? _active
                ?? db.OwnRunsIn(group.Id).OrderByDescending(r => r.StartedUtc).FirstOrDefault()
                ?? db.Runs.Where(r => !r.IsReference).OrderByDescending(r => r.StartedUtc).FirstOrDefault();
        var b = db.Runs.FirstOrDefault(r => r.Id == _cmpBId && r != a);
        if (b == null)
        {
            var t = db.ComparisonTarget(group.Id);
            b = t != a ? t : null;
        }

        ImGui.Text("Run:");
        ImGui.SameLine(60);
        RunPicker("##cmpA", db, a, null, ref _cmpAId, "auto: active / latest run");
        ImGui.Text("vs:");
        ImGui.SameLine(60);
        RunPicker("##cmpB", db, b, a, ref _cmpBId, "auto: group's compare target");

        if (a == null || b == null)
        {
            ImGui.TextColored(Grey, "Pick two runs to compare.");
            return;
        }

        var rows = BuildZoneRows(a, b);
        if (!rows.Any(r => r.Compared))
        {
            ImGui.TextColored(Grey, "Nothing to compare yet.");
            return;
        }

        var bLabel = b.IsReference ? "Ref" : "Them";
        ImGui.Spacing();
        DrawSummaryCards(a, b, rows, bLabel);
        ImGui.Spacing();

        ImGui.TextColored(Gold, "Time lost along the route");
        ImGui.SameLine();
        ImGui.TextColored(Grey, $"(running total of zone-time differences, zones in {bLabel.ToLower()}'s route order; red = losing time - hover for details)");
        DrawCumulativeGraph(rows, bLabel);
        ImGui.Spacing();

        ImGui.TextColored(Gold, "Time per act");
        ImGui.SameLine();
        ImGui.TextColored(Grey, "(blue = zones, orange = town/hideout total for the act)");
        DrawActBars(rows, bLabel);
        ImGui.Spacing();

        ImGui.TextColored(Gold, "Zones");
        ImGui.SameLine();
        ImGui.TextColored(Grey, "(total time in each zone, all visits summed - visit order doesn't matter)");
        DrawZoneTable(rows, bLabel);
    }

    private void RunPicker(string id, RunDatabase db, Run current, Run exclude, ref string selId, string autoLabel)
    {
        ImGui.SetNextItemWidth(Math.Min(560, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo(id, current != null ? CompareLabel(db, current) : "(none)")) return;
        if (ImGui.Selectable($"({autoLabel})", selId == null)) selId = null;
        var list = db.Runs.Where(r => r != exclude && r.Segments.Count > 0)
            .OrderByDescending(r => r == _active)
            .ThenByDescending(r => r.IsReference)
            .ThenByDescending(r => r.StartedUtc);
        foreach (var r in list)
            if (ImGui.Selectable(CompareLabel(db, r) + "##" + r.Id, r == current))
                selId = r.Id;
        ImGui.EndCombo();
    }

    private string CompareLabel(RunDatabase db, Run r)
    {
        var tag = r == _active ? "running" :
            r.IsReference ? "reference" :
            r.Status == RunStatus.Finished ? "finished" :
            r.Status == RunStatus.InProgress ? "paused" : "unfinished";
        var act = r.ProgressActs().Where(x => x is >= 1 and <= 10).DefaultIfEmpty(0).Max();
        var reach = r.Status == RunStatus.Finished ? "" : $", reached act {act}";
        var group = db.Groups.FirstOrDefault(g => g.Id == r.GroupId)?.Name ?? "?";
        return $"{r.Character}  {FormatTime(r.TotalSeconds)}  [{tag}{reach}]  {r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  - {group}";
    }

    // ------------------------------------------------------------ data

    private sealed class RunZones
    {
        public readonly Dictionary<string, (string name, int act, double secs, int visits)> Zones = new();
        public readonly List<string> Order = new(); // first-entry order
        public readonly Dictionary<int, (double secs, int visits)> Town = new();
        public int Reached;
        public bool Done;
        public string LiveKey;

        public ActState State(int act) => Done || act < Reached ? ActState.Full : act == Reached ? ActState.Partial : ActState.None;
    }

    private RunZones Collect(Run r)
    {
        var z = new RunZones { Done = r.Status == RunStatus.Finished && r != _active };
        var acts = r.ProgressActs();
        for (var i = 0; i < r.Segments.Count; i++)
        {
            var s = r.Segments[i];
            var act = acts[i];
            if (act is < 1 or > 10) continue; // campaign only
            z.Reached = Math.Max(z.Reached, act);
            if (s.IsTown)
            {
                var t = z.Town.GetValueOrDefault(act);
                z.Town[act] = (t.secs + s.Seconds, t.visits + 1);
                continue;
            }

            if (z.Zones.TryGetValue(s.Key, out var e))
                z.Zones[s.Key] = (e.name, e.act, e.secs + s.Seconds, e.visits + 1);
            else
            {
                z.Zones[s.Key] = (s.AreaName, act, s.Seconds, 1);
                z.Order.Add(s.Key);
            }
        }

        if (r == _active && r.Segments.Count > 0) z.LiveKey = r.Segments[^1].IsTown ? "town" : r.Segments[^1].Key;
        return z;
    }

    private List<ZoneRow> BuildZoneRows(Run a, Run b)
    {
        var za = Collect(a);
        var zb = Collect(b);

        // B's route order, per act; A-only zones slot in after the zone A visited before them
        var perAct = new SortedDictionary<int, List<string>>();
        List<string> ActList(int act) => perAct.TryGetValue(act, out var l) ? l : perAct[act] = new List<string>();
        foreach (var k in zb.Order) ActList(zb.Zones[k].act).Add(k);
        string prevA = null;
        foreach (var k in za.Order)
        {
            if (!zb.Zones.ContainsKey(k))
            {
                var list = ActList(za.Zones[k].act);
                var at = prevA != null ? list.IndexOf(prevA) : -1;
                list.Insert(at >= 0 ? at + 1 : list.Count, k);
            }

            prevA = k;
        }

        foreach (var act in za.Town.Keys.Concat(zb.Town.Keys)) ActList(act);

        var rows = new List<ZoneRow>();
        foreach (var (act, keys) in perAct)
        {
            var sa = za.State(act);
            var sb = zb.State(act);
            if (sa == ActState.None || sb == ActState.None) continue; // an act only one side has reached

            foreach (var k in keys)
            {
                var hasA = za.Zones.TryGetValue(k, out var ea);
                var hasB = zb.Zones.TryGetValue(k, out var eb);
                rows.Add(new ZoneRow
                {
                    Key = k,
                    Name = hasB ? eb.name : ea.name,
                    Act = act,
                    A = hasA ? ea.secs : 0,
                    B = hasB ? eb.secs : 0,
                    VisitsA = hasA ? ea.visits : 0,
                    VisitsB = hasB ? eb.visits : 0,
                    HasA = hasA,
                    HasB = hasB,
                    // a zone missing on a side that's still mid-act may just not be reached yet
                    Compared = (hasA || sa == ActState.Full) && (hasB || sb == ActState.Full),
                    Live = k == za.LiveKey,
                });
            }

            var ta = za.Town.GetValueOrDefault(act);
            var tb = zb.Town.GetValueOrDefault(act);
            rows.Add(new ZoneRow
            {
                Key = "town|" + act,
                Name = "Town / hideout (total)",
                Act = act,
                Town = true,
                A = ta.secs,
                B = tb.secs,
                VisitsA = ta.visits,
                VisitsB = tb.visits,
                HasA = ta.visits > 0,
                HasB = tb.visits > 0,
                Compared = sa == ActState.Full && sb == ActState.Full,
                Live = za.LiveKey == "town" && act == za.Reached,
            });
        }

        return rows;
    }

    private static bool LooksTownless(Run r) => r.TotalSeconds > 600 && r.TownSeconds / r.TotalSeconds < 0.02;

    // ------------------------------------------------------------ summary

    private void DrawSummaryCards(Run a, Run b, List<ZoneRow> rows, string bLabel)
    {
        var cmp = rows.Where(r => r.Compared).ToList();
        var zA = cmp.Where(r => !r.Town).Sum(r => r.A);
        var zB = cmp.Where(r => !r.Town).Sum(r => r.B);
        var tA = cmp.Where(r => r.Town).Sum(r => r.A);
        var tB = cmp.Where(r => r.Town).Sum(r => r.B);
        var lastAct = rows.Max(r => r.Act);
        var partial = rows.Any(r => !r.Compared);
        var scope = !partial ? "whole campaign" : $"acts 1-{lastAct}, act {lastAct} so far";
        var worst = cmp.OrderByDescending(r => r.Diff).FirstOrDefault();
        var who = bLabel.ToLower();

        if (ImGui.BeginTable("cards", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            var d = zA + tA - zB - tB;
            Card($"Total ({scope})", FormatDelta(d), d <= 0 ? Green : Red, $"you {FormatTime(zA + tA)}  /  {who} {FormatTime(zB + tB)}");
            Card("Zone time", FormatDelta(zA - zB), zA <= zB ? Green : Red, $"you {FormatTime(zA)}  /  {who} {FormatTime(zB)}");
            Card("Town / hideout time", FormatDelta(tA - tB), tA <= tB ? Green : Red,
                $"you {FormatTime(tA)} ({Pct(tA, zA + tA)})  /  {who} {FormatTime(tB)} ({Pct(tB, zB + tB)})");
            if (worst != null && worst.Diff > 0)
                Card("Worst zone", FormatDelta(worst.Diff), Red, $"A{worst.Act} {worst.Name}");
            else
                Card("Worst zone", "-", Grey, "no time lost");
            ImGui.EndTable();
        }

        if (partial)
            ImGui.TextColored(Grey, $"Act {lastAct} is in progress: zones not visited yet and its town total are left out of the totals until the act is done.");
        if (LooksTownless(b))
            ImGui.TextColored(TownCol, $"Note: {who} has almost no town time recorded - its town trips are hidden inside neighbouring zones.");
    }

    private static string Pct(double part, double whole) => whole > 0 ? $"{part / whole * 100:0}%" : "0%";

    private static void Card(string title, string value, Vector4 color, string sub)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(Grey, title);
        ImGui.PushFont(ImGui.GetFont(), ImGui.GetFontSize() * 1.6f);
        ImGui.TextColored(color, value);
        ImGui.PopFont();
        ImGui.TextColored(Grey, sub);
    }

    // ------------------------------------------------------------ cumulative graph

    private void DrawCumulativeGraph(List<ZoneRow> rows, string bLabel)
    {
        // x = cumulative comparison-run time along its route, y = cumulative (you - them)
        var pts = new List<(double x, double y, ZoneRow row)> { (0, 0, null) };
        double x = 0, y = 0;
        foreach (var r in rows.Where(r => r.Compared))
        {
            x += Math.Max(r.B, 1);
            y += r.Diff;
            pts.Add((x, y, r));
        }

        if (pts.Count < 3)
        {
            ImGui.TextColored(Grey, "Not enough compared zones for a graph yet.");
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(300, ImGui.GetContentRegionAvail().X), 200);
        ImGui.InvisibleButton("##cumgraph", size);
        var hovered = ImGui.IsItemHovered();

        const float left = 62, right = 12, top = 18, bottom = 20;
        var plotMin = p0 + new Vector2(left, top);
        var plotMax = p0 + size - new Vector2(right, bottom);
        dl.AddRectFilled(p0, p0 + size, U(PanelBg), 6);

        var xMax = Math.Max(1, pts.Max(p => p.x));
        var yHi = Math.Max(10, pts.Max(p => p.y));
        var yLo = Math.Min(-10, pts.Min(p => p.y));
        var pad = (yHi - yLo) * 0.08;
        yHi += pad;
        yLo -= pad;

        Vector2 Map(double px, double py) => new(
            plotMin.X + (float)(px / xMax) * (plotMax.X - plotMin.X),
            plotMin.Y + (float)((yHi - py) / (yHi - yLo)) * (plotMax.Y - plotMin.Y));

        var step = NiceStep((yHi - yLo) / 4);
        for (var v = Math.Ceiling(yLo / step) * step; v <= yHi; v += step)
        {
            var gy = Map(0, v).Y;
            dl.AddLine(new Vector2(plotMin.X, gy), new Vector2(plotMax.X, gy), U(Math.Abs(v) < 0.01 ? AxisCol : GridCol), 1);
            var label = Math.Abs(v) < 0.01 ? "0" : FormatDelta(v);
            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(plotMin.X - ts.X - 6, gy - ts.Y / 2), U(Grey), label);
        }

        var xStep = NiceStep(xMax / 6);
        for (var v = xStep; v < xMax; v += xStep)
        {
            var gx = Map(v, 0).X;
            var label = FormatTime(v);
            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(gx - ts.X / 2, plotMax.Y + 3), U(Grey), label);
        }

        // act boundaries
        var lastAct = -1;
        for (var i = 1; i < pts.Count; i++)
        {
            if (pts[i].row.Act == lastAct) continue;
            lastAct = pts[i].row.Act;
            var gx = Map(pts[i - 1].x, 0).X;
            dl.AddLine(new Vector2(gx, plotMin.Y - 4), new Vector2(gx, plotMax.Y), U(GridCol), 1);
            dl.AddText(new Vector2(gx + 3, p0.Y + 2), U(Grey), $"A{lastAct}");
        }

        for (var i = 1; i < pts.Count; i++)
        {
            var c = pts[i].row.Town ? TownCol : pts[i].row.Diff > 0 ? Red : Green;
            dl.AddLine(Map(pts[i - 1].x, pts[i - 1].y), Map(pts[i].x, pts[i].y), U(c), 2.2f);
        }

        var hot = -1;
        if (hovered)
        {
            var mx = ImGui.GetIO().MousePos.X;
            var best = float.MaxValue;
            for (var i = 1; i < pts.Count; i++)
            {
                // a row's segment spans (x[i-1], x[i]]
                var mid = (Map(pts[i - 1].x, 0).X + Map(pts[i].x, 0).X) / 2;
                var dx = Math.Abs(mid - mx);
                if (dx < best)
                {
                    best = dx;
                    hot = i;
                }
            }
        }

        for (var i = 1; i < pts.Count; i++)
            if (i == hot || pts[i].row.Town)
                dl.AddCircleFilled(Map(pts[i].x, pts[i].y), i == hot ? 5f : 2.5f, U(pts[i].row.Town ? TownCol : pts[i].y <= 0 ? Green : Red));

        if (hot < 0) return;
        var hp = pts[hot];
        var hx = Map(hp.x, 0).X;
        dl.AddLine(new Vector2(hx, plotMin.Y), new Vector2(hx, plotMax.Y), U(AxisCol), 1);
        ImGui.BeginTooltip();
        RowTooltipBody(hp.row, bLabel);
        ImGui.TextColored(hp.y <= 0 ? Green : Red, $"running total {FormatDelta(hp.y)}");
        ImGui.EndTooltip();
    }

    private static double NiceStep(double raw)
    {
        double[] steps = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        foreach (var s in steps)
            if (s >= raw) return s;
        return 3600 * Math.Ceiling(raw / 3600);
    }

    private static uint U(Vector4 c) => ImGui.GetColorU32(c);

    // ------------------------------------------------------------ act bars

    private void DrawActBars(List<ZoneRow> rows, string bLabel)
    {
        var acts = rows.GroupBy(r => r.Act).Select(g =>
        {
            var zones = g.Where(r => !r.Town && r.Compared).ToList();
            var town = g.FirstOrDefault(r => r.Town);
            return (act: g.Key,
                inProgress: g.Any(r => !r.Compared),
                aZone: zones.Sum(r => r.A), bZone: zones.Sum(r => r.B),
                aTown: town?.A ?? 0, bTown: town?.B ?? 0);
        }).ToList();
        if (acts.Count == 0) return;

        var max = Math.Max(1, acts.Max(r => Math.Max(r.aZone + r.aTown, r.bZone + r.bTown)));
        const float labelW = 150, barH = 13, textW = 260;
        var dl = ImGui.GetWindowDrawList();
        var width = Math.Max(200, ImGui.GetContentRegionAvail().X - labelW - textW);
        var who = bLabel.ToLower();

        foreach (var r in acts)
        {
            var p0 = ImGui.GetCursorScreenPos();
            var rowH = barH * 2 + 6;
            ImGui.InvisibleButton("##act" + r.act, new Vector2(labelW + width + textW, rowH));
            var hov = ImGui.IsItemHovered();
            if (hov) dl.AddRectFilled(p0, p0 + new Vector2(labelW + width + textW, rowH), U(PanelBg), 3);

            dl.AddText(p0 + new Vector2(0, 2), U(Gold), $"Act {r.act}");
            if (r.inProgress) dl.AddText(p0 + new Vector2(42, 2), U(Grey), "(so far)");

            var bx = p0.X + labelW;
            void Bar(float by, double zone, double town, float alpha)
            {
                var zw = (float)(zone / max) * width;
                var tw = (float)(town / max) * width;
                dl.AddRectFilled(new Vector2(bx, by), new Vector2(bx + zw, by + barH), U(ZoneCol with { W = alpha }), 2);
                dl.AddRectFilled(new Vector2(bx + zw, by), new Vector2(bx + zw + tw, by + barH), U(TownCol with { W = alpha }), 2);
            }

            Bar(p0.Y, r.aZone, r.aTown, 1f);
            Bar(p0.Y + barH + 3, r.bZone, r.bTown, 0.45f);
            var aTot = r.aZone + r.aTown;
            var bTot = r.bZone + r.bTown;
            dl.AddText(new Vector2(bx + width + 8, p0.Y - 1), U(new Vector4(1, 1, 1, 1)), $"you  {FormatTime(aTot)}");
            dl.AddText(new Vector2(bx + width + 8, p0.Y + barH + 2), U(Grey), $"{who}  {FormatTime(bTot)}");
            if (!r.inProgress)
            {
                var d = aTot - bTot;
                dl.AddText(new Vector2(bx + width + 150, p0.Y - 1), U(d <= 0 ? Green : Red), "total " + FormatDelta(d));
            }

            var td = r.aTown - r.bTown;
            dl.AddText(new Vector2(bx + width + 150, p0.Y + barH + 2), U(r.inProgress || Math.Abs(td) < 3 ? Grey : td <= 0 ? Green : TownCol),
                "town " + (r.inProgress ? "so far" : FormatDelta(td)));

            if (hov)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(Gold, $"Act {r.act}{(r.inProgress ? " (in progress - zones you've visited so far)" : "")}");
                ImGui.Text($"you:  zones {FormatTime(r.aZone)}  town {FormatTime(r.aTown)}  = {FormatTime(aTot)}");
                ImGui.Text($"{who}:  zones {FormatTime(r.bZone)}  town {FormatTime(r.bTown)}{(r.inProgress ? " (whole act)" : "")}  = {FormatTime(bTot)}");
                ImGui.EndTooltip();
            }
        }
    }

    // ------------------------------------------------------------ zone table

    private static readonly string[] RowModes = { "Biggest losses", "Biggest gains", "Route order" };

    private void DrawZoneTable(List<ZoneRow> all, string bLabel)
    {
        ImGui.SetNextItemWidth(160);
        ImGui.Combo("##rowmode", ref _rowMode, RowModes, RowModes.Length);
        if (_rowMode != 2)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.SliderInt("rows##rowcount", ref _rowCount, 5, 40);
        }

        var cmp = all.Where(r => r.Compared).ToList();
        var rows = _rowMode switch
        {
            0 => cmp.Where(r => r.Diff > 0.5).OrderByDescending(r => r.Diff).Take(_rowCount).ToList(),
            1 => cmp.Where(r => r.Diff < -0.5).OrderBy(r => r.Diff).Take(_rowCount).ToList(),
            _ => all,
        };
        if (rows.Count == 0)
        {
            ImGui.TextColored(Grey, _rowMode == 0 ? "No zone slower than the comparison - nice." : "Nothing here yet.");
            return;
        }

        var maxAbs = Math.Max(1, cmp.Select(r => Math.Abs(r.Diff)).DefaultIfEmpty(1).Max());
        const float barW = 200;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        var height = Math.Min(460, 30 + rows.Count * 24);
        if (!ImGui.BeginTable("zonecmp", 7, flags, new Vector2(0, height))) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Act");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("You");
        ImGui.TableSetupColumn(bLabel);
        ImGui.TableSetupColumn("Diff", ImGuiTableColumnFlags.WidthFixed, barW);
        ImGui.TableSetupColumn("");
        ImGui.TableSetupColumn("Visits (you / " + bLabel.ToLower() + ")");
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(r.Act.ToString());
            ImGui.TableNextColumn();
            if (r.Town) ImGui.TextColored(TownCol, r.Name);
            else if (!r.HasB) ImGui.TextColored(Grey, r.Name + $" (not in {bLabel.ToLower()})");
            else if (!r.HasA) ImGui.TextColored(Grey, r.Name + (r.Compared ? " (skipped)" : " (not yet)"));
            else ImGui.Text(r.Name);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                RowTooltipBody(r, bLabel);
                ImGui.EndTooltip();
            }

            ImGui.TableNextColumn();
            ImGui.Text(r.HasA || r.Compared ? FormatTime(r.A) + (r.Live ? "+" : "") : "-");
            ImGui.TableNextColumn();
            ImGui.Text(r.HasB || r.Compared ? FormatTime(r.B) : "-");

            ImGui.TableNextColumn();
            if (r.Compared)
            {
                var dl = ImGui.GetWindowDrawList();
                var p = ImGui.GetCursorScreenPos();
                var h = ImGui.GetTextLineHeight();
                var mid = p.X + barW / 2;
                var w = (float)(Math.Abs(r.Diff) / maxAbs) * (barW / 2 - 2);
                dl.AddLine(new Vector2(mid, p.Y), new Vector2(mid, p.Y + h), U(AxisCol), 1);
                if (r.Diff > 0) dl.AddRectFilled(new Vector2(mid, p.Y + 2), new Vector2(mid + w, p.Y + h - 2), U(r.Town ? TownCol : Red), 2);
                else dl.AddRectFilled(new Vector2(mid - w, p.Y + 2), new Vector2(mid, p.Y + h - 2), U(Green), 2);
                ImGui.Dummy(new Vector2(barW, h));
            }
            else ImGui.TextColored(Grey, r.Town ? "act in progress" : "not yet");

            ImGui.TableNextColumn();
            if (r.Compared) ImGui.TextColored(r.Diff <= 0 ? Green : Red, FormatDelta(r.Diff));

            ImGui.TableNextColumn();
            var extra = r.VisitsA > r.VisitsB && !r.Town;
            ImGui.TextColored(extra ? TownCol : Grey, $"{r.VisitsA} / {r.VisitsB}{(extra ? "  (backtracked?)" : "")}");
        }

        ImGui.EndTable();
    }

    private static void RowTooltipBody(ZoneRow r, string bLabel)
    {
        var who = bLabel.ToLower();
        ImGui.TextColored(r.Town ? TownCol : Gold, $"Act {r.Act}: {r.Name}");
        ImGui.Text($"you {FormatTime(r.A)}{(r.Live ? " (still here)" : "")} in {r.VisitsA} visit(s)");
        ImGui.Text($"{who} {FormatTime(r.B)} in {r.VisitsB} visit(s)");
        if (r.Compared) ImGui.TextColored(r.Diff <= 0 ? Green : Red, $"difference {FormatDelta(r.Diff)}");
        else ImGui.TextColored(Grey, "not compared yet (act in progress)");
    }
}
