# YardApp

ASP.NET Core 模块化单体 + React/Vite monorepo。

```sh
yard init
yard dev
```

浏览器入口：http://localhost:8080。Notes 是可替换的最小示例，展示模块、EF 持久化、类型客户端和前端交互。没有预置账号或组织。

`yard check` 执行工程检查和隔离数据库测试，`yard verify` 执行隔离浏览器与生命周期验收。`yard fmt` 统一 C# 和前端排版；`yard contracts` 在 API 改变后重新生成类型。

数据库为创建时选择的 __DATABASE__，只维护一条迁移链。`yard db generate Name` 创建迁移，`yard db migrate` 显式应用；`yard dev` 不迁移或安装依赖。`yard db flush` 清空全部应用数据并保留结构/历史，不重放种子数据。

生产应用不引用 Yard 包。无需 Yard 即可构建和发布：

```sh
dotnet restore YardApp.slnx --locked-mode
dotnet build YardApp.slnx
dotnet publish apps/api/src/YardApp.Api -c Release
```

运行 API 时通过标准 `ConnectionStrings__Default` 和 `ASPNETCORE_URLS` 配置数据库及监听地址；数据库应预先应用迁移。后端测试也读取标准连接字符串，使用独立测试库，不要指向开发或生产数据。前端构建使用 `pnpm --dir apps/web build`。

源码、业务依赖和迁移由项目维护。`yard sync` 只更新受控工程配置，`yard add backend/frontend name` 添加标准应用。`.yard` 和 `.temp` 都是本地忽略内容，不能复制到另一 checkout。增加 admin 前端后才有独立 /admin/ 页面。

前端标准工具版本及 TypeScript/Vite 配置由 Yard 管理；可以增加业务依赖，不能用其他版本覆盖标准工具。Yard 直接执行固定工具，不使用项目 scripts 改写标准命令。新增前端使用模板自己的依赖和锁定图，不复制 Web 的业务依赖；失败会恢复本次文件变更。
