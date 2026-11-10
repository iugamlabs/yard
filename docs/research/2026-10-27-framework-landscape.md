# Yard 框架方向：同类产品与基础组件调研

调研日期：2026-10-27。用途：为首版范围和架构讨论提供依据；本文是调研记录，不替代当前 0.2 规格，也不表示已经完成依赖选型。

方法：查阅产品官方文档、官方仓库说明及部分公开源码，结合 Snapmixer 当前产品、技术和重建文档。没有安装或运行这些候选产品，没有进行性能、可靠性或完整兼容性测试。文中“判断”和“建议验证”均为针对 Yard 的分析，区别于上游已经提供的能力。没有修改业务仓库或发布包。

## 研究前提

用户已经明确的方向：

- Yard 同时服务现有 monorepo 的重构标准化和新项目的全栈起步，允许应用依赖 Yard 框架包。
- 后端基础能力通过 NuGet 复用，客户端与共享前端逻辑通过 npm 复用，页面通过可定制的源码 blocks 分发。
- 默认实现应可直接使用，并允许扩展用户模型、业务行为和页面。
- 首个改造目标是 Snapmixer；身份、授权、存储、异步任务和通知均需纳入首版范围讨论。
- 控制实现规模，以真实项目完成改造和新项目能够启动作为交付依据。

先前“生产应用不得引用任何 Yard 包”和“首版只做身份”的建议已不能作为新方向的前提。当前仓库规格仍描述 0.2 的工具定位；正式调整留待后续规划。

## Snapmixer 对候选方案的约束

| 方面 | 已知事实与待定项 | 对研究的影响 |
| --- | --- | --- |
| 工程 | 一个 ASP.NET Core 生产项目、模块内聚、React/Vite | 比较引入框架后的代码组织成本，不能只比较功能列表 |
| 数据库 | 当前使用 SQLite、EF Core、显式迁移 | 任务引擎需核实 SQLite 的实际提供程序与恢复语义；同时考虑 Yard 已有 PostgreSQL 路径 |
| 身份 | 多账号、本机管理命令、Cookie、数据库会话；重置密码和停用撤销全部会话 | 候选默认登录流程是否匹配，用户类型扩展是否贯穿存储、API 与管理入口 |
| 存储 | 需要上传、原图和衍生图；本地/S3、处理方案尚待 T3 确定 | 区分字节存储、文件生命周期、图片业务与访问授权 |
| 任务 | 哈希、缩略图、相似提示和裁剪相关需求保留；执行位置待定 | 不能把进程内异步、cron 和持久队列视为相同能力；不是所有图像操作都必然后台执行 |
| 通知 | 用户本轮明确首版需要；具体内容、通道、已读与离线语义尚未确定 | 纳入研究，不擅自缩成 Toast，也不自动扩张为邮件/短信/推送平台 |
| 业务边界 | 可见性、应用凭证、重复文件处理等 P1–P5 未定 | 框架选型不能替代业务规则确认 |

来源：[产品设计](../../../snap-mixer/snap-mixer-mono/docs/product-design.md)、[技术设计](../../../snap-mixer/snap-mixer-mono/docs/tech-design.md)、[重建计划](../../../snap-mixer/snap-mixer-mono/docs/rebuild-plan.md)。以上链接指向同级代码目录中的本机仓库，属于调研时快照。

## 产品比较

下表中的适配判断是本次分析，不是对产品整体质量的排名。

| 产品 | 主要复用方式与已查见能力 | 对 Yard 的价值及适配成本 |
| --- | --- | --- |
| ABP Framework | 应用框架、模块包、模板与工具；身份授权、BLOB 和任务抽象 | 最值得评估的现成 .NET 框架底座；需验证其模块与实体扩展约定是否符合 Yard 目标。参考：[框架](https://abp.io/framework)、[仓库](https://github.com/abpframework/abp) |
| FullStackHero | .NET 模块化单体 + React/Vite + Aspire，完整源码起步方案 | 技术组合非常接近；默认多租户、模块自己的 DbContext 与 Contracts 项目，与 Yard 当前单生产项目约定有差异。参考：[架构与分发介绍](https://fullstackhero.net/docs/getting-started/introduction/) |
| ASP.NET Zero | 商业应用基础方案，包含用户页面、通知和 Hangfire 接入 | 可参考完整用户旅程和后台交付范围；源码产品与 Yard 的包复用目标不同。参考：[官方功能](https://aspnetzero.com/features) |
| Orchard Core | Framework 与 CMS 分开，模块/主题通过 NuGet 分发 | 可学习模块包组成应用；需要区分 Framework 和 CMS，CMS 的数据与页面体系不是 Yard 当前的自然延续。参考：[官方文档](https://docs.orchardcore.net/en/latest/) |
| Laravel | 框架能力 + Artisan + Starter Kits；存储、队列、通知统一接入 | 很贴近 Yard 期望的开发体验，尤其适合借鉴后端能力与可修改前端的分工。React starter 使用 Inertia，不能原样视为独立 React API 客户端。参考：[Starter Kits](https://laravel.com/framework/docs/13.x/starter-kits) |
| Django | 可扩展用户、管理命令、文件存储后端 | 适合参考应用模型和统一管理入口；任务执行仍要评估外部后端。参考：[认证扩展](https://docs.djangoproject.com/en/6.0/topics/auth/customizing/)、[管理命令](https://docs.djangoproject.com/en/6.0/howto/custom-management-commands/) |
| PocketBase | 集成后端，可独立运行，也能用 Go/JS 扩展 | 学习默认可用的身份、文件与 SDK 体验；数据规则体系和 Go 运行时与现有 C# 应用不同。参考：[官方介绍](https://pocketbase.io/docs/)、[扩展方式](https://pocketbase.io/docs/use-as-framework/) |
| Supabase | Auth、Storage、Realtime 等服务，通过客户端 SDK 使用 | 学习统一客户端与服务边界；自托管涉及多个服务和配置，整套采用会改变部署拓扑。参考：[自托管文档](https://supabase.com/docs/guides/self-hosting/docker) |

ABP Framework 与 ASP.NET Boilerplate/ASP.NET Zero 不能混为同一套产品。后者的通知系统文档可作为历史设计参考，不能据此断言当前 ABP 开源框架自带相同模块；具体模块及其分发范围需要逐项核对。[ASP.NET Boilerplate 通知文档](https://aspnetboilerplate.com/Pages/Documents/Notification-System)

## 最值得深入借鉴的设计

### ABP：共享模块如何扩展

ABP 将 BLOB 操作与具体提供程序分开，并通过容器配置区分不同用途；框架还提供任务抽象和不同执行实现的接入。它证明包化能力可以覆盖多项目公共基础，而不需要项目各自复制全部实现。[BLOB 文档](https://abp.io/docs/latest/framework/infrastructure/blob-storing/)、[任务文档](https://abp.io/docs/latest/framework/infrastructure/background-jobs?LanguageCode=en)

实体扩展是值得单独比较的地方：ABP 的 Module Entity Extensions 可以把额外属性连接到数据库、API 和 UI。这比单纯的 C# 继承覆盖更多层，也需要更多框架机制。[实体扩展文档](https://abp.io/docs/latest/framework/architecture/modularity/extending/module-entity-extensions)

对 Yard 的判断：可以认真验证“使用 ABP 作为底座”的路线，而不是仅因功能多就排除。验证重点是简单项目的接入代码、应用自己的用户类型、模块生命周期、迁移管理和自定义 React 前端。如果这些成本可以接受，现成框架能省下大量维护工作；如果不接受，也应明确具体差异，不能泛称它“太重”。

### FullStackHero：相近技术组合里的完整流程

FullStackHero 更接近源码起步项目，公共 BuildingBlocks 和业务模块进入生成项目；它不等于可以直接引用的 Yard 式共享运行库。是否整体采用或 fork，需要接受随项目维护这些源码的方式。[官方仓库](https://github.com/fullstackhero/dotnet-starter-kit)

Files 模块值得研究的重点是：文件所属业务对象、访问策略、待上传到可用状态、孤儿与删除后的清理。其当前文档描述 S3 预签名上传下载，API 不代理文件字节。这是可借鉴的文件生命周期，不代表 Snapmixer 必须采用同一种上传拓扑。[Files 文档](https://fullstackhero.net/docs/modules/files/)

Notifications 模块将每用户收件箱持久化，提供查询和已读操作，再通过 SignalR 推送更新。这清楚地区分了“通知记录”和“实时传输”。[Notifications 文档](https://fullstackhero.net/docs/modules/notifications/)

Jobs 部分封装 Hangfire 的 DI scope、上下文与日志/遥测，表明基础框架的重要价值在于接好执行环境，而不是重新写调度器。其文档中的持久提供程序为 PostgreSQL/SQL Server；不能据此推出 SQLite 同样可用。[Jobs 文档](https://fullstackhero.net/docs/building-blocks/jobs/)

对 Yard 的判断：优先研究这些具体流程和故障边界，再决定复用代码或依赖。保持图片处理、相册、标签和随机 API 在 Snapmixer 业务模块中；不要因参考项目有 Billing、Chat 等模块就扩大范围。

### Laravel：包化能力与页面源码的分工

Laravel Fortify 是不绑定 UI 的认证后端；Starter Kit 提供应用入口和前端代码，React starter 使用 shadcn/ui，并允许直接修改源码。这与 Yard 的 NuGet/npm/blocks 设想有很强的可比性。[Fortify](https://laravel.com/framework/docs/13.x/fortify)、[Starter Kits](https://laravel.com/framework/docs/13.x/starter-kits)

文件系统通过驱动接入，通知通过不同渠道发送，通知又可以排入队列。这些能力以稳定的开发入口组合起来，具体引擎不必全部由框架实现。[文件系统](https://laravel.com/framework/docs/13.x/filesystem)、[通知](https://laravel.com/framework/docs/13.x/notifications)

事务处理尤其值得借鉴：队列提供提交后派发，事务回滚时不派发，避免任务读到未提交业务数据。[队列事务文档](https://laravel.com/framework/docs/13.x/queues#jobs-and-database-transactions)

工程判断：仅有“提交后调用 enqueue”仍存在提交完成到入队前的崩溃窗口。若要求业务提交与任务可靠关联，需要进一步验证同事务持久化任务或 outbox；不能把 after-commit 等同于完整可靠投递。这是 Yard 的待验证问题，不是对该文档的额外承诺。

### Django、PocketBase：默认可用与可靠性的边界

Django 的文件能力委托 storage backend，默认本地文件系统；可扩展用户和管理命令提供统一入口。[文件存储](https://docs.djangoproject.com/en/6.0/topics/files/)

Django 6.0 新增 Tasks API，但官方明确内置的是开发/测试后端，实际生产执行由外部后端或 worker 完成。因此“像 Django”可以指接口体验，不意味着框架应自己实现任务引擎。[Tasks 文档](https://docs.djangoproject.com/en/6.0/ref/tasks/)

PocketBase 提供本地/S3 文件存储，并以记录访问规则约束受保护文件；它也支持图像变换入口。对私人图片库而言，必须核实文件 URL 的权限语义，其默认文件访问方式不能直接等同于私有存储。[文件文档](https://pocketbase.io/docs/files-handling/)

它的实时 API 使用 SSE 传递记录变化，内置任务调度文档描述 cron。查到这些能力并不足以证明具有持久任务队列的故障恢复、重试或离线通知保证。[实时 API](https://pocketbase.io/docs/api-realtime/)、[定时任务](https://pocketbase.io/docs/go-jobs-scheduling/)

对 Yard 的判断：学习轻量默认体验，但必须分别规定存储、持久任务、站内收件箱和实时更新的责任。

## 可以直接评估的 .NET 基础组件

这些是底层依赖候选，不与完整应用框架在同一层比较。

| 候选 | 官方资料能确认的能力 | 在 Yard 中需要验证的问题 |
| --- | --- | --- |
| ASP.NET Core Identity / Authorization | 自定义用户类型、泛型用户存储、资源授权机制 | YardUser 扩展贯穿全部入口；会话撤销；后台服务身份；保持 DTO 明确。来源：[用户定制](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/customize-identity-model?view=aspnetcore-10.0)、[资源授权](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0) |
| Foundatio Storage | IFileStorage、FolderFileStorage，以及独立提供程序中的 S3 实现 | 流式读写、异常和取消、路径约束、所需元数据与签名 URL 能力；是否直接使用其接口或仅封装文件生命周期。来源：[Storage](https://foundatio.dev/guide/storage.html)、[接口源码](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/IFileStorage.cs) |
| Foundatio Queues / Jobs | 队列完成、放弃、重试、死信等公共语义；多种提供程序 | 官方已查提供程序不能证明存在适合本轮的 SQLite/EF 持久队列；内存队列不满足重启保留要求。来源：[Queues](https://foundatio.dev/guide/queues.html) |
| Hangfire | 持久任务、重试、调度与运维界面 | SQLite、PostgreSQL 提供程序的维护与并发恢复；任务与 EF 事务关系；工具进程避免启动 worker。SQLite/PostgreSQL 存储不能当成同等官方支持。来源：[概览](https://www.hangfire.io/overview)、[提供程序清单](https://www.hangfire.io/extensions.html) |
| Quartz.NET | 持久 JobStore、任务与 trigger；官方配置列出 SQLite 和 Npgsql | 明确选定版本，验证一次性图像任务、重启恢复、失败重试和进度接口；调度器的持久化不自动解决应用事务。来源：[持久化配置](https://www.quartz-scheduler.net/documentation/quartz-3.x/configuration/reference.html) |
| TickerQ | 官方 legacy EF 文档描述 SQLite/PostgreSQL，以及使用应用 DbContext 的路径 | 属于技术上值得观察的候选，但本次查到新旧许可及文档分界，必须基于具体版本重新核实，不能直接沿用搜索摘要中的开源结论。来源：[EF 文档](https://tickerq.net/docs/entity-framework)、[当前许可文件](https://github.com/Arcenox-co/TickerQ/blob/main/LICENSE.md) |

TickerQ 的具体发现：legacy 页面明确链接到 5.x commercial 文档，当前许可文件注明 2026-10-13 生效及对应商业版本边界；同时部分 README/搜索快照仍写 MIT/Apache。本文只记录资料差异，不解释其法律适用条件，也不将它列为已决定采用的依赖。选择时以实际包版本及其不可变分发材料为依据。

对底层复用的判断：身份、存储和任务都已有可评估的实现，Yard 的主要工作应是补齐应用约定、授权与生命周期、客户端、页面和管理入口。只为换包名而逐方法包一层，没有足够收益；确有跨项目语义的接口再由 Yard 定义。

## 会影响首版边界的结论

### 五类能力均纳入，控制每类能力的深度

不能再以“先只做登录”作为 Snapmixer 方案。范围应围绕真实流程研究，例如：上传文件、保存受保护内容、可靠执行处理任务、读取任务状态、保存完成或失败通知、前端刷新结果。

这个流程是后续技术验证建议，不是已确定的 Snapmixer 上传 API 或图片处理规则。某些结果可以直接由请求返回，某些任务需要后台执行，需结合 T3/T5 和实际处理成本决定。

### 文件、任务和通知各有两层

| 能力 | 可复用基础 | 留在业务或明确接入点的内容 |
| --- | --- | --- |
| 存储 | 字节读写、提供程序、文件记录、清理及访问入口 | 相册可见性、重复图片策略、裁剪参数、图像算法 |
| 任务 | 持久执行、重试、执行范围、状态和错误记录 | 图片处理步骤、业务幂等、结果应用 |
| 通知 | 接收人、持久记录、已读、查询、按需推送 | 哪个业务事件通知谁、文案、跳转和渠道选择 |

其中“文件记录与字节存储”以及“任务状态与用户通知”也不能混为一个模型。前端进度、Toast、站内收件箱、邮件和 Web Push 是不同交付要求。

### 一致性比接口数量更重要

以下问题应进入后续候选验证：

- 文件已写入而数据库失败、数据库已提交而任务未入队时，如何恢复和清理。
- worker 在处理一半时退出，重启后是否恢复；任务重复执行是否产生重复衍生图和通知。
- 推送断线后页面如何从数据库恢复真实结果；推送只作为更新提示还是事实来源。
- 图片权限变化后，原图、缩略图、已有下载 URL 和通知中的链接如何约束访问。
- db/契约/账号管理等一次性进程不启动 worker；flush 时正在运行的任务如何处理，框架自建表是否属于清空范围。

这些属于身份、存储、任务和通知组合后的真实边界。引用一个成熟任务包不会自动解决全部问题。

### 前端继续采用运行库与源码页面组合

shadcn registry 已经支持文件、npm 依赖和 registry 依赖，适合用于分发登录、账号、上传、任务状态和通知等 blocks。无需先建设自有页面安装生态。[registry 格式](https://ui.shadcn.com/docs/registry/registry-item-json)

建议研究把请求、会话和状态逻辑放入 npm，把布局及交互页面添加到项目。复制后的页面更新仍需审阅差异；路由、SSR 和服务端 Cookie 处理需要宿主接入，不能把 registry 的可分发性当成 Vite/Next.js 的零适配保证。GitHub registry 依赖可以固定 tag/commit，适合复现安装，但仍要设计 Yard 包、API 与 block 的兼容关系。

## 后续值得比较的三条路线

| 路线 | 可获得的收益 | 必须接受或验证的成本 |
| --- | --- | --- |
| ABP 作为底座，Yard 提供 CLI 与前端体验 | 直接使用成熟模块与公共基础机制 | 模块/实体扩展方式、应用结构、迁移和依赖范围；是否仍符合简单项目的日常开发预期 |
| 采用或 fork FullStackHero 起步工程 | 很多端到端流程已经接好 | 源码复用与集中包升级的差异、多租户及多项目结构、SQLite 适配和上游变化同步 |
| Yard 组合原生能力与成熟组件 | 更容易保留现有单生产项目及自有前端风格 | Yard 自己维护公共 API、身份/文件/任务/通知的组合正确性、版本迁移和页面分发 |

本轮倾向第三条，但这只是基于现有约束的初步判断。第二条值得作为完整流程参考，第一条值得做实际小样验证；本轮文档审阅不足以给出开发量或可靠性排名。

## 正式规划前的最小验证建议

以下均未执行，应在决定底座与任务引擎时提供证据，而不是先建设完整框架：

1. **用户扩展**：默认用户和扩展用户各跑通登录、管理命令、会话撤销、迁移及前端会话；核对扩展字段不会自动泄露到公共契约。
2. **SQLite 持久任务**：同一个最小处理用例比较候选，覆盖入队、重启恢复、重试、重复执行、停止，以及与业务提交的关系；随后验证 PostgreSQL 路径。
3. **文件与通知流程**：真实上传文件并处理；在数据库失败、worker 退出和客户端断线时核对可恢复的事实。验证文件权限和通知接收人边界。
4. **框架接入成本**：分别记录 ABP 小样和直接组合小样需要的生产项目、配置、运行服务与手工代码，再讨论最终包边界。性能只在真实运行后比较。
5. **页面分发**：一个使用共享 npm 逻辑的 block 加入 Vite，并以最小 Next.js 宿主核对路由/客户端边界；不把这项验证扩展成第二套完整项目模板。

验证项目、依赖缓存和日志按仓库约定放在 `.temp`。Snapmixer 先作为需求与最终改造对象，本轮调研没有更改它。完整规划还需确定通知具体语义、存储默认提供程序、任务执行拓扑和数据库所有权。
