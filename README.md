# Yard

约定优先的 C# 模块化单体 + React/Vite monorepo 开发工具。固定目录、格式化和工作流，减少项目切换时需要记住的差异。当前版本 0.2.0，schema 2。

生成应用使用普通 ASP.NET Core、EF Core、React 和 Vite。生产应用没有 Yard 包、基类或管理命令，可以通过原生命令构建、发布和运行。Yard 的 CLI、Aspire/YARP 宿主及数据库/OpenAPI 辅助进程仅用于开发。

```sh
python3 scripts/pack.py
dotnet tool install Iugam.Yard.Cli --version 0.2.0 --tool-path .temp/tools --add-source .temp/feed
.temp/tools/yard new Example --database sqlite
cd Example
../.temp/tools/yard init
../.temp/tools/yard dev
```

默认数据库为 PostgreSQL；SQLite 不要求 Docker。每个项目只维护选定提供程序的一条迁移链。默认生成可删除的 Notes 示例模块，包含数据库、API、类型客户端和页面。开发统一入口为 http://localhost:8080。

常用命令：`yard fmt`、`yard check`、`yard db generate Name`、`yard db migrate`、`yard contracts`、`yard dev status/stop/restart`。完整行为见 [命令参考](docs/commands.md)，边界见 [规格](docs/specification.md)，实际验收见 [实现记录](docs/implementation.md)。

源码检查：

```sh
python3 scripts/format_csharp.py --check
dotnet build Yard.slnx
dotnet test Yard.slnx
python3 scripts/pack.py
```

0.2 不自动升级 0.1 SaaS 项目。旧框架及验收记录保留在 Git 历史和 [0.1 实现记录](docs/implementation-0.1.md)。不修改原有业务仓库，不自动发布远程包。部署继续交给 ship。
