# Yard 0.1 规范

Yard 用固定代码统一 C# 模块化单体和 React monorepo 的开发行为。部署交给 ship。首版为 schema 1、版本 0.1.0；所有包使用 Iugam.Yard 前缀并一起构建。不会自动发布 NuGet，也不会改写现有四个业务仓库。

## 目录与拓扑

每个项目有主后端 apps/api、主前端 apps/web，共享 packages/ui、packages/api-client、packages/platform。额外应用始终位于 apps/<name>。后端只有一个生产 .csproj，内部按 Modules、Infrastructure、Hosting 聚合；测试单独放 tests。Organization 是组织隔离边界，账号可以加入多个组织。

根 yard.json 仅允许 schemaVersion、name、toolVersion、database、backends、frontends、dependencies。未知字段、空值、重复名称、非法应用名和循环依赖报错。应用名为小写 kebab-case，项目名为合法 C# 命名空间。数据库仅 postgresql 或 sqlite，不提供任意任务、路径或脚本覆盖。

```json
{
  "schemaVersion": 1,
  "name": "Example",
  "toolVersion": "0.1.0",
  "database": "postgresql",
  "backends": ["api", "manager"],
  "frontends": ["web", "admin"],
  "dependencies": { "api": ["manager"] }
}
```

应用清单是唯一拓扑来源。解决方案、workspace、工具清单、SDK、标准排版/lint 配置和 Vite 配置由 sync 生成。业务模块、业务依赖和迁移由项目维护。工具不提供 Taskfile、Procfile 或 goreman 扩展入口。

## 运行约定

.NET SDK 10.0.400；Node 24.18.0；pnpm 12.11.2；EF 10.0.13；Aspire 13.6.3；YARP 2.3.0。精确 NuGet 版本见 Directory.Packages.props，前端见模板 package.json 与锁文件。工具只使用仓库固定的本地 CLI 执行已有项目命令。

new 只写文件。init 幂等安装、生成私有配置、准备数据库并执行迁移，不创建用户。dev 不安装包、不更改锁文件、不执行迁移或创建用户；先检查迁移状态与端口。Aspire 模型在 .yard/host 生成，不要求项目手写 AppHost。Vite 自动安装关闭；C# 和 Vite 使用固定轮询监听，Vite 间隔 250ms。Tailwind 扫描目录由前端清单生成，不扫描后端构建产物。

网关默认为 http://localhost:8080。/ 为 Web，/admin/ 为平台页或独立 admin，其他前端为 /<name>/，/api/ 为主 API。额外后端仅通过内部服务地址访问。YARD_PORT 覆盖网关端口。checkout 身份由规范化绝对路径计算；每个 checkout 有独立控制令牌、内部端口、SQLite 文件或 PostgreSQL 容器与持久卷。db down 保留数据；停止与验收清理仅处理所属资源。

生产环境使用持久 Data Protection 密钥目录、HTTPS PublicOrigin、私有 VerificationKey 和连接字符串；通过 ASP.NET Core 环境变量传递。开发 .yard/appsettings.local.json 与所有运行数据忽略 Git。

## 账号与组织

ASP.NET Core Identity、同源 HttpOnly Cookie、CSRF。每次请求检查账号状态与 security stamp。用户名忽略大小写；已验证邮箱、已核实 E.164 手机号支持同一密码登录。默认用户名/密码开放注册。管理员创建的临时密码必须先修改；登录标识、密码、身份绑定、停用和重置均撤销相关会话。首次 bootstrap 与最后一名有效管理员受到事务与并发保护。

GitHub 使用 OpenIddict Client，默认关闭。禁止邮箱自动合并；绑定和解绑必须显式操作。注册选择用户名并确认已验证联系邮箱。无密码用户通过邮箱一次性证明执行敏感操作；拥有本地密码后才能解绑最后一种登录方式。

SMTP 使用 MailKit/MimeKit，可替换 IEmailSender。验证码为安全随机六位数字，五分钟有效，六十秒重发间隔、每次最多五次验证；按用途、邮箱、账号、浏览器与会话绑定，只保存 HMAC 摘要。重发失效旧码；发送失败回滚挑战；成功产生用途限定短期一次性证明。服务端同时执行发送和验证限流。开发收件箱展示实际产生的完整邮件；生产未配置 SMTP 时明确拒绝邮件操作。验证码不进入普通 API 响应、URL 或日志。

组织支持创建、维护、归档恢复和原子所有权转移。唯一所有者不可移除。管理员/成员为初始角色，自定义组织角色从权限目录选择，可分配多个角色。角色变更即时生效。站内邀请只发送给已有账号，支持过期、重发、撤销和接受；不提供邀请注册。平台管理员与组织成员分离，平台管理操作记录真实操作者，不隐式冒充组织成员。

业务组织实体实现 IOrganizationEntity。服务端先调用 OrganizationOperations.Require 建立显式组织上下文；模型最终化约定对业务后注册实体也施加命名查询过滤和写入保护。OrganizationId 不可在更新中改变，并作为并发条件防止脱离跟踪的跨组织写入。IgnoreQueryFilters/原始 SQL 仅限经过显式授权的内部操作。

## 工程与升级

C# 排版固定为 CSharpier 后 Roslyn whitespace；前端固定 Prettier。fmt --check 只检查，不改源码。check 包括同步、格式、lint、类型、契约、构建和测试；后端测试迁移到隔离数据库，不复用开发业务库。contracts 由管理模式导出 OpenAPI，再运行 openapi-typescript，不启动 HTTP 或后台任务。

PostgreSQL、SQLite 各有迁移链。generate 先构建目标后端，并为两个提供程序分别生成迁移；migrate 用运行时同一配置执行。启动只检查状态，不自动迁移；未知迁移历史拒绝应用。--all 按依赖顺序逐库执行，失败停止并报告已完成目标，不承诺跨数据库回滚。

数据库操作默认主 api，--app 与 --all 互斥。migrate、migrate --plan、sql 和 flush 共用迁移历史与模型检查，拒绝未知或不连续历史及模型漂移。migrate --plan 仅列出待应用迁移；sql 根据当前数据库历史生成待执行 SQL，不提供离线区间或幂等脚本，stdout 保持纯 SQL、构建日志进入 stderr。预览不创建或启动数据库，不应用迁移。

flush 清空目标库所有应用表，包括账号、组织、审计及原始 SQL 创建的表；保留结构、迁移历史、数据库系统表及迁移锁表，重置所属自增计数。每库在同一事务内清空并恢复默认平台设置及账号并发锁，不重放业务种子数据；之后可重新 bootstrap。要求全部迁移已应用。交互列出 checkout、提供程序及目标并要求输入 yes，脚本必须使用 --yes；失败或取消回滚当前库。shell 使用所属 PostgreSQL 容器内 psql 或本机 sqlite3，继承终端和标准输入，保留客户端退出码；不安装依赖、不自动启动数据库，也不隐式创建 SQLite 文件。

sync --check 与 upgrade --plan 只读。首版只注册 0.1.0，无历史版本迁移；同版本升级无变化时为 no-op，受控文件漂移视为冲突，未知来源/目标拒绝修改。新增版本必须显式增加迁移与冲突检查，不得跳版本或自动推断数据库转换。非 Yard 仓库 init 给出整改审查，不重写业务架构或数据。

退出码：0 成功，1 操作失败，2 参数/规范错误，130 取消；外部工具失败保留其非零退出码。--json 输出结构化错误诊断；YARD_DEBUG=1 在本地显示异常堆栈，不默认泄露配置。
