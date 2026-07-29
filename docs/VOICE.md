# Voice Sidecars

When the source demo contains usable `CCLCMsg_VoiceData` frames, the desktop
converter writes one compact sidecar per exported round:

```text
<archive>/
  manifest.json
  round00/t/*.dtr
  round00/ct/*.dtr
  voice/round00.dtv
```

Voice export is enabled by default and is independent of cosmetic export. No
sidecar is written when the demo contains no valid voice payloads or speaker
XUIDs. Community, FACEIT, and 5E recordings are more likely to preserve voice
than Valve demos.

## Playback

```text
dtr_voice_auto on
dtr_go seq "<manifest.json>" 0
```

DemoTracer maps sidecar speaker XUIDs to loaded replay bots and schedules the
original encoded Opus payloads on the round timeline. Keep `voice/roundXX.dtv`
next to the matching manifest archive; copying only `.dtr` files is insufficient.

Recipients follow team visibility:

- Observers hear all replay voice.
- T players hear T replay bots.
- CT players hear CT replay bots.
- Bots and HLTV are not recipients.

## Diagnostics

```text
dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]
dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]
dtr_voice_stop
```

If `voice_auto=unavailable`, update the complete playback bundle and check
`dtr_runtime`. If `voice_auto=map_failed`, verify that the sidecar, source round,
manifest, and loaded replay identities match.
