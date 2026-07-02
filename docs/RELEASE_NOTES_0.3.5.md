# CS2 DemoTracer v0.3.5

v0.3.5 is a hotfix release for a P0 bug in v0.3.4.

Users on v0.3.4 should upgrade immediately.

## Hotfix Summary

- Fixes a CounterStrikeSharp runtime crash path that could make CS2 clients fail
  during replay startup with an error like:
  `FATAL Error: WriteEnterPVS: GetEntServerClass failed for ent...`.
- The crash was caused by the v0.3.4 replay loadout cleanup path directly
  removing a replay weapon from the pawn inventory and then killing that entity.
  In CS2 this can leave an unsafe entity/PVS state during network snapshot
  writing.
- DemoTracer now returns to the safer engine drop path and delays cleanup of the
  dropped weapon entity instead of directly removing the weapon from the slot.

## Fixed

- Avoid direct `RemovePlayerItem` + entity kill during replay weapon replacement.
- Prefer strict CS2 bots before BotHider-managed fallback candidates when
  assigning replay slots, reducing the chance that a sixth team user occupies a
  DTR slot before a real bot.

## Compatibility

- No `.dtr` format changes.
- No manifest ABI changes.
- No BotController native ABI changes.
- No DemoTracer companion API changes.
- No behavior-breaking converter or runtime command changes.

## Upgrade Guidance

Upgrade any v0.3.4 server bundle immediately before replaying rounds that need
weapon/loadout alignment. Replace the server-side DemoTracer package with the
v0.3.5 server bundle, then restart the server.

Recommended post-upgrade checks:

```text
dtr_runtime
bc_status
```

Expected ABI remains:

```text
expected_abi=16 runtime_abi=16
```

## Assets

- `cs2-demotracer-v0.3.5-windows-x64.zip`: converter CLI and Rust GUI.
- `cs2-demotracer-server-v0.3.5-windows-x64.zip`: server playback bundle with
  BotController runtime and DemoTracer CounterStrikeSharp plugin.
- `SHA256SUMS.txt`: SHA-256 checksums for release assets.
