# CS2 DemoTracer

Trace CS2 demos into bot-executable route replays.

**语言：** [English](../README.md) | 简体中文

把 CS2 比赛 demo 里的真人路线，转换成 bot 可以在本地服务器里执行的 route replay。

如果这个项目对你有帮助，欢迎给一个 Star。这样其他 CS2 工具和插件开发者也更容易找到它。

## 演示

bot 会回放从 CS2 demo 转换出的移动、视角、开火和武器状态；第一人称观察视角会跟随 replay 状态同步。

<table>
  <tr>
    <td align="center" width="50%">
      <img src="media/first-person-replay-nuke.gif" alt="Nuke 第一人称 CS2 bot replay" width="100%"><br>
      <sub>第一人称路线回放</sub>
    </td>
    <td align="center" width="50%">
      <img src="media/first-person-replay-route.gif" alt="室内路线第一人称 CS2 bot replay" width="100%"><br>
      <sub>室内路线回放</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="media/mirage-opening-replay.gif" alt="Mirage 多 bot 开局回放" width="100%"><br>
      <sub>Mirage 多 bot 开局路线</sub>
    </td>
    <td align="center" width="50%">
      <img src="media/mirage-projectile-smokes.gif" alt="Mirage 烟雾弹投掷物对齐回放" width="100%"><br>
      <sub>Mirage 烟雾弹投掷物对齐</sub>
    </td>
  </tr>
</table>

简单说：你给它一个 `.dem`，它会分析每个回合，导出压缩 `.dtr` 回放文件。进 CS2 本地服务器后，插件可以按回合让 bot 复刻 demo 里的走位、视角、跳跃、下蹲、开火和基础武器切换。

它也可以用 Demo2Nade 路径导出“单个道具投掷 clip”：围绕 demo 里的投掷 release tick，保留很短的移动/视角上下文，输出 `.dtr` 和 typed manifest，方便本地工具索引、查询和播放真实职业哥道具。

这个项目还在 MVP 阶段，但已经可以做端到端测试。

如果你只想查看 `.dtr` 字段布局，可以看主 README 里的 [`.dtr` Format Contract](../README.md#dtr-format-contract)。详细转换器用法见 [`USAGE.zh-Hans.md`](USAGE.zh-Hans.md)，服务器指令的用途和实现边界见 [`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md)。

## 适合谁

- 想把职业比赛 demo 里的 10 人轨迹搬进本地 CS2 服务器。
- 想用快速 CLI 完成 `.dem` -> `.dtr` 转换。
- 想从 demo 中生成本地真实道具库。
- 想做 CS2 路线回放、bot playback 或 demo 分析工具。

## 你需要准备什么

- Windows x64，用来运行主要维护的打包转换器。
- Linux 可以尝试从源码构建，但目前不维护 Linux 二进制发布包。
- 如果从源码构建转换器，需要 Rust。
- 如果要在游戏里播放，需要本地 CS2 服务器、Metamod 和 CounterStrikeSharp。

转换器本身是独立 CLI exe。只有把 `.dtr` 放进 CS2 本地服务器播放时，才需要插件。

如果你只是想测试插件播放效果，可以从 Release assets 下载这个已经预先转换好的 Mirage 样例包：[`cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip`](https://github.com/unicbm/cs2-demotracer/releases/download/v0.1.3/cs2-demotracer-sample-spirit-vs-falcons-m2-mirage-full.zip)。解压后直接用里面的 `manifest.json` 播放即可。

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
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --freeze-preroll-seconds 10
```

`inspect` 会输出地图、tick rate、行数，以及推荐/可疑回合列表。`convert` 默认只导出推荐回合；只有明确需要可疑回合时再加 `--include-suspicious`。默认导出的 replay 会在 C4 开始安放前截断，先专注“开局路线”；需要整回合时加 `--full-round`。回合 replay 默认最多保留同一回合内 10 秒 freeze-time 上下文，用来保留开局前按住的道具 attack 状态，可用 `--freeze-preroll-seconds` 调整。

导出后会生成类似这样的目录：

```text
output/<demo-id>/manifest.json
output/<demo-id>/round00/t/<玩家>.dtr
output/<demo-id>/round00/ct/<玩家>.dtr
output/<demo-id>/round01/...
```

`<demo-id>` 是 `<demo-stem>-<hash12>`，其中 `hash12` 来自 demo 文件内容。这样即使不同赛事的文件名很像，也不会互相覆盖。

`manifest.json` 是播放时最方便使用的入口文件。

如果你不会写 Rust，可以看 [`examples/`](../examples/) 里的 Python 和 Node.js
小脚本。它们只是调用 CLI、定位生成的 `manifest.json`，再打印一条 CS2 console
指令；这些是集成示例，不是稳定的 Python/Node SDK。

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

## Demo2Nade：导出单个道具 clip

Demo2Nade 会把 demo 中真实出现过的投掷物，导出成短 `.dtr` clip 和
`nade_manifest.json`。默认保留 release 前 `1.0s` 和 release 后 `0.5s`。

```powershell
cs2-demotracer.exe convert-nades --demo "<demo.dem>" --output "<输出目录>\nades"
cs2-demotracer.exe convert-nades --demo "<demo.dem>" --output "<输出目录>\nades" --side t --rounds 0,1,5-8 --pre-roll 1.0 --post-roll 0.5
```

输出结构：

```text
<输出目录>/nades/<demo-id>/nade_manifest.json
<输出目录>/nades/<demo-id>/nade_manifest.json.br
<输出目录>/nades/<demo-id>/nades/<side>/<phase>/<kind>/<clip-id>.dtr
```

`phase` 分为 `opening`、`combat`、`retake`。`opening` 默认是 freeze time 结束后的
20 秒窗口，可以用 `--opening-seconds` 调整。

批量从 demo 目录生成按地图聚合的本地道具库：

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir "<demo根目录>" --output "<输出目录>\nade_library" --recursive --jobs 8
```

它会把每个 demo 的原始 clip 放在 `demos/`，把按地图聚合的 manifest 放在
`maps/<map>/`，并写出顶层 `nade_library.json(.br)`。默认会在地图级 manifest
里合并非常接近的重复道具；如果要保留所有 source throws，可以加 `--no-dedupe`。

Rust 本地工具也可以直接调用 API：

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeClipExportRequest::new("match.dem", "out/nades");
request.context = NadeContextOptions {
    pre_roll_seconds: 1.0,
    post_roll_seconds: 0.5,
    opening_seconds: 20.0,
};

let report = export_nade_clips_from_demo_path(&request)?;
println!("clips={}", report.clips_written);
```

更完整的 CLI 和 Rust API 示例见 [`USAGE.zh-Hans.md`](USAGE.zh-Hans.md#3-demo2nade-道具-clip)。

## 第二步：进游戏播放

先确保 CS2 本地服务器已经加载：

- Metamod runtime：`BotController`
- CounterStrikeSharp 插件：`DemoTracer`

进入服务器后，在控制台输入：

```text
css_plugins reload DemoTracer
dtr_set align weapons on
dtr_set align projectiles on
dtr_set handoff death_or_contact slot
dtr_set allow_partial on
dtr_go seq "<输出目录>\<demo-id>\manifest.json" 0
```

含义：

- `seq` 会从指定的 manifest source round 开始按顺序播放。
- 最后的 `0` 是 `from_source_round=0`，不是“只播放 round 0”。
- 如果只想播放单个 source round，用 `dtr_go round "<manifest.json>" 0`。
- 插件会在 `round_start` 准备 bot，在 `round_freeze_end` 开始播放。

完整回合回放开始时，DemoTracer 会把选中的 replay bot 当作回合起点状态处理：
仍然存活的 replay bot 会恢复到 100 HP；已经死亡的 replay bot 会先复活，再开始
播放；武器/loadout 同步只会吞掉 DemoTracer 自己为了替换 bot slot 而主动丢出的
武器，不会扫描或清理场上无关的可拾取实体。

`dtr_projectile_align 1` 会在“出生后修正稳定”的情况下使用 demo 投掷物元数据。
火（molotov/incendiary）保留 CS2 原生 projectile 和 inferno 行为，因为出生后修改
火瓶实体可能破坏本来有效的燃烧。

如果只想测试某一回合，可以把最后的数字改成对应 round：

```text
dtr_go round "<输出目录>\<demo-id>\manifest.json" 12
```

如果要重置本地 round，并从某个 source round 的 C4 安装完成后开始：

```text
dtr_go_at "<输出目录>\<demo-id>\manifest.json" 33 bomb
```

玩家也可以直接在聊天框用快捷入口：

```text
.replay "<输出目录>\<demo-id>\manifest.json" 33
```

如果想自己代入某个 demo 选手打这个 post-plant：

```text
.moment "<输出目录>\<demo-id>\manifest.json" 33 magixx
```

plant 后锚点需要转换时加 `--full-round`；普通转换默认会在 C4 开始安放前截断，
用于开局路线 replay。

如果使用 Mirage 回合池：

```text
dtr_go pool "<输出目录>\mirage_pool\pool_manifest.json" 0
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

- 目前主要面向 Windows x64 本地 CS2 环境。Linux 可以尝试从源码构建，
  但 converter/runtime 的 Linux 发布包目前不是维护目标。
- 需要同一张地图，并且服务器里要有足够的 bot。
- `.dtr` 是无损压缩的 BotController 兼容 replay 格式，并包含用于运行时道具对齐的 demo 原始投掷物元数据；离线 subtick 和完整 usercmd 还会继续补。
- 某些武器和默认手枪配置在 CS2 里比较麻烦，目前优先保证不崩服和基本行为正确。
- CS2 demo 可能暴露饰品/econ 元数据，但 DemoTracer 有意不提取或应用皮肤、刀、手套、贴纸、挂件/charms 或探员。Valve 的 [Game Server Operation Guidelines](https://blog.counter-strike.net/server_guidelines/) 禁止社区服务器伪造玩家库存或授予玩家未拥有的物品；Valve 曾经禁用提供这类服务的服务器运营者的 GSLT（Game Server Login Token）。第三方饰品覆写不属于本项目范围，风险由使用者自行承担。
- 这个工具不是作弊工具，也不会接入匹配服务器；它面向本地服务器、研究和内容制作。

## 开发者入口

常用命令：

```powershell
cd cs2-demotracer\converter
cargo test
cargo run --release -- inspect --demo <demo.dem>
cargo run --release -- convert --demo <demo.dem> --output <输出目录>
cargo run --release -- convert-pool --demo-dir <demo根目录> --output <输出目录> --map de_mirage --recursive
cargo run --release -- convert-nades --demo <demo.dem> --output <输出目录>
cargo run --release -- convert-nades-library --demo-dir <demo根目录> --output <输出目录> --recursive
cargo run --release -- validate --input <输出目录>
cargo run --release -- wizard
```

目录：

- `converter/`：Rust CLI、本地 Rust API、prompt-style 向导转换器和 Demo2Nade 导出代码。
- `runtime/BotController/`：CS2 Metamod runtime。
- `css/DemoTracer/`：CounterStrikeSharp 控制插件。
- `css/DemoTracerApi/`：给其他 CounterStrikeSharp 插件引用的 API 契约。
- `docs/`：格式和使用补充说明。
- `examples/`：Python 和 Node.js 的 CLI 集成示例。
- `third_party/`：保留的第三方源码和上游许可文件。

## 致谢

CS2 DemoTracer 建立在多个优秀的开源项目之上。

没有 [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)，
runtime bot replay 路径很难成立。它提供了 GPL-3.0 的 BotController 基础，
包括 replay hook、录制、输入注入和武器锁定。

感谢 [XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)，本项目使用
它提供 BotHider 互操作路径，用于识别 BotHider 管理的 bot，并对齐显示名和
SteamID64。

感谢 [LaihoE/demoparser](https://github.com/LaihoE/demoparser) 提供 Rust CS2 demo
parser。本项目 converter 使用它解析 demo；vendored 源码保留在
`third_party/demoparser`，并保留上游 license 和 README 文件。

感谢 [csgowiki/minidemo-encoder](https://github.com/csgowiki/minidemo-encoder)
提供历史 `.dem -> replay file` 工作流启发。本项目没有复制它的 Go 源码。

CS2 DemoTracer 也使用 [Metamod:Source](https://github.com/alliedmodders/metamod-source)
和 [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 构建
runtime/plugin 栈。

CS2 DemoTracer 自己的代码使用 GPL-3.0-only。vendored 的第三方源码保留各自上游
license 文件。
