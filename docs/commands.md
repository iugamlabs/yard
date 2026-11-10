# Yard 0.2 命令参考

全局 CLI 在仓库中交给本地固定版本。`yard new` 只写文件。生产应用运行不需要 Yard。

| 命令 | 行为 |
| --- | --- |
| yard new Name [-o path] [--database sqlite] | 创建 C# + React/Vite 工程，默认 PostgreSQL；非空目录拒绝 |
| yard install | 恢复锁定依赖、本地工具、Aspire 宿主和开发辅助项目 |
| yard init | install + 私有连接配置 + 所属数据库启动 + 按序迁移；不写业务数据 |
| yard add backend/frontend name | 使用模板的匹配依赖与锁定图添加标准应用；普通失败恢复文件，随后运行 init |
| yard dev [--app name] | 启动应用与依赖；检查端口、模型和迁移，不安装或迁移 |
| yard dev status/stop/restart [--app name] | 控制本 checkout；stop 停止所属整组 |
| yard build/lint/typecheck/test/check [--app name] | 固定工程流程；共享格式、同步和契约仍按仓库检查 |
| yard fmt [--check] [--file path] | CSharpier → Roslyn whitespace；前端 Prettier；check 不改源码 |
| yard db up/down | 启停所属 PostgreSQL，保留数据；SQLite 无进程 |
| yard db status/check [--app api \| --all] | 查看迁移/待应用项或检查模型一致性 |
| yard db generate Name [--app api] | 只为所选提供程序创建一条迁移 |
| yard db migrate [--app api \| --all] | 使用应用标准设计时工厂显式迁移 |
| yard db migrate --plan [--app api \| --all] | 列出待应用迁移，不创建、启动或修改数据库 |
| yard db sql [--app api] [-o file] | 根据当前历史导出待执行 SQL；stdout 纯 SQL，构建日志到 stderr |
| yard db flush [--app api \| --all] [--yes] | 事务清空全部应用数据并重置自增，保留结构和历史，不恢复种子 |
| yard db shell [--app api] | 所属容器 psql 或本机 sqlite3，保留终端、输入和退出码 |
| yard contracts [--check] | 无 HTTP/后台服务导出 OpenAPI 和 TypeScript，允许空业务契约 |
| yard doctor | 检查结构、同步、声明及已安装的固定工具链 |
| yard sync [--check] | 更新受控工程配置或只读检查，不覆盖业务代码 |
| yard upgrade [--plan] [--version 0.2.0] | 当前版本 no-op 或报告冲突；没有 0.1 自动升级 |
| yard verify | 在 .temp 隔离副本运行工程、桌面/手机浏览器及生命周期验收 |

数据库默认 api，--app 与 --all 互斥，shell/sql 每次一个目标。--all 按依赖顺序执行，失败报告已完成目标，不承诺跨库回滚。migrate/plan/sql/flush 拒绝未知、不连续历史及模型漂移。

前端标准命令直接使用根 node_modules 的固定工具，不执行项目 scripts。根标准 devDependencies、Node/pnpm 声明必须保持固定，业务依赖可以增加。sync 管理 TypeScript 和 Vite 配置，不覆盖业务 package.json。

flush 要求全部迁移应用，交互输入 yes；重定向输入必须 --yes。shell 不安装客户端、不启动数据库、不隐式创建 SQLite 文件。示例：`printf 'SELECT 1;\n' | yard db shell`。

`YARD_PORT=8081 yard dev` 可用于另一个 checkout；不要复制 .yard。`--json` 输出结构化错误；`YARD_DEBUG=1` 显示本地异常堆栈。旧 user 命令已删除，应用自行选择账号方案。
