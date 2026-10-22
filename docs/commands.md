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
| yard user bootstrap/create name [--password-stdin] | 首次管理员/临时密码账号；不会启动 HTTP |
| yard user list | 列出主 API 的账号，不输出密码 |
| yard user enable/disable/reset-password/revoke-sessions name | 标准管理模式；重置通过交互或 --password-stdin 取密码 |
| yard contracts [--check] | 管理模式导出 OpenAPI 和类型客户端；检查只验证差异 |
| yard doctor | 检查结构、同步状态和固定工具链 |
| yard sync [--check] | 更新工具管理文件，或只报告漂移 |
| yard upgrade [--plan] [--version 0.1.0] | 检查来源与目标、列出计划并执行已注册迁移；首版无历史迁移 |
| yard verify | 完整工程检查后创建隔离副本，验收桌面/手机浏览器流程并清理运行资源 |

所有相对路径以项目根为基准。运行子进程参数通过参数数组传递；不支持把秘密写到命令行参数。交互密码隐藏输入并确认；脚本通过 --password-stdin 提供。bootstrap 重复执行报冲突且不覆盖已有账号。

YARD_PORT=8081 yard dev 可并行启动另一个 checkout；不要复制 .yard。YARD_DEBUG=1 启用本地堆栈；--json 用于解析错误。关闭前台会停止所属应用，PostgreSQL 数据卷保留以便恢复。
