# Yard 0.2 规范

Yard 是约定优先的 C# 模块化单体 + React/Vite monorepo 开发工具。schema 2、版本 0.2.0，所有开发工具包使用 Iugam.Yard.*。部署交给 ship。不会自动发布包，不会修改已有业务仓库。0.1 的 SaaS 运行库与模板不再维护；历史记录见 implementation-0.1.md。

## 工程与所有权

主后端 apps/api、主前端 apps/web，共享 packages/ui 和 packages/api-client，额外应用位于 apps/<name>。每个后端仅一个生产项目，按 Modules、Infrastructure、Hosting 组织，测试使用独立项目。模块内聚合端点、契约、操作、实体和 EF 映射；端点委托操作，跨模块写入通过所属模块协调事务。

生产应用直接使用 ASP.NET Core、EF Core 和 OpenAPI，不引用 Yard 包，不继承 Yard 类型，不包含 Yard 管理命令。运行时与标准设计时工厂共用数据库配置逻辑，连接字符串使用 ConnectionStrings:Default。原生命令可以恢复、构建、发布和运行应用，无需 Yard CLI 或 .yard。生产源码不读取 yard.json 或 .yard。

默认 Notes 示例支持列表、创建、删除和 1–200 字符校验，前端使用生成契约。示例可删除，不是框架要求。没有账号、组织、多租户、RBAC、OAuth、邮件或平台管理页面。初始迁移只有 Notes，没有预置数据。

根 yard.json 仅允许 schemaVersion、name、toolVersion、database、backends、frontends、dependencies。未知字段、空值、重复名称、非法应用名、保留名称与循环依赖报错。不提供任意命令、路径覆盖或插件入口。只支持 C# 后端与 React/Vite 前端。

```json
{
  "schemaVersion": 2,
  "name": "Example",
  "toolVersion": "0.2.0",
  "database": "postgresql",
  "backends": ["api", "manager"],
  "frontends": ["web", "admin"],
  "dependencies": { "api": ["manager"] }
}
```

应用清单是唯一拓扑来源。sync 管理解决方案、workspace、本地工具清单、SDK、工程排版/lint、Vite、TypeScript 和 Tailwind 扫描配置；业务源码、业务依赖、迁移和测试归项目所有，sync 不覆盖。根 package.json 的标准工具依赖、Node 和 pnpm 声明由 Yard 校验固定，允许增加业务依赖。项目不维护 Taskfile、Procfile 或手写 Aspire AppHost。

## 开发工作流

固定 .NET SDK 10.0.400、Node 24.18.0、pnpm 12.11.2、EF 10.0.13、Aspire 13.6.3、YARP 2.3.0。细节见依赖清单。全局 CLI 在仓库中交给固定版本本地工具。

new 只生成文件，非空目录拒绝覆盖。install 恢复锁定依赖、本地工具和忽略的开发宿主/辅助项目。init 幂等准备所属数据库并显式迁移，不写业务数据。dev 可启动所属数据库；检查端口、模型和迁移状态，不安装包、不改锁文件、不迁移、不写种子数据。C# 与 Vite 固定轮询监听，Vite 间隔 250ms。

add frontend 使用同批模板的依赖声明和 importer，结构化合并模板锁定的 package/snapshot 图，保留项目已有依赖和锁定条目，不联网或重新解析版本。新增应用先准备全部文件，普通写入失败恢复清单、锁文件、同步配置、私有设置和新增目录；不承诺强制终止时的文件系统事务。

Aspire + YARP 与控制接口仅存在于开发侧。网关默认 http://localhost:8080，YARD_PORT 覆盖端口；/ 为 Web，/api/ 为主 API，其他前端为 /<name>/。/admin/ 只有声明 admin 应用才有对应前端。额外后端仅通过内部地址访问，Services:<name> 注入其地址。

checkout 身份由规范化绝对路径计算，每个 checkout 独立端口、控制令牌、SQLite 文件或 PostgreSQL 容器/卷。拒绝复制的 .yard。停止、测试和验收只清理所属资源，db down 保留数据。

开发辅助控制台生成在 .yard/tools/<app>，引用应用及 Iugam.Yard.DotNet；应用不反向引用。数据库命令仅构建设计时 DbContext，不执行应用入口。契约导出复用 AddApplication/UseApplication 建立路由，不启动 HTTP 或后台服务，不要求数据库在线。应用组合方法不迁移、不写种子数据、不主动启动服务。工具只提供固定操作，不承接业务管理命令。

## 数据库、检查和契约

创建时选择 PostgreSQL（默认）或 SQLite，每个项目只生成选定提供程序、一个 AppDbContext 和一条迁移链。generate 先构建再通过本地固定 EF CLI 添加迁移。初始化后不能直接改提供程序；不自动转换现有数据库。

数据库默认主 api，--app 与 --all 互斥。--all 按后端依赖顺序逐库执行，失败停止并报告已完成目标，不承诺跨库回滚。migrate、plan、sql、flush 拒绝模型漂移、未知或不连续的迁移历史。

migrate --plan 只列待应用迁移；sql 根据当前历史导出待执行 SQL，stdout 为纯 SQL，构建日志进入 stderr。预览不创建或启动数据库，不应用迁移，不提供离线区间或幂等脚本。

flush 在每库事务内清空全部应用表和原始 SQL 表，重置所属自增计数，保留结构、迁移历史、系统表和迁移锁表；不恢复基础记录或种子数据。要求迁移全部应用。交互列出 checkout、提供程序和目标并输入 yes；重定向输入必须 --yes。取消或失败回滚当前库。

shell 使用所属容器 psql 或本机 sqlite3，继承终端和输入，保留客户端退出码；不安装客户端、不启动数据库、不隐式创建 SQLite 文件。

fmt 固定 CSharpier → Roslyn whitespace 与前端 Prettier。fmt --check 不改源码。check 包含同步、格式、lint、类型、契约、构建和测试。后端测试使用 .temp 下的隔离数据库，不复用开发库。

前端 dev/build/typecheck/test/lint 由 Yard 直接执行仓库根固定工具链，不委托 package.json scripts；执行前校验实际工具版本。TypeScript/Vite 配置受控，doctor 和工程命令拒绝漂移；doctor 同时核对已安装工具。项目可保留 scripts 供原生命令使用，不能以此改写 Yard 行为。

contracts 通过独立工具导出 OpenAPI，再生成 TypeScript；检查仅比较差异，允许删除示例后的合法空契约。verify 创建 .temp 下隔离副本进行工程、桌面/手机浏览器与生命周期验收，清理所属进程和测试数据库。SQLite 不要求 Docker。

## 版本与分发

本地包为 CLI、Core、DotNet、DevHost、Templates；打包采用明确清单，不包含旧 AspNetCore 或旧 feed 包。仅开发工具恢复使用 Yard 本地源，生产恢复/发布不依赖它。

schema 1 在写文件或数据库前明确拒绝；不提供 0.1 自动升级。sync --check、upgrade --plan 只读；当前同版本无变化为 no-op，受控文件漂移为冲突，未知迁移路径拒绝修改。新增版本必须显式登记升级，不推断业务迁移。非 Yard 仓库 init 只给整改报告。

退出码为 0 成功、1 操作失败、2 参数/规范错误、130 取消；外部工具保留非零退出码。--json 输出结构化错误；YARD_DEBUG=1 才显示本地异常堆栈。
