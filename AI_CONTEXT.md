# Codex Usage Bar

## Project
Windows Codex 桌面独立悬浮条，原生 C# WinForms，使用系统 .NET Framework 编译器构建。此目录是公开源码工作树；`src/UsageBar.cs` 为主实现，`src/TitleAnchor.cs` 仅查询帮助菜单几何。运行数据不放在此目录。

## Current State
1.0.10 已安装并运行，自动启动已指向新版。用户明确授权修复额度显示后上传到已有公开仓库 Leeroysunfury/codex-usage-bar。前次正式发布为 v1.0.9，本次目标标签 v1.0.10。

1.0.10 修复五小时额度列过窄、长标签裁掉百分比的问题：加宽第一列，五小时/周标签根据实际文字宽度缩短，保留完整百分比，不缩小额度字体。同时统一逻辑宽度取整，修复 125% 等缩放下行数和高度阈值不一致造成的底行裁切。

- 保留五小时/周剩余额度、重置时间、本地自然日 Token、缓存量及缓存命中率。
- 1.0.8 左边缘跟随帮助菜单右边缘，普通所属窗口，不设系统置顶；窄窗口避让窗口控制按钮。
- 1.0.9 将原“均速”明确命名为“消耗均速”，新增“输出均速”。前者为本轮输入加输出 / 经过秒数，后者只用输出（含推理输出）/ 同一经过秒数。均包含工具等待，不是逐 Token 实时生成测速。模型调用结束后日志才更新用量。
- 两项速率共用对话选择、轮次起点、完成/中止归零、过期未知规则。默认跟踪最近开始运行的桌面对话，可在菜单指定，不合并 CLI/子代理。
- 保持最大宽度 630px；宽度至少 530px 高 38px，290–529px 高 76px，更窄高 114px，按 DPI 缩放。短标签仍区分消耗/输出，完整口径在提示内。
- `status.json` 保留 `tokensPerSecond` 原口径，新增 `outputTokensPerSecond`、`activeTurnOutputTokens`、`outputRateDefinition`。不得公开真实状态、日志或截图。

## Verification
1.0.10 构建与布局检查通过。修复前新增的额度检查复现 530px @96dpi 的 `5h 剩余 100%` 被裁切；修复后覆盖 10 种宽度、96/120/144/192 DPI，以及 100%、99.9%、9.9%、0% 和未知额度。额度文字尺寸、完整数值与标签重叠均检查通过，虚构预览已检查并更新 README 演示图。
本机原生窗口核验：五小时额度数据可用，五小时/周两个百分比均存在且实际字体尺寸可放入控件，窗口可见并在主窗上方，非 TopMost；自动启动指向 1.0.10。检查结果不含真实额度值。未重复运行计数自测或会模拟鼠标的整套交互测试；计数、菜单和层级代码本次未改，沿用 1.0.9 的对应证据。验证未发送模型推理请求。
发布前应运行公开目录隐私扫描，并核对 ZIP 条目与源码及已安装程序。AI_CONTEXT 仅随源码提交，不包含在便携包内。

## Relevant Paths
- `scripts/build.ps1`：构建。
- `bin/CodexUsageBar.exe --self-test <report>`：计数与规则检查。
- `bin/CodexUsageBar.exe --layout-test <report>`：离屏布局检查，不模拟鼠标。
- `scripts/check-publication.ps1`：公开目录隐私扫描。

## Context Metadata
- Last verified: 2026-10-17T17:23:17+08:00
- Git revision: e71f9fd（本次修复前基线）
- Working tree: 1.0.10 额度显示修复及发布文档待提交；不以基线 revision 代替完整状态。
- Context schema: 1
