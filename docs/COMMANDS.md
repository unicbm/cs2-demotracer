# DemoTracer Command Reference

These commands are entered in the CS2 server console after the Metamod
`BotController` runtime and the CounterStrikeSharp `DemoTracer` plugin are
loaded. Add semicolons only when you want to paste several commands as one
console line.

## Recommended Baseline

```text
css_plugins reload DemoTracer
dtr_config_status
dtr_go seq "<output-dir>\<demo-id>\manifest.json" 0
```

Replay identity, weapon/loadout alignment, projectile alignment, and crosshair
alignment are on by default. Identity alignment only writes demo names and
SteamID64 values when BotHider is present and managing the target replay bot
slots. If the manifest contains demo-provided PNG avatar overrides, identity
`full` also applies them for matching SteamID64 values.

Use `seq` for "sequence from source round", `round` for one source round only,
and `pool` for economy-matched pool playback. `dtr_go` validates the plan,
arms it, then issues `mp_restartgame 1` so playback catches a fresh
`round_start`.

## Runtime Config JSON

DemoTracer reads optional runtime defaults from `demotracer.config.json` next to
`DemoTracer.dll`. The repository ships `demotracer.config.example.json` as a
sanitized starting point. The JSON controls server-local runtime preferences
only; it is not written into `.dtr` files or manifests.

```json
{
  "identity": "full",
  "allow_partial": true,
  "handoff": {
    "mode": "death_contact_c4",
    "scope": "slot",
    "threat_360": true,
    "threat_360_range": 420,
    "threat_360_los": true
  },
  "fidelity": {
    "preset": "default"
  },
  "match": {
    "preset": "off"
  },
  "cosmetics": {
    "preset": "off"
  }
}
```

Use `dtr_config_reload` after editing the file. Console commands such as
`dtr_set handoff ...` still work as temporary overrides until the config is
reloaded or the plugin is reloaded. Legacy `"align"` config blocks are still
accepted, but new `"fidelity"`, `"match"`, and `"cosmetics"` sections override
matching legacy fields.

## Defaults

| Setting | Default | Meaning |
| --- | --- | --- |
| `dtr_align default` | on | Replay fidelity: weapons/loadout, projectiles, crosshair, and left-hand desired writes. |
| `dtr_match off` | off | Match presentation sync, including scoreboard/KDA/MVP/team score. |
| `dtr_cosmetics off` | off | High-risk cosmetic evidence replay for skins, knives, gloves, names, stickers, and charms. |
| `dtr_handoff` | `death_contact_c4 slot` | Release the contacted/dead replay slot after contact or death; C4 planted releases all active replay slots. |
| `dtr_partial` | `1` | Allow replay with fewer bots than manifest players. |
| `dtr_replay_identity` | `full` | Write demo name, SteamID64, and demo-provided avatar overrides through BotHider-managed replay bot slots when available. |
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

### `dtr_go pool <pool_manifest.json> [server_round]`

Validates and arms a pool plan, then issues `mp_restartgame 1`. `server_round`
is a local server round hint for economy/pistol matching, not a manifest source
round.

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

Compatibility alias for old scripts, not the preferred quick start:
`dtr_run_manifest <manifest.json> [from_source_round]`.

### `dtr_stop_sequence`

Stops an armed or running manifest sequence. It does not delete files and does
not change plugin settings. It only stops future sequence scheduling; use
`dtr_stop_all` if you also need to stop already playing slots.

### `dtr_arm pool <pool_manifest.json> [server_round]`

Arms economy-matched playback from a converted map pool without restarting.

Implementation:

- Reads `pool_manifest.json`.
- On `round_start`, snapshots current T/CT equipment value plus available
  account money, picks a candidate, loads it, and sets native buy skip before
  vanilla bot buying can fight the replay loadout.
- Strictly keeps pistol rounds on demo round 0/12.
- For non-pistol rounds, builds a soft economy-matched candidate set, applies
  recent-candidate and recent-demo penalties, and samples from the best window
  instead of always taking the nearest neighbor.
- The economy match allows limited upward counterfactuals, so a weaker current
  buy can still draw a stronger opening route with better weapons or utility;
  drawing a poorer route from a stronger current buy is penalized.
- Starts the prepared replay on `round_freeze_end`.

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

## Replay Fidelity: `dtr_align`

`dtr_align` controls replay-fidelity behavior only. Scoreboard sync lives under
`dtr_match`; cosmetics live under `dtr_cosmetics`.

```text
dtr_align
dtr_align status
dtr_align default
dtr_align full
dtr_align handoff_safe
dtr_align off
dtr_align weapons <on|off>
dtr_align projectiles <on|off>
dtr_align crosshair <on|off>
dtr_align left_hand <on|off>
```

Presets:

- `default` / `full`: weapons, projectiles, crosshair, and left-hand desired
  writes are on.
- `handoff_safe`: keeps weapons/projectiles/crosshair on, but turns
  `left_hand` off for smoother handoff.
- `off`: disables replay-fidelity alignment switches; useful for debugging,
  not normal playback.

Aliases such as `loadout`, `active_weapon`, and `slot_lock` are accepted and
currently share the `weapons` implementation.

## Match Presentation: `dtr_match`

`dtr_match` controls local match presentation. It does not change replay
movement, weapons, projectiles, or cosmetics.

```text
dtr_match
dtr_match status
dtr_match off
dtr_match scoreboard
dtr_match scoreboard <on|off>
dtr_match full
```

`dtr_match scoreboard` syncs best-effort scoreboard/KDA/MVP/team score fields.
It is default-off.

## Cosmetic Evidence / Risk: `dtr_cosmetics`

`dtr_cosmetics` consumes explicitly exported demo cosmetic evidence. It is
default-off and may carry Valve GSLT/server-guideline risk outside private local
validation.

```text
dtr_cosmetics
dtr_cosmetics status
dtr_cosmetics off
dtr_cosmetics weapons
dtr_cosmetics basic
dtr_cosmetics full
dtr_cosmetics weapons <on|off>
dtr_cosmetics knives <on|off>
dtr_cosmetics gloves <on|off>
dtr_cosmetics names <on|off>
dtr_cosmetics stickers <on|off>
dtr_cosmetics charms <on|off>
```

Presets:

- `weapons`: weapon paint and weapon custom names only.
- `basic`: weapons, knives, gloves, and custom names; no stickers or charms.
- `full`: `basic` plus stickers and charms.

## Handoff / Partial / Identity And Legacy Aliases

The old `dtr_set align ...` and direct `dtr_*_align` commands remain accepted
for existing scripts during the beta migration window. New users should prefer
`dtr_align`, `dtr_match`, and `dtr_cosmetics`.

### `dtr_weapon_align <0|1>`

Enables or disables weapon/loadout alignment.

Implementation when enabled:

- At round load, native buy control is set to skip vanilla bot buying for replay
  slots.
- At pre-start, the plugin applies manifest loadout data: armor, helmet, CT kit,
  grenades, primary/secondary candidates, and start weapon.
- During replay ticks, the plugin follows `.dtr` weapon def indices and asks the
  native runtime to switch active weapon and lock the matching inventory slot.
- For `.dtr` v6+ files, player-scoped equipment/C4 events are processed once by
  replay cursor. Combat events are loaded as record-only metadata and do not
  force health or death.
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

### `dtr_cosmetic_align <0|1>`

Enables or disables cosmetic alignment. It is off by default and has no effect
unless the converter wrote manifest `cosmetics` evidence through the explicit
`--export-cosmetics`, `--acknowledge-cosmetic-gslt-risk`, and
`--accept-cosmetic-export-disclaimer` flags.

Implementation when enabled:

- Applies only manifest `cosmetics` evidence exported from the demo player's
  observed round data.
- Supports weapon paint kit/seed/wear, stable weapon/knife custom names, knife
  item def plus paint kit/seed/wear, and glove item def plus paint kit/wear
  where the demo exposes it. If
  demoparser exposes glove item def/paint/wear but no glove seed, the converter
  writes deterministic seed `0` for that glove.
- Weapon stickers are not part of this legacy command alone. They require
  `--export-stickers` during conversion and `dtr_cosmetics stickers on` at
  runtime. Legacy aliases `dtr_sticker_align 1` and
  `dtr_set align stickers on` still work.
- Weapon charms/keychains are not part of this legacy command alone. They
  require `--export-charms` during conversion and `dtr_cosmetics charms on` at
  runtime. Legacy aliases `dtr_charm_align 1` and
  `dtr_set align charms on` still work.
- Applies only to safe replay bot slots after weapon/loadout alignment has
  confirmed the replay inventory path.
- Never picks random cosmetics, never reads a server profile/database, and never
  applies to real human players.

Important limits:

- Agents are not applied.
- StatTrak is limited to demo-observed weapon cosmetic evidence:
  `quality=9` may be applied. If the manifest has no nonnegative
  `stattrak_counter`, runtime writes display counter `0` to request the
  StatTrak counter model; this is not a demo kill-count claim.
- Missing, zero, contradictory, or unsupported demo evidence is skipped.
- This is a replay-fidelity feature intended for local/private validation.
- A local listen/practice server may not have the same GSLT exposure as a
  dedicated server, but bot-only cosmetic mutation is not a policy exemption if
  human players can observe, control, possess, inspect, or otherwise use those
  bot items.
- On dedicated, community, or public servers, cosmetic/inventory simulation can
  fall under Valve server-operation policy. Use outside private local
  validation is at the operator's own risk.

### `dtr_sticker_align <0|1>`

Enables or disables weapon sticker alignment. It is off by default and has no
effect unless cosmetic alignment is also enabled and the manifest was exported
with `--export-stickers` in addition to the cosmetic export risk flags.

Implementation when enabled:

- Applies only stable manifest sticker evidence attached to confirmed replay
  weapon cosmetics.
- Supports sticker slot, sticker id, wear, offset x, offset y, rotation, and
  raw scale metadata.
- Does not apply schema or agents. Charms/keychains use
  `dtr_charm_align`. StatTrak comes from weapon cosmetic evidence, not sticker
  alignment.
- Sticker write failures are counted as skipped stickers and do not roll back
  weapon paint, knife, glove, or custom-name alignment.

### `dtr_charm_align <0|1>`

Enables or disables weapon charm/keychain alignment. It is off by default and
has no effect unless cosmetic alignment is also enabled and the manifest was
exported with `--export-charms` in addition to the cosmetic export risk flags.

Implementation when enabled:

- Applies only stable manifest charm/keychain evidence attached to confirmed
  replay weapon cosmetics.
- Supports charm slot 0 id, offset x, offset y, offset z, optional seed,
  optional highlight, and optional charm sticker id.
- Does not apply random charms, profile/database inventory, agents, or
  unsupported charm slots.
- Charm write failures are counted as skipped charms and do not roll back weapon
  paint, knife, glove, custom-name, StatTrak, or sticker alignment.

### `dtr_crosshair_align <0|1>`

Enables or disables crosshair alignment. It is on by default.

When enabled, DemoTracer uses manifest `view.crosshair_code` evidence exported
from the demo player's stable `crosshair_code` value while a human viewer is
watching a safe replay bot in-eye. Missing or contradictory demo evidence is
skipped. This affects POV/spectator fidelity only; it does not change movement,
weapons, projectiles, replay bot state, or inventory cosmetics.

### `dtr_left_hand_desired <0|1>`

Controls whether newly loaded `.dtr` v7 command frames keep
`left_hand_desired` writes.

- `1`: preserve demo left-hand/right-hand desired state. This is the default
  and highest-fidelity behavior.
- `0`: strip left-hand desired writes before loading replay frames into native
  playback. This lowers replay fidelity, but significantly improves handoff
  smoothness when a left-hand replay bot would otherwise switch back to the
  server default right-hand viewmodel after handoff.

The setting affects replays loaded after the command is changed. Reload the
round, sequence, or pool plan to apply it to already loaded replay slots.

### `dtr_replay_identity <0|1>`

Controls BotHider identity alignment.

When enabled and BotHider is available, manifest loading queues name and
SteamID64 updates for BotHider-managed bot slots using the demo player's
`player_name` and `steam_id`. If the manifest contains PNG `avatar_overrides`,
`full` mode also writes the matching server avatar override and enables
`sv_reliableavatardata`. The default mode is `full`.

This is mainly for POV/spectator clarity. If BotHider is not installed or is not
managing a replay bot slot, identity alignment skips that slot instead of
applying to real human players.

### `dtr_partial <0|1>`

Controls whether a round may load with fewer safe bot slots than manifest
players.

- `1`: load as many safe same-side bot slots as available and report skipped
  T/CT counts.
- `0`: fail loading unless all manifest players can be assigned.

### `dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [all|slot]`

Controls when replay releases bot control back to normal bot behavior.

Modes:

- `off`: never hand off automatically.
- `death`: hand off when a replay-controlled player dies or kills.
- `contact`: hand off on combat/contact detection.
- `death_or_contact`: use both death and contact triggers.
- `death_contact_c4`: use death, contact, and C4 planted triggers. This is the
  default.

Scope:

- `slot`: release only the trigger slot. This is the intended safe default.
- `all`: release every replaying slot when one trigger fires. Use only for
  experiments.

C4 planted is round-phase handoff, not an individual duel trigger. It releases
all active replay slots even when scope is `slot`.

Contact implementation:

- Uses bullet damage/hurt events and replay-bot enemy visibility checks.
- On contact, DemoTracer stops replay control, releases native locks, and resets
  replay-owned bot state. Post-handoff fighting is left to the normal CS2 bot AI;
  DemoTracer does not run a CSGO-style combat executor.
- Ignores the first short replay grace window after start to avoid immediate
  false handoff.
- With `threat_360_los=true`, 360 threats require line of sight across
  `threat_360_range`; close LOS threats trigger immediately, while farther LOS
  threats require a short hold. With `threat_360_los=false`, the full configured
  360 range remains an experimental no-LOS trigger.

## Diagnostics

### `dtr_config_reload`

Reloads `demotracer.config.json` from the plugin directory and applies it to the
current runtime settings. If the file is missing, built-in defaults remain
active.

### `dtr_config_status`

Prints the config path, whether the file exists, and the effective runtime
settings.

### `dtr_runtime`

Prints the runtime version matrix: expected and loaded native ABI, capability
bitset, missing required capability bits, native build id, optional
`UsercmdMovementIntent`/`LeftHandIntent` export status, supported `.dtr` reader
range, platform, and `DemoTracerApi` version.

### `dtr_doctor [manifest.json|pool_manifest.json]`

Prints a compact health check: native ABI compatibility, capability bitset,
native build id, optional `UsercmdMovementIntent`/`LeftHandIntent` export
status, supported `.dtr` reader range, platform, `DemoTracerApi` version,
current map/time, freeze-time ConVar, bot counts, BotHider-managed slots, safe
replay targets, loaded/playing replay counts, alignment settings, handoff mode,
RayTrace status, and optional manifest or pool-manifest summary.

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
