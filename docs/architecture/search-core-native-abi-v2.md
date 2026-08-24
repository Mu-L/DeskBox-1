# DeskBox SearchCore 原生 ABI v2

- 日期：2026-08-23
- 模块：`deskbox_search_core.dll`
- 目标：`x86_64-pc-windows-msvc`
- ABI：2
- 结构版本：1
- 产品状态：独立实验模块，未加入普通 Debug、Native AOT 或安装产物

## 1. v2 增量

ABI v2 完整保留 v1 的 create/add-batch/seal/query/copy/stats/destroy 契约，新增：

- `deskbox_search_core_open_dbix_v1`：直接打开当前 DeskBox DBIX v1 文件并返回已封存句柄；
- `DESKBOX_SEARCH_STATUS_IO_ERROR`；
- `DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT`；
- `DESKBOX_SEARCH_STATUS_CORRUPT_DATA`。

当前必需导出共 11 个。生产 `deskbox_native.dll` 是另一模块，继续保持其既有 ABI 2、能力 511 和
十导出，本 ABI 升级不会改变已完成的主程序 AOT 门禁。

## 2. DBIX 直载边界

`DeskBoxSearchOpenDbixRequestV1` 只接受调用方拥有的绝对 UTF-16 路径、最大 entry 数和可选 Windows
event。Rust 自己打开并顺序读取文件，直接填充：

- 固定宽度 entry 数组；
- 目录 descriptor 数组；
- 目录 UTF-16 arena；
- 文件名 UTF-16 arena。

DBIX 已经包含目录池，因此直载不会创建目录 hash lookup，也不会恢复托管完整路径
`Dictionary<string, ...>`。成功返回的句柄已经封存，`build_lookup_capacity_bytes` 必须为 0。

解析期间的临时对象只有 64 KiB 文件缓冲、单个可复用 UTF-8 字节缓冲和最终四块紧凑存储。Rust
不会先构造第二份完整路径集合。

## 3. 当前 DBIX v1 格式

按 little-endian 顺序解析：

1. `0x58494244`，即 `DBIX`；
2. `int32` 版本，当前必须为 1；
3. `int64` 持久化 UTC ticks；
4. `int32` 目录数；
5. 每个目录使用 .NET `BinaryWriter.Write(string)` 的 7-bit UTF-8 字节长度和正文；
6. `int32` entry 数；
7. 每个 entry 包含目录 ID、`int32` UTF-8 文件名字节数、文件名、Boolean 和
   `DateTime.ToBinary()` 值。

文件上限 128 MiB，entry 上限 300,000，单字符串 UTF-8 上限 1 MiB，两个 UTF-16 arena 分别不超过
64M code units。负数、越界目录 ID、无效 UTF-8、截断、尾随数据和空 entry 集都返回明确失败。

## 4. 时间语义

当前索引来源是 `FileInfo.LastWriteTime`，其 DBIX 值通常为 Local `DateTime.ToBinary()`。对于 Local
值，.NET 把 UTC ticks 存入低 62 位；Rust 按同一 wrap 规则恢复，用于搜索日期排序。UTC 值直接保留
ticks，既有 `DateTime.MinValue` 零 sentinel 也被接受。

非零 Unspecified `DateTime` 需要当前时区转换。v2 不进行近似转换，而是返回
`UNSUPPORTED_FORMAT`，要求托管侧重建或回退，避免日期排序悄然变化。该选择依据 [.NET DateTime
FromBinary/ToBinary 源码](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/System/DateTime.cs)。

## 5. 原子失败与取消

解析始终在尚未公开的临时 `SearchCore` 中完成。只有完整验证、EOF 检查和封存全部成功后才把句柄
写入结果；任何失败的 `handle` 都必须为 null，因此调用方不会观察到半索引或空搜索后端。

可选 `cancel_event` 是调用方在整个调用期间保持有效的 waitable Windows event。Rust 在文件 I/O 前、
每 256 个目录/entry 以及提交前做零等待检查；已触发时返回 `CANCELLED` 且不返回句柄。它不跨 ABI
保存托管 delegate，也不要求调用方共享非可移植的原子内存。

## 6. 所有权与并发

- 成功句柄只由本 DLL 的 `deskbox_search_core_destroy_v1` 销毁一次；
- open/build/query/copy/stats 由调用方串行化；
- 已有 query cancellation 仍是唯一允许与 query 并发的句柄操作；
- open 的 event cancellation 不依赖尚未创建的句柄；
- panic policy 为 abort，Rust unwind、allocator 指针、`String` 和 `Vec` 都不跨 C ABI。

## 7. v2 未覆盖范围

- 没有增量 upsert/remove；
- 没有 recent files/frequent folders 原生投影；
- 没有 watcher overflow/reconciliation 事务；
- 没有 ARM64；
- 没有产品 opt-in 或默认切换。

这些能力进入下一阶段 6C。在它们完成前，产品仍只使用当前 C# `SearchIndexService`，不会同时常驻
C# 和 Rust 两份索引。
