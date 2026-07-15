# Voice Export and Replay

DemoTracer can export in-game voice only when the source CS2 demo actually
contains demo-backed voice netmessages. The normal export path is:

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --export-voice
```

This writes compact per-round voice sidecars next to the `.dtr` replay files:

```text
<output-dir>/
  <demo-id>/
    manifest.json
    round00/t/*.dtr
    round00/ct/*.dtr
    voice/
      round00.dtv
      round01.dtv
```

`voice/roundXX.dtv` is required for automatic voice playback. Copying only the
`.dtr` files is not enough.

In the GUI, `Export voice if present` is enabled by default. Analyze collects
voice metadata, Convert writes `.dtv` sidecars when the demo has usable voice,
and copied console commands include `dtr_voice_auto on` after voice sidecars are
exported.

## When Voice Is Available

Not every demo records in-game voice. Voice export needs usable
`CCLCMsg_VoiceData` frames with audio payloads and valid speaker XUIDs.

More likely to work:

- Community server demos.
- FACEIT demos.
- 5E demos.
- Other server recordings that preserve in-game voice netmessages.

Often no voice is exported because:

- The platform did not record voice.
- Voice data was stripped.
- Players did not use in-game voice.
- Voice frames did not have valid XUIDs.

If no usable frames are found, conversion can still produce route replay files,
but voice sidecars are skipped.

## Convert With Cosmetics and Voice

Voice export is independent from cosmetic export. To export both voice and
demo-backed cosmetic evidence:

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --export-voice --export-cosmetics --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

To include stickers and charms too:

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --export-voice --export-cosmetics --export-stickers --export-charms --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

## Server Playback

Automatic playback is controlled by `dtr_voice_auto`:

```text
dtr_voice_auto on
dtr_go seq "<manifest.json>" 0
```

A practical full-fidelity local test command is:

```text
dtr_replay_identity steam; dtr_align full; dtr_match full; dtr_cosmetics full; dtr_partial 1; dtr_handoff death_contact_c4 slot; dtr_voice_auto on; dtr_go seq "<manifest.json>" 0
```

When a source round starts, DemoTracer looks for `voice/roundXX.dtv` near the
manifest, maps speaker XUIDs to loaded replay bots, and sends the original voice
payload on the round timeline. If the converted round includes bounded freeze
pre-roll, matching voice evidence starts during freeze time and an utterance may
continue across `round_freeze_end` without being restarted.

The `.dtv` container preserves the demo's encoded Opus payload bytes. Its
timeline and speaker metadata use compact integer encoding, but the Opus audio
blob is not wrapped in another lossless compressor. File size therefore mostly
tracks how much in-game voice the source demo contains; comparing `.dtv` size
directly with route-oriented `.dtr` size is not a compression-ratio test.

## Listening Rules

- Observers hear all replay voice.
- T players hear T replay bot voice.
- CT players hear CT replay bot voice.
- Bots and HLTV are not voice recipients.

## Manual Debug Commands

Normal sequence playback should use `dtr_voice_auto on`. These commands are for
debugging a `.dtv` sidecar:

```text
dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]
dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]
dtr_voice_stop
```

There is also a low-level converter command for creating one explicit continuous
voice clip for local tests:

```powershell
cs2-demotracer.exe export-voice-clip --demo "<demo.dem>" --output "<clip.dtv>" --all-speakers --start-tick <tick> --seconds 8
```

That command is not the normal round replay workflow; prefer
`convert --export-voice` for DemoTracer sequence playback.

## Troubleshooting

No `voice/` directory:

- Confirm you passed `--export-voice`.
- Confirm the demo contains recorded in-game voice.
- Try a community/FACEIT/5E demo known to include voice.

`voice_auto=unavailable`:

- Update the full playback bundle.
- Check `dtr_runtime`; BotController must expose voice send capability.

`voice_auto=map_failed`:

- The sidecar speakers did not map to currently loaded replay bots.
- Check that the manifest source round and `voice/roundXX.dtv` match.
- Partial replay can skip some speakers if matching replay bots were not loaded.
