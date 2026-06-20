# DemoTracer Command Reference

These commands are entered in the CS2 server console after the Metamod
`BotController` runtime and the CounterStrikeSharp `DemoTracer` plugin are
loaded. Add semicolons only when you want to paste several commands as one
console line.

## Recommended Baseline

```text
css_plugins reload DemoTracer
dtr_set identity full
dtr_set align weapons on
dtr_set align projectiles on
dtr_set handoff death_or_contact slot
dtr_set allow_partial on
dtr_go seq "<output-dir>\<demo-id>\manifest.json" 0
```

`dtr_set identity full` is useful when BotHider is present because the replay
slots inherit demo names and SteamID64 values. If BotHider is unavailable, leave
it off.

Use `seq` for "sequence from source round", `round` for one source round only,
and `pool` for economy-matched pool playback. `dtr_go` validates the plan,
arms it, then issues `mp_restartgame 1` so playback catches a fresh
`round_start`.

## Defaults

| Setting | Default | Meaning |
| --- | --- | --- |
| `dtr_weapon_align` | `1` | Align loadout, buy behavior, active weapon, and weapon slot locks. |
| `dtr_projectile_align` | `1` | Align grenade projectile initial vectors from `.dtr` v4+ data. |
| `dtr_handoff` | `death_or_contact slot` | Release only the contacted/dead replay slot after contact or death. |
| `dtr_partial` | `1` | Allow replay with fewer bots than manifest players. |
| `dtr_replay_identity` | `0` | Do not write BotHider name/SteamID unless explicitly enabled. |
| `dtr_util_trace` | `0` | Utility CSV trace disabled. |
| `bc_replay_pov` | `spectated` | Publish expensive native first-person POV updates only for replay bots watched in-eye. |

## High-Level Playback

### `dtr_go seq <manifest.json> [from_source_round]`

Validates and arms a manifest sequence, then issues `mp_restartgame 1`.
`from_source_round` defaults to `0` and means "start the sequence at this demo
source round", not "play only this round".

### `dtr_go round <manifest.json> <source_round>`

Validates and arms exactly one demo source round, then issues
`mp_restartgame 1`. This does not advance to later manifest rounds.

### `dtr_go_at <manifest.json> <source_round> <seconds_after_live|bomb|bomb+seconds> [loop:0|1]`

Validates and arms exactly one demo source round, starts it from the requested
live-round offset, then issues `mp_restartgame 1`.

Use `bomb` to start at the completed C4 plant event recorded in `manifest.json`,
or `bomb+2.5` to start a few seconds after the plant. Numeric values are seconds
after live round start. Post-plant playback requires full-round converter output:
convert with `--full-round`.

`dtr_arm_at` has the same arguments but waits for the next natural
`round_start`.

### `dtr_go pool <pool_manifest.json> [server_round]`

Validates and arms a pool plan, then issues `mp_restartgame 1`. `server_round`
is a local server round hint for economy/pistol matching, not a manifest source
round.

## Chat Shortcut

### `.replay "<manifest.json>" <source_round> [bomb|seconds|bomb+seconds] [loop:0|1]`

Players can type this in chat for quick local testing. It behaves like
`dtr_go_at` and restarts the round. The start anchor defaults to `bomb`, so the
short form is:

```text
.replay "<output-dir>\<demo-id>\manifest.json" 33
```

Use `.replay stop` to stop DemoTracer replay state.

## Moment Playback

### `dtr_moment <manifest.json> <source_round> <bomb|seconds|bomb+seconds> <player_name|steamid> [human_slot] [loop:0|1]`

Starts an interactive moment: the selected human player is placed at the chosen
demo player's replay snapshot, while other players still alive at that anchor
are loaded onto replay bots and started from the same point.

When run by a player, `human_slot` is omitted. When run from the server console,
pass the human slot after the demo player selector.

```text
dtr_moment "<output-dir>\<demo-id>\manifest.json" 33 bomb magixx
```

Chat shortcut:

```text
.moment "<output-dir>\<demo-id>\manifest.json" 33 magixx
```

Moment v1 uses replay position/view/velocity, round loadout, armor/helmet/kit,
and active weapon def. Exact anchor HP, used utility, ammo, and planted C4 state
are not yet full game-state snapshots.

## Sequence Playback

### `dtr_arm seq <manifest.json> [from_source_round]`

Arms sequential playback without restarting the server round.

Implementation:

- Reads all playable rounds from `manifest.json`.
- Stops and unloads any current replay state.
- On the next `round_start`, prepares the current round by loading per-player
  `.dtr` files onto safe bot slots.
- On `round_freeze_end`, starts all loaded replays.
- After each started round, advances to the next round in the manifest.

Legacy alias: `dtr_run_manifest <manifest.json> [from_source_round]`.

### `dtr_stop_sequence`

Stops an armed or running manifest sequence. It does not delete files and does
not change plugin settings. It only stops future sequence scheduling; use
`dtr_stop_all` if you also need to stop already playing slots.

### `dtr_arm pool <pool_manifest.json> [server_round]`

Arms economy-matched playback from a converted map pool without restarting.

Implementation:

- Reads `pool_manifest.json`.
- On `round_freeze_end`, snapshots current T/CT equipment value.
- Selects a candidate round by pistol-round status and economy similarity.
- Loads that candidate round and starts replay immediately.
- Tracks recently used candidates to reduce repeated picks.

Use this when you want a local game to keep choosing similar opening routes from
a pool instead of replaying one fixed demo.

### `dtr_stop_pool`

Stops future pool selection and clears the in-memory pool state. It does not
stop slots that are already playing; use `dtr_stop_all` for that.

## Manual Loading And Playback

### `dtr_load round <manifest.json> <source_round>`

Loads one round from a manifest onto available replay bot slots, but does not
start playback.

Implementation:

- Assigns T files to T bot slots and CT files to CT bot slots.
- Uses safe candidates only: strict CS2 bots or BotHider-managed bot slots.
- Applies buy skip for loaded slots so vanilla bot buying does not fight the
  replay loadout.
- Records per-slot manifest metadata such as player name, SteamID64, loadout,
  preload weapon defs, and projectile events.

Legacy alias: `dtr_load_round <manifest.json> <source_round>`.

### `dtr_arm round <manifest.json> <source_round> [loop:0|1]`

Arms one source round to load on the next `round_start` and start live playback
on `round_freeze_end`.

This is useful for testing a specific round with normal freeze-time timing.

Legacy alias: `dtr_arm_round <manifest.json> <source_round> [loop:0|1]`.

### `dtr_play loaded [loop:0|1]`

Starts every currently loaded slot immediately.

Before starting, the plugin preloads replay loadouts and start weapons when
`dtr_weapon_align` is enabled.

This is a manual/debug command. It bypasses lifecycle-safe `round_start` and
`round_freeze_end` alignment.

### `dtr_load slot <slot> <absolute-or-game-path.dtr>`

Loads a single `.dtr` file into one bot slot. This is a low-level manual command
for experiments. It does not get manifest-only metadata such as `player_name`,
`steam_id`, or full loadout unless those can be scanned from the `.dtr` itself.

### `dtr_play slot <slot> [loop:0|1]`

Starts replay for one loaded slot, after checking that the target is still a
safe bot target.

### `dtr_stop slot <slot>`

Stops replay on one slot and releases runtime locks, pending alignments, buy
plans, and replay brain state for that slot.

### `dtr_stop_all`

Stops all currently loaded slots and disables active sequence/pool/armed state.
Loaded slot metadata may remain in memory; use `dtr_unload` when you want to
remove a specific loaded replay from a slot.

### `dtr_unload <slot>`

Unloads one slot and clears the plugin metadata for that slot.

## Nade Clip Playback

Nade clip manifests are produced by `convert-nades` or by a map manifest from
`convert-nades-library`. These commands are for local inspection and playback of
one real demo-derived throw at a time.

### `dtr_list_nades <nade_manifest.json|nade_manifest.json.br> [kind]`

Prints clip IDs from a nade manifest.

`kind` is optional and can be `smoke`, `flash`, `he`, `molotov`, `incgrenade`,
`decoy`, or a weapon def index such as `48`. The printed clip ID is the value
used by `dtr_run_nade`.

### `dtr_run_nade <nade_manifest.json|nade_manifest.json.br> <clip_id> <slot> [loop:0|1]`

Loads one nade `.dtr` clip from the manifest onto a bot slot and starts it
immediately.

Implementation:

- Resolves the clip path relative to the manifest path.
- Uses the clip's manifest metadata for the thrower's side, phase, grenade kind,
  start weapon, loadout, and projectile event.
- Uses safe replay targets only: strict CS2 bots or BotHider-managed bot slots.
- Applies normal replay cleanup on stop, finish, unload, or target failure.

Use this for validating a specific throw from `convert-nades` output.

### `dtr_cycle_smokes|dtr_cycle_flashes|dtr_cycle_he|dtr_cycle_fire|dtr_cycle_random_nades <nade_manifest.json|nade_manifest.json.br> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]`

Cycles through matching nade clips on one bot slot with a fixed gap between
clips. This is mainly for local library inspection. `all` includes opening
clips; the current cycle parser does not expose a separate `opening` filter. It
does not move the bot between lineup starts; choose clips whose start positions
are suitable for your current test setup.

## Fidelity And Handoff Controls

### `dtr_weapon_align <0|1>`

Enables or disables weapon/loadout alignment.

Implementation when enabled:

- At round load, native buy control is set to skip vanilla bot buying for replay
  slots.
- At pre-start, the plugin applies manifest loadout data: armor, helmet, CT kit,
  grenades, primary/secondary candidates, and start weapon.
- During replay ticks, the plugin follows `.dtr` weapon def indices and asks the
  native runtime to switch active weapon and lock the matching inventory slot.
- For missing weapons, the plugin uses CS2 item giving and cautious slot
  replacement instead of trying to fake a buy menu purchase.

Important limits:

- This is replay fidelity alignment, not a full economy simulator.
- Team-restricted live buying is bypassed; the plugin works from demo loadout
  data where possible.
- CS2 default pistol and inventory-slot behavior can still cause approximate
  results in edge cases.

### `dtr_projectile_align <0|1>`

Enables or disables projectile initial-vector alignment.

Implementation when enabled:

- Requires `.dtr` v4+ projectile events from the converter.
- Matches grenade projectile entities for smoke, flash, HE, molotov,
  incendiary, and decoy when matching replay metadata is available.
- The bot still performs the throw action naturally. The plugin waits for CS2 to
  spawn the projectile, resolves its thrower slot, matches the next demo
  projectile event near the replay cursor, and writes:
  `InitialPosition`, `InitialVelocity`, `AbsOrigin`, and `AbsVelocity`.
- Matching is retried for a few ticks because CS2 may not attach the thrower or
  final projectile fields immediately at spawn time.
- Smoke detonation metadata is still the most complete diagnostic path because
  smoke projectile lifetime and detonation events are exposed clearly enough for
  tracing.

Why it exists:

Replaying player origin, velocity, view angles, buttons, and subtick input does
not always reproduce the same grenade initial velocity. Small velocity or height
differences can make precision smokes hit a different collision edge. The
projectile data records the demo result directly and corrects that bias.

### `dtr_replay_identity <0|1>`

Controls BotHider identity alignment.

When enabled and BotHider is available, manifest loading queues name and
SteamID64 updates for BotHider-managed bot slots using the demo player's
`player_name` and `steam_id`.

This is mainly for POV/spectator clarity. It is off by default because it
depends on BotHider being installed and managing the replay bot slots.

### `dtr_partial <0|1>`

Controls whether a round may load with fewer safe bot slots than manifest
players.

- `1`: load as many safe same-side bot slots as available and report skipped
  T/CT counts.
- `0`: fail loading unless all manifest players can be assigned.

### `dtr_handoff <off|death|contact|death_or_contact> [all|slot]`

Controls when replay releases bot control back to normal bot behavior.

Modes:

- `off`: never hand off automatically.
- `death`: hand off when a replay-controlled player dies or kills.
- `contact`: hand off on combat/contact detection.
- `death_or_contact`: use both death and contact triggers.

Scope:

- `slot`: release only the trigger slot. This is the intended safe default.
- `all`: release every replaying slot when one trigger fires. Use only for
  experiments.

Contact implementation:

- Uses bullet damage/hurt events and replay-bot enemy visibility checks.
- Ignores the first short replay grace window after start to avoid immediate
  false handoff.
- Resets native locks and bot brain state when releasing a slot.

## Diagnostics

### `dtr_doctor [manifest.json|pool_manifest.json]`

Prints a compact health check: native ABI compatibility, current map/time,
freeze-time ConVar, bot counts, BotHider-managed slots, safe replay targets,
loaded/playing replay counts, alignment settings, handoff mode, RayTrace status,
and optional manifest or pool-manifest summary.

Use this first when playback does not start, starts with fewer slots than
expected, or a sample pack is being checked on a new server.

### `dtr_bots`

Prints team players, strict bot status, BotHider-managed status, native
`controllingBot` state, replay-candidate status, slot, team, and name.

Use this before playback if a manifest refuses to load or assigns fewer slots
than expected.

### `dtr_status <slot>`

Prints native ABI, replay cursor/total, playback state for one slot, handoff
mode, partial mode, identity mode, projectile align state, and active
sequence/pool pointer.

### `dtr_util_trace <0|1> [path]`

Writes a CSV trace for utility debugging.

The trace includes slot replay cursor, live/replay positions and velocities,
weapon state, grenade stash state, smoke projectile state, smoke detonation
events, and internal projectile-align messages.

This is a debugging command. It can produce large CSV files and should stay off
for normal playback.

### `bc_status`

This command comes from the native `BotController` runtime, not the CSS
DemoTracer plugin. It is still useful because it prints hook status, replay
hook counters, lock counts, and buy-plan status.

### `bc_replay_pov [off|spectated|always]`

Controls native first-person replay POV publishing.

- `spectated` is the default. DemoTracer sends a per-slot mask for human
  spectators currently watching a replay bot in first person.
- `always` restores the older behavior where every replay bot publishes server
  view-angle changes every tick.
- `off` disables this POV publishing path for maximum runtime performance.

Movement replay, weapon switching, projectile alignment, and handoff behavior
do not depend on this setting.

### `bc_perf [0|1|reset]`

Toggles, resets, and prints native replay performance counters.

Use it when testing 10-bot playback. With `bc_replay_pov spectated` and nobody
watching a replay bot in first person, server-view writes and `VirtualQuery`
counts should stay near zero. With one in-eye spectator, they should scale like
one bot per tick instead of every loaded replay bot.
