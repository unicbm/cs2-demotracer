# CS2 Demo BotMimic

**语言：** [English](../README.md) | 简体中文

把 CS2 比赛 demo 里的真人移动，转换成 bot 可以在本地服务器里回放的工具。

如果这个项目对你有帮助，欢迎给一个 Star。这样其他 CS2 工具和插件开发者也更容易找到它。

简单说：你给它一个 `.dem`，它会分析每个回合，导出 `.cs2rec` 回放文件。进 CS2 本地服务器后，插件可以按回合让 bot 复刻 demo 里的走位、视角、跳跃、下蹲、开火和基础武器切换。

这个项目还在 MVP 阶段，但已经可以做端到端测试。

## 适合谁

- 想把职业比赛 demo 里的 10 人轨迹搬进本地 CS2 服务器。
- 想用 GUI 点选 demo、分析回合、导出回放文件。
- 想做类似 BotMimic 的 CS2 版本，而不是只看 demo 录像。

## 你需要准备什么

- Windows 版 CS2。
- Rust，用来运行转换器。
- 本地 CS2 服务器环境。
- Metamod + CounterStrikeSharp，用来加载播放插件。

后面会尽量提供打包好的 exe 和插件包；当前开发版需要本地构建。

## 第一步：用 GUI 转换 demo

打开 PowerShell：

```powershell
cd cs2-demo-botmimic\converter
cargo run --release -- gui
```

GUI 里按这个流程：

1. 选择一个 CS2 `.dem` 文件。
2. 选择输出目录。
3. 点击“分析回合”。
4. 看表格里的回合人数、时长和问题说明。
5. 默认勾选推荐回合即可。
6. 点击导出。

导出后会生成类似这样的目录：

```text
output/<demo名字>/manifest.json
output/<demo名字>/round00/t/<玩家>.cs2rec
output/<demo名字>/round00/ct/<玩家>.cs2rec
output/<demo名字>/round01/...
```

`manifest.json` 是播放时最方便使用的入口文件。

## 第二步：进游戏播放

先确保 CS2 本地服务器已经加载：

- Metamod runtime：`BotLocker`
- CounterStrikeSharp 插件：`Cs2DemoBotMimic`

进入服务器后，在控制台输入：

```text
css_plugins reload Cs2DemoBotMimic
cs2bm_weapon_align 1
cs2bm_run_manifest "<输出目录>\<demo名字>\manifest.json" 0
```

含义：

- `cs2bm_run_manifest` 会按回合顺序播放。
- 最后的 `0` 表示从 round 0 开始。
- 插件会在 `round_start` 准备 bot，在 `round_freeze_end` 开始播放。

如果只想测试某一回合，可以把最后的数字改成对应 round：

```text
cs2bm_run_manifest "<输出目录>\<demo名字>\manifest.json" 12
```

查看状态：

```text
cs2bm_status 0
cs2bm_bots
```

停止：

```text
cs2bm_stop_all
```

## 回合表怎么看

转换器会把每个 round 标成“推荐”或“可疑”。

常见可疑原因：

- 人数不足 10 个。
- T 或 CT 人数不正常。
- 回合太短。
- demo 尾部有比赛结束后的垃圾回合。
- 断线重连导致轨迹缺失。

普通使用建议只导出推荐回合。可疑回合一般不适合作为训练或复刻数据。

## 当前限制

- 目前主要面向 Windows x64 本地 CS2 环境。
- 需要同一张地图，并且服务器里要有足够的 bot。
- v1 先保证 tick 级轨迹回放流畅；subtick 和完整 usercmd 还会继续补。
- 某些武器和皮肤/默认手枪配置在 CS2 里比较麻烦，目前优先保证不崩服和基本行为正确。
- 这个工具不是作弊工具，也不会接入匹配服务器；它面向本地服务器、研究和内容制作。

## 开发者入口

常用命令：

```powershell
cd cs2-demo-botmimic\converter
cargo test
cargo run --release -- inspect --demo <demo.dem>
cargo run --release -- convert --demo <demo.dem> --output <输出目录>
```

目录：

- `converter/`：Rust GUI/CLI 转换器。
- `runtime/BotMimicRuntime/`：CS2 Metamod runtime。
- `css/`：CounterStrikeSharp 控制插件。
- `docs/`：格式和使用补充说明。
- `third_party/`：保留的第三方源码和许可说明。

## Credits

感谢这些项目和作者：

- [XBribo/CS2-Bot-Locker](https://github.com/XBribo/CS2-Bot-Locker)：CS2 bot hook、录制/回放和武器锁定思路，本项目 runtime 基于它继续改造。
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser)：Rust CS2 demo parser，本项目 converter 使用它解析 demo。
- [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder)：CS:GO demo 到 BotMimic/minidemo 风格回放的思路参考。
- Metamod:Source 和 CounterStrikeSharp 社区：CS2 本地插件生态。

本项目使用 GPL-3.0 license。第三方项目的原始许可见 `NOTICE.md` 和对应源码目录。
