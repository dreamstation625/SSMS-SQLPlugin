# SSMS 22 扩展接入：已验证事实与踩坑记录

> 2026-11-11 ｜ 环境：SSMS 22.11.12328.437 / VS 18.11.3 (2026) / net48
> 目的：把「M0-1 扩展能否加载」这一轮排查中所有**已证实**的东西记下来，避免重复踩。

---

## 1. 结论（已解决）

**根因：pkgdef 里 `InprocServer32` 指向了托管 DLL，而它必须指向 `$WinDir$\SYSTEM32\MSCOREE.DLL`。**

托管包的 COM 注册是"进程外/垫片式"的：`InprocServer32` 要指向 .NET 的 COM 垫片 `mscoree.dll`，由它再去按 `Assembly` + `Class` + `CodeBase` 加载真正的托管程序集。直接指向托管 DLL，COM 激活会失败，**且完全静默**——注册表里所有键值看起来都正确，Shell 也不报错，只是永远不创建包。

另一个连带错误：后台加载的键名是 **`AllowsBackgroundLoad`**（不是 `AllowsBackgroundLoading`）。

修正后（0.0.5）首次启动即加载成功，实测日志：

```
=== InitializeAsync 进入（自动加载路径）===
扩展程序集 : ...\Extensions\ocnybrrt.303\SsmsSqlPlugin.Probe.dll
程序集版本 : 0.0.5.0
宿主进程   : SSMS / x64 / CLR 4.0.30319.42000
SSMS.exe   : 22.11.12328.437
Shell 15.0 : 18.0.0.0 @ ...\PublicAssemblies\Microsoft.VisualStudio.Shell.15.0.dll
```

---

## 2. 目录与缓存机制（已实测）

| 项 | 实际值 |
|---|---|
| 安装目录 | `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\` |
| 安装级扩展目录 | `...\IDE\Extensions\<名字>\` —— **只由安装程序/`/setup` 合并 pkgdef，启动时不扫描** |
| 用户级扩展目录 | `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<InstallationID>\Extensions\<名字>\` —— **启动时扫描，可写，不需要管理员** |
| InstallationID | 见 `...\IDE\SSMS.isolation.ini` 的 `InstallationID=a832b12e` |
| 扩展元数据缓存 | 上面那个 `Extensions\ExtensionMetadata2.0.mpack` |
| 私有注册表 | `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<id>\privateregistry.bin`（运行时独占锁定，读不了） |
| 缓存重建触发 | `Extensions\extensions.configurationchanged` 的**时间戳**（0 字节标记文件）。手动 touch 它即可让 Shell 重建缓存 |
| 活动日志 | `%APPDATA%\Microsoft\SSMS\22.0_<id>\ActivityLog.xml`，**UTF-16 编码**，必须用 `SSMS.exe /log` 启动才会重新生成 |

> 注意：SSMS 的用户数据根是 `AppData\Local\Microsoft\SSMS\`，**不是** `AppData\Local\Microsoft\SQL Server Management Studio\`。后者只有 Roaming 下放了 UserSettings.xml。

---

## 3. 架构与版本陷阱（踩过的坑）

### 3.1 引用程序集版本：17 和 18 都能用

| 位置 | AssemblyVersion |
|---|---|
| `...\IDE\PublicAssemblies\Microsoft.VisualStudio.Shell.15.0.dll` | 18.0.0.0 |
| NuGet `Microsoft.VisualStudio.SDK 17.0.32112.339` | 17.0.0.0 |

`SSMS.exe.config` 里有绑定重定向 `<bindingRedirect oldVersion="0.0.0.0-18.0.0.0" newVersion="18.0.0.0" />`，
所以两者运行时都解析到 18.0.0.0。**版本不是加载失败的根因**（早期误判过一次）。
实际跑通的是 NuGet 17.x 编译、运行时由重定向到 18.0.0.0。

### 3.2 pkgdef 的正确写法（这一段错了就是静默失败）

```
[$RootKey$\Packages\{包GUID}]
@="显示名"
"Class"="命名空间.包类"
"Assembly"="程序集名, Version=..., Culture=neutral, PublicKeyToken=null"
"InprocServer32"="$WinDir$\SYSTEM32\MSCOREE.DLL"      <- 必须是这个，不是托管 DLL
"CodeBase"="$PackageFolder$\xxx.dll"
"AllowsBackgroundLoad"=dword:00000001                  <- 注意不是 AllowsBackgroundLoading

[$RootKey$\Menus]
"{命令集GUID}"=",Menus.ctmenu,1"

[$RootKey$\AutoLoadPackages\{UICONTEXT GUID}]
"{包GUID}"=dword:00000002
```

**`AutoLoadPackages` 是「上下文 → 包」，键名放 UICONTEXT，值名放包 GUID。写反了不报错，只是永远不触发。**

- SSMS 自己的上下文：`{d114938f-591c-46cf-a785-500a82d97410}`
- NoSolution：`{ADFC4E64-0397-11D1-9F4E-00A0C911004F}`
- SolutionExists：`{F1536EF8-92EC-443C-9ED7-FDADF150DA82}`

> pkgdef 里**不要用 `;` 当注释**。微软自己的 pkgdef 用 `//`，或干脆不写注释。

### 3.3 VSIX 清单

```xml
<InstallationTarget Id="Microsoft.VisualStudio.Ssms" Version="[22.0,]">
  <ProductArchitecture>amd64</ProductArchitecture>
</InstallationTarget>
```
- **`ProductArchitecture` 必须有**，否则 VSIXInstaller 报「此扩展不能安装在任何当前安装的产品上」
- 版本区间用开区间 `[22.0,]`（微软 SSMS 自带扩展都这么写）
- `<Asset Type="Microsoft.VisualStudio.VsPackage" Path="xxx.pkgdef" />`；带 `d:Source="File"` 也能装

### 3.4 正规打包才带 catalog 文件

VSIX v3 的包里应有 `manifest.json` + `catalog.json`（VSSDK 打包器生成），手工 zip 的包没有它们。shell 仍会安装并登记（mpack 里能看到），但资产状态是 `Disabled`——**这条是否致命尚未证实**。

### 3.5 SDK 风格工程 + VSSDK BuildTools 产不出 VSIX

实测：`Project Sdk="Microsoft.NET.Sdk"` 会被解析到 **dotnet SDK 10** 的目标文件，VSSDK 的打包目标被挂进 `PrepareForRunDependsOn`，在 VS 18 的 MSBuild 下不执行；日志里只有编译，没有 `CreateVsixContainer`。

要走官方打包，需要**经典（非 SDK）csproj**：`ToolsVersion` + `Microsoft.CSharp.targets` + 显式 `<Import Project="$(VSToolsPath)\Microsoft.VsSDK.targets" />`。另外 `obj` 删掉后必须 `/restore`，`/t:Rebuild` 不含还原。

---

### 3.6 补全管线：可以直接用 VS 的异步补全（已实测）

**结论**：SSMS 22 的 SQL 编辑器就是标准 `WpfTextView`，**支持 `IAsyncCompletionSourceProvider`**，不需要另找 SSMS 私有补全接口。

实测数据（2026-11-11）：

```
内容类型总数 = 56，SQL 相关只有 1 个，名字就是 "SQL"
IAsyncCompletionBroker.IsCompletionSupported(SQL)  = True
IAsyncCompletionBroker.IsCompletionSupported(code) = False
IAsyncCompletionBroker.IsCompletionSupported(text) = False
```

**接入方式**（三步，缺一不可）：

```csharp
// 1) MEF 导出（注意是 MEF 1 的 System.ComponentModel.Composition）
[Export(typeof(IAsyncCompletionSourceProvider))]
[ContentType("SQL")]
[Name("SsmsSqlPlugin.Completion")]
internal sealed class MyCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    public IAsyncCompletionSource GetOrCreate(ITextView textView) => new MyCompletionSource();
}

// 2) 接口在 Microsoft.VisualStudio.Language 包的
//    Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion 命名空间
//    数据类在 ...AsyncCompletion.Data 命名空间
public interface IAsyncCompletionSource
{
    CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint location, CancellationToken token);
    Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger,
        SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token);
    Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token);
}
```

```xml
<!-- 3) VSIX 清单必须声明 MefComponent，否则 Shell 根本不扫这个 DLL -->
<Asset Type="Microsoft.VisualStudio.MefComponent" d:Source="File" Path="SsmsSqlPlugin.Probe.dll" />
```

**实测回调**（在查询窗口输入 `test`，每字符一次）：

```
[MEF] GetOrCreate 被调用，ContentType = SQL
[补全] InitializeCompletion reason=Insertion 位置=1
...（共 4 次，位置递增）
```

**容易踩的点**：

| 问题 | 处理 |
|---|---|
| `System.ComponentModel.Composition` 找不到 | SDK 风格工程不会自动引用 GAC 程序集，要显式 `<Reference Include="System.ComponentModel.Composition" />` |
| `GetExtensions<T>()` 没有 `.Count` | 它返回 `IEnumerable<T>`，自己 foreach 计数或加 `System.Linq` |
| `GetOrCreate` 不触发 | 先确认 `IsCompletionSupported(内容类型)` 是 True，再确认窗口真的拿到焦点（我这边是因为同时开了 3 个 SSMS 实例抢日志，误判过一轮） |

---

### 3.7 连接契约：`ISqlEditorService` 必须用服务 ID 类获取

**结论**：`ISqlEditorService` 能用，但**必须传 `SSqlEditorService`（服务 ID 类）去 `GetService`，传接口类型拿不到。**

四种获取方式实测：

| 写法 | 结果 |
|---|---|
| `GetService(typeof(ISqlEditorService))` | null |
| `Package.GetGlobalService(typeof(ISqlEditorService))` | null |
| MEF `DefaultExportProvider.GetExports(...)` | 0 个导出 |
| **`GetService(typeof(SSqlEditorService))`** | ✅ 拿到 `...Editors.SqlEditorService` 实例 |

服务 GUID：**`72BB0B14-2D2B-454A-B0AC-1890C2994A22`**
接口在 `SqlWorkbench.Interfaces.dll`（22.200.0.0），实现在 `SqlEditors.dll`。

**接口全部方法**（21 个，反射 dump）：

```csharp
IDbConnection GetCurrentConnection();
string GetActiveEditorMoniker();
ActiveEditorDetails GetActiveEditorDetails();
IDbConnection GetConnectionForSpecificQueryEditor(string moniker);
List<...> ListOpenedQueryEditorCaptionsWithMonikers(bool onlyActiveConnections);
event ... ConnectionChanged;          // 切连接/切库
event ... ConnectionDisconnected;
event ... CaptionChanged;
event ... SqlEditorSettingsChanged;
Color GetActiveEditorCustomConnectionColor();
Color GetCustomConnectionColorForSpecificQueryEditor(string moniker);
void NotifyCaptionChanged(object docView, string newOwnerCaption);
UIConnectionInfo GetUIConnectionInfoForSpecificQueryEditor(string moniker);
SqlEditorSettings GetSqlEditorSettingsForSpecificQueryEditor(string moniker);
bool ChangeCurrentDatabase(string databaseName);
bool ChangeCurrentConnection();
Task SetSqlCmdModeAsync(object docView, bool enable);
```

**实测输出 A：未连接状态**（2026-11-11 12:01）：

```
GetActiveEditorMoniker()              -> H:\code\SSMS-SQLPlugin\tmp\probe.sql   ← 服务是活的
GetActiveEditorDetails()              -> ActiveEditorDetails 实例
GetActiveEditorCustomConnectionColor()-> Color [Empty]
GetCurrentConnection()                -> null                                   ← 未连接，符合预期
```

**接入注意**：

- 接口是 **SSMS 内部类型，不要编译期引用**（版本一变就炸）。用反射或 `dynamic`，把 `Type` / `MethodInfo` 缓存在一个适配器里，所有调用包 try/catch 并支持降级。
- `GetActiveEditorMoniker()` 返回的是**编辑器标识**（本例是文件路径），可作为多查询窗口的 key，配合 `GetConnectionForSpecificQueryEditor(moniker)` 做按窗口区分。
- 事件名 `ConnectionChanged` / `ConnectionDisconnected` 直接对应"切库"和"断连"，是做元数据缓存失效的钩子。

**实测输出 B：已连接状态**（`localhost,11433` / `master` / `sa`，2026-11-11 13:32）：

```
[连接对象] Microsoft.Data.SqlClient.SqlConnection
  DataSource       = localhost,11433
  Database         = master
  State            = Closed                      ← 关键！
  ConnectionTimeout = 15
  WorkstationId    = DREAM-WORK
  PacketSize       = 8000
  ConnectionString = data source=localhost,11433;initial catalog=master;
                     user id=sa;password=***;encrypt=False;trust server certificate=True;
                     application name="Microsoft SQL Server Management Studio - 查询"
  ServerVersion 读取失败: 无效操作。连接被关闭。
  ExecuteScalar 失败: 要求已打开且可用的 Connection
```

**`State = Closed` 是最重要的一点**：`GetCurrentConnection()` 返回的是**连接描述（模板）**，不是已打开的活连接。

- ✅ 可以读 `DataSource` / `Database` / 连接串 → **知道当前连的哪台机、哪个库**
- ❌ **不能直接 `ExecuteScalar`** → 要查元数据必须自己 `new SqlConnection(该连接串)` 另开一条，或调 `Open()`
- ⚠️ 密码在连接串里是明文（本例能读到 `password=`），**任何日志都必须脱敏**

**接入建议**：

```csharp
var conn = InvokeGetCurrentConnection(svc);        // 反射拿
string server   = conn.DataSource;                 // "localhost,11433"
string database = conn.Database;                   // "master"
string cs       = conn.ConnectionString;           // 复制一份，改 Application Name
// 查元数据：自己开一条连接，不要复用这个对象
using (var mine = new SqlConnection(cs)) { mine.Open(); ... }
```
注意 `GetCurrentConnection()` 每次调用返回新对象，**不要持有**，用完即弃；也别指望它保持打开。

**未验证**：有连接时 `GetCurrentConnection()` 的真实返回值。本机 LocalDB 启动失败（`Cannot create an automatic instance`），没有可用数据库实例。

### 3.8 执行捕获（F5）：`AddCommandFilter` + `IVsTextView`

**结论**：能拦截执行命令，也能拿到待执行的 SQL。**全程不需要 `ITextView`**。

**做法**：实现 `IOleCommandTarget`，用 `IVsTextView.AddCommandFilter` 挂到编辑器命令链上。

```csharp
var filter = new CommandFilter(vsView);
IOleCommandTarget next;
int hr = vsView.AddCommandFilter(filter, out next);   // hr=0 成功，next=CommandChainNode
filter.Next = next;   // 记得把下游传下去
```

**F5 对应的命令（实测）**：

| 项 | 值 |
|---|---|
| 命令集 GUID | `1496a755-94de-11d0-8c3f-00c04fc2aae2`（`GUID_VSStandardCommandSet2K`） |
| 命令 ID | `0x0067` (103) |
| 紧随其后 | `52692960-56bc-4989-b5d3-94c47a513e8d` / `0x0001`（SSMS 自己的后续动作） |

两次按 F5 稳定复现同一组，`0x0067` 是执行命令无疑。

**取待执行 SQL**：

```csharp
// 有选区
string selected;
vsView.GetSelectedText(out selected);

// 无选区：取全文
IVsTextLines lines;
vsView.GetBuffer(out lines);
int lastLine, lastIndex;
lines.GetLastLineIndex(out lastLine, out lastIndex);
string all;
lines.GetLineText(0, 0, lastLine, lastIndex, out all);
```

**实测输出**：

```
=== [M0-4] 捕获到执行命令（VSStd2K / 0x0067）===
  执行范围 = 全文（20 字符，共 2 行）
  SQL: SELECT 42 AS answer
```

### 3.9 致命坑：不要在 `AsyncPackage` 的方法签名里用 `ITextView`

**现象**：包完全静默不加载。`probe.log` 里只有 MEF 组件构造函数那一行，`InitializeAsync 进入` 一个字都没有。`tasklist /M` 看不到扩展 DLL 被加载。

**原因**：把 `Microsoft.VisualStudio.Text.Editor.ITextView` 作为参数写进了包的私有方法签名（`InstallCommandFilter(IVsTextView, ITextView)`）。Shell 在创建包类型时需要解析签名，而 `Microsoft.VisualStudio.Text.Editor` 不在它的程序集探测路径里 → 类型加载失败 → 包创建失败，全程无提示。

**验证过程**：加了这个参数 → 0.0.10 三连启动全失败；改回只用 `IVsTextView` → 0.0.11 一次成功。

**规避**：

- 包类（以及它直接引用的类型）的方法签名里，**只用 `Microsoft.VisualStudio.Shell.*` / `TextManager.Interop` / `OLE.Interop` 这些宿主必加载的程序集**
- 需要 `ITextView` 之类时，在**内部私有类**里用，别让它出现在包类型的公开/私有方法参数上
- 同理适用于字段、属性、基类

> 这个坑极难排查：编译通过、pkgdef 正确、文件到位，就是什么都不发生。

### 3.10 存储：直接复用宿主的 SQLite，不需要 AppDomain 隔离

**实测结论**（2026-11-11）：**SQLite 在 SSMS 进程里可以直接用，不需要任何隔离。**

SSMS 22.11 自带一整套 SQLitePCLRaw，位于 `<IDE>\PublicAssemblies\SQLite\`：

```
SQLite-net.dll                          120 KB
SQLitePCLRaw.batteries_v2.dll            15 KB
SQLitePCLRaw.core.dll                    60 KB
SQLitePCLRaw.provider.dynamic_cdecl.dll  76 KB
SQLitePCLRaw.provider.e_sqlite3.dll      50 KB
e_sqlite3.dll                         1.93 MB   ← native，SQLite 3.53.4
```

**验证输出**：

```
已加载 sqlite native 模块数 = 0
Batteries_V2.Init() OK
[Init 后 native] e_sqlite3.DLL @ ...\IDE\PublicAssemblies\SQLite\e_sqlite3.DLL
Init 后 sqlite native 模块数 = 4
SQLiteConnection(:memory:) OK
SELECT sqlite_version() -> 3.53.4
SELECT 1 + 1 -> 2
建表/写入/读回 OK：count=2 first=SELECT 1
```

**为什么不会冲突**：SSMS 用的是 **SQLitePCLRaw 生态**，不是 `System.Data.SQLite`。之前担心的"native DLL 进程级单例会撞"，针对的是后者（有 `SQLite.Interop.dll`），而 SSMS 根本不用那套。只要**我们也用 SQLitePCLRaw 且不打包自己的 native**，就走同一个 `e_sqlite3.dll`，相安无事。

**接入要点**：

- **不要把 SQLite / SQLitePCLRaw 打进 VSIX**，运行时从宿主目录加载
- 不需要独立 `AppDomain`、不需要单独 `.config`、不需要跨域 DTO
- 加载顺序：`LoadFrom(core)` → `LoadFrom(provider.e_sqlite3)` → `LoadFrom(batteries_v2)` → `Batteries_V2.Init()` → `new SQLiteConnection(...)`
- `SQLiteConnection` 的构造函数第一个参数是 string、可能有第二个 `bool storeDateTimeAsTicks`，反射创建时要按实际参数个数组装
- `ExecuteScalar<T>` / `Execute` 的签名带 `params object[]`，反射调用要补 `new object[0]`

**降级路径**（若某版 SSMS 改路径或移除）：

1. 宿主目录找不到 → 用扩展目录自带的 native（此时才需要隔离）
2. 都不行 → append-only JSON 日志 + 内存索引

---

## 4. 菜单（已解决：改用运行时注入）
包能加载，但 `[$RootKey$\Menus]` 注册的命令表**始终不出现在 工具菜单**，Shell 全程不报错。

### 4.1 已确认的事实

| 检查项 | 结果 |
|---|---|
| ActivityLog 有包加载记录 | ✅ `Begin/End package load [SSMS-SQLPlugin Probe]` |
| ActivityLog 有菜单相关记录 | ❌ 零条（`ctmenu`/`Menus`/命令集 GUID 都搜不到） |
| pkgdef 的 `Menus` 段已合并 | ✅ `privateregistry.bin` 里能读到 `{cmdsetGUID}=",Menus.ctmenu,1"` |
| CTC 资源名 | ✅ `Menus.ctmenu`（`Assembly.GetManifestResourceStream` 可读） |
| CTC 内容 | ✅ 反编译回 `.vsct` 后父节点 `IDM_VS_MENU_TOOLS`(0x0085) 正确 |
| CTC 格式与官方一致 | ✅ VSSDK 的 VSCT 目标编译产物与本机手编产物魔数/版本相同（`CFCF` v5） |
| 机器级安装（Program Files + `/setup`） | ❌ 菜单不出现 |
| 用户级安装 | ❌ 同上 |
| **正规安装（VSIXInstaller）+ 正确 pkgdef** | ✅ 装成功、包加载成功、**菜单仍不出现** |
| **CTC 作为独立文件 `Menus.ctmenu` 放扩展目录** | ❌ 无效 |
| **pkgdef 改成 UTF-16 LE（与微软一致）** | ✅ 值确实以 UTF-16 写入 registry，但**菜单仍不出现** |

### 4.2 决定性对照：能用的那个扩展根本不用 CTC

`brink-daniel/ssms-object-explorer-menu` v4.1.2（SSMS 22 有实际用户）的 pkgdef **没有 `Menus` 段**，包里**没有 `.ctmenu` 文件**，DLL 里也**没有 CTC 资源**。

它用了完全不同的机制（反编译字符串可见）：

```
IObjectExplorerService / ContextMenu / TreeView_ContextMenuStripChanged
EnvDTE / EnvDTE80 / get_DTE / ExecuteCommand
```

即：**直接挂 WinForms `ContextMenuStrip` 到 SSMS 的对象资源管理器 TreeView 上**，点击走自己的回调，完全绕开 VS 命令系统。

结论：**SSMS 22 上"能工作的第三方菜单"走的不是 CTC。** CTC 在这条"手搓 VSIX + pkgdef"的路上打不通，投再多时间也不划算。

### 4.3 pkgdef 编码差异（重要，但不是菜单的根因）

微软所有 pkgdef 都是 **UTF-16 LE with BOM**，我们最初存成了 ASCII/UTF-8。字节级对比 `privateregistry.bin` 可见差异：

```
我们（ASCII）：  ,Menus.ctmenu,1        ← 单字节
微软（UTF-16）：, TemplateBrowserMenus.ctmenu, 1   ← 双字节
```

改成 UTF-16 后值类型确实对齐了（`28000000 2c00 4d00 6e00...`），但菜单仍不出现。**说明编码只是必要条件，不是充分条件。** 不过正式版 pkgdef 应该统一用 UTF-16 LE + BOM，与微软保持一致。

### 4.4 最终方案：运行时注入（已实测可用）

**不再使用 CTC。** 在 `AsyncPackage.InitializeAsync` 里切到 UI 线程，用下面三步加菜单：

```csharp
// 前置：await JoinableTaskFactory.SwitchToMainThreadAsync(ct);

// 1) 注册命名命令。注意是 IVsProfferCommands3（1/2 在 SDK 里不可见），
//    签名共 13 个参数，编译器会告诉你准确顺序。
IVsProfferCommands3 proffer = GetService(typeof(SVsProfferCommands)) as IVsProfferCommands3;
uint cmdId = 0;
Guid pkgGuid = new Guid(PackageGuidString);
Guid cmdSet = CommandSet;
int hr = proffer.AddNamedCommand(
    ref pkgGuid, ref cmdSet, "SsmsSqlPlugin.Ping", out cmdId,
    "SSMS-SQLPlugin: Ping", "SSMS-SQLPlugin: Ping", "tooltip",
    null,   // pszBmpFile
    0, 0,   // flags, cUIContexts
    1, 0,   // dwUIElementType(msoControlButton), dwCmdFlags
    new Guid[0]);
// 关键：hr==0 = 新注册，hr==1(S_FALSE) = 命令已存在（跨会话记住）。
//      两者都算成功，只有 hr<0 才是失败。别用 hr != 0 判断。

// 2) 取 CommandBars —— 必须强类型 cast，不能用 dynamic
EnvDTE.DTE dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
var cmdBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
var bar = cmdBars["MenuBar"];              // 顶层 20 个菜单
var controls = bar.Controls;
// 遍历 controls[i].Caption 找「工具(&T)」（本机索引 15）

// 3) 直接创建按钮并订阅 Click —— 不走命令系统
var popup = (Microsoft.VisualStudio.CommandBars.CommandBarPopup)controls[15];
var btn = (Microsoft.VisualStudio.CommandBars.CommandBarButton)popup.Controls.Add(
    Microsoft.VisualStudio.CommandBars.MsoControlType.msoControlButton,
    Type.Missing, Type.Missing, 1, true);   // 第 4 个参数是插入位置，1 = 插到最前面
btn.Caption = "SSMS-SQLPlugin: Ping";
btn.Visible = true;
btn.Click += OnMenuClick;                   // 签名：(CommandBarButton ctrl, ref bool cancelDefault)
```

**三个必须知道的坑**：

| 现象 | 原因 | 处理 |
|---|---|---|
| `IVsProfferCommands` / `IVsProfferCommands2` 编译报 CS0246 | 这两个接口在 VS SDK 里不可见 | 用 `IVsProfferCommands3`（在 `Shell.Interop.8.0.dll`） |
| `dynamic c.Caption` 抛 `RuntimeBinderException: _Marshaler 未包含 Caption 的定义` | SSMS 22.11 的 CommandBars 是 VS 自己的 Automation 包装（`Microsoft.VisualStudio.PlatformUI.Automation.*`），不支持 IDispatch 晚绑定 | **全部改用强类型 interop 接口**，直接调 vtable |
| `AddCommandBarControl` 返回 `0x80004002` (E_NOINTERFACE) | 该 API 对 Automation 包装对象 QueryInterface 失败 | 不用它，直接 `popup.Controls.Add(...)` + `Click` 事件 |

**验收证据**（2026-11-11 11:26，0.0.7）：

```
日志：步骤1 OK IVsProfferCommands
      步骤2 AddNamedCommand hr=0x00000001 cmdId=0x0001（已存在，成功）
      步骤3 强类型 MenuBar OK，Controls 数 = 20
      步骤4 命中工具菜单: 索引 15 Caption=工具(&T)
      步骤4 OK 按钮已挂到工具菜单，Caption=SSMS-SQLPlugin: Ping
UI Automation：工具菜单 13 项，第 1 项 = "SSMS-SQLPlugin: Ping"
点击验证：Invoke 后日志出现「=== 运行时菜单按钮被点击 ===」
```

**结论：菜单问题彻底解决。产品 UI 入口统一走这条运行时注入路径，CTC 相关的 VSCT / Menus 段 / .ctmenu 资源全部不再需要。**

### 4.5 官方打包链在本机的限制

`dotnet build`（.NET Core MSBuild）能跑 VSCT / pkgdef 目标，但走到 `VsixUtil` 时 `Process.Start` 抛异常；`MSBuild.exe`（桌面版）被工具策略拦截。因此本机暂时无法产出官方格式的 `.vsix`，只能手搓 OPC 包。

---

## 5. 已排除清单

| # | 假设 | 排除依据 |
|---|---|---|
| 1 | 扩展目录放错 | 改到用户级目录后，`.mpack` 里出现扩展 ID 与版本号 |
| 2 | pkgdef 未合并 | `privateregistry.bin` 中逐字节找到 `Packages\{GUID}`、`Class`、`Assembly`、`Menus`、`AutoLoadPackages` |
| 3 | `;` 注释导致解析中断 | 去掉注释后现象不变 |
| 4 | 缓存未刷新 | 手动 touch 标记后 `.mpack` 时间戳确实变化，且内容含新版本号 |
| 5 | AutoLoadPackages 方向错 | 已按正确方向重写并确认合并 |
| 6 | 引用了 18.0.0.0 的 Shell | 改用 NuGet 17.0.0.0 后，AssemblyRef 与可用扩展完全一致 |
| 7 | 缺 ProductArchitecture | 补上后安装器不再报"无法安装" |
| 8 | 重复注册 | 安装目录下的旧副本已清除，只剩一份 |
| 9 | 正规安装 + 正确 pkgdef 组合 | VSIXInstaller 静默安装成功，包加载成功，菜单仍不出现 |
| 10 | CTC 作为独立文件 | 放入 `Menus.ctmenu` 到扩展目录，无效 |
| 11 | pkgdef 编码（ASCII vs UTF-16） | 转成 UTF-16 LE + BOM 后值已按 UTF-16 写入 registry，菜单仍不出现 |
| 12 | Tools 菜单 12 项 / 扩展菜单 1 项逐项核对 | 用 UI Automation 直接读菜单树，确认无我们的命令（日志可能不记录 CTC，故改用 UIA 取证） |

**已排除**：`SSMS.exe /setup` 也试过了（`privateregistry.bin` 从 4.7MB 重建为 5.5MB），重启后菜单仍不出现。

> 第 9~12 条是同一结论的不同侧面：**CTC 这条路在 SSMS 22.11 + 手搓 VSIX 的组合下不通**。已改走 §4.4 的运行时注入并验证成功，CTC 相关的 VSCT / `Menus` 段 / `.ctmenu` 资源全部废弃。

---

## 5.1 部署铁律：装完必须刷新配置标记

**现象**：新版本装好、文件也到位，但包完全不加载，日志一个字都没有。

**原因**：Shell 不扫描文件系统，靠 `%LOCALAPPDATA%\Microsoft\SSMS\22.0_a832b12e\Extensions\extensions.configurationchanged` 的时间戳决定是否重建扩展元数据缓存。VSIXInstaller 静默安装**不会**更新这个标记，于是 Shell 继续用旧缓存（指向已删除的旧版本目录和旧 AssemblyVersion）→ 加载失败，静默。

**做法**：每次安装/覆盖文件后，必须

```bash
touch "%LOCALAPPDATA%\Microsoft\SSMS\22.0_a832b12e\Extensions\extensions.configurationchanged"
```

再启动 SSMS。开发期推荐直接覆盖用户级扩展目录 + touch，比跑安装器快得多。

---

## 6. 环境/工具限制（本机特有）

| 限制 | 变通 |
|---|---|
| `api.nuget.org` 曾偶发不可达（返回 000） | 重试即可，实际可用 |
| Bash 里出现 `MSBuild` 字样会被安全策略拦截 | 用 `.bat` 交给用户执行，或改用 `dotnet build` |
| 从 Bash 调 `powershell` 被拦截 | 不要用 PowerShell 自提权，改为提示用户右键以管理员身份运行 |
| MSYS 会吞掉 `/开头` 的参数 | 设 `MSYS2_ARG_CONV_EXCL='*'`，或用 `//out=...` |
| `Program Files` 不可写 | 开发期一律部署到用户级扩展目录，免管理员 |
| `.bat` 必须是 ASCII + CRLF | 用 Python 以 `encoding='ascii'` 落盘并转 CRLF |
