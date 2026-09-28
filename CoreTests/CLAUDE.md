# CoreTests

## Build

```powershell
.\build.ps1
```

## Run

The project uses `<OutputType>WinExe</OutputType>` which detaches stdout from the terminal.
`run.ps1` handles `--no-build`, `-c Release`, and `| Out-Host` automatically.

```powershell
.\run.ps1 [global flags] <suite> [suite args]
```

## Global flags

| Flag | Description |
|------|-------------|
| `--log-times` | Enable overall timing statistics |
| `--no-gpu` | Disable GPU acceleration |
| `--no-log-update` | Disable per-iteration update logging in NPC tests |
| `--dxgi` | Use DXGI screen capture instead of WGC (default) |
| `--delay <ms>` | Set delay in milliseconds between iterations (default: 150) |

## Suites

### decision - Read-only decision diagnostics

```powershell
.\run.ps1 decision
.\run.ps1 decision watch
.\run.ps1 decision --file states\hunter_combat.json
.\run.ps1 decision selftest
```

The file and selftest variants run without WoW. Live `decision` captures a
read-only AI observation and calls Laya for a preview. No variant sends input.

### ai-test - Live AI one-mob kill and loot

Runs the production bot using the supplied AI endpoint until it finds one
hostile mob, kills it, and confirms the corpse was looted. The command-line
endpoint overrides AI connection settings in this test session.
AI mode obtains each next action from Laya and executes it through capabilities;
the local GOAP planner and goal OnEnter/Update methods do not choose or perform
these actions.
Ready skills from the selected class profile's Combat, Pull and Adhoc sections
are offered as individual AI choices. The one-mob test disables Flee and keeps
the killed corpse pending until the AI chooses Loot and the bot completes it.

Start WoW with the normal addon/profile setup, stand alive and out of combat, clear the current
target, and ensure Loot is enabled in the selected class profile. Keep the Laya
`/predict` service running. This is a live gameplay command and can control the
character until success or timeout.

```powershell
.\run.ps1 ai-test --ai-name Laya --endpoint http://127.0.0.1:8000/predict
```

The static endpoint/subset evaluation remains available with `--offline`:

```powershell
.\run.ps1 ai-test --offline --ai-name MyAI --endpoint https://example.test/decision --subset .\my-subset.json
```

### Help

```powershell
.\run.ps1
```

### npc - NPC Name Finder

Args: `[NpcNames...] [count]`

Defaults (Friendly|Neutral, 100 iterations):
```powershell
.\run.ps1 npc
```

Enemy only:
```powershell
.\run.ps1 npc enemy
```

Enemy + Neutral with 10000 iterations:
```powershell
.\run.ps1 npc enemy neutral 10000
```

All types with timing stats:
```powershell
.\run.ps1 --log-times npc enemy friendly neutral corpse nameplate
```

Without GPU:
```powershell
.\run.ps1 --no-gpu npc
```

Stats only (no per-iteration logging), GPU, default delay:
```powershell
.\run.ps1 --log-times --no-log-update --delay 150 npc friendly neutral 500
```

Stats only, CPU (no GPU), default delay:
```powershell
.\run.ps1 --log-times --no-log-update --no-gpu --delay 150 npc friendly neutral 500
```

Stats only, fast iterations (10ms delay):
```powershell
.\run.ps1 --log-times --no-log-update --delay 10 npc friendly neutral 500
```

GPU vs CPU comparison (run both, compare stats):
```powershell
.\run.ps1 --log-times --no-log-update npc enemy neutral 1000
.\run.ps1 --log-times --no-log-update --no-gpu npc enemy neutral 1000
```

DXGI capture with stats:
```powershell
.\run.ps1 --log-times --no-log-update --dxgi npc friendly neutral 500
```

WGC vs DXGI comparison:
```powershell
.\run.ps1 --log-times --no-log-update npc friendly neutral 500
.\run.ps1 --log-times --no-log-update --dxgi npc friendly neutral 500
```

All enemy types, high iteration count:
```powershell
.\run.ps1 --log-times --no-log-update npc enemy 10000
```

All types combined:
```powershell
.\run.ps1 --log-times --no-log-update npc enemy friendly neutral corpse nameplate 1000
```

NpcNames values: `Enemy`, `Friendly`, `Neutral`, `Corpse`, `NamePlate`

### input - Mouse & Keyboard Input

```powershell
.\run.ps1 input
```

### HidTester - ESP32-S3 GUI

Run the independent .NET 8 WinForms HID tester:

```powershell
.\HidTester\run.ps1
```

It reports CDC command acknowledgements and Windows low-level keyboard/mouse
events in separate panels. Detailed S3 `[RX]`, `[HID]`, and `[DONE]` logs are
available only on the firmware UART monitor. PASS requires both a CDC ACK and
the expected Windows input events. Foreground-app reactions require manual
confirmation.

### cursor-grab - Cursor Type Classification

```powershell
.\run.ps1 cursor-grab
```

### cursor-compare - Cursor Classification Performance

```powershell
.\run.ps1 cursor-compare
```

### minimap - Minimap Node Finder

Default (100 samples):
```powershell
.\run.ps1 minimap
```

With timing stats:
```powershell
.\run.ps1 --log-times minimap
```

### find-target - Find Target By Cursor

Args: `[NpcNames...]`

Defaults (Friendly|Neutral):
```powershell
.\run.ps1 find-target
```

Enemy targets:
```powershell
.\run.ps1 find-target enemy
```

Enemy + Neutral:
```powershell
.\run.ps1 find-target enemy neutral
```

### pather - PPather Pathfinding

Args: `[expansion]`

Defaults to SoM:
```powershell
.\run.ps1 pather
```

TBC expansion:
```powershell
.\run.ps1 pather tbc
```

Wrath expansion:
```powershell
.\run.ps1 pather wrath
```

Expansion values: `SoM`, `TBC`, `Wrath`, `Cata`, `Mop`, `Retail`

### routegen - Generated Grind Routes

Builds routes from `Json/` + the baked navmesh, with the player stubbed - no game client.
See `docs/generated-routes-design.md`.

Args: `--exp <client>` (default `som`), `--zone`, `--subzone`, `--mobs`, `--stops`,
`--mode Wander|Loop`, `--focus Density|Balanced|Coverage`, `--level`, `--race`, `--faction`,
`--uimap`, `--seed <n|random>`, `--profile <class json>`, `--output <dir>`,
`--format svg|png|webp`.

`--seed` defaults to a fixed value so two runs are comparable; pass `--seed random` for a
fresh one. `Loop` ignores it - that mode is deterministic by construction.

`--output <dir>` takes any folder, absolute or relative; bare `--output` writes to
`routegen-out`. **Relative paths resolve against the working directory, which for `run.ps1`
is the project folder** (`dotnet run` does not use the output directory) - so a relative
`--output out` lands in `CoreTests\out`. The resolved path is logged as `<format> output:`
at startup.

`--svg` is the old name for this flag, from when SVG was the only encoding. Still accepted,
warns, and behaves identically.

**Do not point `--output` at `routegen`.** Windows paths are case-insensitive, so that is
the same directory as the `RouteGen/` source folder - clearing the output then deletes the
source. The default is `routegen-out` for that reason.

Ad-hoc query:
```powershell
.\run.ps1 routegen --exp som --subzone "Northshire Valley" --mobs "Name:Wolf || Name:Kobold"
```

Every generated path in a profile, rendered over the minimap tiles:
```powershell
.\run.ps1 routegen --exp wrath --profile "..\Json\class\_\Warrior_1-10.json" --race Draenei --level 2 --output routegen-out
```

```powershell
.\run.ps1 routegen --exp wrath --profile "..\Json\class\_\Warrior_1-10--------------.json" --race Draenei --level 2 --seed random --output routegen-out --format webp
```

`--output` writes one file per path - spawns green, route blue, numbered stops - with the
Leaflet tiles behind it, so it opens with no server running.

`--format` picks the encoding, default `svg`. All three share `RouteLayout`, so they frame
the route identically; only the encoding differs:

| format | notes |
|---|---|
| `svg` | Tiles inlined as base64 data URIs. Sharp at any zoom, but needs a browser. |
| `png` | Lossless raster. **Largest of the three** - the tiles re-encode worse than they arrive. |
| `webp` | Quality 90. Smallest, and opens in any viewer - the one to attach to an issue. |

On one Ammen Vale loop: svg 127 KB, png 738 KB, webp 99 KB.

`--format` without `--output <dir>` writes nothing and says so. Raster text needs a system
font (Consolas, Courier New, DejaVu Sans Mono, Segoe UI, then any); with none installed the
route still renders and only the labels are dropped.

**It reads `BaoServer/appsettings.json` for the navmesh options.** Those feed the tile
cache settings hash, which names the cache directory: on defaults it looks in a directory
nothing was baked into and reports "no baked navmesh" for a continent that is in fact baked.
`MinWorldZ.Expansion01 = -700` is exactly that case - Azeroth resolves, Outland does not.

### navmesh - Navmesh Coordinate Checks

Pure math validation of the wow<->rc coordinate mapping and Detour tile
indexing (no game data required):

```powershell
.\run.ps1 navmesh
```

### grind - Production Grind Cycles

Runs one or more real production target -> pull -> combat -> death -> loot
cycles in a single `ProductionTestSession` and stops on the first failure.

```powershell
.\run.ps1 grind 1
.\run.ps1 grind 3
```
