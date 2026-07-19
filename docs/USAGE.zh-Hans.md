# 使用说明

这份文档说明 converter 工作流、GUI、pool conversion 和本地 Rust API。
CS2 服务器控制台指令见 [`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md)。

## 1. 转换 demo 为回合 replay

先分析 demo：

```powershell
cs2-demotracer.exe inspect --demo <demo.dem>
```

同一组命令也可以直接接受 FACEIT 常见的 `<demo.dem.zst>`。Zstandard 解压在
DemoTracer 进程内完成，归档身份按解压后的 Demo 内容计算。

对于 `<match>-p1.dem`、`<match>-p2.dem` 这类连续 HLTV 分段，只需传入任意一段。
DemoTracer 会验证并合并完整回合链，生成一份逻辑分析和一个 manifest；遇到缺段或
不兼容分段会明确拒绝，不会分别产出两份残缺归档。

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

`--rounds` 等回合过滤参数只筛选分析/导出结果。Demoparser 仍然会解析整场比赛，
所以只选一个回合也不是廉价的局部解析。

`--export-voice` 会写出 `voice/roundXX.dtv`，用于 runtime 自动语音回放。它只在
demo 本身录到了游戏内语音时生效。社区服、FACEIT、5E demo 更可能包含语音；
被平台剥离 voice data 的 demo 不会生成语音 sidecar。完整流程见
[语音导出和回放](VOICE.zh-Hans.md)。

GUI 里 `导出语音(若有)` 默认开启。GUI 会在解析阶段收集 voice metadata；转换时如果
demo 里确实有语音，就写出 `.dtv` sidecar；如果导出了语音，复制出来的控制台指令会自动
带 `dtr_voice_auto on`。

GUI 替换已有输出时，会先在同一输出文件夹的 staging 目录中写完并校验新的 DTR、头像、
语音和 manifest。只有完整 pack 才会提升为正式目录；转换、语音、校验或提升失败都会保留
旧输出，中途打断的目录交换会在下次转换时恢复。

GUI 首页使用一个主回放库，也可以同时索引多个额外归档目录。Windows 首次运行默认位置是
`文档\CS2 DemoTracer\Library`；已有自选主库不会自动搬动。新的 GUI 输出按
`<库>\<地图>\<可读名称>--<hash12>\` 归档，CLI 的显式 `--output` 语义保持不变。扫描优先
读取 `demo-info.json` 的紧凑元数据，缺失时才保守退回 `manifest.json`；不会重新解析源 Demo，
也不会逐个解压全部 `.dtr`。打开某个条目后，才进入严格 Manifest reader 并校验其中引用的
回放文件。新转换会直接复用同一份 `ParsedDemo` 生成本机 sidecar，按正式 `round_end`
事件和稳定队伍身份计算换边后的比分，并保存 K/D/A、
header/服务器平台证据、`CDemoFileInfo` 完整时长，以及原 `.dem` 或 `.dem.zst` 的绝对本地路径；可移植
`manifest.json` 仍保持脱敏，另有小型 `demo-source.json` 独立保存来源指针。没有明确终局面板的比分只会标成“截至 Demo 结束处”；旧 Manifest 的
回合开始比分不再作为最终比分显示。补全资料会先自动校验并使用已记录的原 Demo 路径，只对
无法定位的归档询问新文件或搜索目录，成功后只更新本机 sidecar，不会重写
`.dtr`。归档里的“重新选择轮次”会复用同一条源文件指针。源文件 mtime 只会
标成近似的 Demo 文件时间，因为 Demo 本身没有可靠的绝对比赛时间戳。“整理旧归档”会
严格校验散落的回放目录，按完整 Demo SHA-256 去重后复制进按地图归类的主库，原目录不会
被移动或删除。

GUI 的“设置”工作区会把输出/归档目录与原始 `.dem`/`.dem.zst` 库目录分开，保存安全的导出和播放
默认项，并提供本地环境体检。只有玩家主动点击自动检测时，GUI 才会读取 Steam 安装
信息；玩家始终可以手动填写 CS2 根目录或 `game/csgo`。体检只读检查 Metamod、
CounterStrikeSharp、DemoTracer、本地 CSS 插件、安装收据和已知 vendor 冲突，不会加载
扫描到的 DLL，也不会改写游戏目录。设置好的原始 demo 库会在批量修复归档 metadata 时
优先搜索，仍未找到时 GUI 才会再询问目录。本地服务器运行后，短时有效的
`demotracer-runtime.v1.json` heartbeat 会让同一页面核对已加载的 BotController
ABI/capability、BotHider provider、CounterStrikeSharp host 版本、饰品对齐开关和 CSS 插件目录名；过期证据只会显示为
“未运行”，绝不会冒充插件仍在生效。

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

每件导出的武器、刀具和手套饰品都会在 manifest 中附带可粘贴到 CS2 控制台的
`inspect.command`。编码结果未超过 Steam protocol 的 300 字符限制时，还会附带可直接
启动预览的 `inspect.steam_url`；贴纸/挂件组合过长时只省略 URL，控制台命令仍然保留。
这种 synthetic preview payload 不依赖 Steam 库存、市场挂单、GC 查询或第三方 API。

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

playback bundle 包含 `BotController`、DemoTracer 自维护的 `BotHider`、
`DemoTracer`、`DemoTracerBotHider`、对应 API assembly、
`demotracer-econ-index.v1.json` 和干净的示例配置；不包含 Metamod:Source 或
CounterStrikeSharp。全部 CounterStrikeSharp 插件统一以 .NET 10 为目标。安装播放包前
必须移除另外安装的公开 BotHider CSS 插件。

然后使用 [`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md) 里的服务器指令。

普通回合 manifest 推荐使用高层 `dtr_go seq|round|pool` 命令。
`dtr_run_manifest` 和 `dtr_run_pool` 是给旧脚本保留的兼容 alias，
不是推荐的新手 quick start 路径。
桌面 GUI 的完成页会在本机记忆播放开关，并为当前 manifest 生成紧凑的
`dtr_preset 0x...; dtr_go ...` 指令。

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

准星对齐默认开启。DemoTracer 会为安全 replay bot 租用 demo 中稳定观测到的
`crosshair_code`，由 bundle 自带的 BotHider 作为唯一 writer，
通过 controller 的服务器复制字段发布。handoff、replay 结束、sequence 完成、后续服务器
round 和比赛结束只释放 playback control，不改变最近一次成功 DTR 批次的 presentation；
新成功批次会原子替换它，显式按 slot unload/kick、断线、换图、slot 重用或插件卸载才恢复
当前 persona 基础值。这条路径完全由服务器发布，不写真人客户端配置，也不向客户端注入代码。
如需关闭，执行 `dtr_align crosshair off`。
