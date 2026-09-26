# Training Data Recorder (schema 2)

## Current code map

| Concern | Source and actual meaning |
| --- | --- |
| Game state | `AddonDataProvider` decodes the DataToColor pixel frames. `AddonReader.Update` refreshes typed `IReader` components. `PlayerReader` reads player, target, mouse-over, map and cast cells; `AddonBits` reads boolean flags. |
| Decision loop | `GoapAgent.GoapThread` waits for `AddonReader.DataReady`, computes its world-state bit vector, chooses a `GoapGoal`, then calls `OnEnter`/`Update`. The selected goal is the recorded behavior context and decision. |
| Movement and route | `Navigation`, `PlayerDirection`, `StuckDetector`, and `FollowRouteGoal` produce movement commands. `RouteInfo.Route` and `RouteToWaypoint` expose enumerable map and path points. The actual movement action is observed at `ConfigurableInput` and `WowProcessInput`. |
| Combat | `PullTargetGoal`, `CombatGoal`, `CastingHandler`, `CombatLog`, and action-bar readers determine combat actions. The snapshot currently includes combat flags, health, GCD, cast spell IDs, cast time and aura counts. |
| Target selection | `TargetFinder`/`NpcNameTargeting` use `NpcNameFinder` screen detections and cursor targeting; `PlayerReader` exposes current target GUID/ID and mouse-over GUID/ID. |
| Loot | `LootGoal.OnEnter` performs the interaction and determines success from loot window, bag and money changes. Recorder hooks its start/success/failure/end points. |
| Stuck | `StuckDetector` tracks its attempt ladder and best approach. Recorder captures attempt count and unreachable state and records detection/recovery events. |
| Input execution | `ConfigurableInput` maps class actions to keys; `WowProcessInput` tracks held keys and invokes `InputWindowsNative`, which posts Win32 keyboard and mouse messages to the WoW window. There is no HID transport on this production execution path. `ExecutedAction` records the virtual key code actually passed to a successful `PostMessage` call, including layout translation and modifiers. It does not independently confirm that the game acted on the message. |
| Settings | `BaoServer` binds `appsettings.json` via `IConfiguration`; Settings is a shared Blazor page. The training switch persists the existing `Training:EnableTrainingDataCollection` key in `BaoServer/appsettings.json`, preserving JSONC comments and unrelated settings. `HeadlessServer/headless_appsettings.json` has the same default-off key for CLI runs. PathingAPI renders the control disabled because it has no bot settings service. |
| CoreTests | `Program` dispatches suites. `state`, `state watch`, and `state bindings` remain intact; `record validate` is an offline check. |

## Data available now

`GameStateSnapshot` contains timestamp, session and sequence **and a copy of every decoded addon integer cell** (`rawAddonCells`). The typed view includes player map XYZ (the Z supplied by the current reader), direction, run speed, moving/dead/combat/casting/mounted/swimming/falling flags, spell ID, health, mana, dynamic power, level, XP, map IDs, class, race and client version. It contains current target GUID, ID, name, health, level, classification, minimum/maximum range, dead/hostile/casting and target-of-target fields, plus mouse-over GUID, ID, name, level, classification and hostile flag. `TargetState` values are nullable when there is no target. The integer frame enables future DatasetBuilders to reinterpret cells that schema 1 does not yet expose as typed fields. Readers and the recorder run on separate threads, so the copy is a best-effort observation rather than an atomic game-state transaction.

`ScreenObservedEntities` keeps `NpcCount`, `TargetCount`, `AddCount`, detection type and every `NpcNameFinder.Npcs` entry with bounding rectangle, center, click point, target-screen-region flag and add-screen-region flag. These are screen observations. `NpcNameFinder` internally builds line segments, but it does not expose a stable public line-segment list; the recorder does not copy its scratch buffer. `NpcPosition` has no recognized name, nameplate color or confidence score, so none is invented. The visual list can be incomplete due to viewpoint, occlusion, UI and scan region.

`NavigationState` captures the complete route and path-to-waypoint points. In schema 2, the recorder stores each distinct coordinate sequence once in `routes.jsonl` or `paths.jsonl`; snapshots carry `routeId` and `pathId` while retaining next waypoint and distance. SHA-256 hashes use X/Y/Z rounded to four decimal places with invariant formatting, while the first stored point sequence retains its original float values. The observed production routes and paths had no collisions after this normalization. `MovementInputState` records the actual held forward/back/turn/strafe/jump keys at the snapshot tick. `BotInternalState` keeps GOAP goal, world-state bits, loot/corpse counters, gathering and stuck ladder state/duration. `CombatState` keeps player and player-or-pet combat flags, combat-log counts, pending pull count, GCD, auto-attack, casts and aura **counts**. The existing addon also has action-bar and aura readers, but this version does not yet copy a reliable enumerated buff/debuff/cooldown/spell-state list into the schema. The full addon integer frame remains available for later reinterpretation. Count is never presented as a full list.

The addon does **not** supply actual target XYZ, target heading or an exact target distance. `PlayerReader.TargetMapPos` estimates a position from range and heading; it is intentionally excluded from raw target world data. No target distance or angle is fabricated from it. Target min/max range are preserved instead. Player energy/rage are represented by the addon's dynamic power value; its resource type is class-dependent and not claimed to be a dedicated energy/rage field. The reader does not currently expose a reliable player vertical movement state, cooldown list, route waypoint index, per-buff IDs/stacks, target attackability, or world-complete nearby entity list. No screenshot is saved in schema 1.

## Timeline and file format

When the setting is OFF, `GoapAgent` skips snapshot construction, record methods return immediately, and no session directory or writer is created. When enabled and the bot starts, a new session is opened. Runtime OFF flushes and closes the writer; a later ON opens a different session. Each normal stop also closes its session.

The GOAP tick captures state before the goal acts. Requested actions are observed in `ConfigurableInput`; dispatched keyboard/mouse actions are observed in `InputWindowsNative` at successful Win32 calls. The next reader tick closes a transition with `stateAfter` and derives position, heading, health, target-health and waypoint-distance deltas when valid. A stopped session with an action but no next tick emits `result: SessionStoppedBeforeNextState` and a null `stateAfter` rather than inventing an outcome. Raw states are separately sampled at most once per second. Repeated identical requested actions within a transition are coalesced. Records are placed on a bounded queue and serialized by one background writer with periodic flushing; the manifest reports dropped records if the queue fills. All recorded facts have UTC timestamps and monotonic session sequences. `episodeId` is present for later grouping; a new ID starts when a new target is acquired.

Schema 2 states and transitions rotate before a new record when it would exceed 16 MiB or when the shard has covered 10 minutes of record timestamps. Sequence and session IDs continue across shards. The background writer keeps one JSON object per line and flushes periodically. `index.json` is atomically refreshed during recording and at close with each file's type, sequence/time range, count and byte size. Events, routes and paths remain unsharded.

Example layout (relative to the host's current working directory):

```text
training-data/
  session_20260924_120000_123_<guid>/
    manifest.json
    index.json
    routes.jsonl
    paths.jsonl
    events.jsonl
    states_000001.jsonl
    transitions_000001.jsonl
```

Schema 1 sessions keep their original `raw-states.jsonl` and inline `navigation.route` / `navigation.pathToWaypoint` arrays; they are not migrated or rewritten. `TrainingSessionReader` reads both schema versions, loads route/path stores, resolves references, and reads state shards.

The manifest includes schema version, session start/end, bot and game versions, class/race/map, class profile, mode, path profile, recorder version, setting value, capabilities and dropped-record count. Git commit is nullable when the build does not supply it. Example transition, abbreviated for readability:

```json
{"sessionId":"session_...","episodeId":"...","sequence":8,"timestamp":"2026-09-24T04:00:00Z","behaviorContext":"Follow Route","stateBefore":{"player":{"mapPosition":{"x":1,"y":2,"z":3}},"screenObservedEntities":{"npcCount":1,"npcs":[{"x":100,"y":200,"width":30,"height":10,"clickX":115,"clickY":212}]}},"decision":"Follow Route","requestedActions":[{"type":"MoveForward","key":"W"}],"executedActions":[{"deviceType":"Keyboard","operation":"KeyDown","key":"W","keyCode":87},{"deviceType":"Keyboard","operation":"KeyUp","key":"W","keyCode":87,"durationMs":836}],"actionSource":"RULE","actionDurationMs":900,"stateAfter":{"player":{"mapPosition":{"x":2,"y":2,"z":3}}},"result":"Observed","eventReferences":[]}
```

The complete JSONL entries include all snapshot fields. The example's `actionDurationMs` is the transition window; each key event's `durationMs` reports the input hold. `schemaVersion` is in `manifest.json`.

## Verification

Run `dotnet build MasterOfPuppets.sln` and `dotnet run --project CoreTests -- record validate`. The offline suite asserts OFF creates no directory, ON persists and starts, transitions retain state/action data, route/path deduplication resolves to original points, byte/time shard rotation preserves sequence and session IDs, V1 inline data remains readable, and a second ON creates a new session. Existing `state` and `state watch` commands are unchanged. A live WoW session is still needed to verify visual detections, real key dispatch, and behavior timing.

To build the first MoveDataset later, read manifest/schema version and ordered transitions; select movement contexts and executed key intervals; join `stateBefore` and `stateAfter`; compute relative waypoint bearing, heading error, position and progress deltas from raw coordinates. Filter or weight failures in that **separate** DatasetBuilder. The recorder retains failed episodes and never trains a model.
