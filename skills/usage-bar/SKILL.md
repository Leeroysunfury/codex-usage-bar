---
name: usage-bar
description: 启动、停止或排查 Windows Codex 桌面额度条，显示五小时与周剩余额度、本日 Token、缓存命中量和命中率、活跃桌面对话本轮平均 Token/秒；不用于切换模型或提供商。
---

额度条程序在此插件的 `bin/CodexUsageBar.exe`。启动时使用 `scripts/start.ps1`；停止时使用 `scripts/stop.ps1`。程序有单实例锁，重复启动不会创建重复条。

这是贴在 Codex 桌面窗口顶部中央的独立悬浮条，并非修改桌面原生状态栏。窄窗口会缩短标签和数字，悬停能看完整数值。只作为 Codex 的普通所属窗口，不设系统置顶，正常定位刷新保留层级，让输入法候选弹窗保持在上方。窗口最小化或用户切到其他应用时自动隐藏。

1.0.5 修复额度条已经自动启动、状态为 visible=true，却被 Codex 主窗口盖住的问题。每次定位检查实际窗口顺序；仅在额度条落到主窗口后面时插回主窗口前方，保留原先在主窗口上方的菜单和候选窗，不持续提升，不设系统置顶。排查启动应同时看跟随器进程、状态更新时间和 status.json 的 barWindow、aboveApp；仅凭 visible=true 或 DrawToBitmap 预览不能证明屏幕上可见。

菜单打开期间临时检测菜单外点击，并立即关闭菜单，原点击照常传递；菜单关闭后卸载检测，不收集鼠标历史或键盘输入。

两项额度来自官方 app-server `account/rateLimits/read`，显示 `100-usedPercent`，只匹配 300 与 10080 分钟窗口。各自下方常驻显示 `resetsAt` 对应的系统本地时间：五小时当天为 HH:mm，跨日带月日；周窗口始终带月日。窄窗口缩短标签，悬停查看完整日期。缺失或无效时间显示未知，不推算重置时间。每 45 秒刷新，不发送模型请求、不消耗额度重置。

1.0.4 修复登录启动的跟随器没有继承 Codex 独立代理环境，导致每次启动限额超时的问题。额度后台启动前只读识别当前 Codex 主进程的 `--proxy-server` 与 `--proxy-bypass-list` 参数，并仅写入额度查询子进程的代理环境；不会修改系统代理、TUN、Codex 启动器或全局环境。明确直连参数会清除查询子进程的遗留代理；没有应用代理参数时保留原环境或使用已启用的系统代理。失败后按 5、10、20、30 秒重试并重新检测网络设置；成功后恢复 45 秒间隔。后台提前退出会立即报错，不再等到超时。

排查重复启动故障时先看 `quotaNetworkSource`、`quotaProxyEndpoint`、`quotaQueryPhase` 和 `quotaConsecutiveFailures`。代理诊断只存不含用户名密码的协议、主机、端口，不存完整命令行或认证。使用 `--quota-check .\test-results\quota-check.json` 可只读验证两项额度；要模拟登录启动，清除该测试子进程的 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY，而非更改用户或系统环境。不能仅凭从 Codex 终端手动启动成功就认定自动启动已修复。

本日 Token 来自 `CODEX_HOME/sessions` 与 `archived_sessions` 的本机记录，按 Windows 当前时区的自然日累计输入和输出 Token 的增量；缓存输入已在输入中，推理已在输出中，不能重复相加。它不覆盖其他设备和未下载的云端记录。官方账号 Token 日接口没有数据时，不能将其空结果说成零消耗。

1.0.7 时间统一跟随 Windows 当前时区。重置时间按对应日期应用本地偏移与夏令时；本日 Token 与缓存统计按当地零点切换，运行中切换系统时区会重新扫描同一日期的计数。status.json 的 timeZone 为实际 Windows 时区 ID，timeZoneDisplayName 为显示名称；展示的完整重置时间附本地 UTC 偏移。不会修改系统时区。

1.0.6 常驻显示本日缓存命中量和命中率，分别位于本日 Token 和均速下方。命中量 = 本日 cached_input_tokens 增量总和；命中率 = 缓存输入 Token / 全部输入 Token（含缓存），不包含输出，不是按请求次数统计的命中率或费用折扣。输入为零时显示未知。范围与本日 Token 一致，含本机桌面与 CLI 会话，供应商混用时不分开统计；供应商未报告缓存用量时不能保证反映真实缓存。悬停查看完整数值和口径。宽窗口保持 38px 高，极窄窗口分四行、78px 高，按 DPI 缩放。status.json 的 cachedInputTokens 为命中量，cacheHitPercent 为百分比（无输入为 null），cacheHitDefinition 说明分母。不新增模型调用或网络请求。

第四项“均速”每秒刷新，计算选中桌面对话本轮已经记录的输入和输出 Token / 本轮经过秒数，包含缓存输入及工具等待时间。日志在每次模型调用结束后更新用量；不要说成逐 Token 实时生成速度或费用速率。默认跟踪最近开始运行的桌面对话，不能据此断言是当前界面正在查看的对话。多对话并行时可从“速率跟踪对话”菜单指定，选择保存到状态目录 `rate-preferences.json`。不合并 CLI 或子代理。完成/中止显示 0，记录超过 15 分钟未更新显示未知。新会话发现最多约 5 秒。

排查时读取 `%LOCALAPPDATA%/CodexUsageBar/status.json`，它仅含额度、重置时间、Token 统计、跟踪会话/轮次 ID、速率口径、窗口位置和更新时间，不含认证或对话文本。`fiveHourResetsAt`、`weeklyResetsAt` 是官方 Unix 秒时间戳，未知为 null；`fiveHourResetTime`、`weeklyResetTime` 为完整系统本地时间，`fiveHourResetText`、`weeklyResetText` 为当前显示文字。`tokensPerSecond` 是本轮平均值，`activeThreadId` 标识跟踪对象，`rateMode` 说明自动或指定。后台程序自己复用 Codex 当前认证，勿读取或输出凭据。

菜单“随 Codex 启动”启用当前用户 Windows 登录启动项：同一个程序使用 `--watch-codex` 参数运行轻量跟随器，检测 Codex 可见或最小化的主窗口。出现主窗口后启动额度条；最后一个 Codex 窗口关闭后停止额度条及其 RPC，不在 Codex 关闭时扫描日志或查询额度。最小化只隐藏额度条，不结束本次跟随。跟随器不立即重启用户手动退出的额度条，下次 Codex 启动时再启动。

使用 `bin/CodexUsageBar.exe --enable-autostart` 启用并立即启动跟随器；`--disable-autostart` 删除启动项，现有跟随器会自行退出，不关闭当前额度条。启动项在 `HKCU/Software/Microsoft/Windows/CurrentVersion/Run` 的 `CodexUsageBar`，不要改其他启动项。新版安装后需将已启用的启动项指向新版程序。`follower-status.json` 可检查跟随器 PID、Codex 是否存在和更新时间。它是本机应用跟随器，不是 heartbeat 或 cron automation。

生命周期启动 hook 只在跟随开关开启时补充启动，仍需通过 Codex 的正常信任流程；不要伪造 hook 信任记录。跟随器本身不依赖 hook 的信任状态。

验证代码使用 `scripts/build.ps1`，然后 `bin/CodexUsageBar.exe --self-test .\test-results\self-test.json`。数据查询失败应显示最近结果并标明离线；缺少窗口值应显示未知，不能填造百分比。



