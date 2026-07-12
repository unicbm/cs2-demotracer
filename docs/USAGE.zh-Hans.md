# 使用说明

这份文档说明 converter 工作流、GUI、pool conversion 和本地 Rust API。
CS2 服务器控制台指令见 [`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md)。

## 1. 转换 demo 为回合 replay

先分析 demo：

```powershell
cs2-demotracer.exe inspect --demo <demo.dem>
```

转换推荐回合：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录>
```

转换推荐回合并导出 demo 自带游戏内语音：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --export-voice
```

校验输出：

```powershell
cs2-demotracer.exe validate --input <输出目录>
```

默认会在 C4 开始安放前截断，只导出开局路线。需要整回合时加 `--full-round`。
回合 replay 默认最多保留同一回合内 10 秒 freeze-time 上下文，用来让
`round_freeze_end` 后的道具松开动作能接上开局前的按住状态；可用
`--freeze-preroll-seconds` 调整。
`--full-round` 只表示导出的 replay 覆盖整回合。CSS 插件仍然从 `round_start` /
freeze time 开始播放，让 CS2 自己正常模拟后续回合状态。

常用选项：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --export-voice
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --rounds 0,1,2,5-8
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --side t
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --include-suspicious
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --freeze-preroll-seconds 10
```

`--export-voice` 会写出 `voice/roundXX.dtv`，用于 runtime 自动语音回放。它只在
demo 本身录到了游戏内语音时生效。社区服、FACEIT、5E demo 更可能包含语音；
被平台剥离 voice data 的 demo 不会生成语音 sidecar。完整流程见
[语音导出和回放](VOICE.zh-Hans.md)。

GUI 里 `导出语音(若有)` 默认开启。GUI 会在解析阶段收集 voice metadata；转换时如果
demo 里确实有语音，就写出 `.dtv` sidecar；如果导出了语音，复制出来的控制台指令会自动
带 `dtr_voice_auto on`。

饰品/econ 元数据默认绝不导出，所以普通 manifest 不包含 `cosmetics` block。若明确要导出
demo 观测到的武器 paint、刀具、手套、探员模型元数据，以及稳定的武器/刀具 custom name，
必须同时传入三个 flag：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --export-cosmetics --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

如果还要导出稳定的武器贴纸 slot/id/wear/offset/rotation/scale 元数据，再额外传入
`--export-stickers`。如果还要导出稳定的武器挂件/keychain slot 0 id、offset 以及可选
seed/highlight/sticker 元数据，再额外传入 `--export-charms`。`convert-pool` 支持同样
flag；不传时每个 replay manifest 都保持无饰品字段。

GUI 里饰品导出是一个高风险主开关；贴纸和挂坠是饰品详情里的记忆子选项。因为部分导出
并不会让后续 runtime 饰品对齐变成 GSLT 安全操作，所以它们不会和主开关并列展示。

输出目录里最重要的是：

```text
manifest.json
avatars/<sha256>.<ext>
round00/t/*.dtr
round00/ct/*.dtr
voice/round00.dtv
```

实际输出目录名会是 `<demo-stem>-<hash12>`；`hash12` 来自 demo 文件内容，用来避免同名 demo 互相覆盖。
`voice/` 只在传入 `--export-voice` 且 demo 含有可用 voice frame 时生成。
`avatars/` 只在 demo 包含比赛服务器头像覆写时生成；manifest 会记录每个头像对应的 SteamID64。
显式把 replay identity 设为 `avatar` 时，DemoTracer 会用真实 demo SteamID64 把通过
校验的匹配 PNG 头像覆写应用到 BotHider 管理的 replay bot，并由 native runtime 启用
`sv_reliableavatardata`，因此原生 Steam 资料卡信息仍然可用。如果匹配 PNG 缺失或无效，
则回退到 Steam 头像。默认 `steam` identity 不写头像覆写。

## 2. 批量生成地图回合池

批量生成 Mirage 回合池：

```powershell
cs2-demotracer.exe convert-pool --demo-dir <demo根目录> --output <输出目录>\mirage_pool --map de_mirage --recursive
```

输出结构：

```text
pool_manifest.json
replays/<demo-id>/manifest.json
replays/<demo-id>/roundNN/...
```

`convert-pool` 会按地图过滤 demo，并记录经济信息，让服务器插件可以在本地游戏中挑相似回合。

## 3. 底层 Rust API

转换器 crate 通过 `cs2_demotracer::dtr` 提供无损 `.dtr` IO：

```rust
use cs2_demotracer::dtr::{read_rec_file, write_rec_file};

let rec = read_rec_file("clip.dtr")?;
write_rec_file("copy.dtr", &rec)?;
```

这个 Rust API 目前面向本地工具和 git dependency 使用；crate 暂时没有发布到 crates.io。

## 4. 进 CS2 播放

先确保 CS2 本地服务器已经加载：

- Metamod:Source
- CounterStrikeSharp
- DemoTracer Metamod runtime：`BotController`
- DemoTracer CounterStrikeSharp 插件：`DemoTracer`

server bundle 包含 `BotController`、`DemoTracer`、`DemoTracerApi.dll`、
`demotracer-econ-index.v1.json` 和干净的示例配置；不包含 Metamod:Source、CounterStrikeSharp 或
CS2-Bot-Hider。BotHider 是可选依赖，只用于 BotHider 管理的 replay slot，以及 demo
显示名、SteamID64 对齐、demo 头像覆写这类 identity 功能。

然后使用 [`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md) 里的服务器指令。

普通回合 manifest 推荐使用高层 `dtr_go seq|round|pool` 命令。
`dtr_run_manifest` 和 `dtr_run_pool` 是给旧脚本保留的兼容 alias，
不是推荐的新手 quick start 路径。

饰品对齐是可选功能，默认关闭。只有 round manifest 是用 `--export-cosmetics` 和两个
风险确认 flag 导出，并且里面确实有 `cosmetics` 证据时，它才会生效。生效时
DemoTracer 也只会把 demo 观测到的武器 paint、刀、手套元数据，以及稳定的武器/刀具
custom name 和探员模型证据应用到安全 replay bot。默认不会随机分配饰品，不会读取
profile/database，也不会应用非 demo 证据的探员。有 `cosmetics.agent` 证据且开启
`dtr_cosmetics agents` 时，会把对应安全 replay bot slot 换成 demo 中的探员模型。
它可以应用 demo 观测到的 StatTrak/暗金武器质量 (`quality=9`)；
如果 demo 没暴露 StatTrak 计数器，runtime 会写显示用 `0`，让 CS2 选择带计数器的
StatTrak 模型，但这不代表伪造了 demo 击杀数。武器贴纸需要额外的
`--export-stickers` 转换 flag 和 runtime 的 `dtr_cosmetics stickers on`。武器挂件/keychain
需要额外的 `--export-charms` 转换 flag 和 runtime 的 `dtr_cosmetics charms on`。

这个功能面向本地/私有 replay 验证。listen/practice server 未必有专用服那样的 GSLT
暴露面，但只写 bot 不是规则豁免；如果真人玩家可以观察、接管或使用这些 bot 物品外观，
仍应按饰品/库存模拟风险处理。专用服、社区服或公网服应按 Valve server guidelines 下的
运营风险看待，非私有本地环境启用请自行承担风险。

准星对齐默认关闭。只有显式执行 `dtr_align crosshair on` 后，如果真人观察者正在第一人称
观察安全 replay bot，DemoTracer 才会把 demo 中稳定观测到的 `crosshair_code` 临时应用到
这个观察者，并在离开 replay POV 时恢复原准星。

目前先保持显式 opt-in：它会写真人观察者的客户端准星配置，而 handoff、bot takeover、
断线和 reload 时机附近的恢复生命周期还需要加固。
