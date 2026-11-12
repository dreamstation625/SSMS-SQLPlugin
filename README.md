# SSMS-SQLPlugin

SQL Server Management Studio 的格式化、补全和历史工具。

仓库：<https://github.com/dreamstation625/SSMS-SQLPlugin>

> 非官方第三方扩展，与 Microsoft 无关。使用前请先备份重要查询，风险自负。

## 功能

- **SQL 格式化**：`Ctrl+K, Ctrl+D` 格式化全文，`Ctrl+K, Ctrl+F` 只格式化选区；保留注释和空行。
- **补全**：按当前数据库补全表、字段和常用类型，`Tab` 提交。
- **SQL 历史**：记录执行和编辑。支持搜索、筛选、预览、复制、在新查询窗口打开、多选删除和回收站（30 天）。
- **文件快照**：打开或保存 SQL 文件时记录版本和路径。
- **设置**：缩进、SELECT 列表风格、子查询展开、空格后关键字自动大写、自动记录策略。
- **入口**：顶部 `SSMS-SQLPlugin` 菜单、编辑器右键菜单、刷新补全缓存、打开日志、检查更新。

![格式化示例](docs/images/ssms-format-demo.png)

## 兼容性

| 项 | 要求 |
|---|---|
| SSMS | 22.x |
| 系统 | Windows 10 / 11 x64 |
| .NET | 安装器与插件基于 .NET Framework 4.8 |

SSMS 21 / 20 尚未适配。

## 安装

1. 从 Releases 下载 `SSMS-SQLPlugin-Setup.exe`。
2. **关闭所有 SSMS 窗口**。
3. 双击安装器，点「安装 / 更新」。
4. 启动 SSMS，顶部菜单会出现 `SSMS-SQLPlugin`。

安装器会调用 SSMS 自带的 `VSIXInstaller.exe` 注册扩展，安装和卸载前都需要关闭 SSMS。调用期间会显示“正在调用 VSIXInstaller”的遮罩和进度条，请不要重复点击或关闭窗口。如果 360 等安全软件提示 VSIXInstaller 要修改 `SSMS.exe.config`，请按提示放行，否则安装会被拦截。

当前构建未做代码签名，Windows SmartScreen 可能提示未知发布者。请确认文件来自本项目 Releases 后再继续。

![安装器](docs/images/installer.png)

## 卸载

- 在 Windows「设置 → 应用」中找到 `SSMS-SQLPlugin` 卸载，或
- 运行 `%LOCALAPPDATA%\SsmsSqlPlugin\Installer\SSMS-SQLPlugin-Setup.exe`，点「卸载」。

卸载只删除扩展文件，保留历史记录和设置。

## 使用

| 操作 | 方式 |
|---|---|
| 格式化全文 | `Ctrl+K, Ctrl+D` 或菜单「格式化 SQL」 |
| 格式化选区 | `Ctrl+K, Ctrl+F` 或菜单「格式化选定内容」 |
| SQL 历史 | 顶部菜单「SQL 历史...」 |
| 多选删除/恢复 | 列表内 `Ctrl/Shift` 多选、`Ctrl+A` 全选、`Delete` 删除 |
| 复制 SQL | 列表内 `Ctrl+C` 或「复制 SQL」 |
| 打开 SQL | 双击记录或「打开 SQL」 |

![SQL 历史](docs/images/history.png)

![关于](docs/images/about.png)

## 数据位置

| 内容 | 路径 |
|---|---|
| 设置 | `%LOCALAPPDATA%\SsmsSqlPlugin\options.json` |
| 自动记录配置 | `%LOCALAPPDATA%\SsmsSqlPlugin\autosave.cfg` |
| 历史数据库 | `%LOCALAPPDATA%\SsmsSqlPlugin\data.db` |
| 日志 | `%TEMP%\SsmsSqlPlugin\plugin.log` |

所有数据都在本机，不上传网络。

## 从源码构建

环境：

- .NET SDK 10
- Python 3.12
- Windows

构建 VSIX 和安装器：

```bash
python src/SsmsSqlPlugin.Vsix/build.py --no-deploy --installer
```

产物：

- `src/SsmsSqlPlugin.Vsix/dist/SSMS-SQLPlugin.vsix`
- `src/SsmsSqlPlugin.Vsix/dist/SSMS-SQLPlugin-Setup.exe`

开发时直接部署到本机 SSMS（需要先关闭 SSMS）：

```bash
python src/SsmsSqlPlugin.Vsix/build.py
```

## 回归测试

```bash
dotnet run --project tools/FormatCli -- --quiet
dotnet run --project tools/FormatCli -- --dir tools/FormatCli/Corpus
```

第一组是内置格式化用例，第二组是复杂 SQL 语料（CTE、MERGE、控制流、DDL、注释、Unicode 等）。

## 已知限制

- 函数名大小写、别名纵向对齐未做。
- 游标 `DECLARE ... FOR SELECT` 的 body 未额外缩进。
- SSMS 21 / 20 暂不支持。
- 当前 Release 为未签名构建。

## 免责声明

本项目是第三方 SSMS 扩展，不受 Microsoft 支持。格式化会修改编辑器中的 SQL 文本（可用 `Ctrl+Z` 撤销），自动记录会在本机保存 SQL 内容。请在符合你所在组织安全要求的前提下使用。
