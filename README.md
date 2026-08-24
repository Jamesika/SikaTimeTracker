# Sika Time Tracker

Sika Time Tracker 是一款面向个人使用的 Windows 本地时间追踪应用。它自动记录活跃窗口，使用可配置规则进行活动分类，并通过贡献热力图与每日时间轴展示时间投入。

当前状态：开发中。

产品范围与技术方案见 [docs/PROJECT_BRIEF.md](docs/PROJECT_BRIEF.md)。

## 目标平台与技术栈

- Windows 10/11
- C# / .NET 8
- WinUI 3 / Windows App SDK
- SQLite
- MVVM

## 隐私原则

所有活动数据默认仅保存在本机。软件不会记录键盘输入、截图或网页内容，也不会在未经用户明确操作的情况下上传窗口标题或进程信息。
