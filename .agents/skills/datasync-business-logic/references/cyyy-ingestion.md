# CYYY 主动采集与同步编排

## 职责与入口

`DataSync.CYYY` 是 .NET 9 应用，负责配置数据来源、定时采集、本地入池、生成待同步队列、失败重试和向下游推送。

主要入口：

- 采集：`IngestionService`、`IngestionWorker`、`DataLakeClient`、`SqlServerQueryService`。
- 编排：`SyncOrchestrator`、`SyncWorker`、`PendingSyncService`、`SyncLogService`。
- 推送：`ApiPushService`、`DatabasePushService`、`PushServiceFactory`。
- 本地查询：`LocalQueryService`。
- EF 上下文：`SyncDbContext`，平台连接名为 `SyncDb`，主要 schema 为 `cyyy`。

## 数据模型

- `data_lake_configs`：数据湖连接与认证配置。
- `ingestion_sources`：采集源和查询定义。
- `database_resources`：医院数据库资源。
- `dl_*`：按 `serverCode` 形成的本地数据池。
- `sync_tasks`、`sync_task_interfaces`：同步任务、接口和路由。
- `pending_sync_items`：待同步队列。
- `sync_logs`、`ingestion_logs`：处理结果与诊断记录。
- `active_sync_*`：主动病例同步的病例、来源和回执状态。

表数量、任务数量、启用状态和队列数量不得固化到知识库；需要时查询当前数据库或配置。

## 采集和推送流程

1. Worker 读取启用采集源。
2. 数据湖来源通过 OAuth Token 调用 REST JSON；数据库来源执行配置 SQL。
3. 采集结果写入 `cyyy.dl_*` 数据池并记录采集日志。
4. 同步任务根据触发字段生成或领取 `pending_sync_items`。
5. `PushServiceFactory` 按任务配置选择 API 或 PostgreSQL 推送。
6. API 主路径把包含接口编码和业务数据的消息推到 LHYY `/api/esb`。
7. 记录成功、失败和重试状态；不得以最大水位永久跳过本地失败项。

## 补数据规则

- SQL 已显式包含 `@queryValue` 或 `:queryValue` 时，配置 SQL自行决定过滤位置。
- Oracle `WITH` CTE 等未包含占位符的查询可在外层按任务配置的 `PatientIdField` 或 `VisitSnField` 过滤；最终结果必须含对应字段。
- 修改过滤策略时同时检查 SQL Server、Oracle、数据湖和本地数据池路径，避免只修一个来源。

## 推送边界

- `ApiPushService` 面向 ESB JSON 接收链路。
- `DatabasePushService` 按 `serverCode` 生成目标表并使用幂等插入语义；启用前必须核对目标连接和表结构。
- 推送类型与目标格式不一致时不得猜测，应检查当前任务记录和实际报文。

## 变更检查

- 数据库结构变更遵循 `DataSync.CYYY/AGENTS.md`，并增加对应迁移文件。
- 修改采集源、过滤字段、触发条件、推送报文或重试水位时更新本文件。
- 至少验证受影响来源的查询、队列状态迁移、重复执行和下游报文结构。
