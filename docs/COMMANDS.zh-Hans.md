# DemoTracer 指令参考

这些指令在 CS2 服务器控制台中输入，前提是 Metamod runtime `BotController`
和 CounterStrikeSharp 插件 `DemoTracer` 已经加载。只有在需要把多条指令粘成一行
时，才需要在每条后面加分号。

服务器前置依赖是 Metamod:Source 和 CounterStrikeSharp。DemoTracer server bundle
自带 `BotController`、`DemoTracer`、`DemoTracerApi.dll`、`skins_en.json` 和示例配置。
CS2-Bot-Hider 是可选依赖，只用于 BotHider 管理的 replay slot，以及 demo 显示名、
SteamID64 对齐、demo 头像覆写这类 identity 功能。

## 推荐基础配置

```text
css_plugins reload DemoTracer
dtr_config_status
dtr_go seq "<输出目录>\<demo-id>\manifest.json" 0
```

replay identity、武器/loadout 对齐、投掷物对齐和准星对齐默认开启。identity 对齐只会在
BotHider 存在且管理目标 replay bot slot 时写入 demo 名字和 SteamID64。如果 manifest
包含 demo 提供的 PNG 头像覆写，`full` identity 也会按 SteamID64 应用。

`seq` 表示“从某个 source round 开始顺序播放”，`round` 表示“只播放一个
source round”，`pool` 表示“按本地经济从回合池选择”。`dtr_go` 会先校验并
armed plan，再执行 `mp_restartgame 1`，确保接住新的 `round_start`。

## Runtime Config JSON

DemoTracer 会从 `DemoTracer.dll` 同目录读取可选的 `demotracer.config.json`，作为
服务器本地 runtime 默认值。仓库里提供 `demotracer.config.example.json` 作为干净示例。
这个 JSON 只控制服务器偏好，不会写进 `.dtr` 或 manifest。

```json
{
  "identity": "steam",
  "allow_partial": true,
  "handoff": {
    "mode": "death_contact_c4",
    "scope": "slot",
    "threat_360": true,
    "threat_360_range": 420,
    "threat_360_los": true
  },
  "fidelity": {
    "preset": "default"
  },
  "match": {
    "preset": "off"
  },
  "cosmetics": {
    "preset": "off",
    "agents": false,
    "preserve_native": false
  }
}
```

修改文件后执行 `dtr_config_reload`。`dtr_set handoff ...` 这类控制台命令仍然可以
临时覆盖当前运行状态，直到再次 reload config 或 reload plugin。旧 `"align"`
配置块仍然兼容，但新的 `"fidelity"`、`"match"`、`"cosmetics"` 会覆盖旧字段里的
同类设置。

## 默认值

| 设置 | 默认值 | 含义 |
| --- | --- | --- |
| `dtr_align default` | on | Replay 保真：武器/loadout、投掷物、准星、左右手 desired 写入。 |
| `dtr_match off` | off | 赛事展示同步，包括比分板、KDA、MVP、team score。 |
| `dtr_cosmetics off` | off | 高风险饰品证据 replay，包括皮肤、刀、手套、名字、探员、贴纸和挂件。 |
| `dtr_handoff` | `death_contact_c4 slot` | 接触或死亡后释放触发的 replay slot；C4 安装后释放全部 active replay slot。 |
| `dtr_partial` | `1` | bot 数量不足时允许部分 replay。 |
| `dtr_replay_identity` | `steam` | BotHider 可用时，通过其管理的 replay bot slot 写入 demo 名字和 SteamID64。队伍/赛事 PNG 头像需要显式 `avatar`；`full` 保留旧的真实 SteamID 头像覆写路径。 |
| `dtr_util_trace` | `0` | 默认不写 utility CSV trace。 |
| `bc_replay_pov` | `spectated` | 只给正在被第一人称观察的 replay bot 发布昂贵的 native POV 更新。 |

## 高层播放

### `dtr_go seq <manifest.json> [from_source_round]`

校验并 armed 一个 manifest sequence，然后执行 `mp_restartgame 1`。
`from_source_round` 默认是 `0`，表示从 demo manifest 的这个 source round 开始
顺序播放，不是“只播放这个 round”。

直接 restart alias：`dtr_seq_restart <manifest.json> [from_source_round]`。

### `dtr_go round <manifest.json> <source_round>`

校验并 armed 单个 demo source round，然后执行 `mp_restartgame 1`。这个命令不会
推进到后续 source rounds。

直接 restart alias：`dtr_round_restart <manifest.json> <source_round>`。

### `dtr_go pool <pool_manifest.json> [server_round]`

校验并 armed pool plan，然后执行 `mp_restartgame 1`。`server_round` 是本地服务器
round hint，用于经济/手枪局匹配，不是 manifest source round。

直接 restart alias：`dtr_pool_restart <pool_manifest.json> [server_round]`。

## 顺序播放

### `dtr_arm seq <manifest.json> [from_source_round]`

不 restart，只 armed 转换后的 demo manifest sequence。

实现方式：

- 从 `manifest.json` 读取所有可播放 round。
- 停止并卸载当前 replay 状态。
- 下一个 `round_start` 时准备当前 round，把每个玩家的 `.dtr` 加载到安全 bot slot。
- `round_freeze_end` 时启动所有已加载 replay。
- 每启动一个 source round 后，自动推进到 manifest 里的下一个 source round。

给旧脚本保留的兼容 alias，不作为推荐 quick start：
`dtr_run_manifest <manifest.json> [from_source_round]`。

### `dtr_stop_sequence`

停止已经 armed 或正在推进的 manifest sequence。它不会删除文件，也不会修改插件开关。
它只停止后续 sequence 调度；如果要同时停止已经开始播放的 slot，用 `dtr_stop_all`。

### `dtr_arm pool <pool_manifest.json> [server_round]`

不 restart，从地图回合池里按经济自动选择相似回合播放。

兼容 alias：`dtr_run_pool <pool_manifest.json> [server_round]`。

实现方式：

- 读取 `pool_manifest.json`。
- 在 `round_start` 时读取当前 T/CT 装备价值和账户钱，先选择 candidate、加载 replay，
  并设置 native buy skip，避免 vanilla bot 购买和 replay loadout 打架。
- 手枪局严格只匹配 demo round 0/12。
- 非手枪局会先构建软经济匹配候选集，再叠加近期 candidate / 近期 demo 惩罚，
  最后从最佳窗口里带权随机抽取，而不是固定拿最近邻。
- 经济匹配允许有限的“向上反事实”：当前较弱 buy 可以抽到武器或道具更好的开局路线；
  当前较强 buy 抽到更穷路线会被加重惩罚。
- 在 `round_freeze_end` 启动已经准备好的 replay。

当你希望本地游戏不断从一组 demo 中挑相似开局路线，而不是固定播放一个 demo 时，用这个。

### `dtr_stop_pool`

停止后续 pool 选择并清理内存中的 pool 状态。它不会停止已经开始播放的 slot；
如果要停止当前 replay，用 `dtr_stop_all`。

## 手动加载和播放

### `dtr_load round <manifest.json> <source_round>`

从 manifest 加载一个 round 到可用 replay bot slot，但不立刻开始播放。

实现方式：

- T 文件分配给 T bot slot，CT 文件分配给 CT bot slot。
- 只使用安全目标：严格 CS2 bot，或 BotHider 管理的 bot slot。
- 对已加载 slot 设置 native buy skip，避免 vanilla bot 购买和 replay loadout 打架。
- 记录每个 slot 的 manifest 元数据，例如玩家名、SteamID64、loadout、预加载武器 def、
  projectile events。

旧命令兼容：`dtr_load_round <manifest.json> <source_round>`。

### `dtr_arm round <manifest.json> <source_round> [loop:0|1]`

armed 一个 source round，在下一个 `round_start` 加载，并在 `round_freeze_end`
进入 live playback。

这个适合按正常 freeze-time 节奏测试某个特定 round。

旧命令兼容：`dtr_arm_round <manifest.json> <source_round> [loop:0|1]`。

### `dtr_play loaded [loop:0|1]`

立即启动当前所有已加载 slot。

如果 `dtr_weapon_align` 开启，启动前会预加载 replay loadout 和初始武器。如果
`dtr_weapon_align` 关闭，则不会主动同步 manifest loadout。这个命令是手动/调试入口，
不会自动等待 `round_start` / `round_freeze_end`。

旧命令兼容：`dtr_play_loaded [loop:0|1]`。

### `dtr_load slot <slot> <absolute-or-game-path.dtr>`

把单个 `.dtr` 加载到一个 bot slot。这个是偏底层的实验指令。它拿不到
manifest 里才有的完整元数据，例如 `player_name`、`steam_id` 或完整 loadout，
除非这些信息能直接从 `.dtr` 扫描出来。

### `dtr_play slot <slot> [loop:0|1]`

启动单个已加载 slot。启动前会确认目标仍然是安全 bot 目标。

### `dtr_stop <sequence|pool|replay|slot|all> ...`

停止指定的调度或 replay 状态：

- `dtr_stop sequence` 或 `dtr_stop seq`：停止后续 manifest sequence 调度。
- `dtr_stop pool`：停止后续 pool 选择。
- `dtr_stop replay` 或 `dtr_stop loaded`：停止当前已加载/正在运行的 replay slot。
- `dtr_stop slot <slot>`：停止一个 replay slot，并释放该 slot 的 runtime locks、
  pending alignments、buy plan 和 replay bot 状态。
- `dtr_stop all`：停止所有 DemoTracer replay 状态。

旧命令兼容：`dtr_stop <slot>` 等同于 `dtr_stop slot <slot>`。

### `dtr_stop_all`

停止所有当前已加载 slot，并关闭 active sequence/pool/armed 状态。已加载 slot
元数据可能仍留在内存里；如果要从某个 slot 移除 replay，用 `dtr_unload`。

这是 `dtr_stop all` 的旧便利 alias。

### `dtr_unload <slot>`

卸载一个 slot，并清理该 slot 的插件侧元数据。

## 单点道具 clip 播放

道具 manifest 由 `convert-nades` 生成，也可以来自 `convert-nades-library` 的地图级
manifest。这些指令用于本地检查和播放 demo 中真实出现过的单个道具投掷。

### `dtr_list_nades <nade_manifest.json|nade_manifest.json.br> [kind]`

列出 nade manifest 里的 clip ID。

`kind` 可选，可以是 `smoke`、`flash`、`he`、`molotov`、`incgrenade`、`decoy`，
也可以是 `48` 这样的 weapon def index。输出的 clip ID 可直接传给 `dtr_run_nade`。

### `dtr_run_nade <nade_manifest.json|nade_manifest.json.br> <clip_id> <slot> [loop:0|1]`

从 manifest 加载一个道具 `.dtr` clip 到指定 bot slot，并立即播放。

实现方式：

- clip 路径会相对 manifest 路径解析。
- 使用 manifest 里的阵营、phase、道具类型、初始武器、loadout 和 projectile event 元数据。
- 只使用安全 replay 目标：严格 CS2 bot，或 BotHider 管理的 bot slot。
- stop、finish、unload 或目标失效时会走正常 replay 清理流程。

这个指令适合验证 `convert-nades` 导出的某个具体道具。

### `dtr_cycle_smokes|dtr_cycle_flashes|dtr_cycle_he|dtr_cycle_fire|dtr_cycle_random_nades <nade_manifest.json|nade_manifest.json.br> <slot> [t|ct|all] [combat|retake|all] [gap_seconds]`

在一个 bot slot 上按固定间隔轮询匹配的道具 clip。这个主要用于本地检查道具库。
`all` 会包含 opening clip；当前 cycle parser 没有单独暴露 `opening` 过滤。它不会在
lineup start 之间移动 bot；测试时请选择和当前位置适配的 clip。

### `dtr_stop_nade_cycle`

停止当前 active nade cycle；如果当前 cycle 正在播放某个 replay slot，也会停止它。

## Replay 保真：`dtr_align`

`dtr_align` 只管 replay 保真。scoreboard 展示同步属于 `dtr_match`；饰品风险功能属于
`dtr_cosmetics`。

```text
dtr_align
dtr_align status
dtr_align default
dtr_align full
dtr_align handoff_safe
dtr_align off
dtr_align weapons <on|off>
dtr_align projectiles <on|off>
dtr_align crosshair <on|off>
dtr_align left_hand <on|off>
```

Preset：

- `default` / `full`：武器、投掷物、准星、左右手 desired 写入全部开启。
- `handoff_safe`：保留武器/投掷物/准星，但关闭 `left_hand`，换取更顺的 handoff。
- `off`：关闭 replay 保真对齐开关，只建议调试用。

`loadout`、`active_weapon`、`slot_lock` 等 alias 仍可用，目前都共享 `weapons`
实现。

## 赛事展示同步：`dtr_match`

`dtr_match` 只管本地赛事展示，不改变 replay 移动、武器、投掷物或饰品。

```text
dtr_match
dtr_match status
dtr_match off
dtr_match scoreboard
dtr_match scoreboard <on|off>
dtr_match full
```

`dtr_match scoreboard` 会尽力同步 scoreboard/KDA/MVP/team score、demo 的 CT/T
队名（`mp_teamname_1` 写 CT，`mp_teamname_2` 写 T），以及 manifest 中存在的 demo
选手颜色证据。默认关闭。

## 饰品风险功能：`dtr_cosmetics`

`dtr_cosmetics` 只消费显式导出的 demo 饰品证据。默认关闭；在私有本地验证以外使用时，
可能带来 Valve GSLT/server guideline 风险。

```text
dtr_cosmetics
dtr_cosmetics status
dtr_cosmetics off
dtr_cosmetics weapons
dtr_cosmetics basic
dtr_cosmetics full
dtr_cosmetics weapons <on|off>
dtr_cosmetics knives <on|off>
dtr_cosmetics gloves <on|off>
dtr_cosmetics names <on|off>
dtr_cosmetics agents <on|off>
dtr_cosmetics stickers <on|off>
dtr_cosmetics charms <on|off>
dtr_cosmetics preserve_native <on|off>
```

Preset：

- `weapons`：只应用武器皮肤和武器 custom name。
- `basic`：武器、刀、手套、custom name 和 demo-backed 探员模型；不应用贴纸和挂件。
- `full`：`basic` 加贴纸和挂件。

`preserve_native` 是给已经接受 bot 饰品风险的服务器运营者用的本地策略。开启后，
DemoTracer 不会因为缺少对应 demo 证据就清掉 bot 原本由 CS2/服务器提供的 native 饰品。
目前它主要影响 `gloves` 开启但 replay 没有手套证据的情况：不再主动清空 bot 手套。
它不会随机生成饰品，也不会读取 profile 或 inventory database。

## Handoff / Partial / Identity 和旧命令 alias

旧 `dtr_set align ...` 和直接的 `dtr_*_align` 命令在 beta 迁移期仍然兼容。新用户
应优先使用 `dtr_align`、`dtr_match`、`dtr_cosmetics`。

### `dtr_weapon_align <0|1>`

开关武器和 loadout 对齐。

开启后的实现方式：

- 加载 round 时，native buy control 会让 replay slot 跳过 vanilla bot 购买。
- 开始前，插件根据 manifest loadout 写入护甲、头盔、CT kit、道具、主/副武器候选
  和初始武器。
- replay 过程中，插件跟随 `.dtr` 每 tick 的 weapon def index，让 native runtime
  切换当前武器并锁定对应武器槽。
- 对 `.dtr` v6+，按玩家记录的 equipment/C4 事件会跟随 replay cursor 执行一次。
  combat 事件目前只作为 metadata 读取，不强制改血量或死亡。
- 缺失武器会通过 CS2 item give 和谨慎的 slot replacement 处理，而不是模拟买菜单。

限制：

- 这是 replay fidelity 对齐，不是完整经济模拟器。
- 它绕过阵营购买限制，尽量按 demo loadout 还原。
- CS2 默认手枪和库存槽行为仍可能在边界情况下导致近似结果。

### `dtr_projectile_align <0|1>`

开关投掷物初始矢量对齐。

开启后的实现方式：

- 需要 converter 写出的 `.dtr` v4+ projectile events。
- 当 replay metadata 可用时，匹配 smoke、flash、HE 和 decoy projectile entity。
  火（molotov/incendiary）会保留 CS2 原生 projectile 和 inferno 行为。
- bot 仍然正常执行投掷动作。插件等待 CS2 spawn projectile 后，解析 thrower
  slot，在 replay cursor 附近匹配下一个 demo projectile event，然后写入：
  `InitialPosition`、`InitialVelocity`、`AbsOrigin`、`AbsVelocity`。
- 匹配会延迟重试几个 tick，因为 CS2 在 projectile 刚 spawn 时可能还没有挂上
  thrower，或字段还没最终稳定。
- 烟雾弹的爆开元数据仍是最完整的诊断路径，因为 smoke projectile lifetime 和
  detonation event 更容易被稳定 trace。

为什么需要它：

只回放人物 origin、velocity、view angles、buttons 和 subtick input，不能保证 CS2
重新算出同一个 grenade initial velocity。很小的速度或高度偏差，就可能让关键烟撞到
不同碰撞边缘。projectile data 直接记录 demo 的投掷物结果，用它修正这种 bias。

### `dtr_cosmetic_align <0|1>`

开关饰品对齐。默认关闭。只有 converter 通过 `--export-cosmetics`、
`--acknowledge-cosmetic-gslt-risk` 和 `--accept-cosmetic-export-disclaimer`
显式写出了 manifest `cosmetics` 证据时，它才会生效。

开启后的实现方式：

- 只使用 converter 从 demo 玩家本回合观测数据里导出的 manifest `cosmetics` 证据。
- 支持武器 paint kit/seed/wear、稳定的武器/刀具 custom name、刀具 item def +
  paint kit/seed/wear，以及 demo 暴露时的手套 item def + paint kit/wear。如果
  demoparser 暴露了手套 item def、paint、wear
  但没有暴露 seed，converter 会为该手套写入确定性的 `0` seed。
- manifest 含 `cosmetics.agent` 时，支持 demo-backed 探员模型证据；
  `dtr_cosmetics agents off` 可单独关闭这个 component。
- 武器贴纸不随这个旧命令单独启用。贴纸还需要转换时传入 `--export-stickers`，
  runtime 再执行 `dtr_cosmetics stickers on`。旧 alias `dtr_sticker_align 1` 和
  `dtr_set align stickers on` 仍然可用。
- 武器挂件/keychain 不随这个旧命令单独启用。挂件还需要转换时传入 `--export-charms`，
  runtime 再执行 `dtr_cosmetics charms on`。旧 alias `dtr_charm_align 1` 和
  `dtr_set align charms on` 仍然可用。
- 只在 weapon/loadout alignment 已确认 replay inventory 路径后，对安全 replay bot
  slot 应用。
- 不随机分配饰品，不读取服务器 profile/database，也不会应用到真人玩家。

限制：

- StatTrak/暗金只来自 demo 观测到的武器饰品证据：可以应用 `quality=9`，
  如果 manifest 没有非负 `stattrak_counter`，runtime 会写显示用 `0` 来请求
  StatTrak 计数器模型；这不是 demo 击杀数断言。
- 缺失、为 0、互相矛盾或当前不支持的 demo 证据会直接跳过。
- 默认情况下，如果开启了手套对齐而 manifest 没有手套证据，runtime 仍会清掉 replay
  bot 手套以匹配“无证据”。如果希望保留服务器本来给 bot 的饰品，用
  `dtr_cosmetics preserve_native on`，或在配置里写
  `"cosmetics": { "preserve_native": true }`。
- 这是面向本地/私有验证的 replay fidelity 功能。
- listen/practice server 未必有专用服同样的 GSLT 暴露面，但只写 bot 不是规则豁免；
  如果真人玩家可以观察、接管、持有、检视或以其他方式使用这些 bot 物品外观，就应按
  饰品/库存模拟风险处理。
- 专用服、社区服或公网服上的饰品/库存模拟可能进入 Valve server-operation policy
  范围；非私有本地验证场景启用请自行承担运营风险。

### `dtr_sticker_align <0|1>`

开关武器贴纸对齐。默认关闭。只有同时开启饰品对齐，并且 manifest 是在饰品导出风险
flag 之外又加了 `--export-stickers` 生成时，它才会生效。

开启后的实现方式：

- 只应用挂在已确认 replay weapon cosmetic 上的稳定 manifest 贴纸证据。
- 支持贴纸 slot、sticker id、wear、offset x、offset y、rotation，以及
  原始 scale 元数据。
- 不应用 schema。探员模型使用 `dtr_cosmetics agents`；挂件/keychain 使用
  `dtr_charm_align`。StatTrak/暗金来自武器饰品证据，不是贴纸对齐的一部分。
- 贴纸写入失败只计入 skipped sticker，不回滚武器 paint、刀、手套或 custom name 对齐。

### `dtr_charm_align <0|1>`

开关武器挂件/keychain 对齐。默认关闭。只有同时开启饰品对齐，并且 manifest 是在
饰品导出风险 flag 之外又加了 `--export-charms` 生成时，它才会生效。

开启后的实现方式：

- 只应用挂在已确认 replay weapon cosmetic 上的稳定 manifest 挂件/keychain 证据。
- 支持 charm slot 0 id、offset x、offset y、offset z、可选 seed、可选 highlight
  和可选 charm sticker id。
- 不随机分配挂件，不读取 profile/database 库存，也不应用当前不支持的 charm slot。
  探员模型使用 `dtr_cosmetics agents`。
- 挂件写入失败只计入 skipped charm，不回滚武器 paint、刀、手套、custom name、
  StatTrak/暗金或贴纸对齐。

### `dtr_crosshair_align <0|1>`

开关准星对齐。默认开启。

开启后，如果真人观察者正在第一人称观察安全 replay bot，DemoTracer 会把 converter
从 demo 玩家稳定 `crosshair_code` 中导出的 manifest `view.crosshair_code` 临时应用到
这个观察者，并在离开 replay POV 时恢复原准星。缺失或互相矛盾的 demo 证据会跳过。
这个功能只影响 POV/spectator 观察拟真度，不改变移动、武器、投掷物、replay bot 状态或饰品库存。

### `dtr_left_hand_desired <0|1>`

控制新加载的 `.dtr` v7 command frames 是否保留 `left_hand_desired` 写入。

- `1`：保留 demo 里的左/右手 desired 状态。这是默认值，也是最高保真度行为。
- `0`：加载 replay frames 到 native playback 前清掉 left-hand desired 写入。它会降低保真度，但显著增高handoff流畅性，适合左手 replay bot 在 handoff 后会切回服务器默认右手视角并触发前摇的场景。

这个设置只影响命令改动之后加载的 replay。已经加载到 slot 上的 round、sequence 或
pool plan 需要重新加载后才会应用。

### `dtr_replay_identity <off|name|steam|avatar|full|0|1>`

控制 BotHider identity 对齐。

开启后，如果 BotHider 可用且 slot 由 BotHider 管理，加载 manifest 时会把 demo 玩家
的 `player_name` 和 `steam_id` 写给对应 bot slot。默认模式是 `steam`，不会写
`ServerAvatarOverrides`；`1`/`on` 也表示 `steam`。

用 `avatar` 可以应用 manifest 里的 PNG 头像覆写，例如队伍/赛事 logo，但不会把真实
选手 SteamID64 当作头像覆写 key。这个模式会给 replay bot 写入一个 DTR 合成
SteamID64；如果有匹配的头像证据，就把对应 PNG 绑定到这个合成 key。

只有明确想走旧路径时才用 `full`：真实 demo SteamID64 加 demo PNG 头像覆写，并且
头像覆写仍按真实 SteamID64 keyed。

头像覆写在延迟写入前会重新做 slot 校验：该 slot 必须仍然加载同一个 SteamID64，
仍然是安全 replay target，并且仍由 BotHider 管理。CS2 的
`ServerAvatarOverrides` 底层仍然按 SteamID64 生效；`avatar` 用 DTR 合成 key 避免
真实玩家冲突，`full` 保留真实 SteamID 冲突 caveat。

这个主要用于 POV 和 spectator 观察清晰度。如果没有安装 BotHider，或目标 slot 不由
BotHider 管理，identity 对齐会跳过该 slot，而不会应用到真人玩家。

### `dtr_partial <0|1>`

控制 bot 数不足时是否允许加载部分 replay。

- `1`：有多少安全同阵营 bot slot 就加载多少，并报告跳过的 T/CT 数量。
- `0`：如果不能分配 manifest 里的所有玩家，就加载失败。

### `dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [all|slot]`

控制 replay 什么时候把 bot 控制权交回普通 bot 行为。

模式：

- `off`：不自动 handoff。
- `death`：replay 控制玩家死亡或击杀时 handoff。
- `contact`：检测到战斗/接触时 handoff。
- `death_or_contact`：死亡和接触都触发。
- `death_contact_c4`：死亡、接触和 C4 安装都触发。这是默认值。

范围：

- `slot`：只释放触发的 slot。这是推荐安全默认值。
- `all`：一个触发释放所有 replaying slots。只建议实验时使用。

C4 安装是回合阶段 handoff，不是单个对枪触发；即使 scope 是 `slot`，也会释放所有
active replay slots。

接触检测实现：

- 使用 bullet damage / player hurt 事件，以及 replay bot 是否看见敌人的检查。
- contact handoff 会停止 replay control、释放 native locks，并重置 replay 持有的 bot
  状态。handoff 后的开火交给正常 CS2 bot AI；DemoTracer 不运行 CSGO-style combat
  executor。
- replay 刚开始有很短 grace window，避免启动瞬间误触发。
- `threat_360_los=true` 时，360 threat 在 `threat_360_range` 内要求 LOS；近距离
  LOS threat 立即触发，更远的 LOS threat 需要短暂 hold。`threat_360_los=false`
  时，完整 360 range 仍是实验性的 noLOS 触发。

### `dtr_handoff_360 [0|1] [range] [los|nolos]`

控制 contact handoff 使用的 360 度 threat trigger。

- `0`/`off`：关闭 360 threat handoff。
- `1`/`on`：开启 360 threat handoff。
- `range`：游戏单位半径，插件会夹到允许范围内。
- `los`/`raytrace`：有 RayTrace provider 时要求 LOS。
- `nolos`：只按距离触发；更偏实验。

这个命令会输出实际设置和当前 RayTrace 状态。

## 诊断

### `dtr_config_reload`

从插件目录重新读取 `demotracer.config.json` 并应用到当前 runtime 设置。如果文件不存在，
继续使用内置默认值。

### `dtr_config_status`

输出 config 路径、文件是否存在，以及当前有效 runtime 设置。

### `dtr_runtime`

输出 runtime version matrix：期望和已加载的 native ABI、capability bitset、
缺失的 required capability bits、native build id、可选的 `UsercmdMovementIntent` /
`LeftHandIntent` export 状态、支持的 `.dtr` reader 版本范围、平台，以及
`DemoTracerApi` version。

### `dtr_doctor [manifest.json|pool_manifest.json]`

输出一组紧凑的健康检查：native ABI 是否兼容、capability bitset、native build id、
可选的 `UsercmdMovementIntent` / `LeftHandIntent` export 状态、支持的 `.dtr` reader
版本范围、平台、`DemoTracerApi` version、当前地图/时间、freeze-time ConVar、bot 数量、
BotHider managed slot、安全 replay target 数、已加载/正在播放的 replay 数量、对齐开关、
handoff mode、RayTrace 状态，以及可选的 manifest 或 pool manifest 摘要。

当 playback 不启动、slot 数少于预期，或要在新服务器上检查 sample pack 时，先看这个。

### `dtr_bots`

输出队伍玩家、严格 bot 状态、BotHider managed 状态、native `controllingBot` 状态、
是否可作为 replay candidate、slot、队伍和名字。对于已加载的 DemoTracer replay slot，
也会输出 `dtr_kick` 提示。

如果 manifest 拒绝加载，或加载 slot 数少于预期，先看这个。

### `dtr_kick <exact-name>|slot <slot>|sid <steamid64>`

释放指定 DemoTracer replay slot 后踢掉对应 bot。这是移除 BotHider identity replay bot
的推荐方式，因为它会先停止 native playback、卸载 replay buffer、清理 DemoTracer
slot state、使 pending avatar 写入失效，然后执行 `kickid <userid>`。

目标规则：

- `dtr_kick slot <slot>` 指定一个精确 replay slot。
- `dtr_kick sid <steamid64>` 按 loaded replay 玩家 SteamID64 匹配。
- `dtr_kick "<exact-name>"` 按 loaded replay 玩家名或当前 live 显示名做大小写不敏感的
  精确匹配。
- 名字或 SteamID 命中多个目标时会拒绝执行；改用 `dtr_kick slot <slot>`。
- 真人玩家和非 DemoTracer bot slot 会被拒绝。

### `dtr_status <slot>`

输出 native ABI、某个 slot 的 replay cursor/total、播放状态、handoff mode、partial
mode、identity mode、projectile align 状态，以及当前 sequence/pool 指针。

### `dtr_util_trace <0|1> [path]`

写 utility 调试 CSV。

trace 包含 slot replay cursor、live/replay 位置和速度、武器状态、grenade stash
状态、烟雾弹 projectile 状态、烟雾弹爆开事件，以及内部 projectile-align message。

这是调试指令，会生成很大的 CSV。正常播放时应该关闭。

### `bc_status`

这个指令来自 native `BotController` runtime，不是 CSS `DemoTracer` 插件。但它很有用，
因为会输出 hook 状态、replay hook 计数、锁数量和 buy-plan 状态。

### `bc_replay_pov [off|spectated|always]`

控制 native 第一人称 replay POV 发布。

- `spectated` 是默认值。DemoTracer 会把当前正在第一人称观察 replay bot 的 human
  spectator 转成 per-slot mask 传给 native。
- `always` 恢复旧行为：每个 replay bot 每 tick 都发布 server view-angle changes。
- `off` 关闭这条 POV 发布路径，性能最好。

移动 replay、武器切换、投掷物对齐和 handoff 行为不依赖这个设置。

### `bc_perf [0|1|reset]`

开启、关闭、重置并打印 native replay 性能计数器。

测试 10 bot playback 时使用它。`bc_replay_pov spectated` 且没人第一人称观察 replay
bot 时，server-view writes 和 `VirtualQuery` 计数应该接近 0；只有一个第一人称观察者时，
它们应该接近每 tick 一个 bot，而不是每个 loaded replay bot。
