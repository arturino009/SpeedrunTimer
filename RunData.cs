using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpeedrunTimer;

public enum RunStatus
{
    InProgress,
    Finished,
    Abandoned,
}

/// <summary>One continuous stay in one area instance (re-entering a zone creates a new segment).</summary>
public class Segment
{
    public string AreaId { get; set; } = "";
    public string AreaName { get; set; } = "";
    public int Act { get; set; }
    public bool IsTown { get; set; }

    /// <summary>Run time (seconds) at the moment this segment began.</summary>
    public double StartAt { get; set; }

    public double Seconds { get; set; }
    public int LevelOnEnter { get; set; }

    /// <summary>
    /// Comparison key: act + display name. Name-based so runs imported from video/OCR (no area ids) line up
    /// with live runs, act-qualified so Act 1 and Act 6 "The Coast" stay separate.
    /// </summary>
    [JsonIgnore]
    public string Key => $"{Act}|{AreaName}";
}

public enum AreaKind
{
    Campaign, // regular act zone or town: has a meaningful act number
    Endgame,  // maps, Karui Shores, atlas: past the end of a campaign run
    Inherit,  // hideouts, labyrinth, unknown: belongs to whatever act you came from
}

public static class AreaClassifier
{
    private static readonly Regex CampaignId = new(@"^[12]_\d+_", RegexOptions.Compiled);

    public static AreaKind Classify(string id, int act, bool isHideout)
    {
        if (isHideout || string.IsNullOrEmpty(id)) return AreaKind.Inherit;
        if (id.Contains("Labyrinth", StringComparison.OrdinalIgnoreCase) || id.Contains("Hideout", StringComparison.OrdinalIgnoreCase))
            return AreaKind.Inherit;
        if (CampaignId.IsMatch(id) && act is >= 1 and <= 10) return AreaKind.Campaign;
        if (act >= 11 || id.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase)) return AreaKind.Endgame;
        return AreaKind.Inherit;
    }
}

public class Run
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = "";
    public string Character { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.InProgress;
    public int FinalLevel { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Imported run (e.g. from a video). Usable as a comparison target, excluded from your own stats.</summary>
    public bool IsReference { get; set; }

    public List<Segment> Segments { get; set; } = new();

    public double TotalSeconds => Segments.Sum(s => s.Seconds);
    public double TownSeconds => Segments.Where(s => s.IsTown).Sum(s => s.Seconds);

    /// <summary>Run time at which each zone (Segment.Key) was first entered (LiveSplit-style checkpoints).</summary>
    public Dictionary<string, double> FirstEntryTimes()
    {
        var d = new Dictionary<string, double>();
        foreach (var s in Segments)
            d.TryAdd(s.Key, s.StartAt);
        return d;
    }

    /// <summary>Total time spent in each zone (Segment.Key) across all visits.</summary>
    public Dictionary<string, double> TimePerArea()
    {
        var d = new Dictionary<string, double>();
        foreach (var s in Segments)
            d[s.Key] = d.GetValueOrDefault(s.Key) + s.Seconds;
        return d;
    }

    /// <summary>
    /// The act each segment counts toward: the furthest campaign act reached so far. A trip back to an earlier
    /// act's town (Lioneye's Watch during act 2) is time spent "during act 2".
    /// </summary>
    public int[] ProgressActs()
    {
        var res = new int[Segments.Count];
        var reached = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var a = Segments[i].Act;
            if (a is >= 1 and <= 10) reached = Math.Max(reached, a);
            res[i] = a >= 11 ? a : Math.Max(reached, a);
        }

        return res;
    }

    /// <summary>Total time per act (including town time in that act), by progress act.</summary>
    public Dictionary<int, double> TimePerAct()
    {
        var d = new Dictionary<int, double>();
        var acts = ProgressActs();
        for (var i = 0; i < Segments.Count; i++)
            d[acts[i]] = d.GetValueOrDefault(acts[i]) + Segments[i].Seconds;
        return d;
    }
}

public class RunGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Run the overlay/deltas compare against. Empty = your personal best.</summary>
    public string CompareRunId { get; set; } = "";
}

public class RunDatabase
{
    public int Version { get; set; } = 1;
    public string ActiveGroupId { get; set; } = "";
    public List<RunGroup> Groups { get; set; } = new();
    public List<Run> Runs { get; set; } = new();

    public RunGroup ActiveGroup => Groups.FirstOrDefault(g => g.Id == ActiveGroupId);

    public RunGroup EnsureActiveGroup()
    {
        var g = ActiveGroup;
        if (g != null) return g;
        g = Groups.FirstOrDefault();
        if (g == null)
        {
            g = new RunGroup { Name = "Default" };
            Groups.Add(g);
        }

        ActiveGroupId = g.Id;
        return g;
    }

    public IEnumerable<Run> RunsIn(string groupId) => Runs.Where(r => r.GroupId == groupId);

    /// <summary>Your own runs (imported references excluded).</summary>
    public IEnumerable<Run> OwnRunsIn(string groupId) => RunsIn(groupId).Where(r => !r.IsReference);

    public Run PersonalBest(string groupId) =>
        OwnRunsIn(groupId).Where(r => r.Status == RunStatus.Finished).OrderBy(r => r.TotalSeconds).FirstOrDefault();

    /// <summary>The group's chosen comparison run, falling back to the PB.</summary>
    public Run ComparisonTarget(string groupId)
    {
        var g = Groups.FirstOrDefault(x => x.Id == groupId);
        if (g != null && !string.IsNullOrEmpty(g.CompareRunId))
        {
            var r = Runs.FirstOrDefault(x => x.Id == g.CompareRunId && x.GroupId == groupId);
            if (r != null) return r;
        }

        return PersonalBest(groupId);
    }
}

public class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _writeLock = new();
    private Task _pendingWrite = Task.CompletedTask;

    public RunDatabase Db { get; private set; } = new();

    public RunStore(string path)
    {
        _path = path;
    }

    public string Load()
    {
        try
        {
            if (File.Exists(_path))
                Db = JsonSerializer.Deserialize<RunDatabase>(File.ReadAllText(_path), JsonOptions) ?? new RunDatabase();
        }
        catch (Exception e)
        {
            // keep the broken file around rather than overwriting the user's history
            try { File.Copy(_path, _path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
            Db = new RunDatabase();
            return e.Message;
        }

        Db.EnsureActiveGroup();
        return null;
    }

    /// <summary>Serialises on the calling thread (consistent snapshot), writes the file in the background.</summary>
    public void Save()
    {
        string json;
        try { json = JsonSerializer.Serialize(Db, JsonOptions); }
        catch { return; }

        lock (_writeLock)
        {
            _pendingWrite = _pendingWrite.ContinueWith(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _path, true);
                }
                catch
                {
                    // next save will retry
                }
            });
        }
    }

    public void Flush()
    {
        try { _pendingWrite.Wait(2000); } catch { }
    }
}
