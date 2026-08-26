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

- 悬停托盘图标：查看双口径额度、已用比例和样本数。
- 单击：不执行窗口动作，避免与双击冲突。
- 双击：打开历史曲线和逐点明细。
- 右键：立即查询、打开快速详情、进入总览/设置/诊断或退出。
- 设置 → 外观：选择自动跟随 Windows、简体中文或 English，以及系统/浅色/深色主题。

主窗口包含：

- **总览**：额度、已用/剩余、重置时间、样本和连接状态。
- **历史与图表**：双口径样本、回归曲线、时间筛选和 token 明细。
- **设置**：轮询、数据源、回归方式、语言、主题和开机启动。
- **诊断**：复制不含对话正文和凭据的运行信息。

## 两种金额口径

- **无长上下文加价**：按公开 Standard API 常规价格估算。
- **官方 >272K 加价**：输入超过 272K 时，按官方规则对整次请求应用输入类 2×、输出 1.5×。

两种口径都会应用识别到的 ChatGPT Fast 额度倍率。未知模型、未知服务层级或无法归因的区间不会猜测金额。

## 数据与隐私

数据目录：`%LOCALAPPDATA%\CodexWeeklyQuotaMonitor`

程序保存设置、额度检查点、token 汇总、文件游标、样本和运行诊断；不保存对话正文、工具输出正文、认证凭据或 App Server stderr 原文。

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
