# DeskBox 项目记忆

- **项目**：DeskBox —— WinUI 3 / Windows 11 桌面整理工具（文件收纳、文件夹映射、待办、随记、音乐、天气、搜索）。
- **仓库**：D:\project\wingezi，GitHub `Tianyu199509/DeskBox`。个人开发者维护，GPL-3.0-only，暂不接受外部 PR。
- **技术栈**：net10.0-windows10.0.22621.0 + Windows App SDK 2.2；CommunityToolkit.Mvvm（MVVM）；Microsoft.Extensions.DependencyInjection（DI，ServiceRegistry 统一注册）；CommunityToolkit.WinUI；H.NotifyIcon.WinUI（托盘）。支持 x64/ARM64；Direct(Inno Setup) 与 Store(MSIX) 双渠道（`DeskBoxDistribution` 属性切换）。
- **架构核心**：WidgetShell(外壳) / WidgetWindowBase(窗口管理) / WidgetManager(协调器，partial 拆分) / WidgetRegistry+SessionalManager+ContentFactory / IWidgetContentProvider；ServiceRegistry 全量 Singleton 注册。
- **规模**：~114k LOC、331 个 .cs（282 源码 + 49 测试）、20 XAML。Services 112 / Views 51 / ViewModels 50 / Models 19。
- **注意**：仍有 God-class 残留（SettingsService 2420、WidgetShell 2759、SearchPopupWindow 2906 行）；深层 Win32 P/Invoke 对 Windows 更新敏感；单人维护。
- **数据位置**：设置 `%LocalAppData%\DeskBox\data`；默认收纳根 `%UserProfile%\DeskBox`。

## 开发流程约定（用户 2026-07-23 明确要求）
- **每次改完代码，自动执行：清理运行中的 DeskBox 进程 → 重新编译 → 启动改好的版本**，无需用户提醒。除非用户明确说"只改代码，不启动"。
- 构建命令：`cd /d/project/wingezi && dotnet build src/DeskBox/DeskBox.csproj -c Debug -v minimal -nologo`（增量很快）。
- 本机命令坑（必须照此执行，否则失败）：
  - 杀进程：先 `tasklist | grep -i deskbox` 拿 PID，再 `MSYS_NO_PATHCONV=1 taskkill /F /PID <pid>`。**注意：本环境 `//F` 不会被转成 `/F`，taskkill 会报"无效参数"；`taskkill /IM DeskBox.exe` 也可能因参数转义失败。务必用 `MSYS_NO_PATHCONV=1` + 单斜杠 + PID。**
  - 启动：`cmd /c start` 会被安全策略拦截；直接用 Bash 后台启动 exe：`"/d/project/wingezi/src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe"`（run_in_background=true）。
  - 构建报 MSB3027/MSB3021 "文件被 DeskBox 锁定" = 有运行中的实例占着 exe，杀掉再编。
- Debug 产物路径：`D:\project\wingezi\src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe`。
