# CS2 DemoTracer 0.3.3

## 更新重点

- 导出 demo 中观测到的音乐盒 ID，并写入 replay manifest。
- Runtime 会在加载 replay bot 时应用对应音乐盒，并在 MVP 事件中重发带 replay 音乐盒 ID 的事件。
- 饰品导出现在会额外保留 demo-backed 武器的 econ 身份字段：原始拥有者 SteamID、item account ID、item ID。
- Runtime 会写入这些 econ 身份字段，让转交、捡起或自然掉落后的 demo-backed 武器尽量保留 CS2 原生的“某某的武器”显示。
- `item_drop` / `item_pickup` 高保真事件仍保持 record-only；DemoTracer 仍不会脚本化普通武器或道具的地面 entity 生命周期。

## 使用说明

饰品、贴纸、挂件和 econ identity 导出仍然是显式 opt-in 功能，并继续沿用 GSLT/server guideline 风险确认。DemoTracer 不会生成随机饰品，也不会写入非 demo 证据的 fallback inventory。

音乐盒导出是普通 manifest 元数据，不需要开启饰品导出。Runtime 音乐盒应用只作用于已加载的 replay bot。

## 已验证

- 使用 Falcons vs G2 Inferno 参考 demo 做过正常全场转换，包含饰品、贴纸和挂件数据。
- 本地 CS2 安装测试通过：捡起或转交后的武器可以正常显示 demo 原始拥有者身份。
- 本地 CS2 安装测试通过：音乐盒 runtime 路径可以正常应用。

<details>
<summary>English details</summary>

## Highlights

- Exports demo-observed music kit IDs into replay manifests.
- Applies replay bot music kits at load time and rebroadcasts MVP events with the replay kit ID.
- Exports weapon econ identity fields for demo-backed cosmetics: original owner SteamID, item account ID, and item ID.
- Applies those econ identity fields at runtime so transferred or dropped demo-backed weapons can retain native CS2 owner naming.
- Keeps item drop and pickup high-fidelity events record-only during live replay; ordinary world-entity drop chains are still not scripted.

## Notes

Cosmetic, sticker, charm, and econ identity export remain explicit opt-in converter features and keep the same GSLT/server guideline warning model. DemoTracer does not generate fallback cosmetics or random inventory state.

Music kit export is manifest metadata and does not require cosmetic export. Runtime music kit application is scoped to loaded replay bots.

</details>
