# Codex Usage Bar

## Project
Windows Codex 桌面独立悬浮条，原生 C# WinForms，使用系统 .NET Framework 编译器构建。此目录是公开源码工作树；`src/UsageBar.cs` 为主实现，`src/TitleAnchor.cs` 仅查询帮助菜单几何。运行数据不放在此目录。

## Current State
本地 1.0.9 已安装并运行，自动启动已指向新版。用户于 2026-10-17 明确授权将 1.0.8/1.0.9 本地更新上传到已有公开仓库 Leeroysunfury/codex-usage-bar，目标发布标签为 v1.0.9。发布状态以远端 Release 为准。

- 保留五小时/周剩余额度、重置时间、本地自然日 Token、缓存量及缓存命中率。
- 1.0.8 左边缘跟随帮助菜单右边缘，普通所属窗口，不设系统置顶；窄窗口避让窗口控制按钮。
- 1.0.9 将原“均速”明确命名为“消耗均速”，新增“输出均速”。前者为本轮输入加输出 / 经过秒数，后者只用输出（含推理输出）/ 同一经过秒数。均包含工具等待，不是逐 Token 实时生成测速。模型调用结束后日志才更新用量。
- 两项速率共用对话选择、轮次起点、完成/中止归零、过期未知规则。默认跟踪最近开始运行的桌面对话，可在菜单指定，不合并 CLI/子代理。
- 保持最大宽度 630px；宽度至少 530px 高 38px，290–529px 高 76px，更窄高 114px，按 DPI 缩放。短标签仍区分消耗/输出，完整口径在提示内。
- `status.json` 保留 `tokensPerSecond` 原口径，新增 `outputTokensPerSecond`、`activeTurnOutputTokens`、`outputRateDefinition`。不得公开真实状态、日志或截图。

## Verification
1.0.9 构建及自测通过；输出测试覆盖大输入/缓存不计入输出、轮次重置、结束归零与过期未知。`--layout-test` 通过 10 种宽度、96/144 DPI 的两项速率/缓存文字尺寸及标签重叠检查。虚构预览已检查并更新 README 演示图。
本机原生窗口核验：两个速度标签均存在、窗口可见且位于主窗上方、非 TopMost、输出公式一致、无日志读取错误、自动启动指向新版。未运行会模拟鼠标的整套交互测试；菜单与层级逻辑本次未修改。验证未发送模型推理请求。

## Relevant Paths
- `scripts/build.ps1`：构建。
- `bin/CodexUsageBar.exe --self-test <report>`：计数与规则检查。
- `bin/CodexUsageBar.exe --layout-test <report>`：离屏布局检查，不模拟鼠标。
- `scripts/check-publication.ps1`：公开目录隐私扫描。

## Context Metadata
- Last verified: 2026-10-17T16:11:02+08:00
- Git revision: 88cd252（本次发布准备基线；1.0.9 发布代码以 v1.0.9 标签为准）
- Working tree: 此快照为 1.0.9 发布准备状态，含本地 1.0.8/1.0.9 更新；不以基线 revision 代替完整状态。
- Context schema: 1
