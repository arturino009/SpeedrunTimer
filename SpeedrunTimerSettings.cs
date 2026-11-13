using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace SpeedrunTimer;

public class SpeedrunTimerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Overlay", "Show the in-game timer overlay", 100)]
    public ToggleNode ShowOverlay { get; set; } = new ToggleNode(true);

    [Menu("Show overlay without active run", "Keep the overlay visible (idle) when no run is being timed", 101, 100)]
    public ToggleNode ShowWhenIdle { get; set; } = new ToggleNode(false);

    [Menu("Position X (px)", "Or hold Alt and drag the overlay", 102, 100)]
    public RangeNode<int> PosX { get; set; } = new RangeNode<int>(1500, 0, 4000);

    [Menu("Position Y (px)", "Or hold Alt and drag the overlay", 103, 100)]
    public RangeNode<int> PosY { get; set; } = new RangeNode<int>(150, 0, 2200);

    [Menu("Text size", "Font height in pixels", 104, 100)]
    public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(18f, 8f, 48f);

    [Menu("Padding", "", 109, 100)]
    public RangeNode<int> Padding { get; set; } = new RangeNode<int>(10, 0, 40);

    [Menu("Text color", "", 105, 100)]
    public ColorNode TextColor { get; set; } = new ColorNode(Color.White);

    [Menu("Ahead color", "Delta color when faster than PB", 106, 100)]
    public ColorNode AheadColor { get; set; } = new ColorNode(new Color(80, 220, 100));

    [Menu("Behind color", "Delta color when slower than PB", 107, 100)]
    public ColorNode BehindColor { get; set; } = new ColorNode(new Color(235, 80, 80));

    [Menu("Background color", "", 108, 100)]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(0, 0, 0, 170));

    [Menu("Timing", "", 200)]
    public EmptyNode TimingHeader { get; set; } = new EmptyNode();

    [Menu("Auto-start in Twilight Strand", "Start a new run when a level 1 character enters The Twilight Strand", 201, 200)]
    public ToggleNode AutoStart { get; set; } = new ToggleNode(true);

    [Menu("Auto-finish at Karui Shores", "Finish the run when you enter Karui Shores (after killing Kitava in Act 10)", 202, 200)]
    public ToggleNode AutoFinish { get; set; } = new ToggleNode(true);

    [Menu("Max gap (seconds)", "If the HUD stops ticking for longer than this (game closed, HUD restarted, long AFK at character select) the gap is NOT counted", 203, 200)]
    public RangeNode<int> MaxGapSeconds { get; set; } = new RangeNode<int>(300, 5, 3600);

    [Menu("Include unfinished runs in zone stats", "Zone breakdown uses time from abandoned runs too", 204, 200)]
    public ToggleNode ZoneStatsIncludeUnfinished { get; set; } = new ToggleNode(true);

    [Menu("Hotkeys", "", 300)]
    public EmptyNode HotkeysHeader { get; set; } = new EmptyNode();

    [Menu("Start new run", "Manually start a new run for the current character (replaces any active run)", 301, 300)]
    public HotkeyNodeV2 StartRunKey { get; set; } = new HotkeyNodeV2(Keys.None);

    [Menu("Finish run", "Manually mark the active run as finished", 302, 300)]
    public HotkeyNodeV2 FinishRunKey { get; set; } = new HotkeyNodeV2(Keys.None);

    [Menu("Toggle overlay", "", 303, 300)]
    public HotkeyNodeV2 ToggleOverlayKey { get; set; } = new HotkeyNodeV2(Keys.None);

    [Menu("XP panel", "Level / XP / XP-penalty panel (ported from Exile Campaigns' Statistics overlay). Hold Alt and drag to move it.", 400)]
    public XpPanelSettings XpPanel { get; set; } = new XpPanelSettings();
}

[Submenu]
public class XpPanelSettings
{
    [Menu("Enable")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Show level + XP %")] public ToggleNode ShowLevel { get; set; } = new ToggleNode(true);
    [Menu("Show XP bar")] public ToggleNode ShowXpBar { get; set; } = new ToggleNode(true);
    [Menu("Show XP to level")] public ToggleNode ShowXpToGo { get; set; } = new ToggleNode(true);
    [Menu("Show XP/h")] public ToggleNode ShowXpRate { get; set; } = new ToggleNode(true);
    [Menu("Show ETA to level")] public ToggleNode ShowEta { get; set; } = new ToggleNode(true);
    [Menu("Show XP penalty")] public ToggleNode ShowPenalty { get; set; } = new ToggleNode(true);
    [Menu("Penalty safe-zone bar", "Off = only the penalty % line")] public ToggleNode ShowPenaltyAxis { get; set; } = new ToggleNode(true);

    [Menu("XP rate window (min)", "XP/h and ETA are averaged over the last N minutes")]
    public RangeNode<int> RateWindowMinutes { get; set; } = new RangeNode<int>(5, 1, 30);

    [Menu("Text size", "Font height in pixels")] public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(16f, 8f, 48f);
    [Menu("Padding")] public RangeNode<int> Padding { get; set; } = new RangeNode<int>(12, 0, 40);
    [Menu("Min width")] public RangeNode<int> MinWidth { get; set; } = new RangeNode<int>(220, 100, 800);
    [Menu("Text color")] public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
    [Menu("Background color", "Alpha 0 = no background")] public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(8, 8, 12, 140));

    [Menu("Position X (px)", "Or hold Alt and drag the panel")] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(40, 0, 4000);
    [Menu("Position Y (px)")] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(60, 0, 2200);
}
