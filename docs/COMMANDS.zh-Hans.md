# DemoTracer 指令参考

这些指令在 CS2 服务器控制台中输入，前提是 Metamod runtime `BotController`
和 CounterStrikeSharp 插件 `DemoTracer` 已经加载。只有在需要把多条指令粘成一行
时，才需要在每条后面加分号。

服务器前置依赖是 Metamod:Source 和 CounterStrikeSharp。DemoTracer playback bundle
自带 `BotController`、DemoTracer 自维护的 `BotHider`、`DemoTracer`、
`DemoTracerBotHider`、对应 API assembly、`demotracer-econ-index.v1.json` 和示例配置。
不要同时运行另一个公开 BotHider CSS 插件。

## 推荐基础配置

```text
css_plugins reload DemoTracer
bh_status
dtr_config_status
dtr_preset 0x15; dtr_go seq "<输出目录>\<demo-id>\manifest.json" 0
```

replay identity、武器/loadout、投掷物和准星对齐默认开启。准星完全由 bundle 内置
BotHider 通过服务器 state 发布，不写真人客户端配置。identity 对齐通过 BotHider 的
独占 lease 向受管 replay bot slot 发布 demo 名字和 SteamID64。如果 manifest 包含
demo 提供的 PNG 头像覆写，`full` identity 也会按 SteamID64 应用。

`seq` 表示“从某个 source round 开始顺序播放”，`round` 表示“只播放一个
source round”，`pool` 表示“按本地经济从回合池选择”。`dtr_go` 会先校验并
armed plan，再执行 `mp_restartgame 1`，确保接住新的 `round_start`。

## Runtime Config JSON

DemoTracer 会从 `DemoTracer.dll` 同目录读取可选的 `demotracer.config.json`，作为
服务器本地 runtime 默认值。仓库里提供 `demotracer.config.example.json` 作为干净示例。
这个 JSON 只控制服务器偏好，不会写进 `.dtr` 或 manifest。运行时解析器允许 `//`
注释和尾逗号，因此示例文件会直接在不直观的选项旁说明其作用。

```jsonc
{
  "identity": "steam",
  "allow_partial": true,
  "playoff": false,
  "chat_auto": true,
  "handoff": {
    "mode": "death_contact_c4",
    "scope": "slot",
    "threat_360": true,
    "threat_360_range": 420,
    "threat_360_los": true,
    // "round"：只把最后的 viewmodel/左右手租约保持到回合边界。
    // "release"：handoff/replay 结束时立即恢复 replay 前 viewmodel。
    "viewmodel_continuity": "round"
  },
  "fidelity": {
    "preset": "default",
    // 由 BotHider 从服务器发布；设为 false 可关闭。
    "crosshair": true
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
| `dtr_align default` | on | Replay 保真：武器/loadout、投掷物、左右手 desired 写入，以及服务器发布的准星对齐。 |
| `dtr_match off` | off | 赛事展示同步，包括比分板、KDA、MVP、team score。 |
| `dtr_cosmetics off` | off | 高风险饰品证据 replay，包括皮肤、刀、手套、名字、探员、贴纸和挂件。 |
| `dtr_handoff` | `death_contact_c4 slot` | 接触或死亡后释放触发的 replay slot；C4 安装后释放全部 active replay slot。 |
| `handoff.viewmodel_continuity` | `round` | 活体 handoff 后把最后的 replay viewmodel 和左右手 desired 租约保持到回合边界。 |
| `dtr_partial` | `1` | bot 数量不足时允许部分 replay。 |
| `dtr_playoff` | `off` | manifest sequence 耗尽后，继续从该 manifest 调度按 SteamID 匹配的长枪局开局。 |
| `dtr_chat_auto` | `on` | 按 manifest 元数据和同一回合时间线 replay demo 文字聊天。 |
| `dtr_replay_identity` | `steam` | 通过 bundle 内置 BotHider 的 presentation lease 向受管 replay bot slot 发布 demo 名字和 SteamID64。队伍/赛事 PNG 头像需要显式 `avatar`；`full` 是 `avatar` 的兼容别名。 |
| `dtr_util_trace` | `0` | 默认不写 utility CSV trace。 |
| `bc_replay_pov` | `spectated` | 只给正在被第一人称观察的 replay bot 发布昂贵的 native POV 更新。 |

## 紧凑播放预设：`dtr_preset`

`dtr_preset [status|0xMASK]` 一次应用桌面转换器里提供的六项播放选择，因此一行
生成的控制台指令即可先配置服务器，再启动播放：

```text
dtr_preset 0x15; dtr_go seq "<manifest.json>" 0
```

v1 mask 按十六进制解析。`0x` 前缀可以省略，但生成的指令始终带前缀。以下 bit
定义是稳定契约，后续不会复用：

| Bit | Hex | 行为 |
| ---: | ---: | --- |
| 0 | `0x01` | 武器/loadout 对齐 |
| 1 | `0x02` | 完整的 demo 证据饰品对齐 |
| 2 | `0x04` | Demo 玩家名与 SteamID64 identity |
| 3 | `0x08` | Manifest 头像覆写，不可用时回退 Steam 头像 |
| 4 | `0x10` | 自动播放 demo 语音 |
| 5 | `0x20` | Playoff sequence 续播 |

`0x15` 是推荐基础组合：武器、Steam identity 和语音。`0x00` 关闭这六项，`0x3F`
全部开启。头像同步依赖 Steam identity（`0x08` 需要 `0x04`），饰品同步依赖武器
对齐（`0x02` 需要 `0x01`）。未知 bit 和非规范组合会被拒绝，不会静默修正。

这个 mask 只完整替换上述六项。投掷物、左右手、准星、handoff、赛事展示、partial
replay 和聊天设置保持当前值。饰品仍只消费显式导出的 demo 证据，并保留相同的
Valve / GSLT 风险。Preset 只是当前进程的临时覆盖；reload config 或 plugin 后会恢复
服务器本地默认值。

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

### `dtr_playoff <true|false>`

这是 manifest 证据先耗尽、但本地比赛仍未结束时使用的显式续播开关。可以在
`dtr_arm seq` / `dtr_go seq` 之前或 sequence 播放期间开启。最后一个正常 source
round 启动后，后续每个服务器回合都会独立抽取：

- 一个 manifest 中 `t_economy.class` 为 `full` 的历史 T source round；
- 一个 manifest 中 `ct_economy.class` 为 `full` 的历史 CT source round。

T 和 CT 默认解耦，可以来自两个不同的 source round。分配仍严格按人：当前 replay
bot 保留上一回合的 demo SteamID，只能拿到所选 side/round 中同一 SteamID 的 `.dtr`。
不会跨玩家随机，也不会退化成跨 manifest 的 pool 随机。如果任一侧缺少保留的 SteamID
证据，或没有一个长枪局候选能覆盖该侧所有 replay bot，整个 playoff round 会安全跳过。

因为一个 playoff round 可以由两个来源拼成，所以不会 replay 任何单一来源回合的
scoreboard、聊天或语音 metadata。关闭 playoff 会取消未来或尚未进入 live 的 playoff
调度，但不会中止已经进入 live round 的 replay。默认关闭。
如果希望服务器默认开启，在 `demotracer.config.json` 顶层设置 `"playoff": true`；命令
修改的是当前生效值，下次 reload config 或重新加载插件时会重新采用 JSON 默认值。

给旧脚本保留的兼容 alias，不作为推荐 quick start：
`dtr_run_manifest <manifest.json> [from_source_round]`。

### `dtr_stop_sequence`

停止已经 armed 或正在推进的 manifest sequence，也会停止它未来的 playoff 续播。
它不会删除文件，也不会修改 `dtr_playoff` 开关；如果要同时停止已经进入 live 的 slot，
用 `dtr_stop_all`。

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
  pending alignments、buy plan 和 replay 持有的输入注入状态。
- `dtr_stop all`：停止所有 DemoTracer replay 状态。

旧命令兼容：`dtr_stop <slot>` 等同于 `dtr_stop slot <slot>`。

### `dtr_stop_all`

停止所有当前已加载 slot，并关闭 active sequence/pool/armed 状态。已加载 slot
元数据可能仍留在内存里；如果要从某个 slot 移除 replay，用 `dtr_unload`。

这是 `dtr_stop all` 的旧便利 alias。

### `dtr_unload <slot>`

卸载一个 slot，并清理该 slot 的插件侧元数据。

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

- `default` / `full`：武器、投掷物、左右手 desired 写入和服务器发布的准星对齐开启。
- `handoff_safe`：保留武器/投掷物，但关闭 `left_hand`，换取更顺的 handoff。
  服务器发布的准星对齐仍然开启。
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

### `dtr_chat_auto [status|on|off]`

控制 demo 文字聊天自动 replay，默认开启。

开启后，runtime 会读取 manifest 的 `rounds[].chat_messages`，按 voice 使用的同一
live/freezetime anchor 排程。玩家聊天由匹配到的安全 replay bot 执行 `say` 或
`say_team`；server/admin 类型消息会作为 DemoTracer 服务器全局消息打印。无法匹配到
当前已加载安全 replay bot 的玩家消息会跳过。

文字聊天是瞬发事件。早于当前 playback anchor 的消息，例如从 live 开始 replay 时的
freezetime 消息，会在 replay 开始时补发一次，而不是直接丢弃。语音 replay 仍保持
严格时间窗口。

观察者是否能看到这些原生玩家文字聊天，取决于 CS2 服务器聊天策略。本地 replay 测试
如果希望观察者也收到原生玩家文字聊天，先设置 `sv_full_alltalk 1`。
单独设置 `sv_allchat 1` 不足以让观察者看到这些消息。

### `dtr_chat_test <loaded|any|slot> [all|team] <message>`

不走 manifest 时间线，直接让一个 replay bot 发一条诊断聊天。`loaded` 选择当前已加载的
第一个安全 replay bot；`any` 选择任意安全 bot；数字表示指定 slot。该命令使用和自动
文字 replay 相同的 server-side `say` / `say_team` 路径。

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
  火（molotov/incendiary）还需要新转换 `.dtr` 里的 high-fidelity 起火/爆开
  metadata；旧文件或缺少可靠 fire effect metadata 的火仍保留 CS2 原生 projectile
  和 inferno 行为。
- bot 仍然正常执行投掷动作。插件等待 CS2 spawn projectile 后，解析 thrower
  slot，在 replay cursor 附近匹配下一个 demo projectile event，然后优先让 native
  BotController 在 projectile simulation 前修正 birth-state 字段：
  `InitialPosition`、`InitialVelocity`、`AbsOrigin`、`AbsVelocity`。
  旧 native runtime 会回退到 managed post-spawn 写入路径。
- 匹配会延迟重试几个 tick，因为 CS2 在 projectile 刚 spawn 时可能还没有挂上
  thrower，或字段还没最终稳定。
- 烟雾弹的爆开元数据仍是最完整的诊断路径；火的 effect metadata 会记录 demo
  inferno start/detonation 证据，用来决定 molotov/incendiary 是否允许 align。

为什么需要它：

只回放人物 origin、velocity、view angles、buttons 和 subtick input，不能保证 CS2
重新算出同一个 grenade initial velocity。很小的速度或高度偏差，就可能让关键烟撞到
不同碰撞边缘。projectile data 直接记录 demo 的投掷物结果，用它修正这种 bias。

### `dtr_projectile_align_ticks <status|default|once|2..512|until_delete>`

投掷物 align 的实验性持续写入控制。默认是 `once`：匹配到 projectile 时只排入一次
birth-state 修正。数字值表示总共连续排入/写入多少个 plugin tick。`until_delete`
表示每个 plugin tick 都写，直到 projectile entity 消失。

这个命令只用于本地保真/性能测试。它可以用来判断 per-tick 强拉 projectile 是否会造成
掉帧或卡顿，但它仍不能保证 molotov/inferno 伤害完全正确，因为碰撞、爆开、inferno
spread 和 damage overlap 仍由 CS2 引擎决定。

### `dtr_molotov_align_point <status|off|teleport|detonate> [lead_ticks]`

燃烧弹/incendiary effect 点对齐的实验开关。只会作用于有可靠 demo fire effect
metadata 的火。

- `off`：只使用普通 projectile align。
- `teleport`：接近 demo effect tick 时，把 live molotov projectile 移到 demo
  effect position，并把速度清零。
- `detonate`：移动 `AbsOrigin` 和 `ExplodeEffectOrigin` 后，额外把 molotov
  projectile 的 `DetonateTime` 设为当前 server time。

默认是 `detonate 1`。`lead_ticks` 范围是 `0..8`。它会对齐 demo-backed fire
effect 点，避免 molotov 落点漂移；不会强制写玩家伤害或血量。需要回退纯 CS2
projectile/inferno 模拟时，用 `off`。

### `dtr_projectile_align_log [clear|all|molotov|fire]`

直接在服务器控制台输出最近的 projectile-align 内存日志。测试回合结束后用 `molotov`
或 `fire`，可以看到火焰弹到底是 apply、skipped、匹配超时，还是写入次数结束。
这个命令不需要打开 CSV trace。

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
  开启时，对应安全 replay bot slot 会被换成 demo 中的探员模型。
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

开启后，DemoTracer 会为安全 replay bot 租用 converter 从 demo 玩家稳定
`crosshair_code` 导出的 manifest `view.crosshair_code`。bundle 内置 BotHider 是唯一 writer，
通过 `CCSPlayerController.m_szCrosshairCodes` 和服务器 state replication 发布。死亡、
接触、C4 handoff、replay 结束、sequence 完成、后续服务器 round 和比赛结束都只释放
replay 控制；最近一次成功 DTR 批次的 presentation 会继续保持。只有新成功批次替换、
显式按 slot unload/kick、断线、换图、slot 重用或插件卸载才恢复 provider 当前 persona
基础值。这条路径完全由服务器发布，不写真人客户端配置，也不需要向客户端注入代码。

### `dtr_left_hand_desired <0|1>`

控制新加载的 `.dtr` v7 command frames 是否保留 `left_hand_desired` 写入。

- `1`：保留 demo 里的左/右手 desired 状态。这是默认值，也是最高保真度行为。
  默认 `handoff.viewmodel_continuity="round"` 时，活体 handoff 后会继续续约最后的
  desired 持枪侧直到本回合结束，不会立即切手并重播切枪动作。
- `0`：加载 replay frames 到 native playback 前清掉 left-hand desired 写入。它会降低
  replay 保真度，并禁用 left-hand latch。

这个设置只影响命令改动之后加载的 replay。已经加载到 slot 上的 round、sequence 或
pool plan 需要重新加载后才会应用。

### `dtr_replay_identity <off|name|steam|avatar|full|0|1>`

控制 BotHider identity 对齐。

开启后，加载 manifest 时会通过 bundle 内置 BotHider 的 presentation lease，把 demo
玩家的 `player_name` 和 `steam_id` 发布给受管 bot slot。默认模式是 `steam`，不会写
`ServerAvatarOverrides`；`1`/`on` 也表示 `steam`。

identity lease 跟随“最近一次成功加载的完整 DTR presentation 批次”，不跟随当前是否仍
在注入输入，也不跟随 native replay buffer 的寿命。死亡、接触、replay 结束、C4 handoff、
sequence 完成、后续服务器 round 和比赛结束后，demo 名字、SteamID、头像关联、准星和
flair 都继续保持。新 round 成功加载时原子替换整批 presentation；失败或只加载到一半的
批次不会覆盖上一批完整身份。显式 `dtr_unload`、`dtr_kick`、断线、换图、slot 重用或
插件卸载才恢复相应 slot 的 BotHider persona 基础值。精确 SteamID 批次若存在无法消解的
碰撞会明确拒绝，不会偷偷替换成另一个 persona。

普通 BotHider persona 的 `scoreboard_flair` 只来自服务器本地
`addons/BotHider/bot_info.json`，不冒充 DTR 证据。字段缺失或为 `0` 时保持为空；runtime
不会根据 SteamID 猜测，也不会随机伪造一枚勋章。

用 `avatar` 可以应用 manifest 里的 PNG 头像覆写，例如队伍/赛事 logo。DemoTracer 会
保留真实 demo SteamID64，使原生 Steam 资料卡、勋章和称赞信息仍然可用，再把通过校验
的匹配 PNG 绑定到该 SteamID64。如果 manifest 记录或可用 PNG 缺失，则回退到 Steam
头像，而不是显示问号头像。

`full` 仅作为 `avatar` 的兼容别名保留。

头像覆写在延迟写入前会重新做 slot 校验：该 slot 必须仍然加载同一个 SteamID64，
仍然是安全 replay target，并且仍由 BotHider 管理。CS2 的
`ServerAvatarOverrides` 底层仍然按 SteamID64 生效。DemoTracer 会先保留空的第 0 项，
避免找不到 SteamID 时把某个 DTR 头像错误复用给其他玩家。如果本地服务器中存在使用
同一 SteamID64 的真实账号，它也会命中同一份头像覆写。

这个主要用于 POV 和 spectator 观察清晰度。如果目标 slot 不由 bundle 内置 provider
管理，identity 对齐会跳过该 slot，而不会应用到真人玩家。

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

`demotracer.config.json` 中的 `handoff.viewmodel_continuity` 控制交接后的 viewmodel
租约：

- `round`（默认）：接触/C4 handoff 或 replay 自然结束时，移动注入、replay control、
  buy plan 和武器锁仍然立即释放，但最后的 replay viewmodel 与 native left-hand desired
  latch 会保留到下一个回合边界。这样不会在控制权交接时因为切手重播切枪动作并产生开火
  cooldown。
- `release`：replay control 释放时立即恢复 replay 前 viewmodel，并清掉 left-hand latch。

死亡、unsafe target、显式 stop/unload/kick、断线、换图和插件卸载始终立即清理这份
viewmodel 租约。保留的租约绝不会继续 replay 输入，也不会继续占用武器 slot lock。

接触检测实现：

- 使用 bullet damage / player hurt 事件，以及 replay bot 当前原生
  `m_visibleEnemyParts` 掩码。记忆中的 `m_enemy` handle 和 nearby-enemy 计数不再
  直接视为 LOS contact。只有旧 BotController 不提供原生感知接口时，才回退到
  managed spotted / RayTrace 检测器。
- replay 期间仍会让原生 bot update 和 upkeep 在后台持续运行，同时保持录制的
  input、movement 和 view 为最终输出。contact handoff 会释放 replay control，但不再
  清空已累积的原生感知与决策状态。handoff 后的开火仍交给正常 CS2 bot AI；
  DemoTracer 不运行 CSGO-style combat executor。
- replay 拥有 slot 期间会仲裁原生武器装备/选择出口：冲突的 AI 换枪动作会被拦截，
  当前 replay tick 精确指定的武器仍可通过。
- 仅在 freeze-time pre-roll 期间，DemoTracer 会临时抑制原生 `Update` 和 `Upkeep`；
  `round_freeze_end` 会先释放这把阶段锁，再恢复正常 replay 期间的感知 shadow-run。
- 开启 360 handoff 时，BotController 只关闭 replay 期间原生
  `CCSBot::IsVisible` 的 FOV 检查；LOS、烟雾、目标选择和 reaction state 仍由原生
  AI 决定。原生 contact 不增加人为 grace 或 hold。
- `threat_360_range` 与 `threat_360_los` 只作用于兼容回退检测器。

### `dtr_handoff_360 [0|1] [range] [los|nolos]`

控制 replay 期间的原生 360 度感知及其兼容回退检测器。

- `0`/`off`：关闭 360 threat handoff。
- `1`/`on`：开启 360 threat handoff。
- `range`：兼容回退检测器使用的游戏单位半径，插件会夹到允许范围内。
- `los`/`raytrace`：有 RayTrace provider 时要求 LOS。
- `nolos`：兼容回退检测器只按距离触发；更偏实验。

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
