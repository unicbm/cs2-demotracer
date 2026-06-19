# CS2-Bot-Controller

**Bot replay runtime for CS2 DemoTracer**

## Your stars⭐ are my motivation to keep updating

CS2-Bot-Controller is a Metamod:Source plugin for Counter-Strike 2 that takes
control of a bot's behaviour at the engine level. It can pin a bot's weapon,
freeze its aim, stop it jumping, or hand its movement over to external code —
and it can **record** a human player's per-tick movement and **replay** it back
through any bot.

It exposes both in-game console commands and a C-ABI surface for
CounterStrikeSharp, so a plugin can record, transfer, and replay motion with a
few P/Invoke calls. Win64 and Linux (linuxsteamrt64) are both supported.

------------------------------------------------------------------------

## Locks

- **Weapon** — pin a bot to one weapon slot; AI switches are blocked.
- **Aim** — freeze `CCSBot::Upkeep`; view holds still, AI keeps deciding/moving.
- **Jump** — block `CCSBot::Jump`; bot stops jumping, move/fire/aim unaffected.
- **All** — freeze both `CCSBot::Update` and `CCSBot::Upkeep`, so external code
  (such as motion replay) can drive the bot entirely.

------------------------------------------------------------------------

## Record & Replay

Capture a slot's movement tick by tick — origin, velocity, view angles, button
states, duck/ladder state, active weapon and all subtick input steps — then load
it onto another slot and play it back. Replay is driven through the engine's own
movement path, so it reproduces the original motion subtick-accurate.

Typical flow: lock the source slot if needed → `StartRecord` → move → `StopRecord`
→ `TransferRecordingToReplay` into a bot slot → `Lock(All)` the bot →
`StartReplay`. See the CounterStrikeSharp API section below.

------------------------------------------------------------------------

## Slots

| Target  | Engine | Weapon                  |
| ------- | ------ | ----------------------- |
| `Slot1` | 0      | Primary                 |
| `Slot2` | 1      | Pistol                  |
| `Slot3` | 2      | Knife / Zeus            |
| `Slot4` | 3      | Grenades                |
| `Slot5` | 4      | C4                      |

------------------------------------------------------------------------

## Install

The build stages a ready-to-copy `addons/` tree under `build/package/`.

- `BotController.dll` → `csgo/addons/BotController/bin/win64/`
  (`.so` → `linuxsteamrt64/` on Linux)
- `gamedata.json` → `csgo/addons/BotController/`
- `BotController.vdf`  → `csgo/addons/metamod/`

------------------------------------------------------------------------

## Build

Env: `HL2SDKCS2`, `MMSOURCE_DEV`, `CSGO_PROTO`, `protoc` (3.21.x) on PATH.

```
cmake -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

Config sources (vdf + gamedata) live under `configs/addons/`; the build copies
them into the package tree automatically.

------------------------------------------------------------------------

## Commands

```
bc_lock <all|aim|jump|weapon> <slot> [slot1..slot5]
bc_unlock <all|aim|jump|weapon> <slot>
bc_unlock_all <all|aim|jump|weapon>
bc_replay_pov [off|spectated|always]
bc_perf [0|1|reset]
bc_status
```

`weapon` mode requires the weapon slot as the third argument.

```
bc_lock aim 1                # freeze bot 1's view, AI still runs
bc_lock jump 1               # bot 1 can no longer jump
bc_lock all 1                # full freeze (use this before replay)
bc_lock weapon 1 slot3       # force bot 1 to knife
bc_unlock_all weapon         # clear every weapon lock
bc_replay_pov spectated      # publish replay POV only for watched bots
bc_perf 1                    # enable and print replay perf counters
bc_status                    # print hook status + every per-slot lock
```

Record / replay is driven through the C-ABI below, not console commands.

------------------------------------------------------------------------

## CounterStrikeSharp API

Drop `scripts/BotController.NativeApi.cs` into your project.

```csharp
using BotControllerApi;

if (!BotController.IsCompatible()) return;   // requires ABI 12
```

### Locks

```csharp
BotController.Lock(slot, LockKind.Aim);
BotController.Lock(slot, LockKind.Jump);
BotController.Lock(slot, LockKind.All);
BotController.Lock(slot, LockTarget.Slot3);   // weapon lock
BotController.Unlock(slot, LockKind.Aim);
BotController.UnlockAll(LockKind.Weapon);
BotController.IsLocked(slot, LockKind.Aim);
BotController.GetWeaponLock(slot);            // -> LockTarget
```

### Record & Replay

```csharp
// Record a slot's motion
BotController.StartRecord(srcSlot);
// ... player moves ...
BotController.StopRecord(srcSlot);

// Replay it on a bot
BotController.TransferRecordingToReplay(srcSlot, botSlot);
BotController.Lock(botSlot, LockKind.All);    // hand the bot over
BotController.StartReplay(botSlot, loop: false);

// Or pull the buffers out, persist them, and load later
var (ticks, subs) = BotController.GetRecordedMotion(srcSlot);
BotController.LoadReplay(botSlot, ticks, subs);
BotController.SetReplayPovMask(1UL << botSlot); // publish first-person POV for this replay slot

// Drive weapon/fire from the tick being replayed
if (BotController.TryGetReplayTick(botSlot, out var tick))
    BotController.SwitchBotWeapon(botSlot, tick.WeaponDefIndex);

BotController.ReplayCursor(botSlot);          // current tick, <0 if idle
BotController.ReplayTotal(botSlot);           // loaded tick count
BotController.StopReplay(botSlot);
```

`ReplayTick` / `SubtickMove` mirror the C++ struct layout byte-for-byte, so the
buffers can be serialized and reloaded across rounds. Main thread only.

------------------------------------------------------------------------

## Special thanks

- [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod) for helping determine the replay framework.

------------------------------------------------------------------------

## License

GPL-v3.0

------------------------------------------------------------------------

## Author

**XBribo**
