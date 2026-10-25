# Yard 0.1.0 实现与验收记录

记录日期：2026-10-22。源码位于 `/home/deali/code/yard`；本次没有修改原有业务仓库，没有发布到公共 NuGet 源。测试项目、开发库、日志、浏览器截图和本地包均在忽略的 `.temp` 下。

## 交付内容

五个同版本 NuGet 包：`Iugam.Yard.Cli`、`Iugam.Yard.Core`、`Iugam.Yard.AspNetCore`、`Iugam.Yard.DevHost`、`Iugam.Yard.Templates`。CLI 自带本次分发需要的 Yard 运行包和模板；生成项目实际从这些包安装，不通过 ProjectReference 引用 Yard 源码。

已经实现 `new/install/init/add`、开发启动和所属进程控制、工程检查、双阶段 C# 格式化、双提供程序迁移链、账号管理模式、OpenAPI 与类型客户端、规范同步、诊断及受控升级引擎。默认 API/Web 和共享 UI/平台页面包可以直接运行；额外后端保持内部服务，独立 Admin 使用同一平台页面包。

账号底座包含 Identity、Cookie/CSRF、临时密码、用户名/已验证邮箱/已确认手机号登录、会话撤销、SMTP/开发收件箱、验证码与恢复、OpenIddict GitHub 注册/绑定/解绑。组织底座包含多组织成员关系、邀请、权限目录、多角色、所有权转移、归档恢复、平台管理员审计与 EF 查询/写入隔离。

`upgrade` 具有迁移注册、路径限制、预期摘要校验、冲突计划、计划后修改检查和文件回滚机制。0.1.0 是首个规范版本，没有可真实执行的历史升级链：当前版本无变化为 no-op；未知来源、未知目标和受控文件冲突会拒绝修改。这不表示任意旧项目已经可以自动迁移。旧项目 `init` 仅给出整改报告。

## 验收环境与方法

Linux；.NET SDK 10.0.400、Node 24.18.0、pnpm 12.11.2、Docker 29.8、PostgreSQL 18.6；浏览器使用 Playwright 安装的 Chromium。SQLite 开发还在 `DOCKER_HOST=tcp://127.0.0.1:1` 下实际启动，确认不需要可连接的 Docker 服务。

验收先 `scripts/pack.py` 生成 nupkg，再 `dotnet tool install` 安装 CLI，由安装后的 CLI 创建测试项目。每次打包候选均保留日志；失败记录没有改写成通过。最终结果索引见 [验收清单](acceptance-2026-10-22.json) 和 `.temp/delivery.json`；包摘要见 `.temp/feed/SHA256SUMS`。

| 范围 | 已执行内容 |
| --- | --- |
| 源码回归 | 45 项通过，包含严格清单、同步、升级冲突、真实后端请求、并发注册/验证码消费、权限/租户隔离、所有者和最后管理员保护 |
| SMTP | 独立 TCP SMTP 服务器；认证、邮件内容、TLS、默认客户端拒绝不可信证书、发送失败后挑战回滚 |
| GitHub | 本地 HTTP 协议服务器；真实 OpenIddict PKCE、状态/回调、令牌交换、注册、绑定、解绑、防邮箱自动合并和一次性证明 |
| 分发矩阵 | PostgreSQL、SQLite、双后端、双前端；new 非空拒绝、重复 init/bootstrap、应用增加、配置漂移、非法字段、升级 no-op/冲突、完整 check |
| 数据库 | 空库和增量迁移；同时生成两种提供程序迁移；目标选择、依赖顺序、重复迁移、隔离测试、未知历史拒绝、SQL 失败保持 pending、修复重试 |
| 浏览器 | 桌面和手机各 8 项：注册/组织/设置、管理账号、邮箱绑定、CSRF、临时密码与手机号、邮箱恢复、GitHub 协议和同源 HMR |
| 生命周期 | API restart；C# 更新生效、编译错误恢复、异常退出整组失败、所属端口关闭；两 checkout 并行、同浏览器 Cookie 隔离、独立停止、Ctrl+C、端口冲突、显式应用选择 |
| 原生模板 | 独立隔离 hive 安装模板；dotnet new 生成后 init 与 check；模板没有 post action |
| 格式 | CSharpier → Roslyn whitespace 检查通过；生成项目另含 Prettier、ESLint、TypeScript 和契约差异检查 |

`check` 的后端测试在独立数据库运行。`verify` 复制项目到隔离目录，使用独立端口、数据、Data Protection 密钥和 GitHub 协议测试服务；完成后停止其进程并清理测试 PostgreSQL 容器/卷。开发数据库的 `db down` 只停止容器，保留卷。模板尚无业务前端单元用例，Vitest 的空用例退出不计入上述 45 项；平台页面由浏览器测试覆盖。

## 验收中修复的问题

- 离线 OpenAPI 导出曾得到空 paths，现显式建立路由并拒绝空契约；模板包含 45 个实际端点路径及类型声明。
- Aspire 的 `Finished` 状态曾未触发组停止，现使用其终止状态集合并只监控关键资源；退出前释放 Aspire 并等待所属监听端口关闭。
- 全局到本地工具的交接曾在 Ctrl+C 后过早返回，现通过当前 checkout 的受控停止接口与完成标记等待真正清理结束。
- Tailwind 曾扫描整个 apps，包含后端产物；现由清单生成 `packages/ui/src/sources.css`，只扫描前端和共享组件。开发监听固定使用轮询，避免本次真实出现的系统 inotify 额度耗尽；API 关闭无用的静态内容处理。前端轮询为 250ms，代价是少量持续文件检查。
- 同项目名的不同 checkout 曾共享 Cookie 名称，现开发 Cookie 包含 checkout 身份；复制 .yard、变更已初始化数据库提供程序会明确拒绝。

PostgreSQL 最终分发验收首次在并发还原时遇到 MSBuild MSB4166 子节点退出；原失败记录保留。降低验收并发后，同一分发包的全套工程、浏览器和迁移流程通过，没有将首次失败记为通过。

## 未运行及边界

- macOS、Windows 的实际执行尚未进行；已提供三系统 CI 矩阵，不能把编写 CI 视为这些系统已通过。
- 没有使用真实 GitHub 应用凭据或外部 SMTP 账号完成线上联调；本地完整协议/传输测试通过，不等同于真实供应商账号验收。
- 没有迁移已有四个业务仓库，没有接入 ship 部署，没有执行历史版本生产升级。
- 根构建存在 `NU1510` 的 Xml 显式引用裁剪提示；显式版本固定用于避免较旧传递依赖。该提示未当作测试失败隐藏，也未宣称零警告。
- PostgreSQL 镜像下载曾遇本机 Docker Hub 镜像源 429；验收使用官方镜像的公开 ECR 镜像副本导入本地 `postgres:18.6`。工具仍固定该镜像版本，不静默切换数据库版本。

## 复现

```sh
python3 scripts/pack.py
dotnet tool install Iugam.Yard.Cli --version 0.1.0 --tool-path .temp/recheck --add-source .temp/feed
python3 scripts/acceptance.py --tool .temp/recheck/yard --database sqlite
python3 scripts/acceptance.py --tool .temp/recheck/yard --database postgresql --multi
python3 scripts/acceptance.py --tool .temp/recheck/yard --database sqlite --backends 2 --skip-browser
python3 scripts/acceptance.py --tool .temp/recheck/yard --database sqlite --frontends 2 --skip-browser
python3 scripts/process_acceptance.py --tool .temp/recheck/yard
python3 scripts/migration_acceptance.py --tool .temp/recheck/yard --project .temp/acceptance/<双后端测试项目>/project
```

迁移失败验收脚本只允许修改本仓库 `.temp` 内的测试项目。重复测试时使用新 fixture，避免重用已生成同名迁移的项目。所有原始日志保留在对应 results.json 同级 logs 目录或脚本日志中。

## 数据库命令补齐验收（2026-10-25）

新增 `db flush`、`db shell`、`db migrate --plan` 和 `db sql [-o file]`。清空使用提供程序事务并恢复平台设置及账号并发锁；保留表结构、迁移历史和迁移锁，不重放业务种子数据。管理模式支持取消，普通参数解析错误统一返回约定的退出码 2。迁移执行与预览拒绝未知或不连续历史及模型漂移。

本次在 Linux 执行 `dotnet build Yard.slnx`、`dotnet test Yard.slnx`（59 项通过）、`python3 scripts/format_csharp.py --check` 和 `python3 scripts/pack.py`。构建仍有原有的 `NU1510` 提示，没有宣称零警告。模板锁文件中的 Yard 包摘要由打包脚本更新。

最终本地包实际安装在 `.temp/db-distribution3/tools`，使用独立的 CLI home 和 Yard 包缓存，未通过 ProjectReference 绕过分发链。`scripts/database_acceptance.py` 创建双后端项目和另一个同名 checkout，SQLite 56 项、PostgreSQL 59 项命令检查通过；同时断言清空范围、外键/循环引用、自增重置、基础记录及 bootstrap 恢复、失败回滚、迁移历史保留、不同 checkout 数据隔离、模型漂移拒绝及逐库部分完成报告。真实原生终端验证了管道输入、后端选择、退出码、查询中断后继续输入和清空确认期间 Ctrl+C；PostgreSQL 另验证事务等待期间取消及容器归属拒绝。SQLite 客户端仅解包在 `.temp/db-client`，没有安装到系统。

最终结果分别为 `.temp/database-acceptance/sqlite-e2515d90/results.json` 和 `.temp/database-acceptance/postgresql-b693bcc6/results.json`；日志在各自 `logs` 下，交付索引在 `.temp/database-delivery.json`。此前失败记录保留：普通参数错误退出码与确认读取阻塞已修复；验收脚本对 PostgreSQL 事务起始语句及伪终端尺寸的判断也已修正，没有将此前失败计为通过。本次未执行 macOS 或 Windows 验收，没有远程发布包。

复现（`--tool` 指向实际安装的本地包，SQLite 需要 PATH 中有 `sqlite3`，PostgreSQL 需要可用 Docker）：

```sh
export NUGET_PACKAGES="$PWD/.temp/db-distribution3/packages"
export DOTNET_CLI_HOME="$PWD/.temp/db-distribution3/home"
export PATH="$PWD/.temp/db-client/usr/bin:$PATH"
python3 scripts/database_acceptance.py --tool .temp/db-distribution3/tools/yard --database sqlite
python3 scripts/database_acceptance.py --tool .temp/db-distribution3/tools/yard --database postgresql
```
