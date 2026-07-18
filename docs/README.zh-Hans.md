# CS2 DemoTracer 文档

> [!CAUTION]
> **2026 年 7 月 CS2 更新（1.41.6.9）：**服务器播放需要 CounterStrikeSharp
> v1.0.371 或更新版本；Ray-Trace 需要 v1.0.16 或更新版本。playback bundle（播放包）
> 已自带包含所需 Windows identity offset 的 DemoTracer BotHider runtime。
> DemoTracer 的 Windows 核心 replay 链路已在本地验证；
> 使用新版 delta user-command 编码的 demo 需要 converter v0.5.0 或更新版本，
> `.dtr` 格式保持不变。

**CS2 DemoTracer** 把 CS2 `.dem` 转成 `.dtr` 路线回放文件，然后在本地
CS2 服务器里让 bot 执行这些路线。

## 先看这些

- [使用说明](USAGE.zh-Hans.md)：converter CLI、GUI、pool 和服务器播放流程。
- [语音导出和回放](VOICE.zh-Hans.md)：`--export-voice`、`.dtv` sidecar、自动播放和排错。
- [命令参考](COMMANDS.zh-Hans.md)：`dtr_` 指令、状态检查和诊断输出。
- [依赖说明](DEPENDENCIES.md)：必需依赖、playback bundle 自带 BotHider、RayTrace
  可选集成边界。
- [限制和边界](LIMITATIONS.zh-Hans.md)：已知问题、handoff、头像、BotHider、饰品等边界。

英文参考：

- [Usage](USAGE.md)
- [Voice Export and Replay](VOICE.md)
- [Commands](COMMANDS.md)
- [Format Contract](FORMAT.md)

## 下载选择

在 [GitHub 最新版本](https://github.com/unicbm/cs2-demotracer/releases/latest) 中按需下载：

- `cs2-demotracer-cli-v<version>-windows-x64.zip`：体积最小，适合命令行、wizard、
  批量转换和 pool 工作流。
- `cs2-demotracer-gui-v<version>-windows-x64.zip`：基于 Tauri 的单 demo 桌面图形
  转换器，也可浏览本机已有的 `manifest.json` 回放归档。
- `cs2-demotracer-playback-v<version>-windows-x64.zip`：安装在本地 Windows x64
  CS2 服务器里的 CounterStrikeSharp/Metamod 回放 plugin 和 runtime。

playback bundle 不是云服务器或托管服务。只做 demo 转换时下载 CLI 或 GUI 即可；
只有需要在本地 CS2 服务器里回放路线时才需要安装播放包。

## 最短流程

转换并验证 demo：

```powershell
cs2-demotracer.exe inspect --demo "<demo.dem>"
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>"
cs2-demotracer.exe validate --input "<output-dir>"
```

要导出 demo 自带游戏内语音，加 `--export-voice`：

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<output-dir>" --export-voice
```

这会生成 `voice/roundXX.dtv` sidecar。不是所有 demo 都有语音；社区服、FACEIT、
5E demo 更可能包含。详见 [语音导出和回放](VOICE.zh-Hans.md)。

在本地 CS2 服务器播放：

```text
css_plugins reload DemoTracer
dtr_config_status
dtr_preset 0x15; dtr_go seq "<output-dir>\<demo-id>\manifest.json" 0
```

`0x15` 表示武器对齐、Steam identity 和自动语音。桌面 GUI 会根据本机记忆的播放
开关实时生成这个 preset。
`seq` 表示从某个 source round 开始连续播放；只播放单个 source round 用：

```text
dtr_go round "<manifest.json>" 0
```

GUI 也可以直接打开已有的单 demo `manifest.json`，校验其中引用的 `.dtr`，选择
source round，并在没有原始 `.dem` 的情况下生成连续或单回合播放命令。

GUI 默认进入本地回放库。Windows 首次运行默认位置是
`文档\CS2 DemoTracer\Library`；已有用户选择的主库会继续保留，也可以同时索引多个额外
归档目录。新的 GUI 转换按
`<库>\<地图>\<可读名称>--<hash12>\` 归档，完整 Demo SHA-256 仍是真正身份。每个归档会在
ABI 17 `manifest.json` 旁写一个可重建的本机 `demo-info.json`。其中会记住转换时原始
`.dem` 的完整本地路径，并另写一个很小的 `demo-source.json` 作为独立来源指针；可移植的播放
Manifest 及其 ABI 不包含这个路径。列表只轻量索引这些
文件；只有真正打开某场回放时才读取并完整校验 `.dtr`。卡片会展示地图、比分证据、
双方队伍与玩家、K/D/A、完整 Demo 时长、推断平台、可获得时的近似 Demo 文件时间、
归档时间，以及 ABI、格式和 CS2 patch、转换器版本。平台优先依据 header 服务器名，
仅依据文件名时会标成“可能来源”。CS2 Demo 不提供可靠的绝对比赛时间，因此界面会明确
把文件时间标成近似值，不会伪装成比赛日期。没有当前 sidecar 的旧归档不会再把回合开始
快照显示成最终比分；补全资料会先自动使用已记录的原 `.dem` 路径并校验完整 SHA-256，只有
文件被移动或删除时才要求重新定位，成功后立即更新本机 sidecar，不重写任何 `.dtr`。归档里的
“重新选择轮次”也复用这条已校验的指针，不会再从零询问原 Demo 地址。“整理旧归档”
会严格校验散落的回放目录，按完整 SHA-256 去重后复制进按地图
归类的主库，原目录始终保留不动。

GUI 还提供独立的“设置”工作区，用来管理输出、归档、原始 Demo 库、导出和播放默认项。
环境页既允许手动填写 CS2 路径，也提供由玩家主动触发的 Steam 定位；随后只读检查
Metamod、CounterStrikeSharp、DemoTracer bundle 收据、本地 CSS 插件和已知的
BotController/BotHider vendor 冲突。它不会执行扫描到的 DLL，也不会自动改写游戏安装。
如果存在新鲜且不含玩家隐私的 DemoTracer heartbeat，同一页面还会核对已加载的 runtime
契约和 CSS 插件目录名；过期证据不会显示为仍在生效。

## 环境要求

- **转换：**按需下载 Windows x64 CLI 或 GUI 包，不需要游戏服务器 plugin。
- **GUI 运行环境：**Microsoft Edge WebView2；当前 Windows 10 和 Windows 11
  通常已随系统提供，缺失时需要另行安装当前版本的 WebView2 Runtime。
- **播放：**安装了 [Metamod:Source](https://www.sourcemm.net/)、
  [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 和
  DemoTracer playback bundle 的本地 Windows x64 CS2 服务器。
- **可选：**[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) 或兼容
  provider，用于更严格的 handoff LOS 判断。

playback bundle 提供 DemoTracer 自身的 runtime 和 plugin，但不包含 Metamod:Source、
CounterStrikeSharp 或 RayTrace provider。完整版本、bundle 内容和兼容性边界见
[依赖说明](DEPENDENCIES.md)。

## GSLT 和饰品边界

饰品、custom name、sticker、charm、探员模型 metadata 默认不导出。只有显式传入
`--export-cosmetics`、`--acknowledge-cosmetic-gslt-risk` 和
`--accept-cosmetic-export-disclaimer` 时才会导出；sticker/charm 还需要额外
`--export-stickers` / `--export-charms`。

每件成功导出的武器、刀具和手套饰品还会得到确定性的 `inspect.command`；未超过
Steam protocol 长度限制时还会写出 `inspect.steam_url`。它们是把 manifest 证据直接
编码进去的本地 CS2 预览 payload，不表示该物品仍存在于某个 Steam 库存或市场挂单。
只有显式导出的 sticker/charm 证据才会进入对应预览。

runtime 侧的 cosmetic/agent/sticker/charm alignment 也默认关闭，只消费 manifest 里的
demo 证据。有 `cosmetics.agent` 证据且开启 agent alignment 时，DemoTracer 会把该安全
replay bot slot 换成 demo 对应的探员模型。bot-only inventory mutation 不等于
Valve policy exemption；如果真人可以观察、控制、占有、inspect 或使用这些 bot，
就按服务器运营者风险处理。
