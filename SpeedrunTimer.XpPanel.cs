using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace SpeedrunTimer;

/// <summary>
/// XP panel: level, XP into level, XP/h, ETA and the XP-penalty safe-zone axis.
/// Ported from Exile Campaigns' Statistics overlay (same formulas and colours) so it works without that plugin.
/// </summary>
public partial class SpeedrunTimer
{
    // Cumulative total XP required to REACH each level, index 0 = level 1. Source: poedb.tw/us/Experience
    private static readonly long[] XpCurve =
    {
        0, 525, 1760, 3781, 7184, 12186, 19324, 29377, 43181, 61693,
        85990, 117506, 157384, 207736, 269997, 346462, 439268, 551295, 685171, 843709,
        1030734, 1249629, 1504995, 1800847, 2142652, 2535122, 2984677, 3496798, 4080655, 4742836,
        5490247, 6334393, 7283446, 8348398, 9541110, 10874351, 12361842, 14018289, 15859432, 17905634,
        20171471, 22679999, 25456123, 28517857, 31897771, 35621447, 39721017, 44225461, 49176560, 54607467,
        60565335, 67094245, 74247659, 82075627, 90631041, 99984974, 110197515, 121340161, 133497202, 146749362,
        161191120, 176922628, 194049893, 212684946, 232956711, 255001620, 278952403, 304972236, 333233648, 363906163,
        397194041, 433312945, 472476370, 514937180, 560961898, 610815862, 664824416, 723298169, 786612664, 855129128,
        929261318, 1009443795, 1096169525, 1189918242, 1291270350, 1400795257, 1519130326, 1646943474, 1784977296, 1934009687,
        2094900291, 2268549086, 2455921256, 2658074992, 2876116901, 3111280300, 3364828162, 3638186694, 3932818530, 4250334444,
    };

    private static readonly Color BarTrack = new(20, 26, 41, 255);
    private static readonly Color BarTrackBorder = new(43, 53, 80, 255);
    private static readonly Color AccentBright = new(77, 166, 255, 255);
    private static readonly Color AccentResting = new(41, 97, 179, 255);
    private static readonly Color PenaltySafe = new(111, 207, 122, 255);
    private static readonly Color PenaltyBad = new(224, 103, 103, 255);
    private static readonly Color AxisSafeBand = new(111, 207, 122, 51);
    private static readonly Color AxisSafeEdge = new(111, 207, 122, 255);
    private static readonly Color AxisMarker = new(0, 204, 255, 255);
    private static readonly Color AxisFlankRed = new(224, 103, 103, 140);
    private static readonly Color AxisFlankAmber = new(224, 177, 90, 82);
    private static readonly Color AxisFlankGrey = new(60, 72, 96, 56);

    private long _playerXp;
    private int _areaLevel;
    private readonly List<(DateTime t, long xp)> _xpSamples = new();
    private DateTime _lastXpSample = DateTime.MinValue;
    private string _dragId; // panel currently being Alt-dragged

    private enum XpRowKind { Text, Bar, Axis }

    private readonly record struct XpRow(XpRowKind Kind, string Left, string Right, Color Color, float Fill = 0f);

    /// <summary>Called from Tick after ReadPlayer().</summary>
    private void XpTick()
    {
        try
        {
            _areaLevel = GameController.Area.CurrentArea?.RealLevel ?? 0;
        }
        catch
        {
            _areaLevel = 0;
        }

        if (_playerXp <= 0) return;
        var now = DateTime.Now;
        if ((now - _lastXpSample).TotalSeconds < 2) return;
        _lastXpSample = now;

        // a lower XP value means another character (or a death penalty): restart the rate window
        if (_xpSamples.Count > 0 && _playerXp < _xpSamples[^1].xp) _xpSamples.Clear();
        _xpSamples.Add((now, _playerXp));

        var cutoff = now - TimeSpan.FromMinutes(Math.Max(1, Settings.XpPanel.RateWindowMinutes.Value));
        var drop = 0;
        while (drop + 1 < _xpSamples.Count && _xpSamples[drop + 1].t < cutoff) drop++;
        if (drop > 0) _xpSamples.RemoveRange(0, drop);
    }

    private List<XpRow> BuildXpRows(XpPanelSettings s)
    {
        var rows = new List<XpRow>();
        var tc = s.TextColor.Value;
        var muted = new Color(tc.R, tc.G, tc.B, (byte)170);
        var level = _charLevel;

        long into = 0, need = 0;
        var haveCurve = level is >= 1 and < 100 && _playerXp > 0;
        if (haveCurve)
        {
            long cur = XpCurve[level - 1], next = XpCurve[level];
            into = _playerXp - cur;
            need = next - cur;
        }

        var haveXp = haveCurve && need > 0 && into >= 0;

        if (s.ShowLevel)
        {
            var lvlText = level is >= 1 and < 100 ? $"LVL {level} -> {level + 1}" : $"LVL {level}";
            rows.Add(new XpRow(XpRowKind.Text, lvlText, haveXp ? $"{100.0 * into / need:0.0}%" : null, tc));
        }

        if (s.ShowXpBar && haveXp)
            rows.Add(new XpRow(XpRowKind.Bar, null, null, default, (float)(into / (double)need)));

        double rate = 0;
        var haveRate = false;
        if (_xpSamples.Count >= 2)
        {
            var first = _xpSamples[0];
            var last = _xpSamples[^1];
            var span = (last.t - first.t).TotalSeconds;
            if (span >= 30)
            {
                rate = (last.xp - first.xp) / (span / 3600.0);
                haveRate = rate > 0;
            }
        }

        var rateRight = s.ShowXpRate && haveRate ? FmtRate(rate) : null;
        if (s.ShowXpToGo && haveXp)
            rows.Add(new XpRow(XpRowKind.Text, $"{need - into:N0} to go", rateRight, muted));
        else if (rateRight != null)
            rows.Add(new XpRow(XpRowKind.Text, "Rate", rateRight, muted));

        if (s.ShowEta && haveRate && haveXp)
            rows.Add(new XpRow(XpRowKind.Text, "ETA next lvl", FormatTime((need - into) / rate * 3600.0), muted));

        if (s.ShowPenalty && _areaLevel > 0 && level > 0)
        {
            var effDiff = EffectiveXpDiff(level, _areaLevel);
            var (grade, gradeCol) = PenaltyGrade(level, _areaLevel, effDiff);
            rows.Add(new XpRow(XpRowKind.Text, "XP Penalty", grade, gradeCol));
            if (s.ShowPenaltyAxis)
            {
                rows.Add(new XpRow(XpRowKind.Axis, null, null, default));
                var safe = SafeZone(level);
                rows.Add(new XpRow(XpRowKind.Text, $"Area Lvl {_areaLevel}", $"Safe {Math.Max(1, level - safe)}-{level + safe}", muted));
            }
        }

        return rows;
    }

    private unsafe void RenderXpPanel()
    {
        var s = Settings.XpPanel;
        if (!s.Enable) return;
        var rows = BuildXpRows(s);
        if (rows.Count == 0) return;

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var textSize = s.TextSize.Value;
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        var scale = textSize / baseSize;
        Vector2 Measure(string t) => ImGui.CalcTextSize(t) * scale;

        float pad = s.Padding.Value;
        var lineH = (float)Math.Ceiling(textSize) + 2f;
        float contentW = s.MinWidth.Value;
        foreach (var r in rows)
            if (r.Kind == XpRowKind.Text)
                contentW = Math.Max(contentW, Measure(r.Left ?? "").X + (r.Right != null ? textSize + Measure(r.Right).X : 0));

        float RowH(XpRow r) => r.Kind switch { XpRowKind.Bar => 12f + 6f, XpRowKind.Axis => 14f + 10f, _ => lineH };
        var totalH = pad * 2;
        foreach (var r in rows) totalH += RowH(r);
        var size = new Vector2(contentW + pad * 2, totalH);
        var origin = new Vector2(s.PosX.Value, s.PosY.Value);

        DrawPanelFrame("xp", ref origin, size, s.PosX, s.PosY, s.BackgroundColor.Value);
        var max = origin + size;

        var innerL = origin.X + pad;
        var innerR = max.X - pad;
        var y = origin.Y + pad;
        foreach (var r in rows)
        {
            var h = RowH(r);
            switch (r.Kind)
            {
                case XpRowKind.Bar:
                {
                    var top = y + (h - 12f) / 2f;
                    var a = new Vector2(innerL, top);
                    var b = new Vector2(innerR, top + 12f);
                    dl.AddRectFilled(a, b, U32(BarTrack));
                    var fillW = (innerR - innerL) * Math.Clamp(r.Fill, 0f, 1f);
                    if (fillW > 0.5f)
                        dl.AddRectFilledMultiColor(a, new Vector2(innerL + fillW, b.Y),
                            U32(AccentResting), U32(AccentBright), U32(AccentBright), U32(AccentResting));
                    dl.AddRect(a, b, U32(BarTrackBorder), 0f, 1f);
                    break;
                }
                case XpRowKind.Axis:
                    DrawPenaltyAxis(dl, _charLevel, _areaLevel, innerL, innerR, y, h);
                    break;
                default:
                {
                    var col = U32(r.Color);
                    if (!string.IsNullOrEmpty(r.Left))
                        dl.AddText(font, textSize, new Vector2(innerL, y), col, r.Left);
                    if (r.Right != null)
                        dl.AddText(font, textSize, new Vector2(innerR - Measure(r.Right).X, y), col, r.Right);
                    break;
                }
            }

            y += h;
        }
    }

    // XP-penalty safe-zone axis: severity flank bands, a green safe band with edge lines, cyan marker for the area level.
    private static void DrawPenaltyAxis(ImDrawListPtr dl, int cl, int al, float innerL, float innerR, float y, float rowH)
    {
        const float barH = 14f;
        var safe = SafeZone(cl);
        float half = Math.Max(safe + 5, 8);
        var top = y + (rowH - barH) / 2f;
        var w = innerR - innerL;
        var a = new Vector2(innerL, top);
        var b = new Vector2(innerR, top + barH);

        void Band(float p0, float p1, Color c0, Color c1) =>
            dl.AddRectFilledMultiColor(new Vector2(innerL + w * p0, a.Y), new Vector2(innerL + w * p1, b.Y),
                U32(c0), U32(c1), U32(c1), U32(c0));

        Band(0f, 0.26f, AxisFlankRed, AxisFlankAmber);
        Band(0.26f, 0.5f, AxisFlankAmber, AxisFlankGrey);
        Band(0.5f, 0.74f, AxisFlankGrey, AxisFlankAmber);
        Band(0.74f, 1f, AxisFlankAmber, AxisFlankRed);

        var safeLeft = 0.5f - safe / half * 0.5f;
        var safeRight = 0.5f + safe / half * 0.5f;
        var sa = new Vector2(innerL + w * safeLeft, a.Y);
        var sb = new Vector2(innerL + w * safeRight, b.Y);
        dl.AddRectFilled(sa, sb, U32(AxisSafeBand));
        dl.AddLine(new Vector2(sa.X, a.Y), new Vector2(sa.X, b.Y), U32(AxisSafeEdge), 1f);
        dl.AddLine(new Vector2(sb.X, a.Y), new Vector2(sb.X, b.Y), U32(AxisSafeEdge), 1f);
        dl.AddRect(a, b, U32(BarTrackBorder), 0f, 1f);

        var markerPct = Math.Clamp(0.5f + (al - cl) / half * 0.5f, 0.03f, 0.97f);
        var mx = innerL + w * markerPct;
        dl.AddLine(new Vector2(mx, a.Y - 3f), new Vector2(mx, b.Y + 3f), U32(AxisMarker), 2f);
    }

    // ---------------------------------------------------------------- Alt-drag to move

    private static bool AltHeld => Input.GetKeyState(Keys.LMenu) || Input.GetKeyState(Keys.RMenu);

    private static bool IsMouseIn(Vector2 pos, Vector2 size)
    {
        var m = ImGui.GetMousePos();
        return m.X >= pos.X && m.X <= pos.X + size.X && m.Y >= pos.Y && m.Y <= pos.Y + size.Y;
    }

    /// <summary>
    /// Shared by every panel: handles Alt-drag (writes back posX/posY), draws the background and the
    /// drag highlight. origin is updated in place when the panel is being moved.
    /// </summary>
    private void DrawPanelFrame(string id, ref Vector2 origin, Vector2 size, RangeNode<int> posX, RangeNode<int> posY, Color background)
    {
        var hovered = IsMouseIn(origin, size);
        var dragging = _dragId == id;
        if (dragging)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _dragId = null;
                dragging = false;
            }
            else
            {
                origin += ImGui.GetIO().MouseDelta;
                posX.Value = (int)Math.Max(0, origin.X);
                posY.Value = (int)Math.Max(0, origin.Y);
            }
        }
        else if (_dragId == null && AltHeld && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _dragId = id;
            dragging = true;
        }

        // invisible window under the panel so ImGui captures the click instead of the game
        if (dragging || (AltHeld && hovered))
        {
            ImGui.SetNextWindowPos(origin);
            ImGui.SetNextWindowSize(size);
            ImGui.SetNextWindowBgAlpha(0f);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                           ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse;
            ImGui.Begin("##srt_drag_" + id, flags);
            ImGui.End();
        }

        var dl = ImGui.GetForegroundDrawList();
        var max = origin + size;
        if (background.A > 0) dl.AddRectFilled(origin, max, U32(background));
        if (dragging || (AltHeld && hovered))
            dl.AddRect(origin, max, U32(new Color(120, 220, 255, 200)), 0f, 1.5f);
    }

    // ---------------------------------------------------------------- XP math (poewiki "Experience", campaign range)

    private static (string text, Color color) PenaltyGrade(int charLevel, int areaLevel, int effDiff)
    {
        if (effDiff <= 0) return ("0%  safe", PenaltySafe);
        var mult = XpMultiplier(charLevel, effDiff);
        var pen = (int)Math.Round((1 - mult) * 100);
        var dir = areaLevel > charLevel ? "underleveled" : "overleveled";
        return ($"{pen}%  {dir}", LerpColor(PenaltySafe, PenaltyBad, Math.Clamp(pen / 60f, 0f, 1f)));
    }

    private static int SafeZone(int playerLevel) => 3 + playerLevel / 16;

    private static int EffectiveXpDiff(int playerLevel, int areaLevel) =>
        Math.Max(Math.Abs(playerLevel - areaLevel) - SafeZone(playerLevel), 0);

    private static double XpMultiplier(int playerLevel, int effDiff)
    {
        if (effDiff <= 0) return 1.0;
        double b = playerLevel + 5;
        return Math.Max(Math.Pow(b / (b + Math.Pow(effDiff, 2.5)), 1.5), 0.01);
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }

    private static string FmtRate(double xph) =>
        xph >= 1_000_000 ? $"{xph / 1_000_000:0.0}M/h" : xph >= 1000 ? $"{xph / 1000:0}k/h" : $"{xph:0}/h";

    private static uint U32(Color c) => ImGui.GetColorU32(new Vector4(c.R, c.G, c.B, c.A) / 255f);
}
