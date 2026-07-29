# DemoTracer

DemoTracer（产品名 **CS2 DemoTracer**）是一套开源的 Windows 桌面工具和配套
服务器回放栈。它将 Counter-Strike 2 demo 文件转换为紧凑的 .dtr 回放，再通过
本地 CS2 服务器中的机器人复现移动、视角、指令状态、武器、投掷物、可选语音
和部分演示证据。

[English README](../README.md) · [文档索引](README.md) ·
[开发说明](DEVELOPMENT.md) ·
[最新版本](https://github.com/unicbm/demotracer/releases/latest)

## 主要能力

- 使用中英文 GUI 打开 demo、查看回合、选择选手并维护本地回放库。
- 通过直接链接进桌面的 Rust 转换器生成 .dtr v8 文件和 ABI 17 manifest；
  1.x 不提供独立转换 CLI。
- 通过配套 CounterStrikeSharp 与 Metamod 组件复现移动、subtick 输入、视角、
  武器、投掷物和可选 demo 语音。
- 安装、校验、修复、更新和回滚带签名的服务器播放包，同时保留本地配置。
- 外观、贴纸、挂件、探员和比分板对齐只使用 demo 证据，并维持安全默认值。

DemoTracer 用于本地回放研究、内容制作、分析和插件开发，不用于匹配或作弊。

## 运行要求

- Windows 10 或 Windows 11 x64。
- 桌面 GUI 需要 Microsoft Edge WebView2。
- 服务器回放需要本地 Windows x64 CS2 服务器、Metamod:Source 和
  CounterStrikeSharp。

普通用户只需从
[官方 Release](https://github.com/unicbm/demotracer/releases/latest)
下载安装程序，不需要 Python、Node.js、Rust 或本地编译环境。完整依赖见
[依赖与兼容性](DEPENDENCIES.md)。

## 源码验证

    cd desktop\converter
    cargo test --locked

    cd ..\gui
    pnpm install --frozen-lockfile
    pnpm run check
    pnpm test

    cd ..\..
    .\tooling\scripts\test-css.ps1
    .\tooling\scripts\check-release-contract.ps1

协议真源是
[shared/contracts/playback-contract.v1.json](../shared/contracts/playback-contract.v1.json)：
当前写入 .dtr v8，读取 v3-v8，manifest ABI 为 17，BotController ABI 为
16（minor 33+），BotHider API 为 1，DemoTracer companion API 为 6。

## 开源与官方发行

第一方代码采用 **AGPL-3.0-only**。第三方源码和数据继续遵循各自目录中的许可
与署名。fork 可以准确说明其基于 DemoTracer，但修改版不得冒充 unicbm 发布、
签名或支持的官方构建；详见
[商标与官方构建政策](../TRADEMARKS.md)。
