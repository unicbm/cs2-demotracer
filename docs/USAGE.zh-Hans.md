# 使用说明

这份文档补充 README 里的步骤，面向本地开发版。

## 1. 转换 demo

最推荐用 GUI：

```powershell
cd cs2-demo-botmimic\converter
cargo run --release -- gui
```

流程：

1. 选择 `.dem`。
2. 选择输出目录。
3. 分析回合。
4. 保持默认勾选推荐回合。
5. 导出。

输出目录里最重要的是：

```text
manifest.json
round00/t/*.cs2rec
round00/ct/*.cs2rec
```

播放整场或指定回合时，优先使用 `manifest.json`。

## 2. 命令行转换

分析 demo：

```powershell
cargo run --release -- inspect --demo <demo.dem>
```

转换推荐回合：

```powershell
cargo run --release -- convert --demo <demo.dem> --output <输出目录>
```

只转换指定回合：

```powershell
cargo run --release -- convert --demo <demo.dem> --output <输出目录> --rounds 0,1,2,5-8
```

校验输出：

```powershell
cargo run --release -- validate --input <输出目录>
```

## 3. 进 CS2 播放

服务器需要加载：

- `runtime/BotMimicRuntime` 构建出的 Metamod DLL。
- `css/` 构建出的 CounterStrikeSharp 插件。

进本地服务器后：

```text
css_plugins reload Cs2DemoBotMimic
cs2bm_weapon_align 1
cs2bm_run_manifest "<输出目录>\<demo名字>\manifest.json" 0
```

从指定 round 开始：

```text
cs2bm_run_manifest "<输出目录>\<demo名字>\manifest.json" 12
```

常用检查：

```text
meta list
css_plugins list
cs2bm_bots
cs2bm_status 0
```

停止播放：

```text
cs2bm_stop_all
```

## 4. 回合筛选建议

优先使用“推荐”回合。

如果看到这些情况，通常不要导出：

- 总人数少于 10。
- T/CT 人数明显不对。
- 时长异常短。
- 比赛已经结束后的尾部 round。
- 断线重连造成部分玩家轨迹缺失。

## 5. 已知问题

- 目前优先保证移动、视角和基础输入流畅。
- 武器完全对齐仍受 CS2 默认手枪和 slot 行为限制。
- v1 不是完整 subtick/usercmd 还原。
