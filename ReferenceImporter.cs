using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExileCore.PoEMemory.MemoryObjects;

namespace SpeedrunTimer;

/// <summary>
/// Imports a reference run from a CSV timeline (start,end,duration,duration_s,area) such as the one produced by the
/// video OCR scripts. Only area names are known, so acts are resolved by walking the timeline in order against the
/// game's WorldAreas table (an ambiguous name like "The Coast" picks the earliest act not before the current one).
/// The import stops at the end of the campaign (first endgame area / Karui Shores).
/// </summary>
public static class ReferenceImporter
{
    private record Candidate(string Id, int Act, AreaKind Kind, bool IsTown);

    public class Result
    {
        public Run Run;
        public string Error;
        public List<string> UnknownNames = new();
        public int RowsRead;
        public int RowsUsed;
        public string StoppedAt;
    }

    public static Result Import(string path, IEnumerable<WorldArea> worldAreas, string groupId, string displayName)
    {
        var result = new Result();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e)
        {
            result.Error = e.Message;
            return result;
        }

        var byName = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var wa in worldAreas)
        {
            if (wa == null || string.IsNullOrEmpty(wa.Name) || string.IsNullOrEmpty(wa.Id)) continue;
            var kind = AreaClassifier.Classify(wa.Id, wa.Act, wa.IsHideout);
            if (!byName.TryGetValue(wa.Name, out var list)) byName[wa.Name] = list = new List<Candidate>();
            list.Add(new Candidate(wa.Id, wa.Act, kind, wa.IsTown || wa.IsHideout));
        }

        if (byName.Count == 0)
        {
            result.Error = "Game area table not available - import while logged into the game.";
            return result;
        }

        var rows = new List<(double start, double end, string name)>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            if (f.Count < 5 || !TryParseTime(f[0], out var st) || !TryParseTime(f[1], out var en)) continue;
            rows.Add((st, en, f[4].Trim()));
        }

        result.RowsRead = rows.Count;
        if (rows.Count == 0)
        {
            result.Error = "No rows found. Expected columns: start,end,duration,duration_s,area";
            return result;
        }

        var run = new Run
        {
            GroupId = groupId,
            Character = displayName,
            IsReference = true,
            Status = RunStatus.Finished,
            StartedUtc = File.GetLastWriteTimeUtc(path),
            EndedUtc = File.GetLastWriteTimeUtc(path),
            Note = path,
        };

        var origin = rows[0].start;
        var act = 1;
        foreach (var (start, end, name) in rows)
        {
            int segAct;
            bool isTown;
            if (!byName.TryGetValue(name, out var cands))
            {
                result.UnknownNames.Add(name);
                segAct = act;
                isTown = false;
            }
            else
            {
                var campaign = cands.Where(c => c.Kind == AreaKind.Campaign).ToList();
                if (campaign.Count > 0)
                {
                    // acts advance one at a time: prefer the current act, then the next one; otherwise it's a trip
                    // back to an earlier act (e.g. Lioneye's Watch during act 2 is act 1, not act 6)
                    var pick = campaign.Where(c => c.Act == act || c.Act == act + 1).OrderBy(c => c.Act).FirstOrDefault()
                               ?? campaign.Where(c => c.Act < act).OrderByDescending(c => c.Act).FirstOrDefault()
                               ?? campaign.OrderBy(c => c.Act).First();
                    segAct = pick.Act;
                    isTown = pick.IsTown;
                }
                else if (cands.Any(c => c.Kind == AreaKind.Endgame) && cands.All(c => c.Kind != AreaKind.Inherit))
                {
                    result.StoppedAt = name;
                    break; // campaign is over
                }
                else
                {
                    segAct = act;
                    isTown = cands.Any(c => c.IsTown);
                }
            }

            act = Math.Max(act, segAct);
            run.Segments.Add(new Segment
            {
                AreaId = "",
                AreaName = name,
                Act = segAct,
                IsTown = isTown,
                StartAt = start - origin,
                Seconds = Math.Max(0, end - start),
            });
        }

        // drop trailing hideout/town visits after the last campaign zone (post-Kitava stash stop)
        while (run.Segments.Count > 1 && run.Segments[^1].IsTown)
            run.Segments.RemoveAt(run.Segments.Count - 1);

        result.RowsUsed = run.Segments.Count;
        if (run.Segments.Count == 0)
        {
            result.Error = "Nothing usable in the file.";
            return result;
        }

        result.Run = run;
        return result;
    }

    private static bool TryParseTime(string s, out double seconds)
    {
        seconds = 0;
        var parts = s.Trim().Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }

        seconds = total;
        return parts.Length > 0;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }

        fields.Add(cur.ToString());
        return fields;
    }
}
