# Playback server

This area contains everything installed on a local CS2 server for `.dtr`
playback. It is deliberately separate from the desktop converter.

| Path | Responsibility |
| --- | --- |
| [`plugins/`](plugins/) | CounterStrikeSharp orchestration, commands, tests, and companion API |
| [`runtime/`](runtime/) | Native Metamod replay and bot-presentation runtimes |

The maintained release combines these projects as one versioned playback
bundle. Do not mix binaries from different builds: the manifest, native ABI,
BotHider API, and CounterStrikeSharp reader must remain compatible.

The native runtime is built on the foundational work in
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) and
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider). See the root
[credits](../README.md#credits-and-foundations) and the runtime-specific
upstream notes for the exact maintenance boundary.
