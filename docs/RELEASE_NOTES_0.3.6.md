# CS2 DemoTracer v0.3.6

v0.3.6 是一个面向 demo-backed cosmetic alignment 的稳定性版本。

## 更新摘要

- 改进刀具和手套的 demo 证据缓存：当 bot 进入非 DTR 回合、死亡、重生或 handoff 后，只要已有 demo 证据证明这是该选手的固定饰品，DemoTracer 会继续按缓存证据修复刀和手套。
- 新增 `dtr_cosmetics preserve_native <on|off>` 和 config `"cosmetics": { "preserve_native": true }`。开启后，缺少 demo 证据不会主动清掉 bot 原本由服务器/CS2 提供的 native 饰品。
- 补齐 CS2 刀具 item definition 识别，包括 `weapon_knifegg`、T knife 和常见特殊刀型，避免刀型和刀皮证据在 HUD/模型/动画链路上分离。
- 修正 replay econ owner identity 显示：即使 bot 的 Steam 昵称和 demo 内职业 ID 不一致，武器名牌仍优先追踪 demo 选手身份。
- `avatar` identity 模式现在稳定使用 DTR 合成 SteamID64，只有存在 avatar override 证据时才写入 PNG 覆写，不再对缺头像证据 slot 回退到真实 SteamID64。

## 行为边界

- 仍然不生成随机饰品。
- 仍然不读取 profile 或 inventory database。
- 仍然不会对真人玩家应用 cosmetics。
- 仍然要求 converter 侧显式 opt-in 导出 cosmetics/stickers/charms，runtime 侧再显式开启对应 `dtr_cosmetics` 功能。
- `preserve_native` 只改变“缺证据时是否主动清空 native bot 饰品”的策略，不把缺失证据解释成随机或 fallback inventory。

## 兼容性

- 不改变 `.dtr` 格式，当前 writer 仍为 v7。
- 不改变 manifest ABI，当前 manifest ABI 仍为 17。
- 不改变 BotController native ABI，当前 native ABI 仍为 16。
- 不改变 DemoTracer companion API，当前 API version 仍为 4。
- 维护平台仍为 Windows x64。

## 升级建议

如果你使用 `dtr_cosmetics basic/full` 或者依赖刀、手套、custom name、avatar identity alignment，建议从 v0.3.5 升级到 v0.3.6。

升级后建议检查：

```text
dtr_runtime
bc_status
dtr_cosmetics status
```

需要保留服务器原本给 bot 的 native 饰品时：

```text
dtr_cosmetics preserve_native on
```

## 发布资产

- `cs2-demotracer-v0.3.6-windows-x64.zip`：Windows x64 converter CLI。
- `cs2-demotracer-server-v0.3.6-windows-x64.zip`：server playback bundle，包含 BotController runtime 和 DemoTracer CounterStrikeSharp plugin。
- `SHA256SUMS.txt`：release assets 的 SHA-256 校验值。

<details>
<summary>English details</summary>

## Summary

v0.3.6 is a stability release for demo-backed cosmetic alignment.

- Improves demo evidence caching for knives and gloves. If demo evidence shows that a fixed cosmetic belongs to the replayed player, DemoTracer can repair it across non-DTR rounds, deaths, respawns, and handoff.
- Adds `dtr_cosmetics preserve_native <on|off>` and config `"cosmetics": { "preserve_native": true }`. When enabled, missing demo evidence does not force-clearing server/CS2-native bot cosmetics.
- Expands CS2 knife item definition recognition, including `weapon_knifegg`, T knife, and common special knife classes, reducing HUD/model/animation mismatches caused by split knife evidence.
- Fixes replay econ owner identity display so weapon ownership tracks demo player identity even when a bot's Steam nickname differs from the demo player name.
- Makes `avatar` identity mode consistently use synthetic DTR SteamID64 values, applying PNG avatar overrides only when matching avatar evidence exists.

## Boundaries

- No random cosmetics.
- No profile or inventory database reads.
- No cosmetic application to real human players.
- Cosmetic, sticker, and charm export remain explicit opt-in on the converter side, and runtime application remains explicit opt-in through `dtr_cosmetics`.
- `preserve_native` only changes whether missing evidence clears native bot cosmetics. It does not create fallback inventory.

## Compatibility

- No `.dtr` format change; current writer remains v7.
- No manifest ABI change; current manifest ABI remains 17.
- No BotController native ABI change; current native ABI remains 16.
- No DemoTracer companion API change; current API version remains 4.
- Maintained release platform remains Windows x64.

## Upgrade Guidance

Upgrade from v0.3.5 if you use `dtr_cosmetics basic/full` or rely on knife, glove, custom name, or avatar identity alignment.

Recommended post-upgrade checks:

```text
dtr_runtime
bc_status
dtr_cosmetics status
```

To preserve server-native bot cosmetics when demo evidence is absent:

```text
dtr_cosmetics preserve_native on
```

## Assets

- `cs2-demotracer-v0.3.6-windows-x64.zip`: Windows x64 converter CLI.
- `cs2-demotracer-server-v0.3.6-windows-x64.zip`: server playback bundle with BotController runtime and DemoTracer CounterStrikeSharp plugin.
- `SHA256SUMS.txt`: SHA-256 checksums for release assets.

</details>
