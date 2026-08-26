# 贡献指南

感谢你改进 Codex 周限额监控。English contributors are welcome; issues and pull requests may be written in English or Chinese.

## 开发环境

- Windows 10/11 x64
- PowerShell 7
- .NET 9 SDK
- 可选：已登录的 Codex Desktop，用于本机协议实测

## 修改原则

1. 不把 API 组织账单接口解释为 Codex 订阅额度；界面金额必须继续标为“Standard API 等价估计”。
2. 不读取、保存或上传对话正文、凭据及 App Server stderr 原文。
3. 未知模型、未知服务层级、畸形区间和无法归因的额度变化必须失败关闭，不能猜测价格。
4. 安装升级只能停止可执行路径与安装目标完全相同的进程，并保留现有设置、状态、样本和开机启动能力。
5. 新增函数应有说明其上下文、作用、输入和输出的注释。
6. 优先添加面向业务行为的测试，避免依赖截图像素或实现细节的脆弱测试。

## 本地验证

```powershell
dotnet build .\WeeklyQuotaMonitor.BusinessTests\WeeklyQuotaMonitor.BusinessTests.csproj -c Release
dotnet .\WeeklyQuotaMonitor.BusinessTests\bin\Release\net9.0-windows\WeeklyQuotaMonitor.BusinessTests.dll
```

生成自包含发布包：

```powershell
pwsh -File .\build-release.ps1 -OutputDirectory .\publish
```

## Pull Request

- 一个 PR 聚焦一个清晰目标。
- 说明用户可见变化、数据口径影响、测试证据和未覆盖边界。
- 不提交 `bin/obj`、测试输出、发布产物、本地截图、日志、设置或状态文件。
- 若修改计价或额度倍率，请附公开来源、核对日期和对应业务测试。
- 若修改 UI，请至少检查中英文、浅色/深色和 100%～200% DPI。
