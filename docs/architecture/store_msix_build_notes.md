# DeskBox Store/MSIX 构建说明

本文档记录 Microsoft Store 技术分支的本地构建入口。当前 Direct/Inno 仍是默认发布通道，Store 包需要显式传入 `DeskBoxDistribution=Store`。

## 构建入口

```powershell
.\scripts\build-store-msix.ps1
```

默认行为：

- 构建 `Release x64`
- 使用 `DeskBoxDistribution=Store`
- 跳过 `DeskBox.Updater`
- 默认生成 managed 自包含 .NET 包，避免 Store 用户额外安装 .NET Desktop Runtime
- 关闭 MSIX 签名，适合本机先验证包结构
- 自动定位本机 VC 符号工具并生成 `.appxsym`
- 输出到 `artifacts\store-msix\`

ARM64 测试包：

```powershell
.\scripts\build-store-msix.ps1 -Platform ARM64
```

Native AOT Store 上传包：

```powershell
.\scripts\build-store-msix.ps1 `
  -Configuration Release `
  -Platform x64 `
  -NativeAot `
  -PackageBuildMode StoreUpload `
  -OutputDir .\.artifacts\store-msix-aot-x64
```

ARM64 使用同一入口，只把 `-Platform` 改为 `ARM64`。Native AOT 模式会固定启用 Rust 静态 CRT
`deskbox_native.dll`；文件搜索统一走 Everything SDK（`EverythingSdk.dll` 随包分发并受审计门禁约束）；不会把 Direct 的 Updater、
`deskbox_search_core.dll`、`.NET` deps/runtimeconfig、PDB 或 Direct 素材放入 MSIX。主程序和 Rust PDB
只进入 `.appxsym`。

带证书签名：

```powershell
.\scripts\build-store-msix.ps1 -SignPackage -PackageCertificateKeyFile "path\to\DeskBox.pfx"
```

## Partner Center 身份

`src\DeskBox\Package.appxmanifest` 已使用 Partner Center 分配的正式身份：

- `Identity Name="D1FC332A.DeskBoxWidgets"`
- `Publisher="CN=3B75AA4A-2433-4F71-9CC1-B644B26F474A"`
- `PublisherDisplayName="朱天雨"`

每次打包后仍需从生成包内复核 Identity，确保没有被本地发布配置或临时证书覆盖。

## 当前边界

Store 构建会启用：

- `DESKBOX_STORE` 编译常量
- `StoreAppUpdateService`
- `StoreStartupService`
- `Package.appxmanifest`
- Store 专用 tile/logo/splash 资源

Direct 构建继续使用：

- `WindowsPackageType=None`
- Inno 安装包
- `AppUpdateService`
- `DirectStartupService`
- `DeskBox.Updater`

## Native AOT 包内容审计

`build-stage-7c1-distribution.ps1` 会分别在 x64 和 ARM64 上构建 Direct 安装器及 Store 上传包，并调用：

```powershell
.\scripts\audit-store-native-aot-package.ps1 `
  -MsixPath <DeskBox.msix> `
  -AppxSymPath <DeskBox.appxsym> `
  -ExpectedPlatform x64 `
  -ExpectedPublishDirectory <publish> `
  -OutputDirectory <audit-output>
```

审计会核对正式 Store identity、处理器架构、`Microsoft.WindowsAppRuntime.2` 框架依赖、Native AOT PE
无 CLR header、Rust ABI/导出/静态 CRT、publish 与包内哈希、符号分离，以及 Direct/managed runtime
文件的严格排除清单。结构与哈希通过仍不代表签名、WACK、安装、覆盖升级或 package flight 已执行。

## 本地素材目录边界

`store-assets-html/` 只用于本地生成 Microsoft Store 产品页截图、图标或试验素材。它不是应用资源，也不是 Store MSIX 资源。

- 不提交到 Git。
- 不进入 Direct 安装包。
- 不进入 Store MSIX。
- 需要上传 Partner Center 时，只上传从该目录截图/导出的 PNG，不上传 HTML 源目录。

## 后续验证

真正上架前还需要：

1. 使用正式 Store/Partner Center 流程签名并运行 Windows App Certification Kit。
2. 通过 package flight 做不卸载覆盖升级、退出/重启及设置、Widget、Quick Capture、Todo、索引和日志保留。
3. 在 x64 与可获得的 ARM64 实体设备实测文件拖拽、托盘、开机自启、系统音量、多屏/DPI。
4. 确认关于页不显示跳转 Microsoft Store 的支持入口。
5. 复核包内没有 `DeskBox.Updater.*`、支付二维码或 `store-assets-html/` 这类非 Store 包资源。
