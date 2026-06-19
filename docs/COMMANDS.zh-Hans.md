# DemoTracer 指令参考

这些指令在 CS2 服务器控制台中输入，前提是 Metamod runtime `BotController`
和 CounterStrikeSharp 插件 `DemoTracer` 已经加载。只有在需要把多条指令粘成一行
时，才需要在每条后面加分号。

## 推荐基础配置

```text
css_plugins reload DemoTracer
dtr_replay_identity 1
dtr_weapon_align 1
dtr_projectile_align 1
dtr_handoff death_or_contact slot
dtr_partial 1
dtr_run_manifest "<输出目录>\<demo-id>\manifest.json" 0
```

如果装了 BotHider，`dtr_replay_identity 1` 可以让 replay slot 使用 demo 里的
名字和 SteamID64，便于看 POV。如果没有 BotHider，就保持关闭。

## 默认值

| 设置 | 默认值 | 含义 |
| --- | --- | --- |
| `dtr_weapon_align` | `1` | 对齐 loadout、购买行为、当前武器和武器槽锁定。 |
| `dtr_projectile_align` | `1` | 使用 `.dtr` v4 数据对齐投掷物初始矢量。 |
| `dtr_handoff` | `death_or_contact slot` | 接触或死亡后只释放触发的 replay slot。 |
| `dtr_partial` | `1` | bot 数量不足时允许部分 replay。 |
| `dtr_replay_identity` | `0` | 默认不写 BotHider 名字/SteamID。 |
| `dtr_util_trace` | `0` | 默认不写 utility CSV trace。 |
| `bc_replay_pov` | `spectated` | 只给正在被第一人称观察的 replay bot 发布昂贵的 native POV 更新。 |

## 顺序播放

### `dtr_run_manifest <manifest.json> [start-round]`

按转换后的 demo manifest 顺序播放回合。

实现方式：

- 从 `manifest.json` 读取所有可播放 round。
- 停止并卸载当前 replay 状态。
- 下一个 `round_start` 时准备当前 round，把每个玩家的 `.dtr` 加载到安全 bot slot。
- `round_freeze_end` 时启动所有已加载 replay。
- 每启动一个 round 后，自动推进到 manifest 里的下一个 round。

正常播放一个 demo 时优先用这个指令。

### `dtr_stop_sequence`

停止已经 armed 或正在推进的 manifest sequence。它不会删除文件，也不会修改插件开关。
它只停止后续 sequence 调度；如果要同时停止已经开始播放的 slot，用 `dtr_stop_all`。

### `dtr_run_pool <pool_manifest.json> [start-round]`

从地图回合池里按经济自动选择相似回合播放。

实现方式：

- 读取 `pool_manifest.json`。
- 在 `round_freeze_end` 时读取当前 T/CT 装备价值。
- 根据手枪局状态和双方经济相似度选择 candidate round。
- 加载选中的 round 并立即启动 replay。
- 记录近期用过的 candidate，减少重复选择。

当你希望本地游戏不断从一组 demo 中挑相似开局路线，而不是固定播放一个 demo 时，用这个。

### `dtr_stop_pool`

停止后续 pool 选择并清理内存中的 pool 状态。它不会停止已经开始播放的 slot；
如果要停止当前 replay，用 `dtr_stop_all`。

## 手动加载和播放

### `dtr_load_round <manifest.json> <round>`

从 manifest 加载一个 round 到可用 replay bot slot，但不立刻开始播放。

实现方式：

- T 文件分配给 T bot slot，CT 文件分配给 CT bot slot。
- 只使用安全目标：严格 CS2 bot，或 BotHider 管理的 bot slot。
- 对已加载 slot 设置 native buy skip，避免 vanilla bot 购买和 replay loadout 打架。
- 记录每个 slot 的 manifest 元数据，例如玩家名、SteamID64、loadout、预加载武器 def、
  projectile events。

### `dtr_arm_round <manifest.json> <round> [loop:0|1]`

加载一个 round，并 armed 到下一个 `round_freeze_end` 自动开始。

这个适合按正常 freeze-time 节奏测试某个特定 round。

### `dtr_play_loaded [loop:0|1]`

立即启动当前所有已加载 slot。

如果 `dtr_weapon_align` 开启，启动前会预加载 replay loadout 和初始武器。如果
`dtr_weapon_align` 关闭，则不会主动同步 manifest loadout。

### `dtr_load <slot> <absolute-or-game-path.dtr>`

把单个 `.dtr` 加载到一个 bot slot。这个是偏底层的实验指令。它拿不到
manifest 里才有的完整元数据，例如 `player_name`、`steam_id` 或完整 loadout，
除非这些信息能直接从 `.dtr` 扫描出来。

### `dtr_play <slot> [loop:0|1]`

启动单个已加载 slot。启动前会确认目标仍然是安全 bot 目标。

### `dtr_stop <slot>`

停止一个 slot 的 replay，并释放该 slot 的 runtime locks、pending alignments、
buy plan 和 replay bot 状态。

### `dtr_stop_all`

停止所有当前已加载 slot，并关闭 active sequence/pool/armed 状态。已加载 slot
元数据可能仍留在内存里；如果要从某个 slot 移除 replay，用 `dtr_unload`。

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

## 还原度和 handoff 控制

### `dtr_weapon_align <0|1>`

开关武器和 loadout 对齐。

开启后的实现方式：

- 加载 round 时，native buy control 会让 replay slot 跳过 vanilla bot 购买。
- 开始前，插件根据 manifest loadout 写入护甲、头盔、CT kit、道具、主/副武器候选
  和初始武器。
- replay 过程中，插件跟随 `.dtr` 每 tick 的 weapon def index，让 native runtime
  切换当前武器并锁定对应武器槽。
- 缺失武器会通过 CS2 item give 和谨慎的 slot replacement 处理，而不是模拟买菜单。

限制：

- 这是 replay fidelity 对齐，不是完整经济模拟器。
- 它绕过阵营购买限制，尽量按 demo loadout 还原。
- CS2 默认手枪和库存槽行为仍可能在边界情况下导致近似结果。

### `dtr_projectile_align <0|1>`

开关投掷物初始矢量对齐。

开启后的实现方式：

- 需要 converter 写出的 `.dtr` v4 projectile events。
- 当 replay metadata 可用时，匹配 smoke、flash、HE、molotov、incendiary 和 decoy
  projectile entity。
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
不同碰撞边缘。v4 projectile data 直接记录 demo 的投掷物结果，用它修正这种 bias。

### `dtr_replay_identity <0|1>`

控制 BotHider identity 对齐。

开启后，如果 BotHider 可用且 slot 由 BotHider 管理，加载 manifest 时会把 demo 玩家
的 `player_name` 和 `steam_id` 写给对应 bot slot。

这个主要用于 POV 和 spectator 观察清晰度。默认关闭，因为它依赖 BotHider。

### `dtr_partial <0|1>`

控制 bot 数不足时是否允许加载部分 replay。

- `1`：有多少安全同阵营 bot slot 就加载多少，并报告跳过的 T/CT 数量。
- `0`：如果不能分配 manifest 里的所有玩家，就加载失败。

### `dtr_handoff <off|death|contact|death_or_contact> [all|slot]`

控制 replay 什么时候把 bot 控制权交回普通 bot 行为。

模式：

- `off`：不自动 handoff。
- `death`：replay 控制玩家死亡或击杀时 handoff。
- `contact`：检测到战斗/接触时 handoff。
- `death_or_contact`：死亡和接触都触发。

范围：

- `slot`：只释放触发的 slot。这是推荐安全默认值。
- `all`：一个触发释放所有 replaying slots。只建议实验时使用。

接触检测实现：

- 使用 bullet damage / player hurt 事件，以及 replay bot 是否看见敌人的检查。
- replay 刚开始有很短 grace window，避免启动瞬间误触发。
- 释放 slot 时会重置 native locks 和 bot brain 状态。

## 诊断

### `dtr_bots`

输出队伍玩家、严格 bot 状态、BotHider managed 状态、native `controllingBot` 状态、
是否可作为 replay candidate、slot、队伍和名字。

如果 manifest 拒绝加载，或加载 slot 数少于预期，先看这个。

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
