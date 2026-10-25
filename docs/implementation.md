# Yard 0.2.0 实现与验收记录

日期：2026-10-25。Yard 已收敛为 C# 模块化单体 + React/Vite monorepo 开发工具，使用 schema 2。没有修改原业务仓库，没有发布远程包。0.1 实现与旧验收保存在 [历史记录](implementation-0.1.md) 和 Git 历史中。

## 实现

本地分发五个同版本包：Iugam.Yard.Cli、Core、DotNet、DevHost、Templates。删除了 AspNetCore SaaS 运行库、账号/组织/RBAC、邮件/OAuth、平台页面和 user 命令。

生产应用直接使用 ASP.NET Core、EF Core 和 OpenAPI，采用普通 AppDbContext、标准设计时工厂及项目自己的 AddApplication/UseApplication。生产项目没有 Yard 包、基类或管理分支，不读取 .yard 或 yard.json。标准配置入口为 ConnectionStrings:Default。

数据库与契约操作改由 .yard/tools/<app> 中的开发辅助控制台执行，引用应用与 Iugam.Yard.DotNet，应用不反向引用工具。数据库操作不执行应用入口；契约导出不创建数据库、不启动 HTTP 或后台服务。Aspire/YARP、控制令牌、所属进程与数据库隔离继续保留。未声明 admin 时 /admin/ 返回 404。

创建时选择 PostgreSQL 或 SQLite，只生成对应提供程序及一条迁移链。默认 Notes 示例贯通持久化、API、生成类型和 React 页面，可删除。前端组件独立导出，HMR 保留未提交输入。初始数据库没有预置业务数据。

数据库历史和模型保护、SQL 纯输出、flush 事务/外键/自增、shell 输入输出、逐库部分完成报告保留；清空后不恢复框架基础记录或种子。check 与 verify 的测试数据库和隔离仓库均位于 .temp。sync 不覆盖业务源码。

0.1 schema 在写入前明确拒绝。参数校验移到本地工具交接前，刚生成而未初始化的项目也能获得稳定的参数错误退出码。找不到项目与项目清单不兼容分别报告。

## 最终执行结果

环境为 Linux，.NET SDK 10.0.400、Node 24.18.0、pnpm 12.11.2、Docker 29.8，PostgreSQL 使用固定 postgres:18.6 镜像。

最终分发先运行 `python3 scripts/pack.py`，再从实际 nupkg 安装 CLI。CLI home 与 NuGet 缓存同时隔离在 `.temp/final-0.2.0`；已核实工具解析缓存指向该目录，以及实际恢复的 Core/DotNet/DevHost 包与 feed 字节一致。没有通过 Yard 源码的 ProjectReference 绕过包分发。

| 范围 | 实际结果 |
| --- | --- |
| 源码 | C# 格式检查通过；`dotnet build Yard.slnx` 通过，零警告；`dotnet test Yard.slnx` 34 项通过 |
| 前端 | 每个生成前端 4 项实际客户端测试通过；格式、lint、TypeScript 与构建通过 |
| SQLite 完整分发 | 55 项检查通过，含隔离 verify |
| PostgreSQL 双后端/双前端 | 59 项检查通过，含隔离 verify、shell 和逐库失败报告 |
| SQLite 双后端/双前端 | 58 项检查通过，浏览器由完整 SQLite 分发用例覆盖 |
| 浏览器 | 两种提供程序各执行桌面/手机 3 项，共每种 6 项通过：持久化/删除、服务端校验错误、HMR 保留草稿 |
| 生命周期 | verify 的后端更新、编译错误恢复、异常退出清理通过；独立并行验收 25 项通过，含 checkout 数据/端口隔离、独立停止、Ctrl+C、锁文件不变、未声明 admin 的 404、应用选择和 Docker 不可用时的 SQLite 启动 |
| 原生模板 | 从最终 Templates nupkg 在独立 hive 执行 dotnet new，再 init/check；后端 2 项与前端 4 项通过 |
| 生产独立性 | 两种数据库的隔离副本均在没有 .yard 和本地工具清单时执行原生恢复、构建、发布和 API 启动；依赖图与发布目录没有 Yard 包/程序集 |
| 数据库保护 | 未知/不连续历史、模型漂移、待迁移时拒绝 flush、预览不写库、历史保留、自增重置、失败/取消回滚、shell 管道及部分目标完成报告通过 |
| 工具边界 | 有/无业务端点的契约导出不构造数据库、不启动后台服务或 HTTP；删除示例端点后的空契约实际导出与检查通过 |
| 所有权与版本 | check 保持源码与锁文件不变、sync 保留业务文件、schema 1 无写入拒绝、同版本升级 no-op 通过 |

最终索引见 [机器可读验收记录](acceptance-2026-10-25.json)，详细日志和 results.json 均在索引列出的 .temp 路径。包摘要位于 `.temp/feed/SHA256SUMS`，交付副本为 `.temp/final-0.2.0/delivery.json`。

## 修复与边界

首次普通沙箱测试无法打开测试宿主套接字，后续在授权环境实际执行通过。前期编译暴露了 PostgreSQL 项目中 EF 的传递版本差异，已显式固定 Relational 版本；生成客户端的实际整数类型也已同步到调用方。

同版本候选包曾因共享的 dotnet 工具解析缓存调用旧候选，单独改变 NUGET_PACKAGES 不足以隔离。其结果没有计入最终验收；最终同时使用全新的 DOTNET_CLI_HOME 和 NUGET_PACKAGES，并核实包内容。全新环境还发现了初始化前的参数诊断与旧 schema 报告问题，修复后重新执行最终矩阵。此前失败日志保留在 .temp/refactor 和对应 results.json 中，没有改写为通过。

本次未执行 macOS、Windows；已更新 CI，但不将 CI 定义当作平台验收。没有迁移 0.1 应用或已有业务仓库，没有执行生产部署或远程包发布。

## 复现

```sh
python3 scripts/format_csharp.py --check
dotnet build Yard.slnx
dotnet test Yard.slnx
python3 scripts/pack.py

# 使用新的两个缓存目录，避免本地同版本候选包的工具解析缓存。
export DOTNET_CLI_HOME="$PWD/.temp/recheck-0.2/home"
export NUGET_PACKAGES="$PWD/.temp/recheck-0.2/packages"
dotnet tool install Iugam.Yard.Cli --version 0.2.0 --tool-path .temp/recheck-0.2/tools --add-source .temp/feed
python3 scripts/acceptance.py --tool .temp/recheck-0.2/tools/yard --database sqlite
python3 scripts/acceptance.py --tool .temp/recheck-0.2/tools/yard --database postgresql --multi
python3 scripts/acceptance.py --tool .temp/recheck-0.2/tools/yard --database sqlite --backends 2 --frontends 2 --skip-browser
python3 scripts/process_acceptance.py --tool .temp/recheck-0.2/tools/yard
```

SQLite shell 验收需要 PATH 中有 sqlite3；本次使用既有 `.temp/db-client/usr/bin/sqlite3`，没有安装到系统。PostgreSQL 需要 Docker。验收脚本只生成 .temp 内的项目并清理其所属进程、容器和测试卷；原始日志持续保留。
