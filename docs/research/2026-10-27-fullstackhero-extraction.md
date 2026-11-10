# 从本地 FullStackHero 提取 Yard 框架能力的可行性

日期：2026-10-27。结论性质：本地源码静态分析；可行性判断尚未通过包消费、构建和运行实验验证。

分析对象：`/home/deali/code/2/dotnet-starter-kit`。HEAD 为 `a967537526f3b769c7d268da3ed2a6920af65ce9`，提交日期 2026-10-09。工作区已有 pnpm、依赖及工程文件修改；以下结论针对本轮实际读取的工作区，不把它冒充为未改动的上游 release。分析过程没有修改该副本或业务仓库。

对照对象：Yard 当前 0.2 实现及本次讨论确认的新方向。允许生产应用依赖 Yard，提供 NuGet/npm 能力和源码 blocks；第一版覆盖身份、授权、存储、任务、通知，并以 Snapmixer 为首个应用目标。本文不修改 0.2 规格，也不是已批准的实现计划。

## 判断

**可以基于这些源码建立 Yard 框架，而且有一批值得保留的功能实现和测试。实现方式应是有选择地提取并调整依赖，而不是给完整起步项目改名后直接全部发布。**

需要区分两个目标：

- 把当前项目程序集打成 nupkg：主要是工程打包问题；这还不等于模块具有所需的独立接入和扩展能力。
- 让新应用只引用包，就可以扩展用户、独立选用文件/通知等能力，并使用 Yard 管理：涉及公共契约、实体模型、生命周期和客户端的实质改造。

若尽量沿用 FSH 的 PostgreSQL、多租户、JWT、多 DbContext 和完整 Host，适配较小；若同时要求无多租户、应用自定义用户、SQLite、私有本地文件和纯工程命令，则属于中等到较大的框架提取工作。这不是工期估算，依据见下文。

## 依赖图证明：模块目录不等于最小依赖

对 `.csproj` 的 `ProjectReference` 做递归闭包分析，排除宿主和测试后：

| 起点 | 递归依赖项目数，含自身 |
| --- | ---: |
| Identity runtime | 15 |
| Files runtime | 13 |
| Notifications runtime | 15 |
| 三者合并去重 | 21 |

合并结果包含 11 个 BuildingBlocks、3 个所需 runtime，以及 7 个 Contracts 项目。Contracts 中包含 Auditing、Billing、Chat、Multitenancy。

**这些契约依赖不表示必须运行完整计费和聊天模块。** 但它们说明直接发布现有项目，会把与 Yard 最小能力无关的概念带进包依赖；需要剥离通知中的业务事件处理器、身份中的产品约束及公共基础中的租户设施。

例如，Notifications 项目直接引用 Chat、Multitenancy、Billing 的 Contracts；Storage 引用 Quota、Shared，并引入 Finbuckle；Web 又引用 Jobs、Mailing、Caching、Persistence、Quota。运行时选项关闭某项功能，不会消除编译及包依赖。

证据：[Notifications 项目](../../../2/dotnet-starter-kit/src/Modules/Notifications/Modules.Notifications/Modules.Notifications.csproj)、[Storage 项目](../../../2/dotnet-starter-kit/src/BuildingBlocks/Storage/Storage.csproj)、[Web 项目](../../../2/dotnet-starter-kit/src/BuildingBlocks/Web/Web.csproj)。

## 能力提取评估

| 能力 | 值得保留的实现 | 为 Yard 所需的主要改动 | 相对改造量 |
| --- | --- | --- | --- |
| 身份、授权 | Identity 接入、用户服务拆分、会话/角色/权限操作、验证与测试 | 用户和 DbContext 类型扩展、去租户与订阅规则、默认认证方式和管理入口 | 高 |
| 字节存储 | S3 客户端、预签名地址、内外网签名端点区分、流式下载相关契约 | 拆分配额/租户装饰器、实现可用于私人图片的本地提供程序 | 中 |
| 文件生命周期 | PendingUpload/finalize、访问策略、可见性、删除恢复、清理和搬迁 | 调整 EF/提供程序、任务注册时机、业务 OwnerType 接入、旧产品迁移任务 | 中至高 |
| 异步任务 | Hangfire facade、DI scope、日志和遥测 | 去租户上下文、SQLite 持久化、统一注册与执行边界、面向用户的业务任务状态 | 中 |
| 站内通知 | 收件箱实体、查询/已读 API、前端和实时刷新 | 提取通用写入能力，移出聊天/计费处理器，接入应用通知类型及幂等规则 | 中 |
| 可靠事件 | Outbox/Inbox、事务连接协调、租约/重试、死信 | 去租户、PostgreSQL 之外的行为、与应用事务接入、升级时类型标识 | 高 |
| 前端 npm | fetch 包装、错误处理、刷新去重、认证/实时逻辑 | 去应用级单例、API/凭据存储可配置、SSR 边界、公开导出与包构建 | 中至高 |
| 前端 blocks | 登录、用户管理、文件与通知页面及交互测试 | 替换应用内部 import、路由/导航/品牌/权限接入，建立安装清单 | 中 |
| 开发编排 | Aspire 资源连接和启动依赖有参考价值 | 使用 Yard 的资源归属、端口隔离和 YARP；区分 init/dev/管理操作 | 中 |

“高”指改动会穿透多个模块及数据模型；没有依据将这些改动换算成天数或复用百分比。

## 核心发现与证据

### 用户可以继承，但内部没有跟随应用的用户类型

`FshUser : IdentityUser` 不是 sealed，模型起点与 YardUser 思路相近。但 `IdentityModule` 注册的是 `AddIdentity<FshUser,FshRole>()`；`IdentityDbContext` 固定使用 `MultiTenantIdentityDbContext<FshUser,...>`，构造参数也固定为 `DbContextOptions<IdentityDbContext>`。

在 Identity runtime 的源码统计中，24 个文件提及 FshUser，15 个文件直接提及 `UserManager<FshUser>`，28 个文件提及 IdentityDbContext。简单新增 `ApplicationUser : FshUser` 不会让这些服务自动改用 ApplicationUser。

需要保留有价值的操作逻辑，同时将用户存储、创建用户、上下文映射和服务注册贯穿到应用选择的类型。用户 ID 可以先维持 string；不必同时推广任意主键类型。新增字段进入实体之后，公开 DTO、更新验证和页面仍需明确接入，不能自动完整暴露实体。

证据：[用户实体](../../../2/dotnet-starter-kit/src/Modules/Identity/Modules.Identity/Domain/FshUser.cs)、[IdentityModule](../../../2/dotnet-starter-kit/src/Modules/Identity/Modules.Identity/IdentityModule.cs)、[IdentityDbContext](../../../2/dotnet-starter-kit/src/Modules/Identity/Modules.Identity/Data/IdentityDbContext.cs)、[用户注册服务](../../../2/dotnet-starter-kit/src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs)。

### 多租户已经进入基础设施和普通操作

`BaseDbContext` 继承 Finbuckle 的 MultiTenantDbContext，默认给非全局实体加租户过滤和索引调整。身份、密码和会话服务还会主动校验租户；任务 scope 恢复租户上下文；文件路径与配额也使用租户概念。

因此删除 Multitenancy 模块目录并不能完成“无多租户”改造。

可以比较两条路径：先以固定租户运行来验证上游默认行为，或者真正抽掉租户依赖。前者是更小的验证步骤，仍然保留租户模型及维护成本；不能把它描述为已经完成了单租户框架。

证据：[BaseDbContext](../../../2/dotnet-starter-kit/src/BuildingBlocks/Persistence/Context/BaseDbContext.cs)、[租户模型配置](../../../2/dotnet-starter-kit/src/BuildingBlocks/Persistence/TenantIsolationExtensions.cs)、[会话租户校验](../../../2/dotnet-starter-kit/src/Modules/Identity/Modules.Identity/Services/SessionService.cs)、[任务 scope](../../../2/dotnet-starter-kit/src/BuildingBlocks/Jobs/FshJobActivator.cs)。

### 数据库与迁移不能照搬到当前 Yard 模板

当前 FSH 持久化提供程序分支为 PostgreSQL 和 MSSQL，没有 SQLite 分支。Hangfire 配置也只有这两种持久提供程序。引用 Hangfire.MemoryStorage 不能证明具有 SQLite 持久任务支持。

SQLite 的适配还涉及 jsonb 映射、多个模块 schema、连接构造和迁移；Outbox 的 PostgreSQL 分支使用 `FOR UPDATE SKIP LOCKED`，非 PostgreSQL 分支明确只提供单 dispatcher 安全假设。文件搬迁也在非 PostgreSQL 下退化为无行锁查询。因此不是补一行 UseSqlite 即可。

FSH 有 Identity、Files、Notifications、Eventing 等多个 DbContext，迁移集中在宿主迁移项目并按模块分文件夹。需要决定 Yard 采用框架上下文加业务上下文，还是整合成应用上下文；后者会显著扩大改动，不能作为“打包整理”附带完成。即使放在同一个数据库，多 Context 仍有多条迁移链。

证据：[提供程序分支](../../../2/dotnet-starter-kit/src/BuildingBlocks/Persistence/OptionsBuilderExtensions.cs)、[Hangfire 配置](../../../2/dotnet-starter-kit/src/BuildingBlocks/Jobs/Extensions.cs)、[通知 jsonb 映射](../../../2/dotnet-starter-kit/src/Modules/Notifications/Modules.Notifications/Data/Configurations/NotificationConfiguration.cs)、[Outbox](../../../2/dotnet-starter-kit/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs)、[文件搬迁](../../../2/dotnet-starter-kit/src/Modules/Files/Modules.Files/Services/FileStorageRelocator.cs)。

### 私有本地文件提供程序需要补齐

LocalStorageService 把内容保存在 WebRootPath/`wwwroot`，下载地址直接返回静态相对路径；源码明确标记其预签名实现为开发用途。正常管线在认证前启用静态文件。这可以作为开发默认，但不满足 Snapmixer 私有图片的产品语义。

可保留 S3 实现和 Files 的 OwnerType/访问策略设计；本地提供程序需要将私有对象置于静态目录之外，通过授权下载入口或有明确时效的令牌访问。受保护文件的派生图也要执行对应策略。

Files 模块是文件管理基础，不是图片处理引擎。本轮在这些基础模块及依赖清单中没有发现缩略图、感知哈希和相似图算法实现；这些仍需由 Snapmixer 业务实现或接入图像库。

证据：[本地存储](../../../2/dotnet-starter-kit/src/BuildingBlocks/Storage/Local/LocalStorageService.cs)、[静态文件管线](../../../2/dotnet-starter-kit/src/BuildingBlocks/Web/Extensions.cs)、[访问策略契约](../../../2/dotnet-starter-kit/src/Modules/Files/Modules.Files.Contracts/IFileAccessPolicy.cs)。

### 组合路由时存在数据库副作用

FilesModule.MapEndpoints 除了映射端点，还写入 Hangfire recurring job 定义，并 enqueue 旧版本文件键迁移任务。即使不启动 HTTP 或后台服务，这个组合过程仍可能访问任务存储。

这直接影响 Yard 当前的离线 OpenAPI 导出约定。应把路由映射、任务定义及启动/升级执行分别接入，不能简单让 Yard helper 调用原来的 MapEndpoints。

FSH 自己的 DbMigrator 已经有区分管理执行的努力：关闭 Jobs、用抛错 NoOpJobService 满足依赖，再剔除后台服务。可以复用其管理行为经验，但通用框架应在组合边界明确运行模式，避免每个管理进程通过服务类型删除兜底。

证据：[FilesModule 的任务注册](../../../2/dotnet-starter-kit/src/Modules/Files/Modules.Files/FilesModule.cs)、[DbMigrator 组合](../../../2/dotnet-starter-kit/src/Host/FSH.Starter.DbMigrator/Program.cs)。

### 通知查询比较通用，通知产生仍绑定示例业务

Notification 实体和列表、未读数、已读 API 可复用；具体写入则由 MentionedInChannelIntegrationEventHandler 等处理器完成，包含聊天文案、链接和租户检查，另有计费邮件处理器。

Yard 需要一个通用写入/发送入口，明确接收人、类型、内容、关联资源及去重标识；聊天和计费这些转换器适合放在对应应用或单独集成层。Snapmixer 决定图片处理事件如何转成通知。实时推送负责提示前端重新查询，不替代持久收件箱事实。

本轮发现的 IJobService 主要是 enqueue/schedule/requeue/delete facade，没有面向普通用户的进度、处理结果查询契约。若 Snapmixer 需要进度页，需再明确采用业务处理记录还是公共任务状态模型。

证据：[通知实体](../../../2/dotnet-starter-kit/src/Modules/Notifications/Modules.Notifications/Domain/Notification.cs)、[聊天通知处理器](../../../2/dotnet-starter-kit/src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/MentionedInChannelIntegrationEventHandler.cs)、[Jobs 接口](../../../2/dotnet-starter-kit/src/BuildingBlocks/Jobs/Services/IJobService.cs)。

### 包化要处理宿主注册与进程级状态

全局 `IsPackable=false` 是有意选择的源码分发策略。现有模块通过 ProjectReference 编译；公共运行包需要重新审视 PackageId、依赖、内容资源、文档、迁移及消费测试，而不只是打开打包开关。

宿主显式列出 Mediator 的契约和处理器程序集，再列出模块程序集；DbMigrator 重复对应注册。Mediator.SourceGenerator 是宿主的私有分析器依赖。包化之后需要证明外部消费项目能够发现并注册包内 handler，不能只在原解决方案 ProjectReference 下验证。

ModuleLoader 维护 static 模块列表及 `_modulesLoaded` 标志。普通单宿主可工作；用于同一测试进程的多个 WebApplicationFactory，或辅助宿主并存时，应将注册状态限定到宿主实例并补测试。这个结论是从状态所有权得出的适配要求，不是已运行复现的缺陷报告。

同样，Outbox 保存事件的 AssemblyQualifiedName；命名空间/程序集重命名可能影响已持久化消息解析。引入和升级时要确定类型标识策略，不能认为改包名对数据总是无影响。

证据：[分发设置](../../../2/dotnet-starter-kit/src/Directory.Build.props)、[API 注册](../../../2/dotnet-starter-kit/src/Host/FSH.Starter.Api/Program.cs)、[宿主 SourceGenerator](../../../2/dotnet-starter-kit/src/Host/FSH.Starter.Api/FSH.Starter.Api.csproj)、[ModuleLoader](../../../2/dotnet-starter-kit/src/BuildingBlocks/Web/Modules/ModuleLoader.cs)、[事件反序列化](../../../2/dotnet-starter-kit/src/BuildingBlocks/Eventing/Serialization/JsonEventSerializer.cs)。

### 前端适合提取，尚不是 npm SDK 和 blocks

两个 package.json 都是私有应用，源码通过 `@/` 直接引用应用自己的 env、token store、UI 和页面。认证上下文在状态初始化阶段读取 localStorage；可以在浏览器应用中使用，但不能未经处理就在 Next.js 服务端渲染中运行。

apiFetch 具备错误归一化、超时、单次刷新去重等可保留逻辑，但绑定静态凭据仓库和租户头。适合提取为可创建独立实例的客户端，浏览器认证与服务端请求使用明确接入方式。认证传输也需要决定：FSH 当前是 JWT/refresh token，Snapmixer 当前是 Cookie/CSRF，迁移需要明确行为取舍。

页面可保留完整交互，再按客户端、React hooks、可定制页面分层。UI blocks 应声明其依赖和文件；路由、导航、主题与业务权限由应用接入。当前 TypeScript 契约是手写的，不能直接沿用为 Yard 的生成契约结论。

证据：[前端包](../../../2/dotnet-starter-kit/clients/admin/package.json)、[apiFetch](../../../2/dotnet-starter-kit/clients/admin/src/lib/api-client.ts)、[token store](../../../2/dotnet-starter-kit/clients/admin/src/auth/token-store.ts)、[认证上下文](../../../2/dotnet-starter-kit/clients/admin/src/auth/auth-context.tsx)、[通知页面](../../../2/dotnet-starter-kit/clients/admin/src/pages/notifications/inbox.tsx)。

## 对 Yard 现有工程的影响

Yard 的 CLI、Aspire/YARP、资源隔离和质量工作流可以继续作为工具层；不需要先换成 FSH CLI。FSH AppHost 可参考资源接入，但它会自动执行 migrator 和 demo seeder，这与当前 Yard 的 init/dev 行为不同。

Yard 需要新增或调整的接入点：

1. **应用组合**：当前 helper 固定 AppDbContextFactory 和 AddApplication/UseApplication。新框架组合应区分配置服务、映射路由和执行后台/管理操作。
2. **数据库目标**：若保留多 Context，数据库命令必须识别框架和业务迁移目标，定义执行顺序、历史检查及 flush 范围。不能靠任选第一个 DbContext。
3. **资源拓扑**：按选用的存储、任务和缓存提供程序引入对应资源；保留 checkout 隔离，避免两个相同项目的 checkout 共用 FSH 按项目名命名的卷和固定端口。
4. **前端工程**：保留统一 pnpm/workspace 和质量检查，把共享运行逻辑发布与 block 源码安装接入工程约定。
5. **工具和运行时依赖分开**：当前 Iugam.Yard.Core 保存 Workspace、进程和同步逻辑，不应让应用为了身份或文件存储引用这些工具实现。逻辑能力边界先确定，实际包数随后由消费实验决定，不要求上游每个 csproj 都单独发布。

证据：[Yard helper](../../src/Yard.Cli/Commands.cs)、[Yard Workspace](../../src/Yard.Core/Workspace.cs)、[Yard 开发宿主](../../src/Yard.DevHost/DevelopmentHost.cs)、[FSH AppHost](../../../2/dotnet-starter-kit/src/Host/FSH.Starter.AppHost/AppHost.cs)。

## 推荐的提取策略

建议以 FSH 为可追溯代码来源，保留稳定操作和相关测试，围绕 Yard 的能力边界逐步改造：

- 第一组：身份/授权、存储/文件、任务、通知及其必须的基础设施，全部进入首版验证；计费、聊天、租户业务转换器从公共能力分离。
- 每项能力优先保留算法和业务操作，调整注册、租户依赖、类型参数、生命周期和应用耦合；不同时重写全部 CQRS/验证/事件实现。
- 应用业务模块仍可以采用普通 Minimal APIs + EF Core，不因框架内部使用 Mediator 就强制所有业务照搬全部层次。
- 数据库上下文方案、认证方案和 SQLite 必须单独作出选择。如果目标坚持单 AppDbContext + 单迁移链，应把整合工作作为显式任务，不能将上游的多 Context 视为已满足。
- 保留上游来源、提交及本地补丁记录，保存原始 LICENSE/版权说明。当前副本 LICENSE 为 MIT；第三方依赖仍按各自分发资料记录。
- 不给所有 FSH 项目批量改名后再寻找边界；框架提取完成后，再以统一包前缀和公共 API 命名交付。内部命名是否保留也应考虑合并上游修复的成本。

## 下一步最小证明，应先于大规模迁移

本轮未执行以下实验，它们是消除可行性不确定性的建议：

| 实验 | 需要证明的结果 |
| --- | --- |
| NuGet 外部消费 | 从实际 nupkg 恢复、构建和启动全新应用，不存在跨仓库源码引用；处理器、资源及公共 API 都能解析 |
| 用户扩展 | 默认用户与派生用户均可登录、管理和迁移；增加字段不需要修改框架包 |
| 路由与后台分离 | 数据库不可用时导出 OpenAPI，不注册持久任务、不执行后台服务、不监听 HTTP |
| 私有本地存储 | 原图和衍生图都无法经静态路径绕过授权；S3 使用同一文件业务契约 |
| 持久处理与通知 | SQLite/所选提供程序下上传、处理、重启、重试、通知查询组成真实流程，重复执行有明确结果 |
| 前端包与 block | 实际 npm 包在独立 Vite 应用中使用；添加页面后可定制；Next.js 客户端/服务端边界单独验证 |

应优先验证最昂贵的三个假设：**应用自定义用户可贯穿 Identity、SQLite 持久任务/文件流程可用、NuGet 消费后可以离线组合 API。** 这些成立后，再扩展完整模块和迁移 Snapmixer，能够避免在包名和目录整理上先投入大量工作。

现有测试可作为提取资产，但通过与否需要重新执行。本轮统计的测试源码文件数为 Identity 40、Framework 48、Files 6、Notifications 3，另有集中 Integration.Tests 149 个源码文件；这些数字不是已通过的测试数。前端 Playwright 配置明确使用 route mocks，仍需增加真实后端与数据库的贯通验证。

本轮只执行源码读取、项目引用图和文本统计；没有运行 .NET 构建、测试、打包、浏览器或数据库，也没有声称候选提取方案已经通过这些检查。
