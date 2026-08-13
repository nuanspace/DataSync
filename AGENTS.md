# DataSync Codex 指南

## 工作空间规则

所有对话、说明、代码注释、辅助类说明和记录类文档必须使用中文。程序代码、配置键名、命令、路径、类名和接口字段按项目实际需要保留原文。

## 项目结构与模块组织

本仓库包含一个公共类库、两个 ASP.NET Core/MudBlazor 应用和两个自动化测试项目：

- `DataSync.CYYY/`：.NET 9 数据同步应用。页面在 `Components/Pages/`，服务在 `Services/`，后台任务在 `Workers/`，EF 上下文在 `Data/`，模型在 `Models/`，静态资源在 `wwwroot/`，SQL 位于 `Scripts/` 或 `Migrations/`。
- `DataSync.LHYY.V2/`：.NET 10 ESB 配置应用。页面在 `Components/Pages/`，共享组件在 `Components/Shared/`，业务逻辑在 `Services/` 和 `Handlers/`，EF 上下文在 `Data/`，实体、DTO、枚举在 `Models/`，接口文档在 `ProjectDocuments/`。
- `DataSync.Common/`：同时面向 .NET 9 和 .NET 10 的共享协议与基础能力。
- `DataSync.CYYY.Tests/`、`DataSync.LHYY.V2/DataSync.LHYY.V2.Tests/`：两个应用的自动化测试项目。

解决方案入口为 `DataSync.sln`。Dockerfile 与 NLog 配置均放在各项目目录内。

## 构建、测试与本地开发命令

- `dotnet restore DataSync.sln`：还原两个项目的 NuGet 包。
- `dotnet build DataSync.sln`：编译整个解决方案。
- `dotnet run --project DataSync.CYYY\DataSync.CYYY.csproj`：本地运行 CYYY。
- `dotnet run --project DataSync.LHYY.V2\DataSync.LHYY.V2.csproj`：本地运行 LHYY。
- `dotnet test DataSync.sln`：运行两个测试项目。
- `.\.codex\tools\verify.ps1 -Level Focused|Project|Full`：按风险等级执行知识校验、测试和构建。

本地启动地址以各项目的 `Properties/launchSettings.json` 为准。

## 编码风格与命名约定

遵循项目文件中已启用的可空引用类型和隐式 using。公共类型和成员使用 PascalCase，局部变量和参数使用 camelCase。服务类放在 `Services/`，页面组件放在 `Components/Pages/`，共享组件放在 `Components/Shared/`，EF 实体优先放在 `Models/Entities/`。

改动应保持小而集中，优先匹配邻近代码风格。较大格式调整前可运行 `dotnet format DataSync.sln`。

## 测试指南

行为变更优先在现有测试项目中增加聚焦测试。先运行受影响项目测试，再按风险运行解决方案测试和构建；未执行的验证必须说明原因。不要在知识库中记录会快速过期的测试数量。

## 知识库与流程维护

- 项目 Skill 位于 `.agents/skills/`；`datasync-business-logic` 是业务知识路由入口。
- 开始业务任务时，先运行 `.\.codex\tools\resolve-knowledge.ps1`，只读取推荐领域的 reference；领域不清或跨模块时再扩展读取。
- 任务结束前运行 `.\.codex\tools\detect-knowledge-impact.ps1` 和 `.\.codex\tools\lint-knowledge.ps1`。
- 业务行为、数据流、接口语义、安全边界或稳定入口发生变化时，同步更新对应 reference 与 `.agents/knowledge-map.yaml`；内部重构、格式化和普通缺陷修复不追加流水记录。
- 知识文档保存当前有效事实和长期决策，不保存当前数据量、逐次提交记录、测试总数、患者信息、密码、Token 或内网地址。
- `.agents/skills/` 是 Codex 权威入口；不再新增 `.codex/skills/` 副本。

## 数据库与配置注意事项

不要把环境专用密钥提交到 `appsettings.json`。凭据应通过 user secrets、环境变量或部署配置提供。

`DataSync.LHYY.V2` 的数据库变更必须遵循 `DataSync.LHYY.V2/AGENTS.md`：放入 `Scripts/yyyyMM/yyyyMMdd.sql`，同一天的变更追加到同一个文件，避免拆成编号 SQL 文件。

## 提交与 Pull Request 指南

提交标题保持简洁、动作明确。

PR 应说明变更内容、影响项目（`DataSync.CYYY` 或 `DataSync.LHYY.V2`）、数据库或配置变化、验证结果；涉及界面变更时附截图。
