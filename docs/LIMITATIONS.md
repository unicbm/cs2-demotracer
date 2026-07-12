# Limitations

This document tracks known limitations and edge cases.

## Platform And Server Scope

- Windows x64 local CS2 is the primary target. Linux may work from source, but
  Linux converter/runtime packages are not currently maintained release targets.
- The server should run the same map and have enough bots.
- This is for local servers, research, content creation, and plugin development.
  It is not intended for matchmaking or cheating.

## Replay Model

- `.dtr` uses a lossless compressed BotController-compatible replay format with
  demo-derived projectile metadata, player-scoped high-fidelity events, and
  inventory snapshots. Full offline usercmd reconstruction is future work.
- If other plugins also interfere with native bot AI, unexpected bot buying or
  inventory behavior can happen. DTR replay is slot-bound, not weapon-bound, so
  a bot in a replay slot can have a primary weapon while still following an ECO
  route and acting with pistol-era demo inputs. DemoTracer currently preserves
  the demo's firing and weapon-slot switching behavior instead of hooking it
  away. Treat this as expected current behavior, not a replay bug.

## Bot Identity And Presentation

- If you replay a match where you were one of the original demo players, the
  server can contain both your real player and a bot that looks like you. In
  that case, avoid CS2-Bot-Hider: it can create a bot with the same SteamID and
  avatar as you, which may confuse TAB scoreboard and related UI surfaces. This
  usually should not crash the game, but the presentation can be wrong.
- Avatar overrides come from PNG data exposed by demo metadata. The current
  runtime path validates the replay slot before the delayed avatar write runs,
  so stale unloaded slots should not receive old avatar writes. The underlying
  CS2 `ServerAvatarOverrides` table is still keyed by SteamID64. DemoTracer
  reserves an empty index-zero fallback so unmatched players do not inherit the
  first DTR avatar. The recommended `dtr_replay_identity avatar` mode keeps the
  real demo SteamID64 so Steam profile-card metadata remains available, and
  falls back to the Steam avatar when no valid matching PNG exists. A real
  account with that same SteamID64 will share the override while present on the
  local server. Some demos provide team/default logo PNGs rather than true
  per-player avatars, so TAB, observer, and other UI surfaces can still disagree.
- BotHider rewrites the visible SteamID and in-game bot display name, but it
  does not change the bot name that CS2 native logic considers authoritative.
  A bot can display donk's avatar and SteamID while `bot_kick donk` still does
  not target it; use `dtr_kick` for DemoTracer replay bots instead. One
  possible future approach is strict botprofile ID matching before BotHider
  setup and DTR slot assignment, but large community demo sets can have
  duplicate or complex player names. CS2 botprofile matching is also
  case-insensitive, so `NiKo` and `niko` collapse to the same bot name. Adding
  SteamID suffixes or extra botprofile edits would add complexity and increase
  the profile database footprint, so DemoTracer does not do this for now.

## Handoff And Physics Edge Cases

- Outside freeze-time pre-roll, native AI update and upkeep shadow-run during
  replay, which avoids handing control back to a cold perception/decision
  state. Complex handoffs can still
  be imperfect because replay view output remains authoritative until release
  and DemoTracer deliberately does not seed private enemy or aim state with
  heuristics. Contact is released from native enemy/visibility/nearby state;
  managed RayTrace is only an older-runtime fallback.
- Boosts and other player-on-player arrangements are replayed from recorded
  position and velocity. If a human replaces the lower bot in a boost, the upper
  bot can appear to float because the DTR bot is constrained to the demo's
  original movement state. Reconstructing stable key intent from demo position
  and velocity is non-unique, and small errors cause drift, so these cases are
  not special-cased.
- HLTV and most professional demos are expected to use solid teammates
  (`mp_solid_teammates 1`), where players cannot pass through each other. Bots
  usually do not get stuck because of this. Demos from casual-like modes where
  players can pass through teammates can behave differently.

## Projectile And Cosmetic Fidelity

- Projectile alignment is not guaranteed to match the demo perfectly in every
  case, although most throws should behave correctly. Molotovs/incendiaries are
  the highest-variance case. Newly converted `.dtr` files include fire effect
  metadata and allow conservative fire alignment only when that metadata is
  reliable; older files or uncertain fire throws stay on native CS2 behavior.
- Bot cosmetics are restored as closely as practical and are usually fine, but
  sticker evidence is limited by demoparser behavior and by CS2's richer sticker
  coordinate/rotation model. DemoTracer uses a revised sticker extraction path,
  but exact sticker placement and rotation are not guaranteed for every item.
- Cosmetic/econ export and runtime alignment are explicit opt-in features; see
  [Cosmetic Alignment and GSLT Safety](../README.md#cosmetic-alignment-and-gslt-safety).
