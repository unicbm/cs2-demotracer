# 使用说明

这份文档补充 README 里的步骤，重点写转换器和本地 Rust API。CS2 服务器控制台指令见
[`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md)。

## 1. 转换 demo 为回合 replay

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
回合 replay 默认最多保留同一回合内 10 秒 freeze-time 上下文，用来让
`round_freeze_end` 后的道具松开动作能接上开局前的按住状态；可用
`--freeze-preroll-seconds` 调整。
如果某个 round 有 C4 安装完成事件，`manifest.json` 会记录这个 anchor，供
`dtr_go_at ... bomb` 这类实验诊断使用。服务器回放当前支持边界是从回合开始；plant
后中途起点可能缺少物理、动画和已安装 C4 状态。此类诊断需要 `--full-round`，否则
`.dtr` 文件本身会在 plant 前结束。

常用选项：

```powershell
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --rounds 0,1,2,5-8
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --side t
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --include-suspicious
cs2-demotracer.exe convert --demo <demo.dem> --output <输出目录> --freeze-preroll-seconds 10
```

输出目录里最重要的是：

```text
manifest.json
round00/t/*.dtr
round00/ct/*.dtr
```

实际输出目录名会是 `<demo-stem>-<hash12>`；`hash12` 来自 demo 文件内容，用来避免同名 demo 互相覆盖。

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

## 3. Demo2Nade 道具 clip

`convert-nades` 会扫描单个 demo 中的 grenade projectile，并围绕每次投掷导出一个短
`.dtr` clip。clip 包含投掷者 release 附近的最小移动/视角上下文，以及投掷物初始
位置和速度元数据。

默认上下文：

- `--pre-roll 1.0`：release 前保留秒数。
- `--post-roll 0.5`：release 后保留秒数。
- `--opening-seconds 20.0`：live round 开始后的 opening 判定窗口；post-plant 会优先标成 retake。

基础导出：

```powershell
cs2-demotracer.exe convert-nades --demo <demo.dem> --output <输出目录>\nades
```

筛选导出：

```powershell
cs2-demotracer.exe convert-nades --demo <demo.dem> --output <输出目录>\nades --side ct --rounds 3,4,8-12 --pre-roll 0.8 --post-roll 0.35 --opening-seconds 18
```

输出结构：

```text
<输出目录>/nades/<demo-id>/nade_manifest.json
<输出目录>/nades/<demo-id>/nade_manifest.json.br
<输出目录>/nades/<demo-id>/nade_conversion.log
<输出目录>/nades/<demo-id>/nades/<side>/<phase>/<kind>/<clip-id>.dtr
```

`side` 是 `t` 或 `ct`。`phase` 分为：

- `opening`：freeze time 结束后 opening 窗口内的道具。
- `combat`：C4 安放前的 live-round 道具。
- `retake`：C4 安放后的道具。

支持 smoke、flash、HE、molotov、incendiary、decoy。molotov 和 incendiary 在
manifest 的 `kind` 里同属 `molotov`，由 `weapon_def_index` 区分 `46` 和 `48`。

这些 clip 是地图绝对坐标 `.dtr`，不是相对 lineup。manifest 会记录 start origin、
yaw、投掷物初始矢量、爆点、玩家、回合、阵营、phase 和 source context。

## 4. 构建 Demo2Nade 道具库

`convert-nades-library` 可以从大量 demo 构建按地图索引的本地道具库：

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <输出目录>\nade_library --recursive --jobs 8
```

输出结构：

```text
<输出目录>/nade_library/demos/<demo-id>/nade_manifest.json
<输出目录>/nade_library/maps/<map>/nade_manifest.json
<输出目录>/nade_library/maps/<map>/nade_manifest.json.br
<输出目录>/nade_library/nade_library.json
<输出目录>/nade_library/nade_library.json.br
```

常用选项：

```powershell
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <library目录> --recursive --jobs 1
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <library目录> --recursive --max-demos 20
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <library目录> --aggregate-only
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <library目录> --reuse-root <旧library>\demos
cs2-demotracer.exe convert-nades-library --demo-dir <demo根目录> --output <library目录> --no-dedupe
```

默认 dedupe 只影响地图级 manifest，用来合并非常接近的重复道具；每个 demo 的 source
clip 仍然保留在 `demos/` 里。调参选项：

- `--dedupe-origin-units 48`
- `--dedupe-yaw-degrees 8`
- `--dedupe-velocity-units 120`

修改 dedupe 参数或手动复制已有 per-demo 导出后，可以用 `--aggregate-only` 重新生成
`maps/<map>/` 和顶层 `nade_library.json(.br)`。

如果你使用 Python 或 Node.js，可以看 [`examples/`](../examples/)。这些脚本会
调用 CLI 并读取 `manifest.json`；它们是集成示例，不是稳定的语言绑定。

## 5. Rust API

转换器 crate 提供本地 Rust API，适合不想通过子进程调用 CLI 的工具。

导出单 demo 道具 clip：

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeClipExportRequest::new("match.dem", "out/nades");
request.side = Side::Both;
request.context = NadeContextOptions {
    pre_roll_seconds: 1.0,
    post_roll_seconds: 0.5,
    opening_seconds: 20.0,
};

let report = export_nade_clips_from_demo_path(&request)?;
println!("clips={} skipped={}", report.clips_written, report.skipped);
```

如果你已经自己解析了 demo：

```rust
use cs2_demotracer::prelude::*;

let request = NadeClipExportRequest::for_parsed("out/nades");
let report = export_nade_clips_from_parsed(&parsed_demo, &request)?;
```

安静地构建道具库：

```rust
use cs2_demotracer::prelude::*;

let mut request = NadeLibraryExportRequest::new("demos", "out/nade_library");
request.recursive = true;
request.jobs = 8;
request.dedupe = NadeDedupeOptions::default();

let report = build_nade_library(&request)?;
println!("maps={} clips={}", report.maps_written, report.clips);
```

带结构化进度回调：

```rust
let report = build_nade_library_with_progress(&request, |event| {
    println!("{event:?}");
})?;
```

读取 `.json` 或 `.json.br` manifest：

```rust
let demo_manifest = read_nade_manifest("out/nades/<demo-id>/nade_manifest.json.br")?;
let map_manifest = read_nade_map_manifest("out/nade_library/maps/de_mirage/nade_manifest.json.br")?;
let library = read_nade_library_manifest("out/nade_library/nade_library.json.br")?;
```

底层 `.dtr` IO 在 `cs2_demotracer::dtr`：

```rust
use cs2_demotracer::dtr::{read_rec_file, write_rec_file};

let rec = read_rec_file("clip.dtr")?;
write_rec_file("copy.dtr", &rec)?;
```

这个 Rust API 目前面向本地工具和 git dependency 使用；crate 暂时没有发布到 crates.io。

## 6. 进 CS2 播放

先加载 Metamod `BotController` runtime 和 CounterStrikeSharp `DemoTracer` 插件，再使用
[`COMMANDS.zh-Hans.md`](COMMANDS.zh-Hans.md) 里的服务器指令。

普通回合 manifest 使用 `dtr_run_manifest` 或 `dtr_run_pool`。道具 manifest 使用
`dtr_list_nades` 和 `dtr_run_nade`。

DemoTracer 有意不提取或应用皮肤、刀、手套、贴纸、挂件/charms 或探员等饰品库存元数据。
