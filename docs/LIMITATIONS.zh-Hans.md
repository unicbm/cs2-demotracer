# 当前限制

这份文档记录 DemoTracer 的已知限制和边界情况。

## 平台和服务器范围

- 目前主要面向 Windows x64 本地 CS2 环境。Linux 可以尝试从源码构建，
  但 converter/runtime 的 Linux 发布包目前不是维护目标。
- 需要同一张地图，并且服务器里要有足够的 bot。
- 这个工具面向本地服务器、研究、内容制作和插件开发，不用于匹配或作弊。

## Replay 模型

- `.dtr` 是无损压缩的 BotController 兼容 replay 格式，并包含 demo 原始投掷物元数据、
  按玩家记录的高保真事件、库存快照和 v7 command frame 输入证据。完整离线还原
  usercmd 仍然是未来工作。
- 如果加载了其他干预 native bot AI 的插件，可能出现预期外的 bot 购买或库存行为。
  DTR 本质上是 slot 绑定，不是武器绑定，所以可能出现这个 slot 的 bot 有主武器，
  但依然按照 ECO 路线和手枪时代的 demo 输入行动。本插件目前会保留原本 demo
  中的开枪和切 weapon slot 行为，而不是 hook 掉它们；这不是回放 bug，而是当前行为。

## Bot 身份和展示

- 如果用户要重玩自己原本就在场的 demo，场上会同时有一个像自己的 bot 和一个本人。
  这种情况下应在加载 replay 前使用 `dtr_replay_identity name` 或 `off`。bundle 内置
  BotHider runtime 仍然是必需依赖，但这两个模式不会把 demo SteamID 和头像 lease 给
  replay bot；否则真人和 bot 共用 SteamID 时，TAB 记分板等 UI 可能显示冲突。一般
  不应导致游戏崩溃，但显示会很怪。
- 头像覆写来自 demo 元数据提供的 PNG。当前 runtime 会在延迟写入头像前重新校验 replay
  slot，因此已经 unload 的旧 slot 不应再收到旧头像写入。但 CS2 的
  `ServerAvatarOverrides` 底层仍然按 SteamID64 生效。DemoTracer 会保留空的第 0 项，
  避免未匹配玩家继承第一个 DTR 头像。推荐的 `dtr_replay_identity avatar` 会保留真实
  demo SteamID64，使 Steam 资料卡信息仍然可用；没有通过校验的匹配 PNG 时则回退到
  Steam 头像。同一 SteamID64 的真实账号在本地服务器中会共享这份覆写。有些 demo
  提供的是队伍/default logo PNG，而不是真正的玩家专属头像，所以 TAB、OB 和其他 UI
  surface 之间仍可能不一致。
- bundle 内置 BotHider 会改写可见 SteamID 和 bot 游戏内显示名，但没有改变“游戏 native 认定”
  的 bot 名字。也就是说，一个 bot 可能显示为 donk 的头像和 SteamID，但
  `bot_kick donk` 不生效；DemoTracer replay bot 应使用 `dtr_kick` 定向踢出。
  一个可能的未来方案是
  先按 botprofile 严格匹配 id 正确的 bot，再进行 BotHider 设置和对应 slot 的 DTR
  执行；但大量社区 demo 里玩家 id 可能重名或包含复杂字符，预先写 botprofile 会让
  数据库体积迅速膨胀。CS2 的 botprofile 本质上又不区分大小写，`NiKo` 和 `niko`
  会被认为是同一个 bot。除非加入额外后缀和 SteamID 区分，但这会增加插件复杂度，
  还要额外修改 botprofile，所以目前暂时不做。

## Handoff 和物理边界

- 除 freeze-time pre-roll 外，replay 期间会让原生 AI update 和 upkeep 在后台持续运行，
  避免把控制权交还给一个刚刚冷启动的感知/决策状态。复杂 handoff 仍可能不完美，因为释放前仍以
  replay view 为最终输出，而 DemoTracer 不会用启发式强行写入私有的敌人或瞄准状态。
  contact 由原生 enemy / visible / nearby 状态触发；managed RayTrace 只作为旧 runtime
  的兼容回退。
- 例如双架 boost 行为，如果真人玩家取代了原本应该在下面的 bot，上面的 bot 可能出现
  反物理悬空。这是因为执行 DTR 的 bot 会被约束到 demo 对应的 velocity 和 position，
  不一定尊重当前游戏物理引擎。从 demo 提取的 position/velocity 反推按键意图本质上
  非唯一且不稳定，任何错位都会导致回放漂移，所以目前没有针对这些情况写特判，优先
  遵守 demo 原本意图。
- HLTV 比赛和大部分职业比赛理论上都是 solid teammates，也就是 `mp_solid_teammates 1`，
  不允许队友互相穿过。通常 bot 不会因此卡住，除非 demo 原本来自一些玩家可以互相
  穿过的休闲模式或类似规则环境。

## 投掷物和饰品保真

- 目前并不是所有投掷物都能 100% 符合 demo，但绝大多数不会出错。自由度较高的是燃烧弹。
  新转换的 `.dtr` 会写入 fire effect metadata，只有 metadata 可靠时才允许保守对齐；
  旧文件或证据不确定的火仍保留 CS2 原生 projectile/inferno 行为。
- bot 的饰品已经尽量恢复到正常显示，大部分情况下也不会有问题。但由于 demoparser
  对贴纸解析有问题，目前方案使用了我们自己修订过的提取路径，不能保证所有贴纸都完全
  匹配，尤其是 CS2 允许有坐标和旋转这些复杂 sticker 式样。
- 饰品/econ 导出和 runtime 对齐都是显式 opt-in；见
  [饰品对齐与 GSLT 风险](README.zh-Hans.md#饰品对齐与-gslt-风险)。
