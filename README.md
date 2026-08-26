# Codex 周限额监控

简体中文 | [English](README.en.md)

这是一个 Windows 右下角托盘常驻程序。它读取当前 ChatGPT 登录态的 Codex 周额度百分比，在百分比变化时增量读取本机 Codex rollout 日志，并反推统一口径的“Standard API 等价周额度美元金额”。

> 重要：显示金额是统计估计，不是 OpenAI 订阅账单、退款价值或官方承诺的周额度金额。其他电脑、Web、云任务、图片、工具额外收费和缺失日志都会影响估计。

## v1.3.0 界面与开源版本

- 主窗口改为“总览、历史与图表、设置、诊断”四个一级页签，托盘菜单可直达各页。
- 新增指标卡、时间范围筛选、深浅主题、图表悬停提示与十字线，并保留四类曲线独立显隐。
- 默认语言为“自动（跟随 Windows）”：启动时读取当前用户的 Windows 显示语言；中文系统显示简体中文，其他系统显示英文。
- 设置中的“简体中文”或“English”会持久化并覆盖系统语言；切回“自动”后恢复系统检测。
- 修复首次从托盘直接打开历史页时的 WinForms 句柄创建卡死，并验证 100%、125%、150%、200% 布局缩放。
- 公开仓库提供 MIT License、49 个业务测试、Windows CI、版本发布工作流和贡献/安全说明。

## 安装

1. 解压发布包到普通目录。
2. 在 PowerShell 7 中执行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1
```

如果希望只更新文件、随后自行启动，可执行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1 -NoStart
```

安装脚本会把单文件程序复制到：

```text
%LOCALAPPDATA%\CodexWeeklyQuotaMonitor\Codex周限额监控.exe
```

然后写入当前用户开机启动项并启动程序，不需要管理员权限。

重复执行安装脚本即为原位升级。脚本会先把新 EXE 复制为暂存文件，只停止安装目录中路径完全相同的旧实例，最多等待 15 秒后再替换；`settings.json`、`state.json` 和历史采样不会被删除。若旧实例无法退出或文件仍被其他程序占用，安装会立即终止，不会在复制失败后继续写启动项或伪装成安装成功。

安装包中的 `codex-path.txt` 默认保存稳定定位符 `codex-desktop://current`。监控每次启动都会从当前用户 AppModel 包注册信息解析现有的 `PackageRootFolder\app\resources\codex.exe`，再按包版本同步到数据目录中的用户可执行副本，因此不依赖 Codex Desktop 是否已经启动，不会绑定到 WindowsApps 中带版本号的旧目录，也不受包外进程直接执行 WindowsApps 文件时的 ACL 限制。已有的 `OpenAI.Codex_旧版本` 配置会在首次启动时自动迁移为稳定定位符。

如果你不使用 Codex Desktop，可在右键“设置”中选择其他 `codex.exe`，或安装时显式传入：

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1 -CodexExecutable 'C:\新的路径\codex.exe'
```

## 托盘交互

- 鼠标悬停：显示当前 Standard API 等价周限额、已用百分比和有效采样点数。
- 左键单击：区分显示当前权威已用比例、最新有效金额点、待归因额度点，并显示当前/归档样本数、重置时间、连接状态和日志完整性统计。
- 左键双击：打开统一主窗口的“图表”标签页，显示反推采样点、回归/聚合曲线，以及逐点模型、层级、额度倍率和价格版本明细。
- 右键：立即查询、查看详情、直达“图表”或“设置”标签页、打开数据目录或退出。

图表顶部显式显示当前回归/聚合算出的基础口径和官方 >272K 口径周额度、参与计算点数及样本过滤上限。基础样本折线、基础回归曲线、官方长样本折线和官方长回归曲线可以分别勾选；回归曲线使用粗虚线与原始样本折线区分。旧版默认的 `$1000` 有效样本金额上限会一次性迁移为 `$10000`，避免 Pro 账号的正常两千美元级估计被全部过滤；用户仍可在“设置”标签页自行调整。

## 采样方式

程序启动独立的官方 `codex app-server --listen stdio://`：

1. 首次调用 `account/rateLimits/read` 建立周额度百分比基线。
2. 监听 `account/rateLimits/updated` 通知。
3. 由于独立 App Server 不保证收到其他 Codex 进程的全部变化，仍按设置的低频间隔查询额度。
4. 百分比没有变化时，不做普通增量扫描；若先前未知层级所在文件被补写，或额度点已经出现但日志尚未落盘，则由 `FileSystemWatcher` 标记变化，在下一次额度查询时只按需触发历史重放。
5. 百分比变化时，才按持久化字节游标读取新增 JSONL 行。
6. 以请求边界关联本地模型 `response_item` 和后续逐响应 `last_token_usage`：`item_completed`、agent 状态、工具输出、patch/MCP 完成事件以及 `info=null` 心跳不会中断关联；`turn_context`、`thread_settings_applied` 和新 `task_started` 会清除上一请求的未完成关联；每个真实用量只消费一次，孤立遥测和重复累计状态不会重复计价。
7. 首次安装、重放算法或价格版本变化、权威百分比首次变化，以及待层级/待日志文件发生变化时，会从当前周仍有写入的 rollout 自动重读历史，不修改实时增量游标；普通 UI 或回归设置变化不会重复重放。
8. App Server 超时或退出后解除轮询锁，按 15～300 秒有上限指数退避自动重建连接。
9. 畸形完整 JSONL 行会隔离计数；半行会等待换行；文件截短或替换会重建游标；已删除文件的游标会清理。

stdio 协议使用逐行 JSON，并明确采用不带 BOM 的 UTF-8；新版 App Server 不接受首条 `initialize` 前的 UTF-8 BOM。

首次取得 App Server 权威周窗口后，程序会自动重建该窗口内仍保存在本机的历史样本，因此不必从零等待新的百分比变化。每个权威百分比会持久化首次观察时间，后续响应不会反向污染已经结束的旧区间。只有当前周日志确实缺失，或某个区间包含无法确定模型/服务层级的响应时，该区间才保持未归因；界面会明确列出待归因百分比。

历史重放合并 rollout `token_count.payload.rate_limits` 中的候选点与程序直接保存的 App Server 权威首次观察点。同一账号的并发 Desktop、VS Code 和侧栏任务会产生大量重复或延迟快照，部分内部任务还会写入 resetsAt 持续滑动的 0% 流；程序只接受 10080 分钟周窗口、与当前 App Server 权威 resetsAt 相差不超过 1 分钟且百分比单调上升的首次观察点，其余候选作为排除/去重诊断。若一个文件首批响应早于 `thread_settings_applied`，只有该文件后续所有明确层级证据完全一致时才反向回填；后续补写唯一层级会自动触发恢复，文件内出现 Standard/Fast 切换或完全没有层级证据时仍保持未知并失败关闭。

## 反推公式

每个百分比变化区间并行计算两套 Standard API 等价成本：

```text
无长上下文加价成本
= 短上下文 Standard 单价计算的 token 成本 × ChatGPT 额度倍率

官方 >272K 加价成本
= 输入不超过 272K 时的无长上下文加价成本
  或输入超过 272K 时按整次请求输入类 token 2x、输出 token 1.5x
  再乘 ChatGPT 额度倍率
```

再计算：

```text
各口径反推周限额 USD = 对应区间成本 × 100 / 周额度已用百分比增量
```

“无长上下文加价”用于观察 Codex 订阅不采用 API 长上下文加价时的估计；“官方 >272K 加价”严格使用公开 API 规则。以 GPT-5.6 Sol 为例，OpenAI Docs 明确写明输入超过 272K 后，整次请求输入为 2x、输出为 1.5x；本程序不会把它简化为整次金额统一 2 倍。两套口径都会另外应用 ChatGPT credit multiplier：明确记录为 `standard`、`default`、`auto` 时为 1；`fast`/`priority` 下 GPT-5.6 和 GPT-5.5 为 2.5 倍，GPT-5.4 为 2 倍。程序不会用 API Fast/Priority 价格替代 ChatGPT 额度倍率；服务层级缺失、未知，或官方未定义的模型与 Fast 组合都会明确标为未定价。

新版 Codex rollout 通过 `event_msg.payload.type = "thread_settings_applied"` 下的 `payload.thread_settings.service_tier` 记录任务层级：`default` 对应 Standard，`priority` 对应 Fast。旧版 `turn_context.payload.service_tier` 仍兼容；新版 `turn_context` 缺少该字段时会保留最近一次明确设置，不再静默覆盖为 Standard。从未出现明确设置事件的响应按未知层级失败关闭。模型响应出现时会同时快照当时的模型与层级，后续计价不使用可能已经变化的当前设置。

`codex-auto-review` 是唯一显式配置的内部模型代理：按用户指定策略使用 GPT-5.6 Luna 的 Standard API 价格分别计算无长上下文加价和官方长上下文加价，并应用对应服务层级倍率，同时在样本中保留原始模型名 `codex-auto-review`。这是本监控程序的估算策略，不代表 OpenAI 官方声明 Auto-review 的底层模型就是 GPT-5.6 Luna。

程序内置价格核对日期为 2026-08-24，来源：[OpenAI API Pricing](https://developers.openai.com/api/docs/pricing) 和 [GPT-5.6 Sol pricing notes](https://developers.openai.com/api/docs/models/gpt-5.6-sol)；额度倍率来源：[Codex Fast mode](https://learn.chatgpt.com/docs/agent-configuration/speed#fast-mode)。当前内置模型包括 GPT-5.6 Sol、Terra、Luna、GPT-5.5、GPT-5.4、GPT-5.4 mini、GPT-5.3 Codex 和 GPT-5.2，并为 `codex-auto-review` 配置 GPT-5.6 Luna 代理计价。

每个新样本都会同时保存 `standard-api-equivalent-no-long-context-surcharge-v1` 和 `standard-api-equivalent-official-long-context-surcharge-v1` 两套金额、价格版本、实际服务层级、额度倍率和 `live`/`historical-replay` 来源。托盘悬停、单击详情、双击图表、逐点表格及回归曲线都会并列显示两套结果，表格额外标注“实时”或“历史重放”。升级前的旧口径样本继续保留在 `state.json`，但会作为归档样本排除在新图表和回归之外，避免跨口径混算。

## 回归和聚合方式

- 全局线性回归：对最近指定数量的有效样本做普通最小二乘拟合。
- 时间窗口分段回归：每个时间点只使用其前方指定小时窗口内的样本拟合。
- 高斯时间聚合：按时间距离使用高斯核加权，更适合观察稳定中心和缓慢漂移。

设置中可调整：

- 轮询间隔，最低 10 秒；默认 60 秒。
- 指定额度桶 ID；留空时从满足最短窗口的候选中选择最长窗口。
- 自动选择的最短窗口；默认 1440 分钟，用于排除 5 小时额度桶。
- 形成样本所需的最小百分比变化；默认 0.1%。
- 初始活动日志上下文回看小时数。
- 图表样本保留天数。
- 线性回归样本数、分段窗口、高斯带宽和有效样本金额上限。
- 是否随 Windows 登录自动启动。

## 数据与隐私

数据目录：

```text
%LOCALAPPDATA%\CodexWeeklyQuotaMonitor
```

其中：

- `settings.json`：用户设置。
- `state.json`：额度基线、权威百分比首次观察检查点、待恢复文件长度、带文件创建时间的日志字节游标、金额口径元数据、数据质量统计和反推样本。
- `runtime.log`：协议状态和程序运行信息。
- `codex-runtime\codex.exe` 与 `package-version.txt`：从当前已安装 Codex Desktop 包同步的本地 CLI 运行副本；仅在包版本或文件长度变化时更新，当前版本约占 277 MiB。

程序不保存对话正文、工具输出正文或认证凭据。App Server 的 stderr 原文也不会写入运行日志。

如果稳定定位符暂时找不到可用 Codex 包、本地副本同步失败、请求超时或 App Server 退出，托盘程序会继续运行并在详情中显示原因；后续轮询会自动解析当前 Codex Desktop 版本并重建连接。修改协议或 Sessions 路径也会在下一轮自动重连，无需退出托盘。App Server 初始化仍遵循[官方协议](https://learn.chatgpt.com/docs/app-server)：先发送 `initialize`，再发送 `initialized`。

## 数据质量边界

- 本机监控无法覆盖其他设备、云任务和已经删除的 rollout。
- 历史重放只使用当前周仍存在且窗口内有写入的本机 rollout；其他设备或云任务导致的百分比跳变没有本机响应时会标记为未归因。
- 额度变化但没有本机可归因响应时，该区间记为“未归因变化”，不生成误导样本。
- 区间包含未公开定价模型或未知服务层级时，该区间不生成样本。
- 同一额度桶通常只有在新 `resetsAt` 晚于旧值、且采样时间已经跨过旧 `resetsAt` 时才确认重置；若百分比明确归零、新旧重置承诺至少相差 1 小时，且新窗口起点位于当前观察时刻 5 分钟内，也会确认服务端提前重置。确认后仅使用新窗口起点后的 token，无法确认切分完整性时保守丢弃区间。
- 服务端刚重置时，`resetsAt - windowDurationMins` 可能因时钟或响应延迟比采样时间晚数十秒；程序会把 5 分钟内的未来起点钳制到首次观察时刻，超过 5 分钟则作为无效窗口数据报告而不进入历史重放。若旧版已保存首次 0% 权威检查点但尚未更新重置承诺，升级重启后会利用该检查点自动恢复。普通 `resetsAt` 漂移不会重建零百分比基线；百分比下降但未满足重置证据时仍按疑似服务端修正处理，额度桶切换时不把旧桶 pending 成本带入新桶。
- 区间包含畸形日志或文件轮转时不生成金额样本，并在详情中累计显示诊断数。
- 旧金额定义或旧价格版本样本只保留归档，不参与当前回归。
- 样本量较少时，悬停和详情会显示“等待有效样本”，不会伪造初始金额。

## 本机验证结果

- Windows 10 / .NET 9 自包含单文件发布；窗口采用 PerMonitorV2，并已在本机 125% 缩放下完成详情内容可见性实窗验证。
- Codex Desktop 包定位通过当前用户 AppModel 注册信息完成，不依赖 WindowsApps 版本号或 Codex Desktop 进程是否已经启动。
- 当前 CLI 实测要求 stdin 使用 UTF-8 无 BOM；带 BOM 时 `initialize` 不返回，无 BOM 时可正常完成握手。
- App Server 初始化和 `account/rateLimits/read` 已对当前 ChatGPT 登录态实测成功。
- 当前返回同时包含普通 Codex 周窗口和 GPT-5.3-Codex-Spark 独立额度桶，自动选择逻辑会优先普通 `codex` 周窗口。
- 49 个业务测试覆盖自动/手动语言、schema 2→3、系统/浅/深主题、100%～200% DPI、四页直达、权威/有效/待归因额度点区分、四类系列显隐、图外当前回归额度、旧回归上限迁移、详情窗关闭后重新打开、曲线与表格本地时间对齐、无长上下文加价与官方 >272K 加价双口径、长上下文与 Fast 倍率叠加、Auto-review 到 Luna 的端到端代理计价、GPT-5.6/5.4 Fast 倍率、新版层级事件切换、请求边界跨工具事件关联、`info=null` 心跳、响应关联状态迁移、历史周额度字段提取、稳定 resetsAt 聚类、滑动零值排除、单调时间线、历史样本幂等合入、迟到唯一层级追溯恢复、完全未知/混合层级失败关闭、首次权威观察边界、后续响应隔离、重置后未来 38 秒窗口起点、提前归零重置、旧版 0% 检查点迁移恢复、周窗口、普通重置时刻漂移、跨旧时刻确认重置、未切分重置、百分比修正、桶切换、畸形区间拒绝、旧价格隔离、畸形/半行、截断恢复和游标清理。

本机 `~/.codex/config.toml` 当前还会产生一条独立警告：`service_tier = "default"` 不再是该 App Server 版本接受的显式值（只接受 `fast` 或 `flex`）。App Server 会使用默认配置继续工作，且额度查询已经成功；本程序不会修改你的 Codex 配置。
