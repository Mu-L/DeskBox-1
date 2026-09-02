# DeskBox 文件拖拽与叠放交互契约

本文记录 `FileSurfaceContent` 中“文件拖拽、格子排序、叠放成员关系、真实文件传输”之间的边界。它既是当前实现说明，也是以后修改拖拽代码时的回归清单。

适用基线：DeskBox 1.4.9，2026-09-03 完成的 Win10/Win11 拖拽修复。

## 1. 最重要的结论

DeskBox 的文件拖拽分为两类，绝不能混为一套操作：

1. **内部编排**：同一个格子内调整顺序、加入叠放、移出叠放、叠放成员排序、叠放整体排序。它只修改 DeskBox 的投影和持久化元数据，不移动、复制或删除磁盘文件。
2. **文件系统传输**：桌面/资源管理器与格子之间、不同格子之间的文件拖放。它会执行真实的复制、移动或创建快捷方式，并在完成后刷新源格子和目标格子。

内部编排可以在 `DragOver` 阶段临时接受 `Move`，以保证 WinUI 把 `Drop` 路由给目标；但内部 `Drop` 的最终结果永远不能返回 `Move`。这是避免 `.lnk` 被 Shell 当成“源文件移动完成”后清理进回收站的核心约束。

## 2. 两层数据模型

### 2.1 磁盘文件层

`WidgetViewModel.Items` 是格子当前目录中的真实文件/文件夹集合。文件是否存在、从格子移入或移出、空状态是否显示，都应以这一层为准。

### 2.2 叠放投影层

启用叠放后，`WidgetViewModel.VisibleItems` 使用 `_stackDisplayItems`，把多个真实文件投影为以下显示单元：

- 未分组文件；
- `WidgetStackItem` 叠放卡片；
- 非弹窗模式下展开的叠放成员。

叠放不会创建或搬移真实文件。它主要持久化三类元数据：

- `_stackMemberOverrides`：叠放成员及成员顺序；
- `_stackOrder`：叠放/独立文件这些显示单元的顺序；
- 名称、禁用状态、展开状态等其他叠放覆盖项。

自动叠放一旦发生人工加入、移出或成员排序，会按需要转为手动叠放。移出后不足两个成员时，手动叠放会解散；自动叠放会被禁用或转换，以保证剩余文件回到可见层。

### 2.3 弹窗与非弹窗只是宿主不同

两种打开模式共享同一套 `WidgetViewModel` 和叠放元数据：

| 模式 | 成员宿主 | 成员移出路径 | 成员内部排序 |
| --- | --- | --- | --- |
| 非弹窗模式 | 主格子的 `ListViewBase` | `MoveVisibleItemForReorder` 在越过叠放边界时解除成员关系 | 主表面排序路径 |
| 弹窗模式 | 独立 `StackPopoverHostWindow` 内的 `ListViewBase` | 主格子 `Root_Drop` 调用 `RemoveItemsFromStack` | `StackPopoverItems_Drop` 调用 `MoveStackMembersForReorder` |

弹窗是独立窗口，因此判断鼠标是否仍在弹窗内时，必须使用弹窗自己的窗口句柄和坐标系，不能使用主格子的 `_hostWindowHandle`。

## 3. DeskBox 拖拽载荷

文件拖拽由 `FileItemDragPackage.TryPrepare` 统一创建。除系统标准格式外，DeskBox 在 `DataPackage.Properties` 中写入自己的协议字段：

| 字段 | 含义 |
| --- | --- |
| `DeskBoxSourceWidgetId` | 源格子 ID；用来区分同格编排和跨格传输 |
| `DeskBoxSourcePaths` | 本次拖拽的完整、去重、规范化路径集合 |
| `DeskBoxInternalDragToken` | 当前文件拖拽协议标记 `DeskBox.WidgetItemDrag.v2` |
| `DeskBoxDragSessionId` | 每次拖拽唯一 GUID；约束缓存不能跨会话复用 |
| `DeskBoxStackReorderKey` | 拖动叠放卡片整体排序时的叠放键 |
| `DeskBoxSourceStackKey` | 从叠放弹窗拖出成员时的源叠放键 |

文件载荷还会提供以下一种系统数据形式：

- 普通文件优先使用 `StorageItems`；
- `.lnk` 或 Storage broker 无法完整表示的路径使用原生 Shell `IDataObject`；
- 无法完整提供所选文件时取消拖拽，禁止只拖出部分选择。

### 3.1 `.lnk` 的特殊边界

不要在 UI STA 上同步等待 `StorageFile` broker 解析 `.lnk`。部分 Windows 环境会拒绝快捷方式，消息循环也可能被同步等待卡住。当前实现先尝试 `NativeShellFileDragProvider`，让 Explorer 接收原始文件系统对象和系统拖拽图像。

原生 Shell 载荷同时意味着目标返回的完成操作会影响 Shell 是否清理源文件。因此 `.lnk` 最能暴露“内部编排误报为真实 Move”的问题，但这条安全规则同样适用于普通文件和文件夹。

## 4. RequestedOperation、AllowedOperations 与 AcceptedOperation

这三个值含义不同：

- `DataPackage.RequestedOperation`：源端偏好的**单个**操作；文件拖拽固定为 `Move`。
- `DragStartingEventArgs.AllowedOperations`：源端能力集合；普通文件为 `Copy | Move | Link`，托管快捷方式为 `Move | Link`。
- `DragEventArgs.AcceptedOperation`：目标在当前 `DragOver` 或最终 `Drop` 接受的操作。

### 4.1 为什么 RequestedOperation 必须只有 Move

Windows 10 会把 `RequestedOperation = Copy | Move | Link` 理解成没有明确默认动作，普通左键拖出也可能弹出“复制到当前位置 / 移动到当前位置 / 创建快捷方式”的选择菜单。能力集合应放在 `AllowedOperations`，不能塞进 `RequestedOperation`。

### 4.2 为什么反馈策略与完成策略必须分开

`ListViewBase` 的内置项目拖拽并不保证触发附加到 `UIElement.DragStarting` 的处理器。实际运行时，内部目标有时只能看到 `RequestedOperation=Move`、`AllowedOperations=Move`。

因此当前策略为：

| 阶段 | 内部编排策略 |
| --- | --- |
| `DragOver` 反馈 | 优先 `Link`，其次 `Copy`；如果确认来自 DeskBox 且只允许 `Move`，临时接受 `Move`，让 WinUI 继续路由 `Drop` |
| `Drop` 完成 | 优先 `Link`，其次 `Copy`；只有 `Move` 时返回 `None`，永远不授权 Shell 清理源文件 |

所以以下日志组合是**正确且刻意设计的**：

```text
TargetDecision ... route=stack-membership ... allowed=Move accepted=Move
SourceCompleted ... dropResult=None internalHandled=True
```

第一行的 `Move` 只是目标反馈；第二行的 `None` 才是安全完成结果。不要为了让两行“看起来一致”而把内部 `Drop` 改回 `Move`。

## 5. 完整路由矩阵

| 源 | 目标 | 路由/处理 | 是否改磁盘 | 是否改叠放/顺序 | 关键完成规则 |
| --- | --- | --- | ---: | ---: | --- |
| 主表面独立文件 | 同格主表面插入线 | `surface-reorder` | 否 | 是 | 内部完成不得为 `Move` |
| 非弹窗叠放成员 | 原叠放成员区域 | 主表面成员排序 | 否 | 是 | 保持成员身份，仅改 override 顺序 |
| 非弹窗叠放成员 | 叠放范围外的主表面 | `MoveVisibleItemForReorder` | 否 | 是 | 解除成员关系并插入为独立显示单元 |
| 弹窗叠放成员 | 同一弹窗列表 | `stack-reorder` | 否 | 是 | 使用弹窗插入索引；内部完成不得为 `Move` |
| 弹窗叠放成员 | 所属格子的主表面 | `stack-detach` | 否 | 是 | `RemoveItemsFromStack`，成功后关闭弹窗 |
| 同格独立文件/其他叠放成员 | 目标叠放卡片或弹窗表面 | `stack-membership` | 否 | 是 | `AddItemsToStack`；同一源叠放为无操作 |
| 叠放卡片 | 同格主表面 | `StackReorderKey` 对应的表面排序 | 否 | 是 | 只调整显示单元顺序 |
| 桌面/Explorer | 格子主表面 | 外部导入 | 是 | 可能 | 按映射目录与用户策略复制/移动/创建快捷方式 |
| 桌面/Explorer | 叠放 | 先外部导入，再加入叠放 | 是 | 是 | 只有成功导入的新项才加入叠放 |
| 其他格子 | 本格主表面/叠放 | 跨格文件传输 | 是 | 可能 | 目标完成传输后通知源格子；DeskBox 源不再由 Shell 二次清理 |
| 主表面/弹窗成员 | 桌面、Explorer 或其他应用 | Shell 外部拖出 | 是 | 随刷新清理 | 只在真实移动已发生后按磁盘存在性协调源集合 |
| 任意文件 | 子文件夹卡片 | 文件夹传输目标 | 是 | 否 | 子目标优先于根表面排序，不能被插入线抢走 |

### 5.1 同格与跨格的判定

同时满足协议 token、源格子 ID 等于当前目标格子 ID，并且存在路径或叠放键时，才是 `IsInternalReorder`。源格子不同，即使载荷来自 DeskBox，也必须走真实文件导入/传输路径。

### 5.2 跨格移动为什么最终也可能返回 None

跨格传输由目标格子实际执行文件移动，并通过 `NotifyItemsMovedOutAsync` 告知源格子哪些路径成功。对于 DeskBox 自己的原生 Shell 载荷，再向源端返回 `Move` 会形成第二次源清理。因此 `ResolveSafeDropCompletionOperation` 在“DeskBox 来源 + 目标已完成真实移动”时返回 `None`；目标完成通知和后续磁盘协调才是事实依据。

非 DeskBox 来源只有在请求移动数与实际完成数完全一致时才返回 `Move`。部分完成、取消或失败必须返回 `None`。

## 6. 事件链与状态清理

```text
鼠标拖动
  -> Items_DragItemsStarting
     -> 解析完整选择
     -> 创建唯一 DragSessionId
     -> TryPrepare 生成 StorageItems 或 Shell IDataObject
  -> 子目标或 Root 的 DragOver
     -> GetDragPayload 校验会话缓存
     -> 选择路由并显示高亮/插入线
     -> 反馈策略决定 AcceptedOperation
  -> Drop（正常路径）或鼠标释放恢复（WinUI 漏事件路径）
     -> 修改叠放/顺序元数据，或等待真实文件传输完成
     -> 完成策略决定最终 AcceptedOperation
  -> Items_DragItemsCompleted
     -> 标记内部已处理，或启动外部移出协调
     -> 清理插入线、子目标高亮、载荷缓存和弹窗拖拽状态
```

### 6.1 子目标可能绕过 Root_DragEnter

文件夹和叠放子元素会将 `DragOver` 标记为 `Handled`。快速从一个目标移动到另一个目标时，新的子目标可能先收到事件，而根元素没有机会重新初始化缓存。

因此 `GetDragPayload` 每次都必须校验 `DragSessionId`。会话不同就丢弃 `_dragPayloadSnapshot`，不能仅在 `Root_DragEnter` 清缓存。没有会话 ID 的外部/旧载荷才使用路径、源格子、token 和叠放键进行兼容比较。

### 6.2 高亮条不等于 Drop 已完成

WinUI 偶尔会显示有效插入线，却漏掉最终 `Drop`，或在紧凑窗口恢复探针先观察到鼠标已松开。当前有两个严格受限的恢复入口：

- `CompleteReleasedDragSession`：只在主表面排序已激活、有最后位置、鼠标仍在主格子内、且没有文件夹/叠放子目标高亮时提交；
- `TryCompleteReleasedStackPopoverReorder`：只在弹窗拖拽激活、有有效插入索引、鼠标仍在弹窗列表、存在源路径、且没有其他内部目标已处理时提交。

恢复提交后必须将本次拖拽标记为内部已处理，避免随后迟到的 `Drop` 重复执行，也避免源端启动外部移出协调。

当前字段 `_activeDragHandledAsStackMembership` 的历史命名偏窄，实际语义已经是“本次拖拽已由任一内部编排目标处理”。以后不能按字段名把它重新限制为“仅加入叠放”。

### 6.3 外部移出协调

`DropResult` 不是逐文件完成报告，Shell 还可能在主表面外部移动成功时返回 `None`。主表面保留延迟协调，但每次删除显示项前必须再次检查 `File.Exists`/`Directory.Exists`，防止文件拖出后迅速拖回时被旧任务误删。

弹窗拖拽的 `None` 同时可能表示取消或纯内部编排，不能据此启动长期外部协调；弹窗只在明确得到 `Move` 时按外部移出处理。

## 7. 空状态契约

空状态回答的是“格子目录中是否还有真实项目”，不应依赖叠放投影是否已经重建。因此条件是：

```text
!ViewModel.IsLoading && ViewModel.Items.Count == 0
```

不要改回 `!ViewModel.VisibleItems.Any()`。`VisibleItems` 可能受异步/延迟的叠放投影重建影响，导致：

- 从外部拖入第一个文件后空状态仍显示；
- 移出最后一个文件后空状态没有及时出现。

导入、移出协调、文件监视刷新和集合变化都应最终调用 `UpdateEmptyState`，但可见性的事实来源始终是 `Items.Count`。

## 8. 已发生过的故障与根因

### 8.1 Win10 每次拖出都弹操作选择菜单

根因：把 `Copy | Move | Link` 同时写入 `RequestedOperation`。

正确做法：`RequestedOperation=Move`，完整能力集合写入 `AllowedOperations`。

### 8.2 `.lnk` 在格子/叠放内排序后进入回收站

根因：内部元数据操作最终返回 `Move`，原生 Shell 数据对象把它解释为真实文件移动成功并清理源快捷方式。

正确做法：内部 `DragOver` 可临时反馈 `Move`，内部 `Drop` 完成永不返回 `Move`。

### 8.3 修好误删后，文件无法加入叠放或从弹窗移出

根因：为了避免 `Move`，目标只接受 `Link`/`Copy`；但部分 `ListViewBase` 拖拽实际只向目标暴露 `Move`，于是 WinUI 不再路由 `Drop`。

正确做法：拆分“反馈操作”和“完成操作”，并且只对已识别的 DeskBox 内部载荷在反馈阶段接受 `Move`。

### 8.4 插入高亮出现，松开鼠标却没有排序

根因：WinUI 偶发漏发 `Drop` 或完成回调顺序异常，高亮只证明收到过 `DragOver`。

正确做法：保存最后有效位置，并以窗口坐标、目标高亮和内部已处理状态为边界，在鼠标释放时单次恢复提交。

### 8.5 连续拖拽时路由识别成上一次的源格子/源叠放

根因：载荷缓存只在根 `DragEnter` 初始化，子目标直接收到处理过的 `DragOver` 时复用了旧快照。

正确做法：每次拖拽生成 `DragSessionId`，每次 `GetDragPayload` 都验证缓存归属。

### 8.6 文件已拖回格子，却被旧的移出任务从界面删除

根因：延迟任务使用早期“路径缺失”快照直接裁剪集合。

正确做法：实际裁剪前再次检查路径，并只移除当前仍然不存在的项目及其叠放覆盖。

### 8.7 空状态刷新滞后

根因：空状态依赖延迟更新的 `VisibleItems` 叠放投影。

正确做法：以源集合 `Items.Count` 为准。

## 9. 一定不要踩的坑

1. 不要把多个标志重新写进 `DataPackage.RequestedOperation`。
2. 不要因为内部 `DragOver` 显示 `Move`，就让内部 `Drop` 也返回 `Move`。
3. 不要假设 `UIElement.DragStarting` 一定会在 `ListViewBase` 内置项目拖拽中触发。
4. 不要只在 `Root_DragEnter` 清理或验证载荷缓存。
5. 不要把“出现高亮/插入线”当成元数据已经提交。
6. 不要在 `DragOver` 就设置“内部已处理”；只有 `Drop` 或受边界保护的释放恢复真正提交后才能设置。
7. 不要把 `_activeDragHandledAsStackMembership` 当成仅表示叠放加入；它当前保护所有内部编排免受源端清理。
8. 不要在内部加入/移出/排序时调用文件移动 API。
9. 不要在真实跨格移动完成前返回 `Move`，也不要在 DeskBox 已完成移动后再让 Shell 二次清理。
10. 不要对 `.lnk` 在 UI 线程同步调用 Storage broker；优先原生 Shell 载荷。
11. 不要用弹窗成员列表的坐标配合主窗口句柄判断鼠标位置。
12. 不要让根表面插入线覆盖文件夹或叠放子目标；子目标存在时必须禁止根排序恢复。
13. 不要用叠放投影集合判断格子空状态。
14. 不要只测试一种叠放打开模式，或只在 Win11 上验证 Win10 行为。
15. 不要以管理员身份启动 DeskBox 做拖放验证；不同完整性级别会让 Windows 拦截拖放，形成错误结论。

## 10. 日志判读

诊断时先按会话关联日志：

```powershell
rg -n "\[DragProtocol\]|\[FileStack\]|External drag-out reconciled|\[FileTransfer\]" DeskBox.log
```

关键阶段：

| 日志 | 关注字段 | 含义 |
| --- | --- | --- |
| `stage=PackagePrepared` | `session`、`popover`、`storage`、`nativeShell`、`requested` | 源载荷是否完整，是否为快捷方式原生载荷 |
| `stage=SourceStarting` | `allowed` | 仅作补充证据；没有此日志不代表拖拽未开始 |
| `stage=TargetDecision` | `route`、`sourceWidget`、`sourceStack`、`allowed`、`accepted` | 目标实际采用了哪条路由 |
| `stage=PayloadCacheInvalidated` | 新旧 `session` | 防止跨会话复用；若同一次拖拽频繁出现需继续排查 |
| `stage=SourceCompleted` | `dropResult`、`internalHandled` | 源端是否会进入外部清理/协调 |
| `Recovered internal reorder after pointer release` | `reordered` | WinUI 漏事件后的受控恢复 |
| `External drag-out reconciled` | `removed`、`remaining` | 已按磁盘现状清理源格子 |

危险信号：

- 同格内部路由出现 `SourceCompleted ... dropResult=Move internalHandled=True`；
- 已出现 `TargetDecision route=...`，但完成时 `internalHandled=False`；
- 当前 `session` 却带着上一轮的 `sourceWidget` 或 `sourceStack`；
- 弹窗内取消拖拽后很久又出现来源不明的外部移出清理；
- `nativeShell=True` 的 `.lnk` 内部编排后磁盘路径消失。

## 11. 修改后的最低验证矩阵

每次触碰以下任一文件时，都应重新执行完整矩阵：

- `FileItemDragPackage.cs`
- `FileSurfaceContent.xaml.cs`
- `FileSurfaceContent.ItemVisuals.cs`
- `FileSurfaceContent.StackPopover.cs`
- `DeskBoxDragData.cs`
- `WidgetViewModel.Stacks.cs`
- `WidgetShell.xaml.cs`
- 原生 Shell drop target / 文件传输服务

### 11.1 自动化验证

```powershell
dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj `
  --no-restore --verbosity:minimal -p:Platform=x64
```

重点测试文件：

- `FileItemMultiDragTests`：源操作、反馈/完成拆分、会话缓存、释放恢复、外部协调和空状态；
- `FileSurfaceParityContractTests`：主表面、叠放弹窗、原生拖放和持久化链路；
- `WidgetCompactTrayVisibilityContractTests`：鼠标释放恢复与紧凑窗口状态；
- `NativeDropEffectPolicyTests` 及文件传输相关测试：真实 Copy/Move/Link 决策。

### 11.2 Win10 与 Win11 实机验证

两个系统都要覆盖弹窗模式和非弹窗模式，并至少使用一个普通文件、一个 `.lnk`、一个文件夹和一次多选：

1. 主表面内部排序，插入线出现后松开即完成，并在重启后保持。
2. 叠放成员内部排序，并在重启后保持。
3. 叠放成员移出到所属格子主表面。
4. 所属格子主表面的独立文件拖入叠放。
5. 一个叠放的成员拖入另一个叠放。
6. 文件拖到另一个格子及另一个格子的叠放。
7. 桌面/Explorer 文件拖入格子及叠放。
8. 格子和叠放成员拖到桌面/Explorer。
9. 所有内部动作确认原文件仍存在、回收站没有新增对应文件。
10. Win10 左键拖出不出现操作选择菜单；右键拖拽仍按系统语义工作。
11. 移入第一个文件后空状态立即消失；移出最后一个文件后空状态立即出现。
12. 快速跨过文件夹、叠放、主表面和窗口边缘，不残留高亮或插入线。
13. 取消拖拽后不改变顺序、不改变叠放关系，也不在数秒后误删显示项。

### 11.3 发布边界

- Debug 通过只证明 JIT 路径；GitHub Releases 的 Full Native AOT 安装包必须单独验证。
- Store 与 Direct 使用同一交互代码，但包身份、运行时和更新通道不同；Store 包仍需 flight 覆盖安装与实机拖拽。
- ARM64 静态审计不等于 ARM64 实机拖拽验证。
- 只有日志注册成功、测试通过或构建成功，都不能代替真实鼠标拖放验证。

## 12. 实现索引

| 责任 | 主要位置 |
| --- | --- |
| 源载荷与 `.lnk` Shell 旁路 | `Controls/FileItemDragPackage.cs`、`Controls/NativeShellFileDragProvider.cs` |
| 协议字段与外部载荷读取 | `Services/DeskBoxDragData.cs` |
| 主表面路由、缓存、完成策略、外部协调、空状态 | `Controls/WidgetContents/FileSurfaceContent.xaml.cs` |
| 叠放卡片目标与加入叠放 | `Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs` |
| 弹窗成员排序与释放恢复 | `Controls/WidgetContents/FileSurfaceContent.StackPopover.cs` |
| 叠放投影、成员覆盖和显示单元顺序 | `ViewModels/WidgetViewModel.Stacks.cs` |
| 紧凑窗口鼠标释放恢复 | `Controls/WidgetShell.xaml.cs` |
| 原生 Explorer 拖入 | `Helpers/NativeDropTarget.cs` 及关联文件传输服务 |

以后若需要重构，先保留本文的两层模型、反馈/完成分离和路由矩阵，再逐步替换具体事件实现。不要以“统一操作值”或“统一清理逻辑”为目标把内部编排和真实文件传输重新合并。
