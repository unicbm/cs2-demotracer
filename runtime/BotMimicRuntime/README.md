# CS2-Bot-Locker

CS2-Bot-Locker is a Metamod:Source plugin for Counter-Strike 2 that can lock Bot's Weapon/Aim/Jump/All
It can be installed on win64 clients.

- **Weapon** — pin a bot to one weapon slot; AI switches are blocked.
- **Aim** — freeze `CCSBot::Upkeep`; view holds still, AI keeps deciding/moving.
- **Jump** — block `CCSBot::Jump`; bot stops jumping, move/fire/aim unaffected.
- **All** — freeze both `CCSBot::Update` and `CCSBot::Upkeep`.

## Your stars⭐ are my motivation to keep updating

**Version**: 0.5.0 · **ABI**: 9

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

- `BotLocker.dll` → `csgo/addons/BotLocker/bin/win64/`
- `gamedata.json` → `csgo/addons/BotLocker/`
- `BotLocker.vdf`  → `csgo/addons/metamod/`

------------------------------------------------------------------------

## Build

Env: `HL2SDKCS2`, `MMSOURCE_DEV`, `CSGO_PROTO`, `protoc` on PATH.

```
cmake -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

------------------------------------------------------------------------

## Commands

```
bl_lock <all|aim|jump|weapon> <slot> [slot1..slot5]
bl_unlock <all|aim|jump|weapon> <slot>
bl_unlock_all <all|aim|jump|weapon>
bl_status
```

`weapon` mode requires the weapon slot as the third argument.

```
bl_lock aim 1                # freeze bot 1's view, AI still runs
bl_lock jump 1               # bot 1 can no longer jump
bl_lock all 1                # full freeze
bl_lock weapon 1 slot3       # force bot 1 to knife
bl_unlock_all weapon         # clear every weapon lock
```

------------------------------------------------------------------------

## CounterStrikeSharp API

Drop `scripts/BotLocker.NativeApi.cs` into your project.

```csharp
using BotLockerApi;

if (!BotLocker.IsCompatible()) return;   // requires ABI 5

BotLocker.Lock(slot, LockKind.Aim);
BotLocker.Lock(slot, LockKind.Jump);
BotLocker.Lock(slot, LockKind.All);
BotLocker.Lock(slot, LockTarget.Slot3);  // weapon lock
BotLocker.Unlock(slot, LockKind.Aim);
BotLocker.UnlockAll(LockKind.Weapon);
BotLocker.IsLocked(slot, LockKind.Aim);
BotLocker.GetWeaponLock(slot);
```

Main thread only.

------------------------------------------------------------------------

## License

GPL-v3.0

------------------------------------------------------------------------

## Author

**XBribo**
