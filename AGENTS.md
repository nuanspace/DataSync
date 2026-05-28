# Repository Guidelines

## 工作空间规则

所有对话、说明、代码注释、辅助类说明和记录类文档必须使用中文。程序代码、配置键名、命令、路径、类名和接口字段按项目实际需要保留原文。

## 项目结构与模块组织

本仓库包含两个 ASP.NET Core/MudBlazor 应用：

- `DataSync.CYYY/`：.NET 9 数据同步应用。页面在 `Components/Pages/`，服务在 `Services/`，后台任务在 `Workers/`，EF 上下文在 `Data/`，模型在 `Models/`，静态资源在 `wwwroot/`，SQL 位于 `Scripts/` 或 `Migrations/`。
- `DataSync.LHYY.V2/`：.NET 10 ESB 配置应用。页面在 `Components/Pages/`，共享组件在 `Components/Shared/`，业务逻辑在 `Services/` 和 `Handlers/`，EF 上下文在 `Data/`，实体、DTO、枚举在 `Models/`，接口文档在 `ProjectDocuments/`。

解决方案入口为 `DataSync.sln`。Dockerfile 与 NLog 配置均放在各项目目录内。

## 构建、测试与本地开发命令

- `dotnet restore DataSync.sln`：还原两个项目的 NuGet 包。
- `dotnet build DataSync.sln`：编译整个解决方案。
- `dotnet run --project DataSync.CYYY\DataSync.CYYY.csproj`：本地运行 CYYY。
- `dotnet run --project DataSync.LHYY.V2\DataSync.LHYY.V2.csproj`：本地运行 LHYY。
- `dotnet test DataSync.sln`：运行测试；当前仓库尚未提交测试项目。

本地启动地址以各项目的 `Properties/launchSettings.json` 为准。

## 编码风格与命名约定

遵循项目文件中已启用的可空引用类型和隐式 using。公共类型和成员使用 PascalCase，局部变量和参数使用 camelCase。服务类放在 `Services/`，页面组件放在 `Components/Pages/`，共享组件放在 `Components/Shared/`，EF 实体优先放在 `Models/Entities/`。

改动应保持小而集中，优先匹配邻近代码风格。较大格式调整前可运行 `dotnet format DataSync.sln`。

## 测试指南

当前没有自动化测试项目。涉及行为变更时，优先新增聚焦的 `*.Tests` 项目；暂不添加测试时，需在 PR 中记录手工验证步骤。测试类名应体现被测对象，例如 `SyncOrchestratorTests` 或 `MessageProcessingServiceTests`。

## 数据库与配置注意事项

不要把环境专用密钥提交到 `appsettings.json`。凭据应通过 user secrets、环境变量或部署配置提供。

`DataSync.LHYY.V2` 的数据库变更必须遵循 `DataSync.LHYY.V2/AGENTS.md`：放入 `Scripts/yyyyMM/yyyyMMdd.sql`，同一天的变更追加到同一个文件，避免拆成编号 SQL 文件。

## 提交与 Pull Request 指南

当前提交历史使用简短描述性标题，例如 `初始化干净源码`。后续提交保持简洁、动作明确。

PR 应说明变更内容、影响项目（`DataSync.CYYY` 或 `DataSync.LHYY.V2`）、数据库或配置变化、验证结果；涉及界面变更时附截图。
