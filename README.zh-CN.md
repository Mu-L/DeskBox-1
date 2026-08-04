# DeskBox

**本地优先的 Windows 11 桌面整理工具：用格子管理文件、文件夹、待办、随记、搜索、天气和音乐。**

简体中文 | [English](README.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![最新版本](https://img.shields.io/badge/release-1.3.6-2563EB.svg)](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.3.6)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4.svg)](#环境要求)
[![x64 and ARM64](https://img.shields.io/badge/architecture-x64%20%7C%20ARM64-5C2D91.svg)](#下载)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

![DeskBox Windows 11 桌面整理工具，包含文件、待办、搜索、天气和音乐格子](docs/images/brand/readme-hero-1-3-4-option-c-mica.png)

DeskBox 基于 C#、WinUI 3 和 Windows App SDK 构建，在原生 Windows 桌面上增加一层轻量格子，但不会替换资源管理器，也不会改变文件原本的使用方式。你可以创建真实文件夹支撑的文件格子、映射已有文件夹、记录待办与随记、搜索电脑内容、查看天气或控制当前音乐。格子既能保持展开，也能收起成胶囊，并可通过托盘或全局快捷键临时唤起。

## DeskBox 概览

| | |
| --- | --- |
| **支持平台** | Windows 11，x64 与 ARM64 |
| **技术栈** | C#、WinUI 3、.NET 10、Windows App SDK 2.2 |
| **数据方式** | 本地优先；文件、随记、待办、设置与布局保存在电脑上 |
| **界面语言** | 简体中文、English、日本語、Deutsch、Português do Brasil |
| **开源协议** | GPL-3.0-only |

## 下载

在 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.3.6) 下载 DeskBox 1.3.6：

- [DeskBox 1.3.6 x64 安装包](https://github.com/Tianyu199509/DeskBox/releases/download/v1.3.6/DeskBox_Setup_1.3.6_x64.exe)——适用于大多数 Intel 和 AMD 电脑。
- [DeskBox 1.3.6 ARM64 安装包](https://github.com/Tianyu199509/DeskBox/releases/download/v1.3.6/DeskBox_Setup_1.3.6_arm64.exe)——适用于骁龙、Surface Pro X 等 Windows on ARM 电脑。

安装包采用框架依赖方式，不会把一整套运行时打进安装包。安装程序会检测对应架构的 .NET 10 Runtime 和 Windows App Runtime 2.2：电脑里已有兼容版本就直接复用，缺少时才联网下载并安装。

> 只有缺少运行时依赖时，安装阶段才需要联网下载。Windows 可能为依赖安装弹出管理员权限确认；DeskBox 本体默认安装到当前用户目录。

## 核心功能

### 文件整理与文件夹格子

- 创建由普通文件夹支撑的收纳格子，或把已有文件夹直接映射到桌面，不改变原文件位置。
- 支持图标/列表布局、标题样式、排序、自动叠放、图标大小和显示密度设置。
- 支持拖入、拖出、复制、剪切、粘贴、重命名、删除、打开、在资源管理器中显示，以及通过 Windows Shell 打开快捷方式。
- 可从资源管理器、微信或浏览器拖入内容；浏览器中的远程图片与文件链接可以下载后导入。
- 已运行 [QuickLook](https://github.com/QL-Win/QuickLook) 时，可在格子中按空格预览支持的文件。

### 待办与随记

- 待办支持截止日期、提醒、重复、颜色标记、多附件、筛选与批量操作。
- 随记支持文本、链接、图片和文件，提供固定、纸张样式、多附件与专注编辑。
- 附件可以关联原文件，也可以复制到 DeskBox 管理的数据目录。

### 桌面搜索

- 在一个搜索弹窗或搜索格子中查找文件、文件夹、应用、设置与 DeskBox 内容。
- 可组合使用 Windows 索引与可选的本地 USN 文件索引。
- 支持结果筛选、数量设置、历史、收藏和独立全局快捷键。
- 空闲时预热搜索弹窗外壳，点击搜索格子后优先显示并聚焦窗口，推荐内容、图标与已卸载的本地索引在后台恢复。
- 搜索空闲后可卸载常驻的本地索引，同时保留轻量文件监听记录变化；关闭搜索功能则会释放完整搜索运行资源。

### 天气与音乐

- 天气格子可展示实时天气、逐小时和多日预报，默认使用 MSN 天气，失败时自动回退到 Open-Meteo。
- 天气提供跟随明暗模式的标准皮肤和按天气变化的高级皮肤，日/周视图会随格子尺寸响应式调整。
- 音乐格子通过 Windows 媒体会话控制当前播放器，支持播放模式、进度和系统音量。
- 音乐提供封面、控制、唱片与紧凑布局，并可选择跟随专辑封面的氛围色。

### 胶囊模式与原生交互

- 格子可收起为智能胶囊，支持点击切换或鼠标悬停自动展开。
- 收起后可显示关键信息、简要摘要或仅图标与标题；待办和随记可隐藏敏感正文。
- 胶囊可以独立摆放，也可以组合成可整体移动、可排序的胶囊栏。
- 可通过托盘和自定义全局快捷键显示、隐藏或临时唤起全部格子。
- 支持云母/亚克力材质、透明度、边框、DWM 圆角、动画、标题栏、图标与文字大小。

## 1.3.6 更新亮点

- **格子组切换保持流畅**：文件页会在 Tab 切换之间复用；Ctrl+Tab 按住时只当作一次手势，快速重复触发会节流，中断的同成员切换也可自动恢复，无需手动点击 Tab。
- **格子组层级正确**：启动、合并、拆分、解散和成员切换后，格子组都会保留与独立格子一致的临时前景层级。
- **跨应用拖入一致可用**：浏览器虚拟文件拖到独立格子时会保留扩展名；微信及其他原生拖放源可以拖入格子组内的文件格子。
- **格子组文件页首次加载完整**：图标加载跟随真实窗口可见状态，启动后会自动刷新，不再长期停在占位。加载中的占位改为柔和的正方形圆角卡片。
- **格子组交互补全**：组内选中文件按空格可调用 QuickLook；胶囊/紧凑模式切换尺寸时会延后较重的表面工作，减少频繁操作的卡顿。
- **桌面整理后续优化**：目标标签会展示实际接收文件的已有格子；可见桌面拥挤或格子隐藏时也会继续寻找位置完成整理。
- **轻量 Direct 安装包**：x64 和 ARM64 保持框架依赖模式，复用已有 .NET 10 与 Windows App Runtime 2.2，仅在缺少对应架构运行时时下载。

## 1.3.5 更新亮点

- **桌面整理是 1.3.5 新增能力**：使用响应式卡片预览要移动的文件、目标位置、不会移动的项目和最终收纳位置；1.3.4 尚未提供这项功能。
- **按卡片控制整理**：每个卡片可以单独勾选，并选择新建文件夹或已有格子；面板会解释收纳位置并列出具体不会移动的项目。
- **自动整理是 1.3.5 新增能力**：下载增长、临时/压缩包处理、解压和同路径替换会等待稳定终态；大文件限制为 100 MB，不完整 baseline 不会提交。
- **格子组是 1.3.5 新增能力**：文件格子可以合并、标题滚轮切换、长按标题拖出或解散，并使用更安全的层级和嵌套路径规则。
- **文件格子与映射文件夹保持同步**：目录离线或暂时不可用时保留上次快照，刷新不再破坏手动排序，watcher 代次也会隔离旧路径事件。
- **搜索索引更可靠**：部分扫描、容量受限、离线或权限不足时不再误删已有结果；USN journal 支持增量应用变化，并提供安全回退与恢复诊断。
- **拖动与排序更可控**：跨屏排序使用呼吸式插入线，拖动过程中不改动列表；独立格子和组内格子共用位置计算，并区分拖到文件夹项目与空白区域的行为。
- **Windows 10 与生命周期恢复**：次级窗口背景回退、动画可访问性、休眠/解锁/RDP 恢复、Explorer 重启处理和启动层级统一得到补齐，同时保留 Windows 11 增强效果。
- **更新日志与诊断**：设置内可在独立窗口查看最新双语 Markdown 更新内容，可靠性诊断可以看到 watcher、索引和恢复状态。
- **多语言与细节修复**：五种语言资源同步，文件图标、拖动反馈和窗口细节继续优化，并增加新的稳定性自动化测试。

## 1.3.4 更新亮点

- **资源占用更可控**：关闭功能格子和设置窗口后释放视觉树；音乐与胶囊定时器会正确解绑；缓存增加明确上限；即使格子仍显示在桌面上，满足安全条件时也可执行空闲内存整理。
- **搜索窗口优先出现**：空闲时预热弹窗外壳；连续点击搜索格子只会打开或聚焦，不再反向关闭；窗口先完成显示，推荐内容、图标和已卸载索引随后恢复；缺少真实图标的结果也会保留可识别的占位图标。
- **搜索索引空闲卸载**：搜索连续五分钟未使用时，可从内存卸载体积较大的自定义索引，同时保留轻量监听记录文件变化；唤起弹窗时立即开始恢复，无需等到输入文字。
- **天气视觉重构**：重新设计响应式日/周布局、标准/高级皮肤、不同天气下的文字对比度、日出日落圆弧和预报信息层级；新用户与重置默认使用高级皮肤，并移除持续运行的装饰性天气动效。
- **音乐稳定性与效率**：切歌时等待完整媒体信息，减少歌曲名和歌手名闪烁；封面解码去重并限制资源；隐藏或收起时停止跑马灯和唱片旋转；播放键与辅助控制尺寸更加统一。
- **胶囊交互更可靠**：空闲时预热展开布局；首次启动或唤醒后无需点击即可悬停展开；相邻胶囊不会隔着当前展开格子误触并抢占层级；点击切换模式下点击标题栏立即收起。
- **修复窗口层级**：托盘/F7 临时唤起结束后，鼠标悬停展开胶囊不会再越过当前前台应用并持续置顶。
- **图标更清晰**：大图标使用更高分辨率的系统图标源，小图标改进缩小采样，叠放图标同步跟随尺寸设置。
- **稳定性与菜单补全**：快捷方式改用 Shell 兼容路径启动；待办/随记空白区域显示标题栏菜单；文件与映射文件夹内容菜单增加标题样式、展开方式和带二级确认的“关闭格子”；仅在可用时显示“粘贴”。
- **热键可靠性**：移除低级键盘钩子，改进远程桌面修饰键恢复，搜索快捷键与搜索功能开关联动。
- **发布打包**：应用与安装器版本统一为 1.3.4；x64 和 ARM64 均采用框架依赖安装包，只检测并下载当前架构缺少的运行时；同时移除旧图片画廊格子。

完整内容见 [更新日志](CHANGELOG.md) 和 [1.3.6 发布说明](docs/releases/v1.3.6.md)。

## 当前界面

以下图片用于展示当前 DeskBox 的界面和材质效果。

### 桌面格子与材质

#### 云母材质

![DeskBox 1.3.4 中文界面的 Windows 11 云母材质桌面格子](docs/images/screenshots/zh-cn/云母材质.png)

#### 亚克力材质

![DeskBox 1.3.4 中文界面的 Windows 11 亚克力材质桌面格子](docs/images/screenshots/zh-cn/亚克力材质.png)

### 设置

| 常规 | 外观 |
| --- | --- |
| ![DeskBox 1.3.4 中文常规设置](docs/images/screenshots/zh-cn/常规.png) | ![DeskBox 1.3.4 中文外观设置](docs/images/screenshots/zh-cn/外观.png) |

| 胶囊模式 | 文件格子 |
| --- | --- |
| ![DeskBox 1.3.4 中文胶囊模式设置](docs/images/screenshots/zh-cn/胶囊模式.png) | ![DeskBox 1.3.4 中文文件格子设置](docs/images/screenshots/zh-cn/文件格子.png) |

| 功能格子 | 快捷与交互 |
| --- | --- |
| ![DeskBox 1.3.4 中文功能格子设置](docs/images/screenshots/zh-cn/功能格子.png) | ![DeskBox 1.3.4 中文快捷与交互设置](docs/images/screenshots/zh-cn/快捷与交互.png) |

## 本地数据与隐私

DeskBox 不要求注册账号，也不依赖云同步。格子配置、待办、随记、搜索历史、窗口布局和收纳文件都保存在本机。

以下功能会按使用意图联网：

- 天气数据来自 MSN 天气或 Open-Meteo。
- 更新检查访问 DeskBox 更新服务或 GitHub Releases。
- 安装器只在缺少依赖时下载 .NET 或 Windows App Runtime。
- 从浏览器拖入远程链接时，只有确认导入的内容会被下载。

胶囊隐私选项只是在收起状态下隐藏部分文字，属于展示控制，并不等同于文件加密。

## 环境要求

- Windows 10 21H2（build 19044）或更高版本；Windows 11 22H2 或更高版本可获得完整视觉效果。
- 与安装包匹配的 x64 或 ARM64 处理器。
- .NET 10 Runtime 与 Windows App Runtime 2.2；缺少时可由安装程序自动安装。

Windows 10 会自动降级不受系统支持的材质、圆角和部分动画；文件同步、拖放与格子核心功能仍按兼容基线验证。

## 安装、更新与卸载

DeskBox 使用 Inno Setup 安装器，默认安装到当前用户目录。覆盖安装会保留应用设置、格子配置和收纳目录。旧版如果安装在 Program Files，安装器会进行迁移，以避免管理员权限进程影响资源管理器拖拽。

开机自启会静默启动到托盘。DeskBox 已运行时，再启动一个实例会直接退出，不会重复打开设置窗口。

卸载时可以选择是否删除 `%LocalAppData%\DeskBox` 下的应用数据。DeskBox 不会静默删除收纳目录中的用户文件；任何可能影响用户文件的清理都会先提示。

## 常见问题

### DeskBox 会替换 Windows 桌面吗？

不会。Windows 资源管理器仍是桌面外壳，文件也仍是普通文件和文件夹。DeskBox 只是在现有桌面上增加独立管理的格子。

### DeskBox 把数据保存在哪里？

- 应用设置和格子数据：`%LocalAppData%\DeskBox\data`
- 默认收纳目录：`%UserProfile%\DeskBox`

两类数据都可以通过 DeskBox 设置中的备份功能进行备份。

### 应该下载 x64 还是 ARM64？

绝大多数 Intel、AMD 电脑选择 x64；骁龙等原生 Windows on ARM 设备选择 ARM64。不确定时可在“Windows 设置 → 系统 → 系统信息 → 系统类型”中查看。

### 为什么安装时可能需要联网？

正式安装包不内置 .NET 10 和 Windows App Runtime 2.2。安装器会先检查电脑，只下载当前架构缺少的依赖。

### 关闭功能格子会删除内容吗？

不会。关闭功能会关闭对应界面并释放运行资源，但保存的数据和配置仍会保留，下次开启后继续使用。

## 从源码构建

开发需要 .NET 10 SDK 和 Windows 11 环境，推荐安装带 Windows App SDK 工作负载的 Visual Studio。

还原、测试并构建 x64 Debug 版本：

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64
dotnet test .\DeskBox.sln --configuration Debug --no-restore -p:Platform=x64 -v:minimal
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

生成不内置运行时的 Release 输出：

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\arm64 -v:minimal
```

安装 Inno Setup 6 或更高版本后，编译两个安装包：

```powershell
ISCC.exe .\installer\DeskBox.iss
ISCC.exe .\installer\DeskBox.arm64.iss
```

预期输出：

```text
Output\DeskBox_Setup_1.3.6_x64.exe
Output\DeskBox_Setup_1.3.6_arm64.exe
```

## 项目结构

```text
src\DeskBox                 WinUI 3 应用源码
src\DeskBox.Updater         直发版更新辅助程序
tests\DeskBox.Tests         服务与策略测试
installer                   x64/ARM64 Inno Setup 脚本
docs\user-guide             产品使用说明
docs\images                 README 与发布图片
docs\releases               版本发布文案和测试清单
```

## 反馈与本地化

DeskBox 目前由个人独立开发和维护。为了保持架构一致性与后续版权边界，现阶段暂不接受外部 Pull Request；欢迎通过 [GitHub Issues](https://github.com/Tianyu199509/DeskBox/issues) 提交问题、功能建议、翻译和 UI/UX 反馈。

特别感谢 [@magisph](https://github.com/magisph) 提供巴西葡萄牙语本地化支持。

也可以访问 [deskbox.fun](https://deskbox.fun)，或通过应用“关于”页面中的联系方式反馈。

## 作者与协议

- 开发者：朱天雨
- 项目地址：<https://github.com/Tianyu199509/DeskBox>
- 开源协议：[GPL-3.0-only](LICENSE)

早期已按 MIT 协议发布的 DeskBox 版本继续保持原许可，协议变更不追溯历史版本；详情见 [LICENSE_CHANGE.md](LICENSE_CHANGE.md)。
