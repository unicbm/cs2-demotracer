# CS2 DemoTracer 文档

> [!CAUTION]
> **2026 年 7 月 CS2 更新（1.41.6.9）：**服务器播放需要 CounterStrikeSharp
> v1.0.371 或更新版本；Ray-Trace 需要 v1.0.16 或更新版本。CS2-Bot-Hider
> 需要包含 2026 年 7 月 Windows client identity offset 修复的构建，已发布的
> v0.2.5 早于该修复。DemoTracer 的 Windows 核心 replay 链路已在本地验证；
> 使用新版 delta user-command 编码的 demo 需要 converter v0.5.0 或更新版本，
> `.dtr` 格式保持不变。

**CS2 DemoTracer** 把 CS2 `.dem` 转成 `.dtr` 路线回放文件，然后在本地
CS2 服务器里让 bot 执行这些路线。

## 先看这些

- [使用说明](USAGE.zh-Hans.md)：converter CLI、GUI、pool 和服务器播放流程。
- [语音导出和回放](VOICE.zh-Hans.md)：`--export-voice`、`.dtv` sidecar、自动播放和排错。
- [命令参考](COMMANDS.zh-Hans.md)：`dtr_` 指令、状态检查和诊断输出。
- [依赖说明](DEPENDENCIES.md)：必需依赖、server bundle 自带内容、BotHider/RayTrace
  等可选集成边界。
- [限制和边界](LIMITATIONS.zh-Hans.md)：已知问题、handoff、头像、BotHider、饰品等边界。

英文参考：

- [Usage](USAGE.md)
- [Voice Export and Replay](VOICE.md)
- [Commands](COMMANDS.md)
- [Format Contract](FORMAT.md)

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
dtr_voice_auto on
dtr_go seq "<output-dir>\<demo-id>\manifest.json" 0
```

`seq` 表示从某个 source round 开始连续播放；只播放单个 source round 用：

```text
dtr_go round "<manifest.json>" 0
```

## 依赖概览

转换只需要 Windows x64 converter 包。

服务器播放需要：

- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- DemoTracer server bundle

server bundle 自带：

- `BotController` Metamod runtime
- `DemoTracer` CounterStrikeSharp plugin
- `DemoTracerApi.dll`
- `demotracer-econ-index.v1.json`
- `demotracer.config.example.json`

可选集成：

- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)：用于 BotHider 管理的
  replay slot，以及 demo 显示名、SteamID64、头像 identity 对齐。
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) 或其他提供
  `raytrace:craytraceinterface` 的 RayTrace API provider：用于 handoff 360 threat
  的更严格 LOS 判断；没有它也能运行，`dtr_doctor` 会显示当前 RayTrace 状态。

## GSLT 和饰品边界

饰品、custom name、sticker、charm、探员模型 metadata 默认不导出。只有显式传入
`--export-cosmetics`、`--acknowledge-cosmetic-gslt-risk` 和
`--accept-cosmetic-export-disclaimer` 时才会导出；sticker/charm 还需要额外
`--export-stickers` / `--export-charms`。

runtime 侧的 cosmetic/agent/sticker/charm alignment 也默认关闭，只消费 manifest 里的
demo 证据。有 `cosmetics.agent` 证据且开启 agent alignment 时，DemoTracer 会把该安全
replay bot slot 换成 demo 对应的探员模型。bot-only inventory mutation 不等于
Valve policy exemption；如果真人可以观察、控制、占有、inspect 或使用这些 bot，
就按服务器运营者风险处理。
