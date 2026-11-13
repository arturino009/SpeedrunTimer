using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace SpeedrunTimer;

public partial class SpeedrunTimer
{
    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Green = new(0.35f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Red = new(0.92f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Gold = new(1f, 0.8f, 0.25f, 1f);

    private string _newGroupName = "";
    private string _renameBuffer;
    private string _renameForGroup;
    private string _armedDelete; // id of the thing waiting for a confirming second click
    private double _armedAt;
    private string _detailsRunId;
    private bool _onlyFinished;

    public override void DrawSettings()
    {
        base.DrawSettings();
        if (_store == null) return;
        var db = _store.Db;
        db.EnsureActiveGroup();

        ImGui.Separator();
        DrawActiveRun();
        DrawGroupControls(db);

        var group = db.ActiveGroup;
        var runs = db.RunsIn(group.Id).OrderByDescending(r => r.StartedUtc).ToList();
        var ownRuns = runs.Where(r => !r.IsReference).ToList();

        DrawComparisonControls(db, group, runs);

        if (ImGui.CollapsingHeader("Compare runs###compare", ImGuiTreeNodeFlags.DefaultOpen))
            DrawCompareSection(db, group);

        if (ImGui.CollapsingHeader($"Statistics: {group.Name}###stats"))
            DrawStats(db, group, ownRuns);

        if (ImGui.CollapsingHeader($"Zone breakdown: {group.Name}###zones"))
            DrawZoneBreakdown(db, group, ownRuns);

        if (ImGui.CollapsingHeader($"Runs: {group.Name} ({runs.Count})###runs", ImGuiTreeNodeFlags.DefaultOpen))
            DrawRunList(db, group, runs);
    }

    // ------------------------------------------------------------ comparison + import

    private string _importPath = "";
    private string _importName = "";
    private string _importStatus;
    private bool _importOk;

    private static string RunLabel(Run r) =>
        $"{(r.IsReference ? "[Ref] " : "")}{r.Character} - {FormatTime(r.TotalSeconds)}{(r.Status == RunStatus.Finished ? "" : " (unfinished)")} ({r.StartedUtc.ToLocalTime():yyyy-MM-dd})";

    private void DrawComparisonControls(RunDatabase db, RunGroup group, List<Run> runs)
    {
        var target = db.ComparisonTarget(group.Id);
        var usingPb = string.IsNullOrEmpty(group.CompareRunId) || target == null || target.Id != group.CompareRunId;
        var preview = usingPb ? $"Personal best{(target != null ? " - " + FormatTime(target.TotalSeconds) : " (none yet)")}" : RunLabel(target);

        ImGui.Text("Compare against:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("##compare", preview))
        {
            if (ImGui.Selectable("Personal best", usingPb))
                SetCompareTarget(group, "");
            foreach (var r in runs.Where(r => r != _active && r.Segments.Count > 0)
                         .OrderByDescending(r => r.IsReference).ThenByDescending(r => r.Status == RunStatus.Finished).ThenBy(r => r.TotalSeconds))
                if (ImGui.Selectable(RunLabel(r) + "##cmp" + r.Id, !usingPb && r.Id == group.CompareRunId))
                    SetCompareTarget(group, r.Id);
            ImGui.EndCombo();
        }

        if (ImGui.TreeNode("Import reference run (CSV timeline)"))
        {
            ImGui.TextColored(Grey, "Columns: start,end,duration,duration_s,area (e.g. areas_clean.csv from the video OCR scripts).");
            ImGui.TextColored(Grey, "Only the campaign part is imported (stops at the first map / Karui Shores). Must be logged in.");
            ImGui.SetNextItemWidth(460);
            ImGui.InputTextWithHint("File##importpath", @"C:\...\areas_clean.csv", ref _importPath, 512);
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("Name##importname", "e.g. Streamer RF 3:00", ref _importName, 64);
            if (ImGui.Button($"Import into '{group.Name}'") && !string.IsNullOrWhiteSpace(_importPath))
                DoImport(db, group);
            if (_importStatus != null)
                ImGui.TextColored(_importOk ? Green : Red, _importStatus);
            ImGui.TreePop();
        }

        ImGui.Separator();
    }

    private void SetCompareTarget(RunGroup group, string runId)
    {
        group.CompareRunId = runId;
        _pbDirty = true;
        RecomputeLiveDelta();
        _store.Save();
    }

    /// <summary>After the comparison target changes, recompute the overlay delta for the active run.</summary>
    private void RecomputeLiveDelta()
    {
        _lastDelta = null;
        if (_active == null) return;
        EnsurePbCache();
        var seen = new HashSet<string>();
        foreach (var s in _active.Segments)
            if (seen.Add(s.Key) && _pbFirstEntry.TryGetValue(s.Key, out var at))
                _lastDelta = s.StartAt - at;
    }

    private void DoImport(RunDatabase db, RunGroup group)
    {
        var path = _importPath.Trim().Trim('"');
        var name = string.IsNullOrWhiteSpace(_importName) ? Path.GetFileNameWithoutExtension(path) : _importName.Trim();
        List<ExileCore.PoEMemory.MemoryObjects.WorldArea> areas;
        try { areas = GameController.Files.WorldAreas.EntriesList; }
        catch { areas = new(); }

        var res = ReferenceImporter.Import(path, areas, group.Id, name);
        if (res.Run == null)
        {
            _importOk = false;
            _importStatus = "Import failed: " + res.Error;
            return;
        }

        db.Runs.Add(res.Run);
        SetCompareTarget(group, res.Run.Id);
        _importOk = true;
        _importStatus = $"Imported '{name}': {res.RowsUsed}/{res.RowsRead} rows, {FormatTime(res.Run.TotalSeconds)}" +
                        (res.StoppedAt != null ? $", stopped at '{res.StoppedAt}'" : "") +
                        (res.UnknownNames.Count > 0 ? $". Unknown names: {string.Join(", ", res.UnknownNames.Distinct().Take(8))}" : "") +
                        ". Now used as comparison target.";
    }

    // ------------------------------------------------------------ active run

    private void DrawActiveRun()
    {
        ImGui.Text(string.IsNullOrEmpty(_charName) ? "Active run:" : $"Active run ({_charName}):");
        ImGui.SameLine();
        if (_active == null)
        {
            ImGui.TextColored(Grey, "none");
            if (!string.IsNullOrEmpty(_charName))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Start run for {_charName}"))
                {
                    StartRun(_charName);
                    if (InWorld) OpenSegmentForCurrentArea();
                }
            }
        }
        else
        {
            var groupName = _store.Db.Groups.FirstOrDefault(g => g.Id == _active.GroupId)?.Name ?? "?";
            ImGui.TextColored(Gold, $"{FormatTime(_activeTotal)} (town {FormatTime(_activeTown)}) in '{groupName}'");

            if (ImGui.SmallButton("Finish now")) FinishActive(RunStatus.Finished);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop (keep as unfinished)")) FinishActive(RunStatus.Abandoned);
            ImGui.SameLine();
            if (ConfirmButton("Discard run", "discard-active"))
            {
                _store.Db.Runs.Remove(_active);
                _active = null;
                _curSeg = null;
                RebuildActiveCaches();
                _store.Save();
            }
        }

        // other characters' runs wait here and resume when you log into that character
        var paused = _store.Db.Runs.Where(r => r.Status == RunStatus.InProgress && r != _active).ToList();
        foreach (var r in paused)
        {
            ImGui.PushID("paused" + r.Id);
            var groupName = _store.Db.Groups.FirstOrDefault(g => g.Id == r.GroupId)?.Name ?? "?";
            ImGui.TextColored(Grey, $"Paused: {r.Character} - {FormatTime(r.TotalSeconds)} in '{groupName}' (resumes on login)");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop (keep as unfinished)"))
            {
                r.Status = RunStatus.Abandoned;
                r.EndedUtc = DateTime.UtcNow;
                _store.Save();
            }

            ImGui.PopID();
        }
    }

    // ------------------------------------------------------------ groups

    private void DrawGroupControls(RunDatabase db)
    {
        ImGui.Separator();
        ImGui.Text("Build group (new runs go here, stats below are for this group):");

        var active = db.ActiveGroup;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##group", $"{active.Name} ({db.RunsIn(active.Id).Count()} runs)"))
        {
            foreach (var g in db.Groups)
            {
                if (ImGui.Selectable($"{g.Name} ({db.RunsIn(g.Id).Count()} runs)##{g.Id}", g.Id == active.Id))
                {
                    db.ActiveGroupId = g.Id;
                    _pbDirty = true;
                    _detailsRunId = null;
                    _store.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (_active != null && _active.GroupId != active.Id)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Move active run here"))
            {
                _active.GroupId = active.Id;
                _pbDirty = true;
                _lastDelta = null;
                _store.Save();
            }
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##newgroup", "new group name, e.g. RF Jugg", ref _newGroupName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Create group") && !string.IsNullOrWhiteSpace(_newGroupName))
        {
            var g = new RunGroup { Name = _newGroupName.Trim() };
            db.Groups.Add(g);
            db.ActiveGroupId = g.Id;
            _newGroupName = "";
            _pbDirty = true;
            _store.Save();
        }

        if (_renameForGroup != active.Id)
        {
            _renameForGroup = active.Id;
            _renameBuffer = active.Name;
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##rename", ref _renameBuffer, 64);
        ImGui.SameLine();
        if (ImGui.Button("Rename") && !string.IsNullOrWhiteSpace(_renameBuffer))
        {
            active.Name = _renameBuffer.Trim();
            _store.Save();
        }

        ImGui.SameLine();
        if (ConfirmButton($"Delete group + its {db.RunsIn(active.Id).Count()} runs", "group-" + active.Id))
        {
            if (_active != null && _active.GroupId == active.Id)
            {
                _active = null;
                _curSeg = null;
            }

            db.Runs.RemoveAll(r => r.GroupId == active.Id);
            db.Groups.Remove(active);
            db.ActiveGroupId = "";
            db.EnsureActiveGroup();
            RebuildActiveCaches();
            _store.Save();
        }

        ImGui.Separator();
    }

    // ------------------------------------------------------------ stats

    /// <summary>The comparison target when it is something other than your PB (reference or a chosen run), else null.</summary>
    private static Run ExtraTarget(RunDatabase db, string groupId)
    {
        var cmp = db.ComparisonTarget(groupId);
        return cmp != null && cmp != db.PersonalBest(groupId) ? cmp : null;
    }

    private static string TargetLabel(Run r) => r.IsReference ? "Ref" : "Target";

    private void DrawStats(RunDatabase db, RunGroup group, List<Run> runs)
    {
        var finished = runs.Where(r => r.Status == RunStatus.Finished).ToList();
        var target = ExtraTarget(db, group.Id);
        ImGui.Text($"Runs: {runs.Count}   finished: {finished.Count}   unfinished: {runs.Count(r => r.Status == RunStatus.Abandoned)}");
        if (target != null)
        {
            ImGui.Text($"Comparing against {RunLabel(target)}   town {FormatTime(target.TownSeconds)} ({target.TownSeconds / Math.Max(1, target.TotalSeconds) * 100:0}%)");
        }

        if (finished.Count == 0 && target == null)
        {
            ImGui.TextColored(Grey, "No finished runs yet - PB, averages and deltas appear once a run is finished.");
            return;
        }

        var pb = db.PersonalBest(group.Id);
        if (finished.Count > 0)
        {
            var totals = finished.Select(r => r.TotalSeconds).OrderBy(x => x).ToList();
            var median = totals.Count % 2 == 1 ? totals[totals.Count / 2] : (totals[totals.Count / 2 - 1] + totals[totals.Count / 2]) / 2;
            var avgTown = finished.Average(r => r.TownSeconds);

            ImGui.Text("Personal best:");
            ImGui.SameLine();
            ImGui.TextColored(Gold, $"{FormatTime(pb.TotalSeconds)}  ({pb.Character}, {pb.StartedUtc.ToLocalTime():yyyy-MM-dd})");
            if (target != null)
            {
                ImGui.SameLine();
                var d = pb.TotalSeconds - target.TotalSeconds;
                ImGui.TextColored(d <= 0 ? Green : Red, $"{FormatDelta(d)} vs {TargetLabel(target)}");
            }

            ImGui.Text($"Average: {FormatTime(totals.Average())}   Median: {FormatTime(median)}   Last: {FormatTime(finished.First().TotalSeconds)}");
            ImGui.Text($"Avg town time: {FormatTime(avgTown)} ({avgTown / totals.Average() * 100:0.0}%)   PB town time: {FormatTime(pb.TownSeconds)}");
        }

        // per-act splits
        var perRun = finished.Select(r => r.TimePerAct()).ToList();
        var pbActs = pb?.TimePerAct() ?? new Dictionary<int, double>();
        var tgtActs = target?.TimePerAct() ?? new Dictionary<int, double>();
        var acts = perRun.SelectMany(d => d.Keys).Concat(tgtActs.Keys).Distinct().OrderBy(a => a).ToList();
        double sumOfBest = 0;

        var cols = target != null ? 7 : 5;
        if (ImGui.BeginTable("acts", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Act");
            ImGui.TableSetupColumn("PB run");
            ImGui.TableSetupColumn("Best");
            ImGui.TableSetupColumn("Average");
            ImGui.TableSetupColumn("PB vs best");
            if (target != null)
            {
                ImGui.TableSetupColumn(TargetLabel(target));
                ImGui.TableSetupColumn($"PB vs {TargetLabel(target)}");
            }

            ImGui.TableHeadersRow();
            foreach (var act in acts)
            {
                var vals = perRun.Where(d => d.ContainsKey(act)).Select(d => d[act]).ToList();
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(act >= 11 ? "endgame" : act.ToString());
                ImGui.TableNextColumn(); ImGui.Text(pbActs.TryGetValue(act, out var pbAct) ? FormatTime(pbAct) : "-");
                if (vals.Count > 0)
                {
                    var best = vals.Min();
                    sumOfBest += best;
                    ImGui.TableNextColumn(); ImGui.TextColored(Gold, FormatTime(best));
                    ImGui.TableNextColumn(); ImGui.Text(FormatTime(vals.Average()));
                    ImGui.TableNextColumn();
                    var lost = pbAct - best;
                    if (pb == null || !pbActs.ContainsKey(act)) ImGui.Text("-");
                    else if (lost > 0.5) ImGui.TextColored(Red, FormatDelta(lost));
                    else ImGui.TextColored(Green, "best");
                }
                else
                {
                    ImGui.TableNextColumn(); ImGui.Text("-");
                    ImGui.TableNextColumn(); ImGui.Text("-");
                    ImGui.TableNextColumn(); ImGui.Text("-");
                }

                if (target != null)
                {
                    ImGui.TableNextColumn();
                    if (tgtActs.TryGetValue(act, out var t))
                    {
                        ImGui.Text(FormatTime(t));
                        ImGui.TableNextColumn();
                        if (pbActs.ContainsKey(act))
                        {
                            var d = pbAct - t;
                            ImGui.TextColored(d <= 0 ? Green : Red, FormatDelta(d));
                        }
                        else ImGui.Text("-");
                    }
                    else
                    {
                        ImGui.Text("-");
                        ImGui.TableNextColumn(); ImGui.Text("-");
                    }
                }
            }

            ImGui.EndTable();
        }

        if (pb != null)
        {
            ImGui.Text("Sum of best acts:");
            ImGui.SameLine();
            ImGui.TextColored(Gold, $"{FormatTime(sumOfBest)}  (possible time save vs PB: {FormatTime(pb.TotalSeconds - sumOfBest)})");
        }
    }

    private void DrawZoneBreakdown(RunDatabase db, RunGroup group, List<Run> runs)
    {
        var source = runs.Where(r => r.Status == RunStatus.Finished ||
                                     (Settings.ZoneStatsIncludeUnfinished && r.Status == RunStatus.Abandoned)).ToList();
        var target = ExtraTarget(db, group.Id);
        if (source.Count == 0 && target == null)
        {
            ImGui.TextColored(Grey, "No runs in this group yet.");
            return;
        }

        var pb = db.PersonalBest(group.Id);
        var pbTimes = pb?.TimePerArea() ?? new Dictionary<string, double>();
        var tgtTimes = target?.TimePerArea() ?? new Dictionary<string, double>();

        // aggregate per zone key: name, act, per-run total, order of first entry
        var rows = new Dictionary<string, (string name, int act, List<double> times, List<double> firstAt, bool town)>();

        void Add(Run run, bool countTime)
        {
            var first = run.FirstEntryTimes();
            foreach (var (key, secs) in run.TimePerArea())
            {
                var seg = run.Segments.First(s => s.Key == key);
                if (!rows.TryGetValue(key, out var row))
                    rows[key] = row = (seg.AreaName, seg.Act, new List<double>(), new List<double>(), seg.IsTown);
                if (countTime) row.times.Add(secs);
                row.firstAt.Add(first[key]);
            }
        }

        foreach (var run in source) Add(run, true);
        if (target != null) Add(target, false);

        ImGui.TextColored(Grey, "Time per zone = all visits to that zone summed within a run. 'PB lost' = PB time minus your best in that zone.");
        var cols = target != null ? 9 : 7;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("zones", cols, flags, new Vector2(0, 400)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Act");
            ImGui.TableSetupColumn("Zone");
            ImGui.TableSetupColumn("Runs");
            ImGui.TableSetupColumn("Best");
            ImGui.TableSetupColumn("Average");
            ImGui.TableSetupColumn("PB");
            ImGui.TableSetupColumn("PB lost");
            if (target != null)
            {
                ImGui.TableSetupColumn(TargetLabel(target));
                ImGui.TableSetupColumn($"PB vs {TargetLabel(target)}");
            }

            ImGui.TableHeadersRow();

            foreach (var (key, row) in rows.OrderBy(kv => kv.Value.act).ThenBy(kv => kv.Value.firstAt.Average()))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(row.act >= 11 ? "end" : row.act.ToString());
                ImGui.TableNextColumn();
                if (row.town) ImGui.TextColored(Grey, row.name + " (town)");
                else ImGui.Text(row.name);
                ImGui.TableNextColumn(); ImGui.Text(row.times.Count.ToString());
                if (row.times.Count > 0)
                {
                    ImGui.TableNextColumn(); ImGui.TextColored(Gold, FormatTime(row.times.Min()));
                    ImGui.TableNextColumn(); ImGui.Text(FormatTime(row.times.Average()));
                }
                else
                {
                    ImGui.TableNextColumn(); ImGui.TextColored(Grey, "-");
                    ImGui.TableNextColumn(); ImGui.TextColored(Grey, "-");
                }

                var hasPb = pbTimes.TryGetValue(key, out var pbT);
                ImGui.TableNextColumn();
                if (hasPb) ImGui.Text(FormatTime(pbT));
                else ImGui.TextColored(Grey, "-");
                ImGui.TableNextColumn();
                if (hasPb && row.times.Count > 0)
                {
                    var lost = pbT - row.times.Min();
                    if (lost > 1) ImGui.TextColored(Red, FormatDelta(lost));
                    else ImGui.TextColored(Green, "best");
                }
                else ImGui.TextColored(Grey, "-");

                if (target != null)
                {
                    ImGui.TableNextColumn();
                    if (tgtTimes.TryGetValue(key, out var t))
                    {
                        ImGui.Text(FormatTime(t));
                        ImGui.TableNextColumn();
                        if (hasPb) ImGui.TextColored(pbT <= t ? Green : Red, FormatDelta(pbT - t));
                        else ImGui.TextColored(Grey, "-");
                    }
                    else
                    {
                        ImGui.TextColored(Grey, "-");
                        ImGui.TableNextColumn(); ImGui.TextColored(Grey, "-");
                    }
                }
            }

            ImGui.EndTable();
        }
    }

    // ------------------------------------------------------------ run list

    private void DrawRunList(RunDatabase db, RunGroup group, List<Run> runs)
    {
        ImGui.Checkbox("Only finished runs", ref _onlyFinished);
        var pb = db.PersonalBest(group.Id);
        var cmp = db.ComparisonTarget(group.Id);
        var cmpLabel = cmp == null || cmp == pb ? "PB" : TargetLabel(cmp);
        var shown = _onlyFinished ? runs.Where(r => r.Status == RunStatus.Finished).ToList() : runs;

        Run toDelete = null;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("runs", 9, flags, new Vector2(0, Math.Min(420, 28 + shown.Count * 24))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Date");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Lvl");
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("vs " + cmpLabel);
            ImGui.TableSetupColumn("Town");
            ImGui.TableSetupColumn("Group");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var run in shown)
            {
                ImGui.PushID(run.Id);
                var isActive = run == _active;
                var total = isActive ? _activeTotal : run.TotalSeconds;
                var town = isActive ? _activeTown : run.TownSeconds;

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableNextColumn(); ImGui.Text(run.Character);
                ImGui.TableNextColumn();
                if (isActive) ImGui.TextColored(Gold, "running");
                else if (run.IsReference) ImGui.TextColored(Gold, run == cmp ? "reference *" : "reference");
                else if (run == pb) ImGui.TextColored(Gold, "PB");
                else if (run.Status == RunStatus.Finished) ImGui.TextColored(Green, "finished");
                else if (run.Status == RunStatus.InProgress) ImGui.TextColored(Gold, "paused");
                else ImGui.TextColored(Grey, "unfinished");
                ImGui.TableNextColumn(); ImGui.Text(run.IsReference ? "-" : run.FinalLevel.ToString());
                ImGui.TableNextColumn(); ImGui.Text(FormatTime(total));
                ImGui.TableNextColumn();
                if (cmp != null && run != cmp && run.Status == RunStatus.Finished)
                {
                    var d = total - cmp.TotalSeconds;
                    ImGui.TextColored(d <= 0 ? Green : Red, FormatDelta(d));
                }
                else ImGui.TextColored(Grey, "-");
                ImGui.TableNextColumn(); ImGui.Text($"{FormatTime(town)} ({(total > 0 ? town / total * 100 : 0):0}%)");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(130);
                if (ImGui.BeginCombo("##move", group.Name))
                {
                    foreach (var g in db.Groups)
                    {
                        if (ImGui.Selectable(g.Name + "##" + g.Id, g.Id == run.GroupId) && g.Id != run.GroupId)
                        {
                            run.GroupId = g.Id;
                            if (group.CompareRunId == run.Id) group.CompareRunId = "";
                            _pbDirty = true;
                            if (isActive) RecomputeLiveDelta();
                            _store.Save();
                        }
                    }

                    ImGui.EndCombo();
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Compare"))
                {
                    if (run.IsReference) _cmpBId = run.Id;
                    else _cmpAId = run.Id;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(run.IsReference ? "Use as 'vs' in Compare runs" : "Show this run in Compare runs (at the top)");
                ImGui.SameLine();
                if (ImGui.SmallButton(_detailsRunId == run.Id ? "Hide" : "Details"))
                    _detailsRunId = _detailsRunId == run.Id ? null : run.Id;
                ImGui.SameLine();
                if (ConfirmButton("Delete", "run-" + run.Id))
                    toDelete = run;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (toDelete != null)
        {
            if (toDelete == _active)
            {
                _active = null;
                _curSeg = null;
                RebuildActiveCaches();
            }

            db.Runs.Remove(toDelete);
            if (group.CompareRunId == toDelete.Id) group.CompareRunId = "";
            if (_detailsRunId == toDelete.Id) _detailsRunId = null;
            _pbDirty = true;
            RecomputeLiveDelta();
            _store.Save();
        }

        var details = runs.FirstOrDefault(r => r.Id == _detailsRunId);
        if (details != null) DrawRunDetails(details, cmp, cmpLabel);
    }

    private void DrawRunDetails(Run run, Run cmp, string cmpLabel)
    {
        ImGui.Separator();
        ImGui.TextColored(Gold, $"Run details: {run.Character}, {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

        var note = run.Note ?? "";
        ImGui.SetNextItemWidth(400);
        if (ImGui.InputTextWithHint("Note", "e.g. new route, died to Brutus", ref note, 512))
            run.Note = note;
        if (ImGui.IsItemDeactivatedAfterEdit()) _store.Save();

        if (run != _active && !run.IsReference)
        {
            if (run.Status != RunStatus.Finished && ImGui.SmallButton("Mark as finished"))
            {
                run.Status = RunStatus.Finished;
                run.EndedUtc ??= DateTime.UtcNow;
                _pbDirty = true;
                _store.Save();
            }
            else if (run.Status == RunStatus.Finished && ImGui.SmallButton("Mark as unfinished (exclude from PB/stats)"))
            {
                run.Status = RunStatus.Abandoned;
                _pbDirty = true;
                _store.Save();
            }
        }

        var cmpFirst = cmp != null && cmp != run ? cmp.FirstEntryTimes() : null;
        var seen = new HashSet<string>();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("segments", 7, flags, new Vector2(0, 350)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#");
            ImGui.TableSetupColumn("Act");
            ImGui.TableSetupColumn("Zone");
            ImGui.TableSetupColumn("Entered at");
            ImGui.TableSetupColumn("vs " + cmpLabel);
            ImGui.TableSetupColumn("Time here");
            ImGui.TableSetupColumn("Lvl");
            ImGui.TableHeadersRow();

            for (var i = 0; i < run.Segments.Count; i++)
            {
                var s = run.Segments[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text((i + 1).ToString());
                ImGui.TableNextColumn(); ImGui.Text(s.Act >= 11 ? "end" : s.Act.ToString());
                ImGui.TableNextColumn();
                if (s.IsTown) ImGui.TextColored(Grey, s.AreaName + " (town)");
                else ImGui.Text(s.AreaName);
                ImGui.TableNextColumn(); ImGui.Text(FormatTime(s.StartAt));
                ImGui.TableNextColumn();
                if (seen.Add(s.Key) && cmpFirst != null && cmpFirst.TryGetValue(s.Key, out var at))
                {
                    var d = s.StartAt - at;
                    ImGui.TextColored(d <= 0 ? Green : Red, FormatDelta(d));
                }
                else ImGui.Text("");
                ImGui.TableNextColumn(); ImGui.Text(FormatTime(s.Seconds));
                ImGui.TableNextColumn(); ImGui.Text(run.IsReference ? "-" : s.LevelOnEnter.ToString());
            }

            ImGui.EndTable();
        }
    }

    /// <summary>Button that needs a second click within 3 seconds to fire.</summary>
    private bool ConfirmButton(string label, string key)
    {
        var armed = _armedDelete == key && Now - _armedAt < 3;
        if (armed) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.15f, 0.15f, 1f));
        var clicked = ImGui.SmallButton((armed ? "Click again to confirm" : label) + "##" + key);
        if (armed) ImGui.PopStyleColor();
        if (!clicked) return false;
        if (armed)
        {
            _armedDelete = null;
            return true;
        }

        _armedDelete = key;
        _armedAt = Now;
        return false;
    }
}
