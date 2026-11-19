# FastFind 设计文档

本文记录 FastFind 的关键设计决策、取舍与被否决的方案，便于后续维护者理解"为什么是这样"。

## 1. 问题定义

需求：在 Windows 上快速按文件名/路径找到本机文件，**不要求管理员权限**，**免安装**，**界面简单**。

已有的 Everything 已经很好地解决了 NTFS + 管理员场景。FastFind 的差异化空间在于：

- 普通用户（无管理员）也要有接近的体验；
- 单文件、便携、不写注册表；
- 无外部依赖（不装 .NET SDK、不装数据库、不装运行时）。

## 2. 架构分层

```
┌──────────────────────────── UI (WinForms) ────────────────────────────┐
│ MainForm   HeaderPanel   ResultList   ColumnHeaderRow   Status/Empty  │
│ SettingsForm  FileTypePopup  NoticeForm   Theme   ShellOpen           │
└───────────────▲───────────────────────────────────────▲───────────────┘
                │ 查询/结果                             │ 索引事件
┌───────────────┴───────────────────────────────────────┴───────────────┐
│ IndexManager   搜索调度（线程池 + generation 取消）   FileSystemWatcher │
├───────────────────────────────────────────────────────────────────────┤
│ SearchEngine (并行匹配/排序)   Query (语法解析)   FileTypes (分类)      │
├───────────────────────────────────────────────────────────────────────┤
│ VolumeIndex (SoA 数组)   Scanner (并行枚举)   CacheStore (v4 二进制)    │
├───────────────────────────────────────────────────────────────────────┤
│ Native (FindFirstFileExW / 卷信息 / 总线类型 / 文件属性)   IndexTime    │
└───────────────────────────────────────────────────────────────────────┘
```

UI 不持有扫描线程；索引发布通过"整卷不可变快照"完成，搜索线程读到的数组永远是一致的旧/新两份之一，不需要读写锁。

## 3. 索引数据布局

每个卷一份 `VolumeIndex`：

| 数组 | 类型 | 说明 |
| --- | --- | --- |
| `Pool` | `char[]` | 所有名称字符首尾相接 |
| `Off` | `int[]` | 名称在 Pool 中的起点 |
| `Len` | `ushort[]` | 名称长度 |
| `Parent` | `int[]` | 父节点下标，0 为卷根 |
| `Flags` | `byte[]` | 目录 / 重解析点 |
| `Category` | `byte[]` | 文件类型位（文档/图片/视频/音频/压缩包/程序/代码/其他） |
| `Size` | `long[]` | 文件大小 |
| `MTime` | `int[]` | Unix 秒 - 2000-01-01（体积小、排序方便） |

**为什么不用对象/字符串**：152 万节点时，`string` 对象头 + 引用 + 堆碎片会让内存和分配开销翻倍；结构数组把名称压成连续字符块，缓存友好，且缓存序列化就是 `Buffer.BlockCopy`。

**路径为什么不存**：路径 = `Parent` 链 + 名称，按需拼出。代价是 `GetPath` 是 O(depth)，但搜索阶段只在命中后处理，成本可忽略；收益是 152 万条路径前缀不重复存储。

## 4. 扫描

- 8 线程工作队列，目录出队后枚举子项；
- `FindFirstFileExW` 使用 `FindExInfoBasic`（不取短名）+ `FIND_FIRST_EX_LARGE_FETCH`（大缓冲）+ `ON_DISK_ENTRIES_ONLY`（不访问云占位符）；旧系统返回 87 时自动降级；
- 目录节点立即追加（保证父下标有效），文件每 4096 条批量合并；
- 跳过重解析点目录（junction/symlink）防止环；
- 结束前 trim 数组，避免容量浪费。

## 5. 缓存 v4

```
magic "FFIX" | version | createdTicks | scanMs | serial | flags
count | poolChars | rootChars | skipped | errors
rootPathChars | pool | Off[] | Len[] | Parent[] | Flags[] | Category[] | Size[] | MTime[]
```

- 先写 `index-X.dat.tmp`，`File.Replace` 原子替换；
- 用卷序列号校验，换盘/格式化自动失效；
- 加载 20–40 ms，远快于重新扫描（1.7–4.1 s / 81 万节点）；
- 缓存目录固定在 exe 旁 `data\index`，不写 `%LOCALAPPDATA%`，保证便携。

## 6. 搜索

```
解析 Query → 快速过滤（类型/扩展名/大小/时间/路径）→ 名称匹配（分块并行）
        → 每线程 5 个 rank 桶 → 合并 → Top-K 选择 → 按名称/大小/时间排序
```

- **rank 桶**：精确命中 > 前缀 > 词边界 > 子串，保证"同名文件"优先于"包含该词的长文件名"；
- **取消**：`_searchGeneration` 单调递增，worker 在每个 chunk 边界比较，代价是一次 volatile 读；
- **Top-K**：先收集部分结果再排序，避免对 15 万命中做全排序；
- **大小/时间/路径过滤在名称匹配之前**，因为它们更便宜。

## 7. 界面

- `ListView` 的 `VirtualMode + OwnerDraw`：只创建可见行的绘制，结果集多大都不建 item；
- 原生表头 `ColumnHeaderStyle.None`，换自绘 `ColumnHeaderRow`，解决"自绘行无法与原生表头对齐/主题不一致"的问题；
- 高 DPI：manifest 声明 `PerMonitorV2`，代码里 `Theme.Scale = DeviceDpi / 96`，所有尺寸走 `Theme.S()`；
- 主题：调色板 + 全量 `ApplyTheme` + DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`；
- 空状态：`Idle / NoMatch / Indexing / NoScope`，明确告诉用户"为什么没结果"。

## 8. 可靠性设计（踩坑记录）

| 现象 | 根因 | 处理 |
| --- | --- | --- |
| 双击结果没反应 | `ShellExecute` 抛 `Access denied`，错误只写进隐藏的空状态面板 | 自定义 `NoticeForm` 弹窗 + 状态栏 + 复制路径 |
| 弹窗叠着崩溃框 | 低权限下兜底启动 `explorer.exe`，其初始化失败（0xc0000142） | 定位改用 `SHOpenFolderAndSelectItems`，不创建进程 |
| 系统 MessageBox 文字被裁 | .NET Framework MessageBox 在 200% DPI 下按 96 DPI 计算尺寸 | 自绘 NoticeForm，按 `Theme.S()` 布局 |
| 从智能体面板启动后无法打开文件 | 进程是 Low Integrity，Windows 禁止其启动其他程序 | 启动检测 `TokenIntegrityLevel`，状态栏与弹窗直接提示"请从资源管理器启动" |
| 空白列表挡住鼠标 | 空状态面板 `Dock=Fill` 且 `BringToFront` | 仅无结果时显示，隐藏时 `SendToBack` |

## 9. 被否决的方案

| 方案 | 否决原因 |
| --- | --- |
| 用 USN/MFT 读 NTFS 日志 | 需要管理员权限，与"普通用户可用"的核心目标冲突；可作后续可选加速 |
| 嵌入 SQLite / Lucene | 依赖体积与写放大，单文件目标不符；顺序数组 + 并行扫描已足够快 |
| 后台常驻服务 + 全局索引 | 免安装/不写注册表约束下体验差，且用户不一定愿意常驻 |
| 用 `AutoScaleMode.Font` 做 DPI | 200% 下多次出现错位，改为显式缩放 |
| 结果表用 `ListView` 全量 item | 152 万行会直接 OOM/卡死 |
| 打开失败用 `explorer.exe` 兜底 | 受限环境下 crash，且"打开成文件夹"是假成功 |

## 10. 基准方法

- `build\IndexBench.exe C:\ 8`：冷扫描、缓存保存/加载、典型查询耗时、峰值内存；
- `acceptance.ps1`：构建 → 元数据 → 引擎测试 → 首次索引 → 单实例 → 关闭保存 → 便携布局 → 范围切换与持久化；
- `tests/functional-test.ps1`：8 类查询语法、自动刷新、Ctrl+C、回收站删除、打开失败必有可见提示、不得出现崩溃框。

基准只在一台机器上跑过（Win11 / 16 线程 / NVMe / C: 81 万节点），不同磁盘与文件数会有差异。
