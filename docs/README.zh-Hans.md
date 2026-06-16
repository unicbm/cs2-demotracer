# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**语言：** [English](../README.md) | 简体中文

把 CS2 比赛 demo 里的真人路线，转换成 bot 可以在本地服务器里执行的 route replay。

如果这个项目对你有帮助，欢迎给一个 Star。这样其他 CS2 工具和插件开发者也更容易找到它。

## 演示

bot 会回放从 CS2 demo 转换出的移动、视角、开火和武器状态；第一人称观察视角会跟随 replay 状态同步。

![Nuke 第一人称 CS2 bot replay](media/first-person-replay-nuke.gif)

[查看 720p/60fps MP4](media/first-person-replay-nuke.mp4)

简单说：你给它一个 `.dem`，它会分析每个回合，导出压缩 `.dtr` 回放文件。进 CS2 本地服务器后，插件可以按回合让 bot 复刻 demo 里的走位、视角、跳跃、下蹲、开火和基础武器切换。

这个项目还在 MVP 阶段，但已经可以做端到端测试。

如果你只想查看 `.dtr` 字段布局，可以看 [`FORMAT.md`](FORMAT.md)。

## 适合谁

- 想把职业比赛 demo 里的 10 人轨迹搬进本地 CS2 服务器。
- 想用快速 CLI 完成 `.dem` -> `.dtr` 转换。
- 想做 CS2 路线回放、bot playback 或 demo 分析工具。

## 你需要准备什么

- Windows x64，用来运行打包好的转换器。
- 如果从源码构建转换器，需要 Rust。
- 如果要在游戏里播放，需要本地 CS2 服务器、Metamod 和 CounterStrikeSharp。

转换器本身是独立 CLI exe。只有把 `.dtr` 放进 CS2 本地服务器播放时，才需要插件。

## 转换单个 demo

打开 PowerShell：

```powershell
cs2-demotracer.exe inspect --demo "<demo.dem>"
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>"
cs2-demotracer.exe validate --input "<输出目录>"
```

常用转换选项：

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --rounds 0,1,5-8
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --side t
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --full-round
```

`inspect` 会输出地图、tick rate、行数，以及推荐/可疑回合列表。`convert` 默认只导出推荐回合；只有明确需要可疑回合时再加 `--include-suspicious`。默认导出的 replay 会在 C4 开始安放前截断，先专注“开局路线”；需要整回合时加 `--full-round`。

导出后会生成类似这样的目录：

```text
output/<demo-id>/manifest.json
output/<demo-id>/round00/t/<玩家>.dtr
output/<demo-id>/round00/ct/<玩家>.dtr
output/<demo-id>/round01/...
```

`<demo-id>` 是 `<demo-stem>-<hash12>`，其中 `hash12` 来自 demo 文件内容。这样即使不同赛事的文件名很像，也不会互相覆盖。

`manifest.json` 是播放时最方便使用的入口文件。

也可以使用交互式向导：

```powershell
cs2-demotracer.exe wizard
```

## 批量生成地图回合池

如果你有很多 demo，可以先生成一个 Mirage 回合池，让插件按双方经济自动挑相似回合：

```powershell
cs2-demotracer.exe convert-pool --demo-dir "<demo根目录>" --output "<输出目录>\mirage_pool" --map de_mirage --recursive
```

输出目录会类似这样：

```text
<输出目录>/mirage_pool/pool_manifest.json
<输出目录>/mirage_pool/replays/<demo-id>/manifest.json
<输出目录>/mirage_pool/replays/<demo-id>/roundNN/...
```

`convert-pool` 会按地图过滤 demo，转换每个匹配 demo，并写入回合经济信息供插件选择。

## 第二步：进游戏播放

先确保 CS2 本地服务器已经加载：

- Metamod runtime：`BotController`
- CounterStrikeSharp 插件：`DemoTracer`

进入服务器后，在控制台输入：

```text
css_plugins reload DemoTracer
dtr_weapon_align 1
dtr_run_manifest "<输出目录>\<demo-id>\manifest.json" 0
```

含义：

- `dtr_run_manifest` 会按回合顺序播放。
- 最后的 `0` 表示从 round 0 开始。
- 插件会在 `round_start` 准备 bot，在 `round_freeze_end` 开始播放。

如果只想测试某一回合，可以把最后的数字改成对应 round：

```text
dtr_run_manifest "<输出目录>\<demo-id>\manifest.json" 12
```

如果使用 Mirage 回合池：

```text
dtr_run_pool "<输出目录>\mirage_pool\pool_manifest.json" 0
```

round 0 和 round 12 只会匹配 demo 的 round 0/12 手枪局；其他回合会按双方当前装备价值粗略匹配 eco / force / full。

查看状态：

```text
bc_status
dtr_status 0
dtr_bots
```

停止：

```text
dtr_stop_all
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
- `.dtr` 是无损压缩的 BotController 兼容 replay 格式；离线 subtick 和完整 usercmd 还会继续补。
- 某些武器和皮肤/默认手枪配置在 CS2 里比较麻烦，目前优先保证不崩服和基本行为正确。
- 这个工具不是作弊工具，也不会接入匹配服务器；它面向本地服务器、研究和内容制作。

## 开发者入口

常用命令：

```powershell
cd cs2-demotracer\converter
cargo test
cargo run --release -- inspect --demo <demo.dem>
cargo run --release -- convert --demo <demo.dem> --output <输出目录>
cargo run --release -- convert-pool --demo-dir <demo根目录> --output <输出目录> --map de_mirage --recursive
cargo run --release -- validate --input <输出目录>
cargo run --release -- wizard
```

目录：

- `converter/`：Rust CLI 和 prompt-style 向导转换器。
- `runtime/BotController/`：CS2 Metamod runtime。
- `css/`：CounterStrikeSharp 控制插件。
- `docs/`：格式和使用补充说明。
- `third_party/`：保留的第三方源码和许可说明。

## Credits

感谢这些项目和作者：

- [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)：CS2 bot hook、录制/回放、输入注入和武器锁定思路，本项目使用 BotController runtime 架构。
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser)：Rust CS2 demo parser，本项目 converter 使用它解析 demo。
- [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder)：历史 CS:GO demo-to-replay 工具链思路参考。
- Metamod:Source 和 CounterStrikeSharp 社区：CS2 本地插件生态。

本项目使用 GPL-3.0 license。第三方项目的原始许可见 `NOTICE.md` 和对应源码目录。
