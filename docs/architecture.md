# Yard 0.2 实现架构与所有权

## 包和生产边界

同版本分发五个开发工具包：Iugam.Yard.Cli、Core、DotNet、DevHost、Templates。CLI 携带其他工具包和标准模板；install 恢复本地固定工具与忽略的开发宿主。生产应用直接使用 ASP.NET Core、EF Core 和 OpenAPI，不引用 Yard 包、基类或管理入口，不读取 yard.json 或 .yard。账号、组织、租户隔离等业务能力由应用选择和维护；0.1 的 SaaS 运行库已删除。

每个后端只有一个生产项目，模块内聚合端点、契约、操作、实体和 EF 映射。AppDbContext 与标准设计时工厂共用数据库配置，连接字符串为 ConnectionStrings:Default。应用通过 AddApplication/UseApplication 组合服务与路由，不在组合阶段执行迁移、写种子或启动服务。默认 Notes 是可删除的示例。

数据库和契约操作由 .yard/tools/<app> 的独立辅助控制台执行。它引用应用与 Iugam.Yard.DotNet，应用不反向引用工具。数据库操作不执行应用入口；契约导出建立路由，但不启动 HTTP、后台服务或数据库。

## 工程配置和前端命令

应用清单只描述拓扑。sync 管理解决方案、workspace、本地工具、SDK、排版/lint、Vite、TypeScript 和 Tailwind 扫描配置；业务源码、迁移、测试和业务依赖归项目所有。Tailwind 只扫描声明的前端 src 与共享 UI。

前端标准命令直接通过 Node 执行仓库根 node_modules 中的固定工具：build 为 TypeScript 检查后运行 Vite，typecheck 使用固定项目配置，test 执行 Vitest，lint 执行 ESLint。显式使用受控 Vite 配置，避免旁路配置改变测试或构建入口。fmt 和 contracts 也使用固定的仓库根工具。package.json scripts 仅供项目的原生命令使用，不能改变 Yard 的标准行为。

根 package.json 的 packageManager、Node 版本及标准 devDependencies 必须与 Yard 模板一致；前端和共享包不能声明其他版本来覆盖标准工具或插件。可以增加业务依赖和额外开发依赖。doctor 校验声明和已安装的标准工具版本，工具执行前再次检查实际包版本。sync 不整体覆盖 package.json，也不恢复或删除业务依赖。TypeScript 配置与 Vite 配置由 sync 生成；doctor 和工程命令拒绝它们的漂移。

## 新增应用和锁文件

新增前端的源码、package.json 和 importer 均来自同批标准模板，不能复制已演进 Web 的 importer。锁文件按 YAML 结构处理，支持 pnpm 的多个文档；保留当前所有 importer、已有 package/snapshot 条目与包管理器文档，补入模板缺失的锁定 package/snapshot 节点（包括 peer 和 optional 依赖），添加匹配的新 importer。add 不联网、不重新解析依赖、不安装包。随后运行 init/install 执行冻结安装。

新增后端只生成选定数据库提供程序及一条迁移链。所有新增应用文件、清单、锁文件、同步配置和 checkout 设置先在内存中准备；验证失败不写入。写入期间发生异常时恢复原文件内容、权限并删除本次新增目录。该恢复覆盖命令内的普通 I/O 异常，不承诺断电或强制终止进程时的文件系统事务。

## 开发宿主和版本

Aspire 管理固定 dotnet watch、直接执行根工具链的 Vite 和 YARP，前端启动不执行项目 dev script，也不安装包。C# 和 Vite 固定轮询，Vite 间隔 250ms。网关保留 API/前端前缀；额外后端仅通过内部地址访问。控制入口绑定 loopback，并校验 checkout 随机令牌；异常退出停止所属整组。退出释放 Aspire 并等待所属端口关闭。

checkout 的路径身份隔离端口、连接、数据、控制令牌和所属资源，拒绝复制的 .yard。数据库操作显式执行，dev 不迁移或写种子；db down 保留数据。测试库和 verify 的隔离仓库均位于 .temp。

schema 1 在写入前拒绝，不提供 0.1 自动升级。当前版本升级无变化为 no-op；未知路径和受控文件冲突拒绝修改。后续版本必须显式登记工程迁移，业务数据库迁移单独验收。实际 NuGet 分发验证从本地包安装，不以源码 ProjectReference 代替分发链。
