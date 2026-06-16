# 使用说明

这份文档补充 README 里的步骤，面向本地开发版。

## 1. 转换 demo

先分析 demo：

```powershell
cs2-demotracer.exe inspect --demo <demo.dem>
```

转换推荐回合：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录>
```

校验输出：

```powershell
cs2-demotracer.exe validate --input <输出目录>
```

默认会在 C4 开始安放前截断，只导出开局路线。需要整回合时加 `--full-round`。

输出目录里最重要的是：

```text
manifest.json
round00/t/*.dtr
round00/ct/*.dtr
```

实际输出目录名会是 `<demo-stem>-<hash12>`；`hash12` 来自 demo 文件内容，用来避免同名 demo 互相覆盖。

播放整场或指定回合时，优先使用 `manifest.json`。

## 2. 命令行转换

只转换指定回合：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --rounds 0,1,2,5-8
```

批量生成 Mirage 回合池：

```powershell
cs2-demotracer.exe convert-pool --demo-dir <demo根目录> --output <输出目录>\mirage_pool --map de_mirage --recursive
```

交互式向导：

```powershell
cs2-demotracer.exe wizard
```

## 3. 进 CS2 播放

服务器需要加载：

- `runtime/BotController` 构建出的 Metamod DLL。
- `css/` 构建出的 CounterStrikeSharp 插件。

进本地服务器后：

```text
css_plugins reload DemoTracer
dtr_weapon_align 1
dtr_run_manifest "<输出目录>\<demo-id>\manifest.json" 0
```

从指定 round 开始：

```text
dtr_run_manifest "<输出目录>\<demo-id>\manifest.json" 12
```

使用 Mirage 回合池自动匹配经济：

```text
dtr_run_pool "<输出目录>\mirage_pool\pool_manifest.json" 0
```

round 0 和 round 12 固定只匹配 demo 的 round 0/12 手枪局；其他回合按双方当前装备价值匹配相近路线。

常用检查：

```text
meta list
css_plugins list
bc_status
dtr_bots
dtr_status 0
```

停止播放：

```text
dtr_stop_all
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
- 当前版本仍不是完整离线 subtick/usercmd 还原。
