# 实现架构与扩展

## 包

- Iugam.Yard.Cli：System.CommandLine 命令模型、Spectre.Console 输出、CliWrap 进程执行、隔离 Microsoft.TemplateEngine.IDE Bootstrapper。
- Iugam.Yard.Core：严格清单、应用拓扑、固定版本、同步内容和 checkout 私有设置。
- Iugam.Yard.AspNetCore：标准宿主、Identity、管理模式、邮件与验证码、OpenIddict Client、组织和 RBAC、EF 组织隔离约定。
- Iugam.Yard.DevHost：忽略目录中的 Aspire 宿主支持、YARP 网关、受控 watch 生命周期、日志与状态控制。
- Iugam.Yard.Templates：dotnet new yard-mono 模板，可独立安装。

包同版本构建。CLI 包携带其他 Yard 包和模板内容；install 将其复制到项目本地忽略源，恢复仓库固定的本地工具。测试项目从真实 nupkg 安装，不允许用 ProjectReference 指向 Yard 仓库绕过分发链。

## 后端接入

模板 AppDbContext 继承 YardDbContext，SqliteAppDbContext、PostgresAppDbContext 有独立设计工厂和迁移目录。Hosting/ApplicationHosting.cs 选择固定数据库模式并将业务 AppDbContext 注册到同一 scoped 实例。Program 按序装配标准宿主、模块端点和管理模式。

业务模块在 Modules 下聚合端点、契约、操作、实体和映射；不恢复 Core/Persistence/Adapters/Worker 多生产项目。注册 IYardPermissionProvider 扩展组织权限目录，所有者自动拥有目录中的全部权限。标准命令和身份流程不能通过配置替换。

组织操作示例：先 `await authorization.Require(http, organizationId, "notes.read")`，再查询注入的 AppDbContext。业务实体实现 IOrganizationEntity；模型最终化会追加 Yard.Organization 过滤，不依赖业务 OnModelCreating 的注册顺序。写入检查活动上下文、原始 OrganizationId 与新值；EF 将组织标识放入并发条件。组织内的复合外键也要包含 OrganizationId。平台后台跨组织操作须显式检查 Admin 并记录操作者，不能靠伪造成员完成授权。

管理模式在 WebApplication 建立服务和端点后、Run 之前执行并退出；不会启动 WebApplication、后台服务或监听器。数据库操作通过当前具体 DbContext，OpenAPI 从服务图导出。

## 开发宿主

Aspire 启动固定 dotnet watch、Vite 和 YARP；不允许自定义启动脚本或应用路径。包装进程只负责让 watch 在终止信号后有界停止及识别异常退出；资源依赖、状态、启动/停止仍归 Aspire 管理。控制入口只绑定 loopback，并校验 256 位随机 checkout 令牌。日志订阅按 Aspire ResourceId，不按显示名称。关键应用进入 Aspire 终止状态时停止整组并返回非零；退出前显式释放 Aspire，再等待所属监听端口关闭。CLI 直接运行已构建的宿主 DLL，取消时保留有界清理时间。

YARP 保留 API 和前端前缀，Vite base 与 Router basename 同步；HMR WebSocket 经统一网关。额外后端不会被公开代理，依赖通过 Yard:Services:<name> 提供。数据和进程身份均由 checkout 绝对路径区分。

## 秘密与生产

Development 从 .yard/settings.json 加载 checkout 连接和验证码 HMAC 密钥，再读取私有 appsettings.local.json 和环境变量。生产设置必须来自部署环境；不要提交开发配置。Data Protection 密钥目录应使用持久私有卷。Cookie 在生产要求 HTTPS，修改接口检查 CSRF 与公开 Origin；默认仅信任本机反向代理的 forwarded headers；生产容器网络使用 Yard:TrustedProxies 指定实际代理 IP，Host 仅接受 PublicOrigin 对应主机。

示例变量：Yard__ConnectionString、Yard__VerificationKey（base64 32 字节）、Yard__PublicOrigin、Yard__KeysDirectory、Yard__Mail__Host/Port/Username/Password/FromAddress/FromName/Tls、Yard__GitHub__Enabled/ClientId/ClientSecret。SMTP TLS 固定 StartTls、SslOnConnect，None 仅允许本机测试；正常校验证书。

GitHub 回调：<PublicOrigin>/api/auth/external/github/callback。开发协议测试可使用显式配置的 loopback DevelopmentServer；生产禁止该设置。CI 使用本地协议服务器验证真实 OpenIddict 状态、PKCE、令牌交换、回调和身份操作；不把模拟测试说成持有真实 GitHub 凭据的线上联调。

## 模板与版本

直接模板：dotnet new install /path/Iugam.Yard.Templates.0.1.0.nupkg，然后 dotnet new yard-mono -n Example --database sqlite -o path。安装工具时为其指定同批本地 feed；new 不运行 post action。

sync 使用嵌入 Core 包的固定配置生成受控文件。业务 package.json、Directory.Packages.props 和数据库迁移由项目维护；新增应用只复用模板锁定依赖，不重新解析版本。新增 Yard 版本应显式注册迁移、记录受控文件旧摘要、遇到业务冲突拒绝覆盖；0.1.0 只允许当前版本无变化 no-op，漂移时拒绝覆盖，未知历史一律拒绝。

## 监听范围

Tailwind 关闭自动全仓库扫描；sync 按前端清单生成 sources.css，仅包含声明的前端 src、共享 UI 和平台页面。Vite 固定 250ms 轮询，dotnet watch 固定启用轮询并关闭静态内容处理，避免大量编辑器监听或挂载文件系统导致热更新丢失。该选择依照 [Tailwind 显式源文档](https://tailwindcss.com/docs/detecting-classes-in-source-files)、[Vite watcher 选项](https://vite.dev/config/server-options#server-watch)和 [dotnet watch 环境变量](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch#environment-variables)。
