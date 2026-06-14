# NOTICE

This project includes or adapts ideas/code from the following projects:

- `XBribo/CS2-Bot-Locker`，GPL-v3。本项目的 CS2 runtime 基于该项目的 Metamod hook、bot replay 和 weapon lock 思路继续改造。
- `LaihoE/demoparser`，MIT。本项目 converter vendored 了最小 Rust parser/csgoproto 源码，用于本地解析 CS2 demo。
- `csgowiki/minidemo-encoder`，MIT。本项目没有直接复制其 Go 代码；但 `.dem -> replay file` 的工具形态和 BotMimic/minidemo 兼容思路来自该项目启发。
- Metamod:Source、CounterStrikeSharp 及相关 SDK/社区项目，提供 CS2 插件开发基础设施。

`third_party/demoparser` 中保留了 demoparser 的原始 `LICENSE` 和 README。

本项目自身代码按仓库根目录 `LICENSE` 中的 GPL-3.0 发布。
