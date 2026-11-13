# Speedrun Timer

An ExileApi plugin that times Path of Exile campaign runs zone by zone, keeps a history of every run, and shows where you lose time compared to your PB or an imported reference run.

## Features

- **Automatic timing**: a run starts when a level 1 character enters The Twilight Strand and finishes when you enter Karui Shores. Hotkeys and buttons let you start or finish manually.
- **Per-zone and town time**: every zone visit is recorded, including loading screens. Town and hideout time is tracked separately for each act.
- **Overlay**: shows run time, town time and a live delta against the comparison run. Hold Alt and drag to move it.
- **Run history**: runs are saved per character and resume when you log back in. You can group runs (for example by build), rename or delete them, and mark them finished or unfinished.
- **Compare runs**: pick any two runs and see:
  - summary cards for total, zone and town time, plus your worst zone;
  - a graph of time lost or gained along the route;
  - per-act bars;
  - a zone table sortable by biggest losses, biggest gains or route order.

  Zones are compared by total time spent (all visits added up) in the reference's route order, so a slightly different route still lines up.
- **Reference import**: import someone else's run from a CSV timeline (`start,end,duration,duration_s,area`), for example one produced by OCR of a video. Acts are resolved from the game's area table, so you must be logged in to import.
- **XP panel**: level, XP bar, XP/h, ETA to next level and the XP penalty range.

## Installation

Clone or copy this folder into `Plugins/Source/Speedrun Timer` in your ExileApi (HUD) directory. The HUD compiles it on start.

Run data is stored in `config/SpeedrunTimer/runs.json`.

## Settings

| Setting | Default | Description |
|---|---|---|
| Overlay | on | Show the in-game timer overlay |
| Auto-start in Twilight Strand | on | Start a new run when a level 1 character enters The Twilight Strand |
| Auto-finish at Karui Shores | on | Finish the run when you enter Karui Shores |
| Max gap (seconds) | 300 | Pauses longer than this (game closed, HUD restarted, long AFK at character select) are not counted |
| Include unfinished runs in zone stats | on | The zone breakdown also uses unfinished runs |
| Hotkeys | none | Start run, finish run, toggle overlay |
| XP panel | on | Level / XP / penalty panel, with options for each line |
