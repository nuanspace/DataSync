# CYYY 主动采集与同步编排

## 职责与入口

`DataSync.CYYY` 是 .NET 9 应用，负责配置数据来源、定时采集、本地入池、生成待同步队列、失败重试和向下游推送。

主要入口：

- 采集：`IngestionService`、`IngestionWorker`、`DataLakeClient`、`SqlServerQueryService`。数据库查询服务支持 SQL Server、Oracle、MySQL 和 Doris（MySQL 协议）；MySQL 与 Doris 分别使用 3306 和 9030 默认端口。
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
2. 数据湖来源通过 OAuth Token 调用 REST JSON；数据库来源执行配置 SQL。MySQL/Doris 来源只允许 `SELECT`/`WITH`，并要求数据库账号具备只读权限。
3. 采集结果写入 `cyyy.dl_*` 数据池并记录采集日志。
4. 同步任务根据触发字段生成或领取 `pending_sync_items`。
5. `PushServiceFactory` 按任务配置选择 API 或 PostgreSQL 推送。
6. API 主路径把包含接口编码和业务数据的消息推到 LHYY `/api/esb`。
7. 记录成功、失败和重试状态；不得以最大水位永久跳过本地失败项。

组合接口 `JHIDS-BAS-IMR-025` 挂载 `JHIDS-BAS-FBC-027` 的 `FileContents` 时，只输出一条用于 HTML 解析的文件记录。多条候选优先按 `PDL_LAST_UPDATE` 选择最新值，缺失时回退 `UPDATED_T`，时间相同按 `FBC_ROWKEY` 降序；多条记录均无有效更新时间时拒绝推送，避免 LHYY 的 `$main.FileContents[0]`、幂等键和 HTML 内容指向不同版本。其他组合接口的子记录顺序和数量保持原逻辑。

API 推送记录中，以 `__JSON` 结尾的字段名表示该字段值是待解析的 JSON 数组或对象。推送前移除后缀并解析内容；空值、无效 JSON、标量 JSON 或与普通字段重名均使当前接口失败。未标记字段继续按普通字符串处理。

同一业务主键重新采集时，快照内容未变化则保留成功状态；内容变化则清除成功状态并重新进入待同步队列，使源端更新能够继续传递到下游。

## 补数据规则

- SQL 已显式包含 `@queryValue` 或 `:queryValue` 时，配置 SQL自行决定过滤位置。
- Oracle `WITH` CTE 等未包含占位符的查询可在外层按任务配置的 `PatientIdField` 或 `VisitSnField` 过滤；最终结果必须含对应字段。
- 修改过滤策略时同时检查 SQL Server、Oracle、数据湖和本地数据池路径，避免只修一个来源。
- Active 病历入口使用 LHYY `/api/active-medical-records`，必须返回 JSON；仅配置服务根地址时客户端自动补全该路径，HTML 页面不得作为病例列表解析。
- 单个 Active 补采任务遇到暂时无法连接或响应格式错误时，按任务轮询间隔重试，不中断同轮其他任务。

## 推送边界

- `ApiPushService` 面向 ESB JSON 接收链路。
- `DatabasePushService` 按 `serverCode` 生成目标表并使用幂等插入语义；启用前必须核对目标连接和表结构。
- 推送类型与目标格式不一致时不得猜测，应检查当前任务记录和实际报文。

## 变更检查

- 数据库结构变更遵循 `DataSync.CYYY/AGENTS.md`，并增加对应迁移文件。
- 修改采集源、过滤字段、触发条件、推送报文或重试水位时更新本文件。
- 至少验证受影响来源的查询、队列状态迁移、重复执行、内容更新重新入队和下游报文结构。
