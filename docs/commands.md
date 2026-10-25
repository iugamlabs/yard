# 命令参考

先安装 Iugam.Yard.Cli 0.1.0。全局命令负责 new，项目命令交给 .config/dotnet-tools.json 中的本地固定版本。

| 命令 | 行为 |
| --- | --- |
| yard new Name [-o path] [--database sqlite] | 仅生成文件；默认 PostgreSQL；非空目录拒绝覆盖 |
| yard install | 恢复本地工具、NuGet 锁定依赖、pnpm 冻结依赖并构建忽略的 Aspire 宿主 |
| yard init | install + 私有本地配置 + db up + 按序迁移；不创建用户 |
| yard add backend/frontend name | 生成标准应用并同步拓扑与锁文件；随后 init |
| yard dev [--app name] | 前台运行所有应用或所选应用及其依赖 |
| yard dev status/stop/restart [--app name] | 使用本 checkout 的令牌控制运行宿主；stop 处理整个所属组 |
| yard build/lint/typecheck/test/check [--app name] | 固定工程流程；check 中共享配置/格式/契约仍按仓库检查 |
| yard fmt [--check] [--file path] | C# 双阶段排版，前端 Prettier；检查不修改源码 |
| yard db up/down | 准备/停止所属 PostgreSQL；SQLite 无进程；停止保留数据 |
| yard db status/check [--app api] [--all] | 查看迁移历史/未应用迁移；检查模型与已提交迁移的一致性 |
| yard db generate Migration [--app api] | 为 SQLite 和 PostgreSQL 分别添加迁移；切换数据库不是转换现有数据 |
| yard db migrate [--app api] [--all] | 先构建目标，使用运行时同一配置迁移；--all 遵循后端依赖顺序 |
| yard db migrate --plan [--app api \| --all] | 按依赖顺序列出各目标待应用迁移；不执行迁移 |
| yard db sql [--app api] [-o file] | 根据当前数据库历史生成待执行迁移 SQL；默认仅 SQL 输出到 stdout，构建日志进入 stderr |
| yard db flush [--app api \| --all] [--yes] | 清空全部应用数据并重置自增计数；保留表结构和迁移历史，恢复 Yard 基础记录 |
| yard db shell [--app api] | PostgreSQL 使用所属容器内 psql；SQLite 使用本机 sqlite3；支持交互和管道输入 |
| yard user bootstrap/create name [--password-stdin] | 首次管理员/临时密码账号；不会启动 HTTP |
| yard user list | 列出主 API 的账号，不输出密码 |
| yard user enable/disable/reset-password/revoke-sessions name | 标准管理模式；重置通过交互或 --password-stdin 取密码 |
| yard contracts [--check] | 管理模式导出 OpenAPI 和类型客户端；检查只验证差异 |
| yard doctor | 检查结构、同步状态和固定工具链 |
| yard sync [--check] | 更新工具管理文件，或只报告漂移 |
| yard upgrade [--plan] [--version 0.1.0] | 检查来源与目标、列出计划并执行已注册迁移；首版无历史迁移 |
| yard verify | 完整工程检查后创建隔离副本，验收桌面/手机浏览器流程并清理运行资源 |

所有相对路径以项目根为基准。运行子进程参数通过参数数组传递；不支持把秘密写到命令行参数。交互密码隐藏输入并确认；脚本通过 --password-stdin 提供。bootstrap 重复执行报冲突且不覆盖已有账号。

数据库操作默认选择主 `api`；`--app` 与 `--all` 互斥，`shell` 和 `sql` 每次只处理一个后端。`flush` 会删除账号、组织、审计和业务数据，不重放业务种子数据；成功后重新运行 `yard user bootstrap owner`。交互需输入 `yes`，取消退出码为 130；重定向输入必须显式使用 `--yes`。清空前必须应用全部迁移；每库事务提交，`--all` 失败后报告已完成目标，不回滚其他库。

`migrate`、`migrate --plan`、`sql` 和 `flush` 拒绝未知或不连续的迁移历史及模型漂移。预览不创建数据库、不应用迁移；数据库未就绪时先运行 `yard init` 或 `yard db up`。`sql -o` 的路径以项目根为基准；无待执行迁移时输出 SQL 注释。本轮不提供离线区间脚本或幂等脚本。

`shell` 不安装客户端、不自动启动数据库；SQLite 缺少 `sqlite3` 时需先安装，文件缺失时不会创建空库。客户端失败保留其退出码。例如 `printf 'SELECT 1;\n' | yard db shell`。交互终端的 Ctrl+C 交给客户端处理当前语句，退出使用 `.quit`（SQLite）或 `\q`（PostgreSQL）。

YARD_PORT=8081 yard dev 可并行启动另一个 checkout；不要复制 .yard。YARD_DEBUG=1 启用本地堆栈；--json 用于解析错误。关闭前台会停止所属应用，PostgreSQL 数据卷保留以便恢复。
