# Desktop product

The supported CS2 DemoTracer converter is a Windows x64 desktop application.
This area contains both layers of that product:

| Path | Responsibility |
| --- | --- |
| [`gui/`](gui/) | React interface, Tauri command bridge, installer, and updater |
| [`converter/`](converter/) | Reusable Rust demo parsing, analysis, and `.dtr` writing core |

The GUI calls the converter crate directly. There is no supported public
converter CLI, and server playback code does not depend on the desktop UI.

See [the development guide](../docs/DEVELOPMENT.md) for build commands.
