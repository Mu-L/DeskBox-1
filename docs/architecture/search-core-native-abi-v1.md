# DeskBox SearchCore 原生 ABI v1

> 这是阶段 6A 的基础契约。阶段 6B 已以兼容保留这些 v1 操作的方式把模块 ABI 升到 2，并新增 DBIX
> 直载；当前状态见 `search-core-native-abi-v2.md`。

- 状态：阶段 6A 已实现并通过受控差异测试
- 目标架构：`x86_64-pc-windows-msvc`
- 模块：`deskbox_search_core.dll`
- ABI：1
- 产品状态：显式实验入口，尚未接入 `SearchIndexService` 默认路径

## 1. 边界选择

SearchCore 使用独立 DLL，不扩展 `deskbox_native.dll`。后者已经被 x64 Native AOT 的 ABI 2、
能力 511 和十个必需导出门禁冻结；把搜索实验加入同一模块会迫使已完成的 AOT 契约、哈希与发布
审计整体变更。独立模块允许搜索 ABI 在阶段 6 内继续演进，同时保持现有 AOT 产品边界不变。

阶段 6A 只建立可测的原生核心和显式 C# 所有者。普通 Debug、Release 与 AOT 应用输出都不会自动
复制该 DLL，`SearchIndexService.Search` 仍是唯一产品默认搜索路径。测试必须传入绝对模块路径，
不存在环境变量切换、静态默认加载或失败后静默改用另一后端。

## 2. 数据布局

原生索引将重复目录和文件名分开保存：

- `entries` 是连续的固定宽度描述符，包含目录 ID、文件名 offset/length、UTC ticks 和 flags；
- `directory_utf16` 只保存去重后的目录 UTF-16；
- `file_name_utf16` 连续保存文件名，不保存重复的完整路径；
- 构建期目录表使用 64 位 hash 到候选 ID 的映射，真正相等仍按忽略大小写规则确认；
- `seal` 收缩四个连续容器并释放整个构建期目录查找表；
- 查询只保留最多 `max_results` 个候选的有界 `BinaryHeap`，不为全部匹配项创建结果对象；
- 目录和文件名只在最终 Top-N 结果确定后复制到调用方缓冲区。

该布局直接针对当前托管索引的主要常驻成本：`Dictionary<string, IndexedFileEntry>` 的 key 是每条
完整路径，即使目录字段已池化，完整路径字符串仍重复保存目录前缀。阶段 6A 不把这个结构性差异
直接等同于进程工作集收益；正式结论还需隔离进程的 10k/100k/300k Release 基准。

## 3. 查询语义

C# 桥在进入 ABI 前执行与现有产品相同的 `Trim()`。Rust 相关度顺序严格保持：

1. 完整文件名相等：100；
2. 完整文件名以查询开头：80；
3. 去扩展名后相等：90；
4. 去扩展名后以查询开头：70；
5. 完整文件名包含查询：50；
6. 其余：0。

顺序不能按分数重新排列。例如 `report.txt` 查询 `report` 会先命中完整名称前缀，结果为 80，
与当前 C# 实现一致。Top-N 先按分数降序，再按 UTC 修改时间降序；完全相同的候选使用 entry ID
升序作为原生稳定 tie-break。现有 C# 对完全相同 priority 没有产品可见顺序承诺，因此差异测试
避免依赖未定义 tie。

## 4. OrdinalIgnoreCase 兼容

首轮实现使用 `CompareStringOrdinal`，常见中英文样本通过，但扩展差异矩阵立即发现 `ς/σ`：当前
.NET `StringComparison.OrdinalIgnoreCase` 认为两者相等，Windows API 在本机返回不等。该实现已被
撤销，未进入产品路径。

最终 v1 使用 Windows ICU `u_toupper` 的单 scalar 映射，并实现 .NET 当前 ordinal 规则的两个关键
边界：

- ASCII 只在 ASCII 内折叠，非 ASCII 不能折叠成 ASCII；
- 有效 surrogate pair 作为一个 scalar 处理，映射前后必须保持 UTF-16 宽度；无效 surrogate 保持
  原值。

C# 加载器在加载 DLL 前验证五组 canary：final sigma、Deseret、Kelvin sign、dotless i 和 sharp-s。
若用户通过运行时配置切换到不兼容的全球化模式，Rust 模块会明确拒绝加载，不会产生隐蔽结果
差异。Rust 工具链、Windows ICU 和这组 canary 都属于 ABI v1 的语义契约。

## 5. ABI 生命周期

公开头文件为 `native/include/deskbox_search_core.h`。正常调用顺序：

1. `deskbox_search_core_abi_version`；
2. `deskbox_search_core_create_v1`；
3. 一次或多次 `deskbox_search_core_add_batch_v1`；
4. `deskbox_search_core_seal_v1`；
5. 每次查询前 `deskbox_search_core_reset_cancel_v1`；
6. `deskbox_search_core_query_v1`；
7. 对返回的 entry IDs 调用 `deskbox_search_core_copy_entries_v1`；
8. 按需调用 `deskbox_search_core_stats_v1`；
9. 最终恰好一次 `deskbox_search_core_destroy_v1`。

完整导出清单：

- `deskbox_search_core_abi_version`
- `deskbox_search_core_create_v1`
- `deskbox_search_core_add_batch_v1`
- `deskbox_search_core_seal_v1`
- `deskbox_search_core_reset_cancel_v1`
- `deskbox_search_core_cancel_v1`
- `deskbox_search_core_query_v1`
- `deskbox_search_core_copy_entries_v1`
- `deskbox_search_core_stats_v1`
- `deskbox_search_core_destroy_v1`

所有请求/结果 envelope 都包含 size、version 和 reserved 字段。批量导入使用调用方拥有的
`DeskBoxSearchEntryInputV1[]` 与单个 packed UTF-16 缓冲区；查询与文本复制也只写调用方拥有的固定
容量缓冲区。Rust 的 `String`、`Vec`、allocator 指针或异常都不会跨 ABI。

## 6. 并发与取消

构建、封存和查询由调用方串行化。`cancel` 是唯一允许与查询并发的操作，只写一个 `AtomicBool`；
查询循环逐条读取该标志并返回 `DESKBOX_SEARCH_STATUS_CANCELLED`。下一次查询必须先 reset。C# 桥
将 `CancellationToken` 注册为并发 cancel，并在进入原生查询前处理已经取消的 token。

句柄只能由创建它的模块销毁一次。调用方不得在查询、复制或 cancel 尚未结束时销毁句柄。Release
与 Debug Rust profile 都使用 `panic=abort`，实现不得依赖 unwinding 穿越 C ABI。

## 7. 内存统计

`deskbox_search_core_stats_v1` 返回以下 tracked capacity：

- entry 描述符；
- directory 描述符；
- directory UTF-16 arena；
- file-name UTF-16 arena；
- 构建期 lookup；
- 上述项目合计。

统计不声称包含 allocator 元数据、DLL 映像或进程共享页。20,000 条、同一长目录且目录大小写交替的
测试结果为：

- 目录数：1；
- 封存后 build lookup：0 bytes；
- Rust tracked capacity：1,480,124 bytes；
- 当前托管结构仅重复完整路径 UTF-16 字符载荷的保守下限：3,320,000 bytes；
- 比例：44.58%。

这个比较有意对托管侧取低估值，没有加入 `Dictionary` buckets、entry、字符串对象头和 GC 对齐；
因此它证明紧凑布局存在明确结构收益，但不能替代独立进程工作集/Private Bytes 基准。

## 8. 阶段 6A 未覆盖范围

- 没有增量 upsert/remove、watcher journal 或 tombstone compaction；
- 没有直接读取当前 DBIX 持久化索引；
- 没有 300,000 条 Release P50/P95 与取消延迟基准；
- 没有切换产品查询、最近文件、常用目录或索引构建所有权；
- 没有将 DLL 加入普通应用或 AOT 发布产物；
- ARM64 尚未构建。

上述限制意味着当前实现不会提高用户正在运行版本的内存，也不会增加双后端常驻。它为下一阶段
提供已经通过语义和 ABI 门禁的原生基础。
