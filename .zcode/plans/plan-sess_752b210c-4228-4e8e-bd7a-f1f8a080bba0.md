## 任务：产出 DeskBox 内存优化长期方案文档（不改任何应用代码）

**唯一执行动作**：新建 `docs/memory-optimization-plan.md`（中文），内容基于本次三路并行代码勘察 + 我本人验证的承重结论。文档结构与核心内容如下：

### 一、现状全景（资源持有者分层 + 5 条清理路径）
- 资源按生命周期分五层：进程级（静态缓存/服务）、会话级（widget/surface）、交互级（切换/动画/弹窗）、瞬态（CTS/Storyboard）、原生级（compositor/WinRT RCW），各附 file:line
- 现有清理路径（可见空闲/后台soft-deep/轻重GC/隐藏trim/临时窗口）各自**真实释放什么**、哪些只是 GC+trim 空转

### 二、四大场景逐链路剖析（创建→持有→释放，断点标注）
1. **格子组连续滚动**：每次切换重建 ContentWidgetWindowFactory+7-provider 字典（Groups.cs:1037）；Glance/Music/Weather/Search 未实现 IWidgetGroupContentCacheable → 整树销毁重建；Balanced 档缓存容量=1
2. **胶囊重复展开**：每循环 6-9 个 KeyFrame 动画 + watchdog timer + warmup CTS（配对完整但分配密集）
3. **叠放展开/收起**：投影全量重算 + 每 stack 16 次 PropertyChanged（Stacks.cs:1266-1345）；每成员每动画 4 个 compositor 对象不复用（StackAnimations.cs:273-303）
4. **材质/语言/主题色**：双通道扇出（Preview+SettingsChanged 叠加）；约 10 处热路径每次 new brush（清单含 file:line）；WindowsCompatibilityService 每次 new UISettings（:98-125）；语言切换触发全量 stack 重算

### 三、根因归纳（7 条架构级）
R1 释放不对称（创建即时 vs 释放靠 WS≥240MB/10min空闲等高门槛启发式）；R2 隐藏≠释放（7 类内容仅 2 类实现 OnWindowLongHidden，已验证）；R3 Balanced 默认档全隐藏时无任何 trim 路径（CleanupNever，已验证）；R4 无预算资源（s_shellKindCache 的 Clear 无调用方、s_storedMetadataCache、GlanceWidgetStore.WidgetStores）；R5 热路径分配与重复构建；R6 设置双通道扇出放大重算；R7 无内存回归门禁

### 四、长期方案（交互峰值优先排序）
- **Phase 0 测量基建先行**：每场景自动化采样脚本（滚动/胶囊/叠放/材质各20循环采样 WS+Private）+ 基线记录；**降级策略：真实 UI 驱动连续失败>5 轮即降级**为 PerformanceLogger 快照对比，不再驱动 UI
- **Phase 1 交互峰值削减**：工厂复用、brush 复用推广（以 FileItemSurfaceStyleCache/GetOrUpdateSolidColorBrush/AccentResourceScope 为模板覆盖10处）、UISettings/AccessibilitySettings 缓存、compositor 动画对象缓存（参照 _edgeGlowPulseAnimation）、不可缓存成员改造、扇出合并为 dirty-flags 单通道、叠放投影增量化
- **Phase 2 常驻内存契约**：7 类内容补全 OnWindowLongHidden、Balanced 隐藏 trim 低频兜底、无预算资源纳入上限、BestVisual 名义档清理
- **Phase 3 统一资源台账**（ResourceBudgetRegistry：注册-预算-逐出-观测一体化）
- **Phase 4 门禁固化**（基线断言、报表、防回退）

### 五、验收标准与风险
每场景 20 循环后 Private 净增量阈值、空闲 60s 回落要求、8 小时长稳；各阶段独立可回滚

文档约 300-400 行，全部结论附 file:line 可回查。写入后不提交 git（由你决定是否提交）。