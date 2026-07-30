# Known Limitations

## Scope

- Windows x64 is the maintained converter and playback target.
- Playback expects the source map and enough safe bot slots.
- Round selection happens after a complete demo parse.
- Replay is designed for local servers, research, content creation, and plugin
  development—not matchmaking.

## Replay

- `.dtr` preserves the evidence it stores, but it is not a complete offline
  reconstruction of every CS2 user command or physics interaction.
- Other plugins that control bot AI, buying, inventory, movement, or identity
  can conflict with replay state.
- Boosts and player-on-player movement can look wrong when a human replaces one
  of the recorded participants.
- Handoff restores native AI state conservatively; complex contact transitions
  can still be imperfect.

## Identity and Presentation

- A connected original player and a replay bot can share presentation evidence.
  Use `dtr_replay_identity name` or `off` in that case.
- BotHider changes the visible replay identity, not the native bot name used by
  `bot_kick`; use `dtr_kick` for replay bots.
- Some demos contain team/default avatar images rather than true player avatars,
  so TAB, observer, and profile surfaces may disagree.
- Scoreboard alignment is best-effort and default-off.

## Projectiles, Voice, and Cosmetics

- Projectile alignment is not exact for every throw; molotov/incendiary effects
  have the highest variance. Uncertain evidence stays on native CS2 behavior.
- Voice replay is available only when the demo contains usable voice netmessages.
- Sticker extraction and placement cannot reproduce every CS2 transform exactly.
- Cosmetic/econ export and runtime alignment are explicit opt-in features; see
  the safety warning in the [root README](../README.md#safety-defaults).
- With BotRandomizer installed, demo-backed cosmetic writes require its v1
  writer lease and an authenticated BotHider replay identity. If either
  provider is unavailable, playback continues and DemoTracer fails closed.
  While the lease is active, Agent, Knife, and Gloves are identity fields:
  missing evidence is restored to the native/default state, while ordinary
  weapon fields without positive evidence remain available to Randomizer.
