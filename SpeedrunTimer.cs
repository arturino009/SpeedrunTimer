using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace SpeedrunTimer;

public partial class SpeedrunTimer : BaseSettingsPlugin<SpeedrunTimerSettings>
{
    private const string TwilightStrandId = "1_1_1";
    private const string KaruiShoresId = "2_11_endgame_town";

    private RunStore _store;
    private Run _active;
    private Segment _curSeg;

    // timing
    private long _lastTickTs;
    private double _pendingLoad;
    private double _outOfGameSeconds;
    private bool _areaDirty = true;
    private double _lastSaveAt;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // player snapshot (read in Tick only)
    private string _charName = "";
    private int _charLevel;

    // personal-best comparison cache for the active run's group
    private bool _pbDirty = true;
    private string _pbGroupId;
    private Run _pbRun;
    private Dictionary<string, double> _pbFirstEntry = new();
    private Dictionary<string, double> _pbAreaTimes = new();

    // overlay state, computed in Tick
    private double _activeTotal;
    private double _activeTown;
    private double? _lastDelta;
    private string _lastDeltaArea = "";
    private HashSet<string> _seenAreas = new();
    private Dictionary<string, double> _activeAreaTimes = new();
    private Run _justFinished;
    private bool _justFinishedWasPb;
    private double _justFinishedAt;

    public override bool Initialise()
    {
        Name = "Speedrun Timer";
        _store = new RunStore(Path.Combine(ConfigDirectory, "runs.json"));
        var err = _store.Load();
        if (err != null)
            DebugWindow.LogError($"{Name}: runs.json could not be read ({err}); a backup copy was made and a new history started.");

        // one in-progress run per character (each resumes when you log into that character);
        // duplicates can only come from a crash/manual edit: keep the newest
        foreach (var dupes in _store.Db.Runs.Where(r => r.Status == RunStatus.InProgress).GroupBy(r => r.Character))
        foreach (var r in dupes.OrderByDescending(r => r.StartedUtc).Skip(1))
            r.Status = RunStatus.Abandoned;

        _active = null; // picked up once we know which character is logged in (SwitchCharacter)
        RebuildActiveCaches();
        _lastTickTs = Stopwatch.GetTimestamp();
        return true;
    }

    public override void OnClose()
    {
        _store?.Save();
        _store?.Flush();
    }

    public override void OnUnload()
    {
        _store?.Save();
        _store?.Flush();
    }

    public override void AreaChange(AreaInstance area)
    {
        _areaDirty = true;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Actually in the game world: InGame alone stays true on the character select screen.</summary>
    private bool InWorld
    {
        get
        {
            try
            {
                var g = GameController.Game;
                return GameController.InGame && !g.IsSelectCharacterState && !g.IsLoginState;
            }
            catch
            {
                return false;
            }
        }
    }

    public override Job Tick()
    {
        var ts = Stopwatch.GetTimestamp();
        var delta = (ts - _lastTickTs) / (double)Stopwatch.Frequency;
        _lastTickTs = ts;
        // a long freeze (game closed, HUD restarted, AFK at character select) is not counted
        if (delta > Settings.MaxGapSeconds.Value || delta < 0) delta = 0;

        HandleHotkeys();

        var inGame = InWorld;
        var loading = GameController.IsLoading;

        if (!inGame && !loading)
        {
            // login screen / character select: real time keeps running for a while (logout tricks),
            // but stop counting once it's clearly a break. If you come back on a different character
            // this pending time is dropped (SwitchCharacter), otherwise it goes to the zone you log into.
            _outOfGameSeconds += delta;
            if (_outOfGameSeconds <= Settings.MaxGapSeconds.Value) _pendingLoad += delta;
            return null;
        }

        _outOfGameSeconds = 0;

        if (loading)
        {
            _pendingLoad += delta;
            return null;
        }

        ReadPlayer();
        XpTick();

        if (_areaDirty && !string.IsNullOrEmpty(_charName))
        {
            _areaDirty = false;
            OnEnteredArea();
        }

        if (_active != null && _curSeg != null)
        {
            var add = delta + _pendingLoad;
            _curSeg.Seconds += add;
            _activeTotal += add;
            if (_curSeg.IsTown) _activeTown += add;
            _activeAreaTimes[_curSeg.Key] = _activeAreaTimes.GetValueOrDefault(_curSeg.Key) + add;
            if (_charLevel > _active.FinalLevel) _active.FinalLevel = _charLevel;
        }

        _pendingLoad = 0;

        if (_active != null && Now - _lastSaveAt > 30)
        {
            _lastSaveAt = Now;
            _store.Save();
        }

        return null;
    }

    private void ReadPlayer()
    {
        try
        {
            var p = GameController.Player?.GetComponent<Player>();
            if (p == null) return;
            if (!string.IsNullOrEmpty(p.PlayerName) && p.PlayerName != _charName) SwitchCharacter(p.PlayerName);
            _charLevel = p.Level;
            if (p.XP > 0) _playerXp = p.XP;
        }
        catch
        {
            // player not ready this frame
        }
    }

    /// <summary>
    /// Logged into a different character (or first sight of one after a HUD start): park the previous
    /// character's run as-is and resume this character's in-progress run, if it has one.
    /// </summary>
    private void SwitchCharacter(string name)
    {
        if (_active != null) _store.Save();

        _charName = name;
        _active = _store.Db.Runs.FirstOrDefault(r => r.Status == RunStatus.InProgress && r.Character == name);
        _curSeg = null;
        _pendingLoad = 0; // character-select time doesn't belong to the character you switched to
        _playerXp = 0;
        _xpSamples.Clear();
        RebuildActiveCaches();
        RecomputeLiveDelta();
        _areaDirty = true; // open a segment for wherever this character is
        if (_active != null)
            DebugWindow.LogMsg($"{Name}: resumed run for {name} at {FormatTime(_activeTotal)}");
    }

    private void HandleHotkeys()
    {
        if (Settings.ToggleOverlayKey.PressedOnce())
            Settings.ShowOverlay.Value = !Settings.ShowOverlay.Value;

        if (Settings.StartRunKey.PressedOnce() && !string.IsNullOrEmpty(_charName))
        {
            StartRun(_charName);
            OpenSegmentForCurrentArea();
        }

        if (Settings.FinishRunKey.PressedOnce() && _active != null)
            FinishActive(RunStatus.Finished);
    }

    private void OnEnteredArea()
    {
        var area = GameController.Area.CurrentArea;
        if (area == null) return;
        var areaId = area.Area?.Id ?? area.Name;

        if (Settings.AutoStart && areaId == TwilightStrandId && _charLevel <= 1 && ShouldAutoStart())
            StartRun(_charName);

        if (_active == null || _active.Character != _charName)
            return;

        if (Settings.AutoFinish && areaId == KaruiShoresId)
        {
            FinishActive(RunStatus.Finished);
            return;
        }

        OpenSegmentForCurrentArea();
    }

    private bool ShouldAutoStart()
    {
        if (_active == null) return true;
        if (_active.Character != _charName) return true;
        // same name, level 1, back in the Strand: a relog keeps the run, a re-rolled character with the
        // same name (run already got past the Strand) starts a new one
        return _active.Segments.Any(s => s.AreaId != TwilightStrandId);
    }

    private void OpenSegmentForCurrentArea()
    {
        if (_active == null) return;
        var area = GameController.Area.CurrentArea;
        if (area == null) return;
        var areaId = area.Area?.Id ?? area.Name;

        // hideouts, labyrinth etc. count toward the act you came from; endgame areas are "act 11"
        var prevAct = _active.Segments.LastOrDefault()?.Act ?? 1;
        var act = AreaClassifier.Classify(areaId, area.Act, area.IsHideout) switch
        {
            AreaKind.Campaign => area.Act,
            AreaKind.Endgame => 11,
            _ => prevAct,
        };

        _curSeg = new Segment
        {
            AreaId = areaId,
            AreaName = area.Area?.Name ?? area.Name,
            Act = act,
            IsTown = area.IsTown || area.IsHideout,
            StartAt = _activeTotal,
            LevelOnEnter = _charLevel,
        };
        _active.Segments.Add(_curSeg);

        // LiveSplit-style checkpoint: first entry into a zone vs. when the comparison run first entered it
        if (_seenAreas.Add(_curSeg.Key))
        {
            EnsurePbCache();
            if (_pbFirstEntry.TryGetValue(_curSeg.Key, out var pbAt))
            {
                _lastDelta = _curSeg.StartAt - pbAt;
                _lastDeltaArea = _curSeg.AreaName;
            }
        }

        _store.Save();
        _lastSaveAt = Now;
    }

    private void StartRun(string character)
    {
        if (_active != null)
        {
            // throw away trivial false starts, keep everything else as abandoned history
            if (_active.TotalSeconds < 15) _store.Db.Runs.Remove(_active);
            else FinishActive(RunStatus.Abandoned, save: false);
        }

        var group = _store.Db.EnsureActiveGroup();
        _active = new Run { GroupId = group.Id, Character = character, FinalLevel = _charLevel };
        _store.Db.Runs.Add(_active);
        _curSeg = null;
        _justFinished = null;
        RebuildActiveCaches();
        _store.Save();
        DebugWindow.LogMsg($"{Name}: started run for {character} in group '{group.Name}'");
    }

    private void FinishActive(RunStatus status, bool save = true)
    {
        if (_active == null) return;
        _active.Status = status;
        _active.EndedUtc = DateTime.UtcNow;
        if (status == RunStatus.Finished)
        {
            var prevPb = _store.Db.OwnRunsIn(_active.GroupId)
                .Where(r => r.Status == RunStatus.Finished && r != _active)
                .OrderBy(r => r.TotalSeconds).FirstOrDefault();
            _justFinished = _active;
            _justFinishedWasPb = prevPb == null || prevPb == _active || _active.TotalSeconds < prevPb.TotalSeconds;
            _justFinishedAt = Now;
        }

        _active = null;
        _curSeg = null;
        _pbDirty = true;
        RebuildActiveCaches();
        if (save) _store.Save();
    }

    private void RebuildActiveCaches()
    {
        _seenAreas = new HashSet<string>();
        _activeAreaTimes = new Dictionary<string, double>();
        _activeTotal = 0;
        _activeTown = 0;
        _lastDelta = null;
        _lastDeltaArea = "";
        if (_active == null) return;

        foreach (var s in _active.Segments)
        {
            _seenAreas.Add(s.Key);
            _activeTotal += s.Seconds;
            if (s.IsTown) _activeTown += s.Seconds;
            _activeAreaTimes[s.Key] = _activeAreaTimes.GetValueOrDefault(s.Key) + s.Seconds;
        }

        _pbDirty = true;
    }

    private void EnsurePbCache()
    {
        var groupId = _active?.GroupId ?? _store.Db.ActiveGroupId;
        if (!_pbDirty && _pbGroupId == groupId) return;
        _pbDirty = false;
        _pbGroupId = groupId;
        _pbRun = _store.Db.ComparisonTarget(groupId);
        _pbFirstEntry = _pbRun?.FirstEntryTimes() ?? new Dictionary<string, double>();
        _pbAreaTimes = _pbRun?.TimePerArea() ?? new Dictionary<string, double>();
    }

    // ---------------------------------------------------------------- overlay

    public override unsafe void Render()
    {
        if (!GameController.InGame) return;
        if (InWorld) RenderXpPanel(); // not on character select / login (the timer stays visible there for relogs)
        if (!Settings.ShowOverlay) return;

        var lines = new List<(string text, Color color, string suffix, Color suffixColor)>();
        var text = Settings.TextColor.Value;

        if (_active != null)
        {
            EnsurePbCache();
            var groupName = _store.Db.Groups.FirstOrDefault(g => g.Id == _active.GroupId)?.Name ?? "?";
            lines.Add(($"{_active.Character}  -  {groupName}", new Color(170, 170, 170), null, default));

            string deltaText = null;
            var deltaColor = text;
            if (_lastDelta is { } d)
            {
                deltaText = "  " + FormatDelta(d);
                deltaColor = d <= 0 ? Settings.AheadColor.Value : Settings.BehindColor.Value;
            }

            lines.Add(($"Run   {FormatTime(_activeTotal)}", text, deltaText, deltaColor));

            if (_curSeg != null)
            {
                var zoneTotal = _activeAreaTimes.GetValueOrDefault(_curSeg.Key);
                string pbText = null;
                var pbColor = text;
                if (_pbAreaTimes.TryGetValue(_curSeg.Key, out var pbZone))
                {
                    pbText = $"  / {(_pbRun?.IsReference == true ? "Ref" : "PB")} {FormatTime(pbZone)}";
                    pbColor = zoneTotal <= pbZone ? Settings.AheadColor.Value : Settings.BehindColor.Value;
                }

                var label = _curSeg.IsTown ? "Town" : "Zone";
                lines.Add(($"{label}  {_curSeg.AreaName}  {FormatTime(zoneTotal)}", text, pbText, pbColor));
            }

            var townPct = _activeTotal > 0 ? _activeTown / _activeTotal * 100 : 0;
            lines.Add(($"Town  {FormatTime(_activeTown)} ({townPct:0}%)", text, null, default));
        }
        else if (_justFinished != null && Now - _justFinishedAt < 120)
        {
            lines.Add(($"Run finished: {FormatTime(_justFinished.TotalSeconds)}", text,
                _justFinishedWasPb ? "  NEW PB!" : null, Settings.AheadColor.Value));
        }
        else if (Settings.ShowWhenIdle)
        {
            lines.Add(("Speedrun Timer: no active run", new Color(170, 170, 170), null, default));
        }

        if (lines.Count == 0) return;

        // same ImGui panel style as the XP panel: per-panel font size, Alt-drag to move
        var font = ImGui.GetFont();
        var textSize = Settings.TextSize.Value;
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        var scale = textSize / baseSize;
        Vector2 Measure(string t) => ImGui.CalcTextSize(t) * scale;

        float pad = Settings.Padding.Value;
        var lineH = (float)Math.Ceiling(textSize) + 2f;
        var widths = lines.Select(l => Measure(l.text).X).ToList();
        var contentW = lines.Select((l, i) => widths[i] + (l.suffix != null ? Measure(l.suffix).X : 0)).Max();
        var size = new Vector2(contentW + pad * 2, lineH * lines.Count + pad * 2);
        var origin = new Vector2(Settings.PosX.Value, Settings.PosY.Value);

        DrawPanelFrame("timer", ref origin, size, Settings.PosX, Settings.PosY, Settings.BackgroundColor.Value);

        var dl = ImGui.GetForegroundDrawList();
        var y = origin.Y + pad;
        for (var i = 0; i < lines.Count; i++)
        {
            var pos = new Vector2(origin.X + pad, y);
            dl.AddText(font, textSize, pos, U32(lines[i].color), lines[i].text);
            if (lines[i].suffix != null)
                dl.AddText(font, textSize, pos + new Vector2(widths[i], 0), U32(lines[i].suffixColor), lines[i].suffix);
            y += lineH;
        }
    }

    internal static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    internal static string FormatDelta(double seconds) =>
        (seconds <= 0 ? "-" : "+") + FormatTime(Math.Abs(seconds));
}
