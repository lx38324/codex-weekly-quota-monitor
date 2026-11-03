# Codex 周限额监控

简体中文 | [English](README.en.md)

Windows 托盘小程序：读取当前 Codex 周额度百分比，结合本机 rollout token 日志，估算 **Standard API 等价周额度（USD）**。

> 金额是统计估计，不是 OpenAI 账单或官方承诺。其他设备、云任务和缺失日志会影响结果。

## 快速安装

1. 从 [Releases](https://github.com/lx38324/codex-weekly-quota-monitor/releases) 下载最新版 `win-x64.zip` 并解压。
2. 在解压目录运行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1
```

程序会安装到 `%LOCALAPPDATA%\CodexWeeklyQuotaMonitor`，写入当前用户开机启动项并立即启动。重复执行同一命令即可安全升级；设置、状态和历史样本会保留。

## 怎么用

- 悬停托盘图标：查看 Standard API 等价额度、已用比例和样本数。
- 单击：不执行窗口动作，避免与双击冲突。
- 双击：打开合并后的概览、历史曲线和逐点明细工作台。
- 右键：立即查询、打开快速详情、进入概览与趋势/设置/诊断或退出。
- 设置 → 外观：选择自动跟随 Windows、简体中文或 English，以及系统/浅色/深色主题。
- 设置 → 估算：需要时启用官方 `>272K` 长上下文加价对比；默认关闭。
- 设置 → 模型价格：逐模型调整单价并保存；完整明细可立即离线重算，旧数据首次需扫描补录。等待期间保留标注为“旧价格”的曲线，底部显示进度。“恢复内置价格”只复位价格。

主窗口包含：

- **概览与趋势**：额度、已用/剩余、重置时间、样本、历史曲线、时间筛选和 token 明细。
- **设置**：轮询、数据源、回归方式、模型价格、语言、主题和开机启动。
- **设置 → 常规**：勾选开机启动后保存；可检查实际注册状态、修复开始菜单。开始菜单打开主窗口，登录自启只驻留托盘。
- **诊断**：复制不含对话正文和凭据的运行信息。
- **速度 → OTel · TTFT**：选择“请求 TTFT（日志事件）”，按模型和 Standard/Fast 比较延迟，明细显示事件直报或本地响应关联；无法确认的保持未知，不保存原始日志正文。
- **模型差异检查**：在请求 TTFT 中勾选“仅模型不一致”，查看 OTel / 本地关联模型与匹配依据；明确来源可单独筛选。记忆工作可能共享会话 ID，因此差异不直接等同于服务端切模，缺少来源标记的历史保持未确认。

## 两种金额口径

- **Standard API 等价周额度（默认）**：按公开 Standard API 常规价格估算。
- **官方 >272K 加价（可选）**：输入超过 272K 时，按官方规则对整次请求应用输入类 2×、输出 1.5×；仅在设置中显式启用后显示。

两种口径都会应用识别到的 ChatGPT Fast 额度倍率。断网或旧日志缺少层级时，仅在会话创建前已有明确 `config.toml` 层级证据时恢复并标注 `(config)`；其余未知模型、层级或区间继续失败关闭。

## 数据与隐私

额度、TPS、OTel 默认均为**最近 4 小时**，支持滚轮缩放、拖拽平移、双击复位；额度和 TPS 表格不随缩放裁剪。可选当天或自定义时间；当天起点在**设置 → 常规**调整。TPS 是端到端估计；**速度 → OTel · TTFT** 可查看按模型区分的首 token 延迟等遥测，需 Codex 实际向 `http://127.0.0.1:4318/v1/metrics` 输出 OTLP/HTTP binary 指标。

两张速度图都可筛选 Standard、Fast 或未知模式，卡片与明细同步。全部模式显示混合均值；未知表示数据缺少可靠模式标记，不会按当前配置推断。

速度 → TPS 的指标下拉可选**流式阶段估计 TPS**：按可信输出 item 起点排除前置等待，并显示同批原 TPS 对照、覆盖率及排除原因。只使用 ID 对齐且逐响应用量一致的记录；缺失不补值。仍包含传输和缓冲，不是服务端纯解码速度。旧速度缓存会保留并后台补读现存日志，无需重新采样。

设置 → 遥测可调整端口、保留期和容量（默认 256 MiB）。只保存数值摘要，不保存原始日志/trace；未收到服务端 token 间隔时显示采集证据与缺失原因，不以其他指标替代，也不跳回旧历史。详见[遥测口径](docs/算法与数据口径.md#otel-指标接收与保留)。

生图会消耗 Codex 额度，但目前本地日志未提供独立图像用量，本程序尚未计入其成本；有生图的区间可能低估额度，详见诊断页。

数据目录：`%LOCALAPPDATA%\CodexWeeklyQuotaMonitor`

程序保存设置、额度检查点、token 汇总、逐响应计价明细、文件游标、样本和运行诊断；不保存对话正文、工具输出正文、认证凭据或 App Server stderr 原文。

历史重建会在后台自动合并 `.codex\sessions` 与同级 `.codex\archived_sessions`，并在图表保留期内逐个额度窗口恢复具备完整百分比和 token 证据的旧样本。

## 更多说明

- [算法与数据口径](docs/算法与数据口径.md)
- [安装、升级与故障排查](docs/安装升级与故障排查.md)
- [变更记录](CHANGELOG.md)
- [贡献指南](CONTRIBUTING.md)
- [安全策略](SECURITY.md)

## 开发

需要 Windows、PowerShell 7 和 .NET 9 SDK：

```powershell
dotnet build .\CodexWeeklyQuotaMonitor.sln -c Release
dotnet .\WeeklyQuotaMonitor.BusinessTests\bin\Release\net9.0-windows\WeeklyQuotaMonitor.BusinessTests.dll
```

MIT License。
