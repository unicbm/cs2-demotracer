# 语音导出和回放

DemoTracer 只能导出 demo 里真实存在的游戏内语音。正常导出方式是：

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --export-voice
```

这会在 `.dtr` replay 旁边写出每回合的紧凑语音 sidecar：

```text
<输出目录>/
  <demo-id>/
    manifest.json
    round00/t/*.dtr
    round00/ct/*.dtr
    voice/
      round00.dtv
      round01.dtv
```

`voice/roundXX.dtv` 是自动语音回放需要的文件。只复制 `.dtr` 不会带上语音。

GUI 里 `导出语音(若有)` 默认开启。解析阶段会收集 voice metadata；转换阶段如果 demo
里有可用语音，就写出 `.dtv` sidecar；导出成功后，复制出来的控制台指令会自动带
`dtr_voice_auto on`。

## 什么 demo 会有语音

不是所有 demo 都录了游戏内语音。语音导出需要 demo 中存在可用的
`CCLCMsg_VoiceData`、非空音频 payload 和有效 speaker XUID。

更可能有语音：

- 社区服 demo。
- FACEIT demo。
- 5E demo。
- 其他保留游戏内 voice netmessage 的服务器录像。

常见没有语音的原因：

- 平台没有记录 voice data。
- voice data 被剥离。
- 玩家没有使用游戏内语音。
- voice frame 没有有效 XUID。

如果没有可用 voice frame，转换仍然可以正常导出路线 replay，但会跳过 voice sidecar。

## 同时导出饰品和语音

语音导出和饰品导出互相独立。要同时导出语音和 demo-backed 饰品证据：

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --export-voice --export-cosmetics --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

如果还要贴纸和挂件：

```powershell
cs2-demotracer.exe convert --demo "<demo.dem>" --output "<输出目录>" --export-voice --export-cosmetics --export-stickers --export-charms --acknowledge-cosmetic-gslt-risk --accept-cosmetic-export-disclaimer
```

## 服务器自动播放

自动播放由 `dtr_voice_auto` 控制：

```text
dtr_voice_auto on
dtr_go seq "<manifest.json>" 0
```

完整本地测试 preset：

```text
dtr_replay_identity steam; dtr_align full; dtr_match full; dtr_cosmetics full; dtr_partial 1; dtr_handoff death_contact_c4 slot; dtr_voice_auto on; dtr_go seq "<manifest.json>" 0
```

某个 source round 开始时，DemoTracer 会在 manifest 附近查找
`voice/roundXX.dtv`，把 speaker XUID 映射到当前 loaded replay bot，然后按回合
timeline 发送原始 voice payload。

## 谁能听到

- 观察者可以听到所有 replay voice。
- T 玩家听 T replay bot 的语音。
- CT 玩家听 CT replay bot 的语音。
- bot 和 HLTV 不作为语音接收者。

## 手动 debug 命令

正常 sequence playback 应使用 `dtr_voice_auto on`。下面这些命令主要用于 debug
某个 `.dtv` sidecar：

```text
dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]
dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]
dtr_voice_stop
```

converter 也有一个低层测试命令，可以导出一段显式连续语音 clip：

```powershell
cs2-demotracer.exe export-voice-clip --demo "<demo.dem>" --output "<clip.dtv>" --all-speakers --start-tick <tick> --seconds 8
```

这个命令不是正常 round replay 流程；DemoTracer sequence playback 应优先使用
`convert --export-voice`。

## 排错

没有 `voice/` 目录：

- 确认转换时传了 `--export-voice`。
- 确认 demo 本身录到了游戏内语音。
- 优先用已知包含语音的社区服、FACEIT 或 5E demo 测试。

`voice_auto=unavailable`：

- 更新完整 playback bundle。
- 检查 `dtr_runtime`；BotController 必须暴露 voice send capability。

`voice_auto=map_failed`：

- sidecar speaker 没有映射到当前 loaded replay bot。
- 检查 manifest source round 和 `voice/roundXX.dtv` 是否匹配。
- partial replay 可能跳过部分未加载的 speaker。
