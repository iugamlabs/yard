# Yard

约定优于配置的 C# 模块化单体 + React monorepo 工具。统一项目结构、开发编排、数据库和账号管理、工程检查及脚手架。命令为 `yard`，所有 NuGet 包使用 `Iugam.Yard.*`，当前版本为 `0.1.0`。

默认生成 `apps/api`、`apps/web` 和共享 UI、API 客户端、平台页面包；支持增加标准后端和前端。开发使用 Aspire + YARP，统一入口为 `http://localhost:8080`。PostgreSQL 为默认数据库，SQLite 模式不需要 Docker。部署继续交给 ship。

## 本地安装与开始使用

需要 .NET SDK **10.0.400**、Node **24.18.0**、pnpm **12.11.2**；PostgreSQL 模式还需要可用的 Docker。`yard doctor` 检查固定工具链。

在 Yard 仓库构建本地 NuGet 分发包：

```sh
python3 scripts/pack.py
dotnet tool install Iugam.Yard.Cli --version 0.1.0 --tool-path .temp/tools --add-source .temp/feed
.temp/tools/yard new MyProject --database sqlite --output .temp/my-project
cd .temp/my-project
../tools/yard init
../tools/yard user bootstrap owner
../tools/yard dev
```

交付时 `.temp/tools/yard` 已安装。重新安装同一路径前可先运行 `dotnet tool uninstall Iugam.Yard.Cli --tool-path .temp/tools`。需要全局命令时，从同批 feed 安装：

```sh
dotnet tool install --global Iugam.Yard.Cli --version 0.1.0 --add-source /home/deali/code/yard/.temp/feed
yard new Example --output /path/to/example
```

`new` 只生成文件。`init` 恢复依赖、准备本 checkout 的数据库并迁移，不创建用户；首次管理员通过 `user bootstrap` 显式创建。密码默认隐藏交互输入；脚本使用 `--password-stdin`。进入项目后，命令交给仓库固定版本的本地工具。

## 固定行为

- `yard dev` 启动全部应用；`--app name` 启动所选应用及其依赖。`YARD_PORT=8081 yard dev` 可并行运行另一 checkout。`dev status/stop/restart` 只控制当前 checkout。
- `yard db generate Name` 同时维护 PostgreSQL、SQLite 迁移链；`db migrate --all` 按依赖顺序逐库执行。`db down` 保留数据。启动不会自动迁移。
- `yard db migrate --plan` 查看待执行迁移，`yard db sql [-o file]` 预览对应 SQL。`yard db flush [--yes]` 清空数据并保留结构与迁移历史，随后可重新 bootstrap；`yard db shell` 打开原生客户端（SQLite 需要本机 `sqlite3`）。数据库命令默认主 `api`，可用 `--app` 选择后端。
- `yard fmt` 依次执行 CSharpier、Roslyn whitespace 和前端 Prettier。`check` 检查同步、格式、lint、类型、契约、构建和测试；数据库测试使用隔离资源。
- `yard verify` 在隔离副本中运行桌面/手机浏览器、GitHub 测试协议、HMR、C# 编译恢复、异常退出与端口清理验收，不使用开发业务库。
- `yard sync --check`、`yard contracts --check`、`yard upgrade --plan` 只检查源码差异。首版升级支持当前版本检查和受控迁移引擎；没有历史迁移时明确拒绝未知版本。

内置 Identity、Cookie/CSRF、邮箱验证码与恢复、GitHub 登录、组织、成员邀请和自定义角色。公开用户名注册默认开启，管理员可以关闭。GitHub 默认关闭；配置凭据后启用。开发邮件在 Admin 的收件箱查看；SMTP 和其他秘密放在忽略的 `.yard/appsettings.local.json` 或环境变量中。

## 原生 .NET 模板

```sh
dotnet new install /home/deali/code/yard/.temp/feed/Iugam.Yard.Templates.0.1.0.nupkg
dotnet new yard-mono -n Example --database sqlite -o /path/to/example
```

模板不执行 post action。后续使用同批 Yard CLI 执行 `yard init`。

## 开发和验收 Yard

```sh
dotnet restore Yard.slnx --locked-mode
dotnet build Yard.slnx --no-restore
dotnet test tests/Yard.Tests --no-build
python3 scripts/format_csharp.py --check
python3 scripts/acceptance.py --tool .temp/tools/yard --database sqlite
python3 scripts/acceptance.py --tool .temp/tools/yard --database postgresql --multi
python3 scripts/process_acceptance.py --tool .temp/tools/yard
```

测试项目、数据、日志和包均放在忽略的 `.temp`。分发验收实际安装 NuGet 包，不引用 Yard 源码。构建脚本不发布公共源；现有业务仓库本次未修改。

详见[规范](docs/specification.md)、[命令参考](docs/commands.md)、[架构与扩展](docs/architecture.md)、[实现与验收记录](docs/implementation.md)。
