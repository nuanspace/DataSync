# DataSync 工作空间业务逻辑记录

更新时间：2026-07-17

本文件记录 `D:\Github\DataSync` 工作空间中 ntcare 产品与医院侧系统对接相关的业务逻辑。后续任何业务逻辑改动都必须同步更新本文件。

## 总体定位

本工作空间是一套面向多医院、多来源、多协议的 ntcare 集成适配平台。它不是单纯的数据同步工具，而是用于把医院侧提供或要求交换的数据，转换成 ntcare / Bio.Core 产品库能够识别和落库的患者、事件、表单、题目、子卡片或目标表数据。

典型数据来源和方向包括：

- 医院主动调用我们提供的接口。
- 医院提供数据库、数据池或接口，由我们定时获取。
- 医院提供文件，例如 PDF、DOC、DICOM 或报告文件。
- 我们提供数据或接口，由医院侧调取。
- 我们从医院侧采集数据后，再转交内部 ESB 接收端写入 ntcare。

## 两个项目的分工

### DataSync.CYYY

`DataSync.CYYY` 是 .NET 9 / ASP.NET Core / MudBlazor 应用，主要承担“主动采集与同步编排”职责。

核心职责：

- 配置医院数据来源，包括嘉和数据湖接口、医院 SQL Server / Oracle 等数据库来源。
- 定时采集医院数据并落入本地 `cyyy.dl_*` 数据池表。
- 按同步任务生成待处理队列，记录采集日志、同步日志、重试状态。
- 按任务配置将数据推送到 API 或 PostgreSQL 目标库。
- 当前启用任务主要走 API 推送到 `/api/esb`，再由 `DataSync.LHYY.V2` 处理和写入 ntcare。

关键技术与服务：

- 平台库连接名：`SyncDb`。
- 主要 schema：`cyyy`。
- 采集服务：`IngestionService`、`IngestionWorker`。
- 同步编排：`SyncOrchestrator`、`SyncWorker`。
- 数据湖调用：`DataLakeClient`，使用 OAuth Token + REST JSON 查询。
- 推送服务：`ApiPushService`、`DatabasePushService`、`PushServiceFactory`。
- 本地查询：`LocalQueryService`、`DatabaseQueryService`。

关键表：

- `cyyy.data_lake_configs`：嘉和数据湖连接与认证配置。
- `cyyy.ingestion_sources`：采集源配置。
- `cyyy.database_resources`：医院数据库资源。
- `cyyy.dl_*`：采集后的本地数据池表，表名按 `serverCode` 生成。
- `cyyy.sync_tasks`：同步任务。
- `cyyy.sync_task_interfaces`：任务下游接口和路由配置。
- `cyyy.pending_sync_items`：待同步患者/记录队列。
- `cyyy.sync_logs`、`cyyy.ingestion_logs`：同步和采集日志。
- `cyyy.active_sync_*`：主动病例同步相关表，用于更细粒度的病例/来源/回执状态管理。

当前数据库快照：

- `sync_tasks`：3 个任务。
- `sync_task_interfaces`：13 个任务接口。
- `ingestion_sources`：5 个采集源。
- `pending_sync_items`：812 条，当前快照均为成功。
- 本地数据池示例：`dl_jhids_bas_ifp_030`、`dl_jhids_bas_ihr_003`、`dl_jhids_bas_oap_028`、`dl_lhyy_ph`、`dl_v_blood_vessel_ryxx`。

当前任务配置快照：

- `ntcare`：启用；触发接口 `JHIDS-BAS-IFP-030`；推送类型 `Api`；目标为 `/api/esb`。
- `lhyy_ntcare`：启用；触发接口 `V_BLOOD_VESSEL_RYXX`；推送类型 `Api`；目标为 `/api/esb`。
- `dgs_report`：未启用；历史配置里推送类型字段为 `Api`，但目标像 PostgreSQL 连接串，后续修改前需核对。

当前采集源快照：

- 嘉和数据湖：`JHIDS-BAS-OAP-028` 手术申请、`JHIDS-BAS-IHR-003` 住院就诊、`JHIDS-BAS-IFP-030` 住院病案首页。
- 医院数据库：`lhyy_ph`、`V_BLOOD_VESSEL_RYXX` 等 SQL Server 视图。
- 当前启用的数据库采集源为 `V_BLOOD_VESSEL_RYXX`。

重要边界：

- `DataSync.CYYY` 当前没有引用 `Bio.Core` 包，也没有直接通过 Bio.Core 写入 ntcare。
- 现有启用任务通过 API 推送到 ESB 接收端，实际 Bio.Core 写入发生在 `DataSync.LHYY.V2`。
- `DatabasePushService` 是 PostgreSQL 直连插入能力，按 `serverCode` 生成 `dl_{serverCode}` 表并执行 `INSERT ... ON CONFLICT DO NOTHING`；这不是当前启用 ntcare 任务的主路径。

补数据与数据库采集查询：

- 2026-07-03 起，`DataSync.CYYY` 的 Oracle 数据库采集源按患者 ID 或就诊号补数据时，支持对 `WITH` CTE 查询结果自动按任务配置字段追加外层过滤；患者 ID 与就诊号分别取决于任务的 `PatientIdField`、`VisitSnField`，采集 SQL 最终结果需包含对应字段。
- 如果 SQL 已手动包含 `@queryValue` 或 `:queryValue`，仍直接执行原 SQL，由配置 SQL 自行决定过滤位置。
- 影响链路：主动采集补数据；关键服务为 `IngestionService`、`DatabaseQueryService`。本次未涉及数据库结构变更，不新增迁移文件。

### DataSync.LHYY.V2

`DataSync.LHYY.V2` 是 .NET 10 / ASP.NET Core / MudBlazor 应用，主要承担“统一接收、配置映射、消息处理、写入 ntcare”职责。

核心职责：

- 提供统一 ESB 接收入口 `POST /api/esb`。
- 提供可按接口配置开放的 SOAP 1.1 WebService 入口。
- 管理多医院/多项目接入配置。
- 根据消息内容识别接口。
- 按接口配置决定入队异步处理或直接处理。
- 执行幂等、过滤、字段映射、字典转换、事件定位。
- 通过 Bio.Core 写入患者、事件、FormSet、Question、SubCard。
- Bio.Core 初始化失败时，部分处理器可退回 `target` 表直接写入。
- 管理医院接口文档，支持文档上传、预览和作为配置辅助资料。

关键连接：

- 平台库连接名：`DataSyncDb`，主要 schema 为 `lhyy`。
- 产品库连接名：`CubeDb`，主要 schema 包括 `public`、`care`、`form`、`target`。

关键服务：

- 接收入口：`EsbController`，支持 gzip 请求体，默认最大 100MB。
- 接收处理：`EsbReceiverService`。
- SOAP 适配：`WebServiceController`、`SoapWebServiceService`。
- 接口识别：`InterfaceRecognitionService`。
- 配置服务：`ConfigService`、`IntegrationProjectService`。
- 后台队列：`MessageProcessingService`、`MessageProcessingNotifier`。
- 执行分发：`MessageExecutionService`。
- 通用处理器：`GenericMessageProcessor`。
- 问题/子卡写回处理器：`GenericQuestionWriteBackProcessor`。
- 自定义处理器示例：`PatientUpdateHandler`。
- Bio.Core 封装：`BioCoreIntegrationService`。
- 直接目标表写入：`DirectTargetWriteService`。
- 字段映射：`FieldMappingExecutor`。
- 过滤规则：`FilterRuleService`。
- JSON 读取与路径解析：`MessageJsonHelper`、`SubCardPathHelper`。
- 项目文档：`ProjectDocumentService`。
- LLM 辅助：`LlmService`，用于接口识别/配置辅助场景；配置中不得泄露密钥。

2026-07-16 修复 `BioCoreIntegrationService` 查询有效事件类型时布尔条件的 SQL 拼写错误，避免 PostgreSQL 将 `truew` 误判为列名。该修复不改变事件类型筛选语义。

关键表：

- `lhyy.esb_integration_project`：接入项目。
- `lhyy.esb_integration_project_config`：项目级配置，例如默认 LicenseCode。
- `lhyy.esb_global_config`：全局配置，例如默认接入项目。
- `lhyy.esb_interface_config`：接口注册配置。
- `lhyy.esb_interface_match_rule`：接口匹配规则。
- `lhyy.esb_field_mapping`：字段映射规则。
- `lhyy.esb_filter_rule`：过滤规则。
- `lhyy.esb_idempotent_key_part`：幂等键字段。
- `lhyy.esb_event_identity`：事件定位信息。
- `lhyy.esb_messages`：消息主表。
- `lhyy.esb_message_receipt`：消息回执。
- `lhyy.esb_process_log`：处理日志。
- `lhyy.esb_integration_project_document`：接入项目文档。

当前接入项目：

- `CYYY`：朝阳医院。
- `SJT`：世纪坛医院。
- `LHYY`：潞河医院下肢静脉。

当前配置快照：

- 接入项目：3 个。
- 接口配置：35 个。
- 匹配规则：13 条。
- 字段映射：331 条。
- 过滤规则：33 条。
- ESB 消息：3457 条。
- 消息回执：32016 条。
- 处理日志：3464 条。

当前字典模板：

- `smoking_status`：吸烟情况，分类为“生活史”，推荐按 `priority_first` 匹配。
- `drinking_history`：饮酒史，分类为“生活史”，推荐按 `priority_first` 匹配，目标选项为“从不饮酒”“偶尔饮酒（＜1次/月）”“经常饮酒（≥1次/月）”“偶尔饮酒”“仍在饮酒”，由 `Scripts\202607\20260701.sql` 维护。

当前消息状态快照：

- `LHYY` 项目消息大部分已成功，少量已过滤或部分成功。
- `CYYY` 项目存在 1034 条未匹配消息；修改 CYYY 链路前需重点核对当前项目上下文、接口匹配规则、推送报文结构和 `serverCode` / `tranCode` 对齐情况。

接口处理流程：

1. `EsbController.Receive` 读取 JSON 请求体。
2. `EsbReceiverService.ProcessAsync` 获取当前接入项目，并先写入 `esb_messages`。
3. `InterfaceRecognitionService` 识别接口：
   - 优先识别传统 ESB 结构中的交易码。
   - 再尝试 `serverCode`、`ServerCode`、`tranCode`、`TranCode`、`code`、`Code`。
   - 再按 `esb_interface_match_rule` 执行规则匹配。
4. 根据 `receive_mode` 决定入队异步处理或直接处理。
5. `MessageExecutionService` 根据 `handler_type` 选择处理器。
6. 处理器执行过滤、字段映射、字典转换、事件定位、Bio.Core 写入或目标表直写。
7. 更新消息状态、回执和处理日志。

当前转换边界：

- 除 SOAP 1.1 接收入口外，现有统一处理链路以 JSON 为中心：`EsbController` 读取请求体后交给 `EsbReceiverService`，由 `MessageJsonHelper` 解析为 `JToken`，再通过 JSONPath、字段映射、字典和值表达式完成数据抽取和转换。
- SOAP 1.1 入口由 `WebServiceController` 和 `SoapWebServiceService` 将业务 XML 转为 JSON 后复用统一处理链路；当前没有独立的通用报文转换模块。
- 代码中的 XML 处理主要用于项目文档预览、Word OpenXML 解析和配置导出，不属于医院业务报文转换链路。

数据库升级机制：

- 2026-06-26 调整 `DataSync.LHYY.V2` 数据库升级页的“内置脚本”模式：点击【检查升级】后分为“功能脚本处理”和“优化脚本处理”两个页签。
- 功能脚本处理：`init_database.sql` 和 `Scripts/**.sql` 永远归为功能脚本，保留原有“一次确认后先备份、再按顺序执行多个脚本”的页面方式；该分类不依赖本地状态文件，状态文件丢失也不会把功能脚本混入优化脚本页签。
- 优化脚本处理：`DatabaseUpgrades/**.sql` 归为优化脚本，其中原专项性能优化 SQL `DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql` 作为专项脚本处理；页面以列表展示脚本名称、简介、创建时间、状态和操作，状态包括已执行、未执行、未知。
- 升级脚本执行状态不写入目标 PostgreSQL 主库；页面服务使用应用本地 `DatabaseUpgradeState/database-upgrade-state.json` 记录目标连接指纹、脚本路径、脚本哈希、是否功能脚本、首次发现时间、执行状态、执行时间和错误信息。连接指纹基于目标库 host、port 和 database，不包含数据库密码、用户名或配置别名；读取状态时兼容早期包含连接名和用户名的旧指纹并迁移为新指纹。早期状态文件若已在连接配置别名变更前生成，由于旧指纹不可反推原别名，可能需要重新检查后人工确认未知脚本状态。
- 优化脚本执行已后台任务化：页面提交任务后进入 `DatabaseUpgradeService` 托管后台队列，由同一个 singleton 服务同时承担页面状态查询和 `IHostedService` 队列消费，避免页面状态与后台执行实例分离。页面轮询任务快照，展示等待、连接、备份、执行脚本、迁移、校验、成功或失败等阶段，并在当前页面内展示最多 200 条阶段执行日志；日志只保存在内存任务快照中，不写入 `DatabaseUpgradeState/database-upgrade-state.json`，服务重启或离开当前任务上下文后不作为审计记录保留。服务内存中最多保留 50 个终态任务快照，且终态任务超过 24 小时后可被清理，避免长期运行时无限累积。应用停止时会关闭后台队列并向当前任务传递取消信号；普通 SQL 脚本、专项归档结构升级、历史终态消息迁移、校验和 pg_dump 备份都会传递该取消信号，迁移中未完成的数据库事务会回滚，advisory lock 释放不受取消信号打断；外部 `pg_dump` / `psql` 进程会并发读取输出，取消时终止整个进程树，避免输出管道阻塞或应用停止后残留进程，页面升级服务和直接运行 `message-archive` 工具的备份路径均遵循该清理规则。若任务尚未完成，会释放目标库互斥登记并尽量记录失败状态。优化脚本行内【执行】按钮也必须先在底部确认区输入“确认”才可启用；底部功能脚本批量执行按钮只在“功能脚本处理”页签显示，避免在“优化脚本处理”页签误触功能脚本批量执行。同一目标库同一时间只允许一个升级操作运行，功能脚本批量执行、SQL 文件执行、数据库比对执行和优化脚本后台任务都会使用同一目标库互斥登记。互斥登记按登记 id 精确释放，任务登记对 active 记录缺少快照的短暂并发窗口按“已有任务运行”处理，后台任务在真正备份和执行前会重新检查脚本当前状态仍为未执行，避免提交任务后被其他页面标记状态仍继续执行。页面上传的临时 SQL 文件保存到 `DatabaseSqlFiles/`，属于运行时文件并已忽略提交。
- 2026-07-03 调整优化脚本后台任务进度展示：脚本进入后台执行后，数据库升级页固定显示“脚本正在执行中”和当前步骤，避免长耗时阶段被误认为任务停止；ESB 消息归档专项在迁移历史终态消息前统计待迁移消息总数，迁移过程中按批次回写 `DatabaseUpgradeTaskProgress`，页面显示已迁移消息数、总消息数、百分比、处理日志数、批次和阈值时间。迁移进度和完成总量按本轮实际从热表删除的消息、处理日志行数累计，避免重跑或归档表已有部分数据时因 `ON CONFLICT DO NOTHING` 低估迁移量；批次日志会同时输出新写入归档表的数据量。迁移完成后任务日志会记录“迁移完成：消息 X 条，处理日志 Y 条”，该统计只来自本轮后台任务内存快照，不写入目标库或状态文件。
- 普通优化脚本中的未知状态可以人工标记为已执行或未执行；标记只修改本地状态文件，不执行 SQL，页面要求输入“确认”并二次弹窗确认。服务端会重新校验当前状态仍为未知，且目标库没有正在运行的优化脚本后台任务，才允许标记。专项脚本的未知状态由目标库结构和数据检测决定，不能手工标记。
- 点击【检查升级】时会尝试写入本地状态文件以验证权限；状态文件读取或写入失败时，页面仍尽量展示脚本列表，优化脚本执行和状态标记会禁用并显示状态文件错误，功能脚本处理不依赖该状态文件。状态文件保存时先写入同目录临时文件，再覆盖替换正式 JSON，降低覆盖过程中异常中断损坏正式状态文件的风险。后台任务执行失败时先写入内存任务失败状态，再尝试记录脚本失败状态，避免状态文件异常导致页面一直显示运行中；若数据库动作已经完成，会尽量不受应用停止取消信号影响地写入“已执行”状态，若状态文件更新仍失败，任务会提示“数据库升级已执行，但状态文件更新失败”，不再把已执行脚本反向标记为失败。
- 原专项部署包已纳入优化脚本处理中的 ESB 消息性能优化项。该项不是简单执行 SQL：页面任务会先由升级页完成备份，再复用 `MessageArchiveTool` 执行结构升级、历史终态消息迁移和校验。
- ESB 消息性能优化项状态优先从目标库验证：归档结构未安装时显示未执行；结构不完整或待迁移数据检测失败时显示未知；结构完整但热表仍存在超过热保留天数且符合归档条件的终态消息时显示未执行；结构完整且无待迁移终态消息时显示已执行。
- 命令行 `db-upgrade` 为避免绕过页面状态管理，保留内置脚本检查能力但不再执行脚本；实际升级应进入数据库升级页面操作。
- 影响链路：仅影响 `DataSync.LHYY.V2` 数据库升级/运维页面和 ESB 消息归档优化执行入口，不改变医院 ESB 接收、消息识别、字段映射、Bio.Core 写入或后台消息处理语义。
- 数据库变更脚本：本次没有新增目标业务库表结构脚本；复用既有专项 SQL `DataSync.LHYY.V2/DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql`。
- 验证结果：2026-07-02 使用临时输出目录执行 `dotnet build DataSync.sln --no-restore -p:UseAppHost=false` 通过，0 警告 0 错误；执行 `dotnet test DataSync.sln --no-restore -p:UseAppHost=false` 通过，5 个测试全部通过。

通用 OCR 转换链路：

- 2026-06-26 新增 `DataSync.Common` 通用辅助类库，当前提供 PDF OCR 转换能力，供 `DataSync.CYYY` 和 `DataSync.LHYY.V2` 引用；新增能力默认不改变现有接口处理逻辑。
- OCR 公共服务接口为 `IOcrConversionService`，输入为 `OcrSource`，支持 `FilePath`、`Url`、`Base64` 三种 PDF 来源；输出为 `OcrDocumentResult`，包含 `FullText`、`Pages`、顶层聚合 `TextItems`、`ExtractedFields`、`Metadata` 等标准 JSON 结构，单个文本块包含页码和坐标信息。
- OCR 运行实现采用 Linux 容器优先方案：`pdftoppm` 渲染 PDF，`tesseract` CLI 执行识别；程序运行时不联网下载 traineddata，部署镜像需要预装 `tesseract-ocr`、`tesseract-ocr-chi-sim`、`poppler-utils`。
- `DataSync.LHYY.V2` 新增 `OcrMessageProcessor`，仅当接口配置为 `handler_type=1` 且 `handler_name='OcrMessageProcessor'` 时启用；处理器先按 OCR profile 解析 PDF，再把 OCR 结果挂到原消息 JSON 的 `Ocr` 节点，然后继续调用 `GenericMessageProcessor` 复用现有字段映射和 Bio.Core 写入。
- `Tools/DataSync.PdfOcrTest` 仅作为本地 OCR 验证工具使用，不属于提交范围；仓库忽略该目录，避免本机测试代码、输出文件或 Tesseract 训练数据进入提交。
- OCR 配置表为 `lhyy.esb_ocr_profile`，实体为 `EsbOcrProfile`，读取服务为 `OcrProfileService`。关键字段包括 `tran_code`、`integration_project_code`、`source_kind`、`source_path`、`language`、`dpi`、`page_seg_mode`、`max_pages`、`max_input_bytes`、`timeout_seconds`、`allowed_file_roots`、`output_json_path`。
- OCR 文件路径来源必须配置 `allowed_file_roots` 白名单；会先按配置目录做字面路径初筛，避免探测白名单外路径是否存在；读取前还会解析已存在路径中的 symlink/junction 最终目标，再按 `Path.GetRelativePath` 判断路径归属，避免目录名前缀误匹配和链接逃逸。
- OCR URL 来源必须配置全局 `Ocr:AllowedUrlHosts` 白名单；未配置时拒绝 URL 来源。URL 下载仍限制为 `http/https`、超时和大小，且不使用系统代理。每次请求和重定向都会在 HTTP 连接回调中解析 Host 对应 IP、校验并连接已校验 IP：配置 `Ocr:AllowedUrlCidrs` 时所有解析 IP 必须落入允许网段，未配置时拒绝 loopback、link-local、private、保留、文档、转换用途和多播地址。
- OCR URL 下载禁用 `HttpClient` 自动重定向，由程序最多手动跟随 5 次重定向，并对每一跳的 scheme、host 和实际连接 IP 重新校验，避免白名单 host 通过 30x 跳转到非白名单地址。
- OCR Base64 来源会先按规范化后的 Base64 长度估算解码后大小，超过 `max_input_bytes` 时在分配完整 PDF byte[] 前拒绝，降低大报文造成的内存压力。
- OCR 外部命令使用 `ProcessStartInfo.ArgumentList` 传参，不再拼接命令行字符串；超时时会尝试终止整个进程树，并对终止后的等待设置短超时，避免 `pdftoppm` 或 `tesseract` 子进程残留或清理路径再次卡住。
- `output_json_path` 是 OCR 审计或调试辅助输出位置，不作为业务主链路依赖；配置该字段时必须同时配置全局 `Ocr:AllowedOutputRoots`，运行时会先校验最终输出路径在允许目录内，再启动 OCR，并按 `TranCode`、`MessageId`、时间戳和随机后缀生成唯一 JSON 文件，避免多消息并发覆盖。
- `OcrMessageProcessor` 当前只处理 JSON 对象根节点；顶层数组 OCR 消息会返回明确失败提示，现有非 OCR 顶层数组处理逻辑不变。
- OCR 业务字段提取不另建主映射体系；字段写入仍通过 `lhyy.esb_field_mapping` 的 JSONPath 从 `$.Ocr.FullText`、`$.Ocr.Pages`、`$.Ocr.TextItems` 等节点读取。
- 主动采集 PDF 时，`DataSync.CYYY` 仍负责采集和推送 PDF 路径、URL 或 Base64 到 `/api/esb`；OCR 和写入 ntcare 仍由 `DataSync.LHYY.V2` 完成。
- 数据库脚本：`DataSync.LHYY.V2/Scripts/202606/20260626.sql` 新增 `lhyy.esb_ocr_profile` 表和索引。
- 验证结果：`dotnet build DataSync.sln` 已通过；OCR 外部命令超时等待已改为可取消等待并在超时后终止进程树；当前 Windows 本机未安装 `tesseract` 和 `pdftoppm`，未做本机 OCR 运行验证，Linux 容器依赖已写入 `DataSync.LHYY.V2/Dockerfile`。

识别规则与过滤规则配置：

- `DataSync.LHYY.V2` 的接口识别规则和过滤规则是数据库驱动的业务配置，不写在 `appsettings.json` 中；配置文件主要承载连接、运行参数、LLM 选项等环境或基础配置。
- 接口识别规则表为 `lhyy.esb_interface_match_rule`，实体为 `EsbInterfaceMatchRule`，由 `InterfaceRecognitionService` 读取并执行。
- 接口识别顺序为：先识别传统 ESB 结构中的交易码，再尝试 `serverCode`、`ServerCode`、`tranCode`、`TranCode`、`code`、`Code` 等常见字段，最后按 `esb_interface_match_rule` 执行配置化匹配。
- `esb_interface_match_rule` 的关键字段包括 `tran_code`、`integration_project_code`、`match_group`、`source_path`、`operator`、`compare_value`、`is_enabled`、`sort_order`、`description`。
- 接口识别规则的 `match_group` 表示匹配组：同组规则全部满足才命中，多个组之间满足任一组即可。路径含 `[]` 时支持数组遍历，只要任一数组元素满足条件即可匹配该规则。
- 过滤规则表为 `lhyy.esb_filter_rule`，实体为 `EsbFilterRule`，由 `FilterRuleService` 读取并执行。
- `esb_filter_rule` 的关键字段包括 `tran_code`、`integration_project_code`、`source_path`、`operator`、`compare_value`、`rule_group`、`mapping_id`、`filter_scope`、`is_enabled`、`sort_order`、`description`。
- 过滤规则分为接口级和映射级：`mapping_id IS NULL` 表示接口级过滤，用于判断整条消息是否继续处理；`mapping_id IS NOT NULL` 表示映射级过滤，用于判断某条字段映射是否执行。
- 过滤规则的 `rule_group` 表示规则组：同组 AND，组间 OR；没有任何规则时默认通过。
- 路径支持普通 JSON 路径和 `[]` 数组遍历语法。`filter_scope` 仅在路径含 `[]` 时生效，`MessageCheck = 0` 表示数组中存在任一元素满足条件则消息通过，`RowFilter = 1` 表示只保留满足条件的数组元素。
- 识别规则和过滤规则共用 `FilterRuleService.Evaluate` 的操作符语义，当前支持 `eq`、`neq`、`contains`、`not_contains`、`starts_with`、`ends_with`、`in`、`not_in`、`gt`、`lt`、`gte`、`lte`、`is_empty`、`is_not_empty`、`regex`。
- 规则支持项目级覆盖：查询当前项目规则时优先使用 `integration_project_code` 等于当前项目编码的规则；没有项目专属规则时，才使用 `integration_project_code` 为空的全局规则。
- 接口识别规则通过 `ConfigService` 读取，有 5 分钟内存缓存；修改规则后如需立即生效，需要通过现有配置缓存清理能力或重启服务触发重新加载。
- 过滤规则通常由 `FilterRuleService` 按接口或映射 ID 查询启用规则。接口配置页、过滤规则页、接口向导和映射编辑弹窗可维护这些规则；也可以通过数据库脚本维护。
- `DataSync.LHYY.V2` 数据库配置脚本必须放在 `Scripts/yyyyMM/yyyyMMdd.sql`，同一天变更追加到同一个 SQL 文件。示例：LHYY 血管项目病种过滤使用接口级 `esb_filter_rule`，对入院诊断或出院诊断字段执行 `contains` 判断。
SOAP 1.1 接收流程：

1. 通过“接入配置 → 接口配置”列表查看 WebService 开放状态、服务代码、操作名、`SOAPAction`、服务地址和 WSDL，并使用行内 WebService 配置按钮启用 SOAP；接口完整编辑弹窗的 `WebService` 区域仍可完成同样配置。旧地址 `/config/webservices` 自动跳转到 `/config/interfaces`。
2. 同一服务代码下可配置多个接口动作；服务地址为 `/webservice/{serviceCode}`，WSDL 地址为 `/webservice/{serviceCode}?wsdl`。
3. `WebServiceController` 接收 SOAP 1.1，请求参数名为 `INPUTPARA`，参数值是业务 XML 字符串；同时兼容 `CDATA`、转义 XML 文本和嵌套 XML 节点。
4. `SoapWebServiceService` 按 `SOAPAction` 定位接口及接入项目，把业务 XML 结构化转换为 JSON，并补充 `serverCode` 后调用 `EsbReceiverService`。
5. 后续继续复用接口识别、入队、映射、待身份绑定、幂等和 Bio.Core 写入逻辑。
6. SOAP 返回值仍为字符串，内容格式为 `<RESPONSE><RESULT_CODE>true/false</RESULT_CODE><RESULT_CONTENT>...</RESULT_CONTENT></RESPONSE>`。

接口配置的消息模板输入框支持直接粘贴业务 XML 或完整 SOAP 1.1 请求。页面可立即解析预览，保存时也会自动转换并仅保存内部 JSON 模板；医院实际调用时仍直接发送 XML，不要求医院改成 JSON。

相关接口配置字段：`soap_enabled`、`soap_service_code`、`soap_operation`、`soap_action`。同一服务代码下操作名和 `SOAPAction` 均不得重复；数据库升级脚本为 `DataSync.LHYY.V2/Scripts/202607/20260716.sql`。

页面新建 WebService 配置或打开尚未配置服务代码的接口时，`soap_service_code` 默认带出 `bioo`，用户仍可按部署分组需要修改。

枚举语义：

- `ReceiveMode.PersistAndAsync = 0`：入队异步处理。
- `ReceiveMode.Direct = 1`：收到后直接处理。
- `HandlerType.Generic = 0`：通用患者/事件/表单处理。
- `HandlerType.Custom = 1`：自定义处理器。
- `HandlerType.GenericQuestionWriteBack = 2`：通用题目/子卡写回。
- `MappingTarget.Patient = 0`：映射到患者。
- `MappingTarget.Event = 1`：映射到事件。
- `MappingTarget.Question = 2`：映射到题目值。
- `MappingTarget.SubCard = 3`：映射到子卡。
- `MessageStatus.Pending = 0`、`Processing = 1`、`Success = 2`、`Failed = 3`、`Filtered = 4`、`Unmatched = 5`、`PartialSuccess = 6`。
- `MessageStatus.WaitingIdentity = 7`：待身份绑定。用于生命体征等先于基础住院事件到达的消息；消息已接收并保存原始报文，但暂时无法定位患者事件，不进入普通 `Pending` 队列。

### 待身份绑定消息

2026-07-02 起，`DataSync.LHYY.V2` 支持把缺少现成患者事件身份的回写类消息置为 `WaitingIdentity`。该状态主要服务于“医院实时推送生命体征，CYYY 后续主动采集基础住院/出院数据”的场景。

处理边界：

- 医院主动推送的生命体征仍直接进入 `DataSync.LHYY.V2` 的 `/api/esb`。
- `DataSync.CYYY` 不接收也不暂存这类推送消息，只负责后续主动采集基础住院/出院数据并推送到 LHYY。
- 生命体征建议配置为 `GenericQuestionWriteBack`，接收方式为入队异步处理，事件时间缺失策略选择“允许缺失，回查失败则转待身份绑定”。
- 支持的统一事件身份组合为：住院号；患者 ID + 住院日期；患者 ID + 住院次数。
- 基础住院事件处理成功后，`EventIdentityService.UpsertAsync` 写入或更新 `lhyy.esb_event_identity`，并把匹配的 `WaitingIdentity` 消息改回普通 `Pending`，由后台队列再次处理。
- 匹配不到的消息保持 `WaitingIdentity`，不自动改为失败，不做人工确认流程。
- 本次改造未新增数据库表或字段；数据库仍以 `lhyy.esb_messages.status` 的 `smallint` 保存消息状态。

## 医院接口文档与业务含义

### 世纪坛对外数据服务

文档：`.doc\对外服务接口文档-世纪坛.docx`

含义：

- 医院数据中台通过 RESTful API 提供数据查询。
- 请求方法为 POST，数据格式 JSON。
- 调用方先通过客户端凭据获取 Token，再带 Authorization 调用数据查询接口。
- 业务请求包含 `serverCode`、`sysCode`、`condition`、`orders`、分页参数等。
- 查询条件建议至少包含 `HIS_PAT_ID`、`HIS_VIS_ID`、SN 号之一，以保证稳定性和性能。

与项目对应：

- 对应 `DataSync.CYYY` 的 `DataLakeClient`、`DataLakeConfig`、`IngestionSource`、`dl_*` 本地数据池。
- 当前已配置或映射的典型接口包括：
  - `JHIDS-BAS-IFP-030`：住院病案首页。
  - `JHIDS-BAS-IHR-003`：住院就诊记录。
  - `JHIDS-BAS-OAP-028`：手术申请。
  - `JHIDS-BAS-OAR-041`：手术麻醉/实际手术信息。
  - `JHIDS-BAS-IOR-004`：住院医嘱。
  - `JHIDS-BAS-IVS-034`：生命体征记录。
  - `JHIDS-BAS-LRS-020`：检验结果。
  - `JHIDS-BAS-ERT-010`：检查报告。
  - `JHIDS-BAS-IDR-011`：住院临床诊断记录。

### 嘉和生命体征 WebService

文档：`.doc\JHMK-JHIP-JH1040-嘉和集成平台规范-生命体征接口.docx`

含义：

- 嘉和集成平台生命体征接口使用 WebService。
- 接口名为 `VitaInterface`，输入和返回为字符串。
- 输入是 XML，包含住院流水号、住院次数、科室、床号、身高、体重、疼痛、出入量、有创/无创血压、体温、脉搏、呼吸、记录时间、管路循环项、消息时间、系统类型、增删改标记等。
- 返回 XML 包含 `RESULT_CODE` 和 `RESULT_CONTENT`。

与项目对应：

- 可在 LHYY 接口配置中把 `VitaInterface` 对应接口开放为 SOAP 1.1 动作。
- 输入参数名为 `INPUTPARA`，业务 XML 根节点为 `REQUEST`；转换后的内部 JSON 为 `{"serverCode":"接口事件代码","REQUEST":{...}}`，字段映射从 `$.REQUEST` 下读取。
- 建议使用入队异步处理和待身份绑定策略，按住院流水号、患者 ID + 住院日期或患者 ID + 住院次数复用现有事件定位方式。

### 嘉和移动医护评估单 WebService

文档：`JHMK-JHIP-JH1038-嘉和集成平台规范-移动医护评估单.docx`

含义：

- 接口名为 `MOBILEASSESSMENT`，输入参数 `INPUTPARA` 和返回值均为字符串。
- 输入业务 XML 根节点为 `REQUEST`，包含增删改标记、评估类型、患者 ID、住院次数、病区、评估时间、评估人、总分、风险等级及 `systems/system` 循环评估项。
- 返回 XML 包含区分大小写的 `RESULT_CODE` 和 `RESULT_CONTENT`。

与项目对应：

- 可与 `VitaInterface` 使用同一个 `soap_service_code`，分别配置不同的 `soap_operation` 和 `soap_action`。
- SOAP 入口只做协议转换，评估字段映射和患者事件定位仍由对应 LHYY 接口配置处理。

### 世纪坛无纸化第三方接口

文档：`.doc\世纪坛无纸化第三方接口方案20231130.docx`

含义：

- 医技报告、病历文书等以 PDF 文件流形式传给无纸化系统。
- 归档数据发送包含报告类型编码、OID、住院流水号、患者 ID、住院次数、文件流水号、PDF、CA 签名内容、归档标记、报告处理类型、应传/实传数量、操作时间等。
- 支持归档标记同步、归档情况同步、报告数量同步、归档标记查询。
- 归档状态常见值：`i` 在院/取消归档，`p` 已归档，部分查询场景还有 `o` 待归档。
- 文档中包含系统 OID 和数据中心报告类型编码，例如 HIS、EMR、检验、检查、血透、心理测评等。

与项目对应：

- 当前 `DataSync.LHYY.V2` 已具备项目文档管理和 ESB 消息处理能力，但 PDF/CA/归档 WebService 的完整业务实现需要按具体接口新增适配器或处理器。
- `.doc\report_result.json` 是非结构化报告抽取结果示例，包含患者信息、心率、房早/室早、结论等，提示后续可能需要把 PDF/报告内容结构化后写入 Bio.Core 表单或 target 表。

## Bio.Core / ntcare 写入逻辑

`DataSync.LHYY.V2` 通过 `Bio.Core` 接入 ntcare 产品库。

产品库结构快照：

- `care`：患者、事件、入排、随访等业务表。
- `form`：项目、FormSet、Form、Card、Question 等表单元数据。
- `target`：具体目标数据表，当前约 233 张。

写入路径：

- 患者字段映射到 `MappingTarget.Patient`。
- 事件字段映射到 `MappingTarget.Event`。
- 表单题目值映射到 `MappingTarget.Question`。
- 子卡/明细数组映射到 `MappingTarget.SubCard`。
- 对于子卡写入，处理器会根据 FormSet/Question 元数据选择 Bio.Core 导入服务或直接目标表写入。

当前 CYYY / SJT 映射特征：

- `JHIDS-BAS-IFP-030` 同时映射患者基础信息、住院事件、费用/出院/ICU 等题目或子卡字段。
- `JHIDS-BAS-IVS-034` 映射生命体征题目和子卡。
- `JHIDS-BAS-LRS-020` 映射检验结果明细，常见 `array_path = Datas`。
- `JHIDS-BAS-ERT-010` 映射检查报告子卡。
- `JHIDS-BAS-OAR-041` 映射手术相关事件和题目。

当前 LHYY 映射特征：

- `V_BLOOD_VESSEL_RYXX`、`v_blood_vessel_jbxx` 映射患者和事件。
- `v_blood_vessel_jcxx`、`v_blood_vessel_hysj`、`V_BLOOD_VESSEL_CYXX` 等更多用于题目或子卡写回。
- `MRD0104` 使用 `GenericQuestionWriteBack`，用于病历/文书类题目写回。

## FollowUp 医院数据回传链路

该链路整合在现有两个应用中，不新增独立程序，也不引用 `FollowUp` 项目程序集：

- `DataSync.Common/FollowUp` 保存独立、版本化的三端协议 DTO、外层 envelope 严格解析、包内容清单模型和包链判定。它是协议代码，不是对 `FollowUp` 仓库的项目引用。
- `DataSync.CYYY` 负责通过 SSH forced-command 调用 DMZ 的 `relay-health`、`relay-list`、`relay-pull`、`relay-ack`，原样保存加密包，并维护拉取和 ACK 状态。
- `DataSync.LHYY.V2` 负责验签、解密、结构校验、备份、导入、ACK 生成和恢复。

CYYY 处理规则：

- SSH 请求使用单个 stdin JSON；token、包号等业务参数不进入命令行。SSH 开启严格 host key 校验，禁用交互、PTY 和转发。
- 包流写入隐藏 `.partial` 文件，同时计算大小和 SHA-256；通过后 `fsync` 并原子改名为 `<packageId>.fupkg`，之后才把数据库状态标记为 `Pulled`。
- 同一医院串行拉取。普通同步按最大 `sequenceNo` 查询云端新候选，同时合并本地 `Pending`、`Failed` 以及进程中断遗留的 `Pulling` 包重新拉取，避免低序号包因传输失败或服务重启被后续水位永久跳过；序号只排序、不判断连续。页面仍支持按包号或水位日期范围重拉。
- LHYY 对同一医院、包号、ACK 状态 upsert `cyyy.followup_package_ack_queue`，队列 UUID 的字符串形式作为稳定 `ackId`。CYYY 转发失败时保持 ackId 重试。
- Worker 仅在 CYYY 管理表、Ed25519 私钥/公钥、known-hosts 和 token 文件均就绪时运行。

LHYY 校验与导入顺序：

1. 校验外层包大小和清单 SHA-256，拒绝多余、缺失或重复的 ZIP 顶层条目。
2. 先对 `envelope.json` 精确字节执行 RSA-PSS-SHA256 验签，再严格解析字段、顺序、协议版本和算法。
3. 用院内 RSA 私钥以 OAEP-SHA256 解包 64 字节密钥材料；前 32 字节用于 AES-256-CBC，后 32 字节用于 HMAC-SHA256。
4. `payload.bin` 流式落临时文件并同时校验长度、SHA-256 和 `IV + payload` HMAC，避免大包整体进入内存。
5. 解密后限制 ZIP 条目数、展开总量并阻止路径逃逸，随后校验 `checksums.sha256`、manifest 和三个结构文件 hash。
6. 校验导出契约、最低导入器版本和包链。`sequenceNo` 只排序；Incremental 的前驱必须等于当前主链头；Supplement 引用已导入相关包且不推进主链；Replacement 只替代尚未成功导入的包。
7. 以包内 `table-manifest.json` 为唯一导入范围，按 `ReferenceMaster`、`Relationship`、`BusinessData` 排序，并执行 `UseExistingById`、`RejectIfMissing`、`InsertIfMissing`、`Upsert`。不根据包缺项物理删除目标数据。
8. `Compatible` 自动继续；`RequiresMapping` 可在页面保存目标表、字段、默认值映射，或标记等待数据库升级；`Breaking` 禁止自动导入。
9. 导入前用 `pg_dump -Fc` 备份整个 `CubeDb`，并备份本包会覆盖的附件。数据写入事务在附件原子切换完成后提交；任一环节失败都会回滚数据库并恢复附件。附件自动恢复失败时必须写入 `RestoreFailed`，不能降级为普通 `ImportFailed`；每次校验/导入结束后清理本轮 `verify-*` staging 目录并清空失效路径。
10. 数据提交后写入 `Imported` 状态和 ACK。提交后的审计/ACK 暂时失败不得把成功结果降级为失败，也不得自动重做已成功包。

导入状态与维护门禁补充规则：

- `cyyy.followup_package_pull_state` 再次发现已拉取包时，`lhyy.followup_package_import_state` 只允许新包或 `AwaitingPackage`、`WaitingForPredecessor` 进入 `Pending`；`WaitingForDecision`、`RejectedSchemaMismatch`、`ImportFailed`、`Restored`、`RestoreFailed` 等终态或人工处理状态必须保留。
- 任一包处于 `RestoreFailed` 时，`FollowUpPackageImportWorker` 停止领取所有后续包，页面和服务端禁止直接重新导入覆盖该状态；人工核对后可对同一包重试恢复，恢复成功或人工完成处置并明确解除失败状态后才能继续。
- FollowUp 导入、恢复或以 `CubeDb` 为目标的数据库升级/比对持有同一套独占 `CubeDb` 维护租约时，其他维护操作以及 JSON `/api/esb`、SOAP 1.1 WebService、后台消息 Worker 和页面手工重试均不得进入写库链路；SOAP 返回可重试的 `soap:Server` 故障。`DataSyncDb` 等非 `CubeDb` 升级仍只使用原目标库互斥登记。

恢复规则：

- 页面要求输入“确认”并再次弹窗确认。
- 使用该包导入前的 `pg_restore --clean --if-exists` 完整业务库备份和附件备份恢复。
- 只能恢复当前已导入链头；多包回退必须从高序号到低序号逐包执行。恢复过程写入 `lhyy.followup_package_restore_record` 和导入日志。
- 恢复失败后保持 Worker 关闭，禁止继续导入后续包。恢复成功后可按包链重新执行校验和导入。

配置与部署：

- CYYY 和 LHYY 连接同一个 DataSync 管理库，并挂载同一包仓库；LHYY 的 `CubeDb` 指向目标 ntcare/PostgreSQL 业务库。
- CYYY 镜像需要 `openssh-client`；LHYY 镜像需要 `postgresql-client`。
- CYYY 密钥、known-hosts、token，LHYY 解密私钥、云端验签公钥、包仓库、staging、备份和附件目录都必须持久化并限制权限。
- `FollowUpPackageImport.Enabled` 默认关闭，首次部署应在两个页面完成预检和测试包验证后再开启。

数据库管理表脚本已合并到 `DataSync.LHYY.V2/Scripts/202607/20260708.sql`，包含 `cyyy` 包拉取/ACK 管理表与 `lhyy` 包导入/结构校验/备份恢复管理表。两个应用共用 `DataSyncDb`，现场在 LHYY 数据库升级页面选择 `DataSyncDb` 和“内置脚本”，检查后通过“功能脚本处理”统一备份并执行；脚本使用 `IF NOT EXISTS` 和条件约束保护。影响链路为 FollowUp 文件/报告归档的数据包拉取、校验、导入、ACK 与恢复管理，不改变 ESB 消息处理语义。验证结果：已核对合并文件包含两个来源脚本的全部 SQL，并由项目升级服务的 `Scripts/**.sql` 扫描规则纳入功能脚本列表；未连接现场数据库执行。现场步骤见 `DataSync.LHYY.V2/ProjectDocuments/FollowUp医院数据回传部署与恢复.md`。

2026-07-17 修正 FollowUp 回传链路的重试、包链、维护门禁和状态迁移：影响项目为 `DataSync.CYYY`、`DataSync.LHYY.V2` 和 `DataSync.Common`，影响链路为医院数据包拉取、校验、导入与恢复；关键表为 `cyyy.followup_package_pull_state`、`lhyy.followup_package_import_state`，关键服务为 `FollowUpPackageSyncService`、`FollowUpPackageImportRepository`、`FollowUpPackageImportWorker`、`SoapWebServiceService`。本次不修改数据库结构，不新增 SQL。验证结果：`dotnet test DataSync.sln --no-restore -p:UseAppHost=false` 通过 66 项测试；Release 解决方案构建 0 警告、0 错误；CYYY 和 LHYY 两个最终 Docker 镜像构建通过。

2026-07-17 继续修正 FollowUp 异常恢复与 CubeDb 运维互斥：影响项目为 `DataSync.CYYY`、`DataSync.LHYY.V2` 和 `DataSync.Common`，影响链路为包拉取、校验/导入、附件恢复、数据库升级与比对；关键表仍为 `cyyy.followup_package_pull_state`、`lhyy.followup_package_import_state`，未修改数据库结构且不新增 SQL。`Pulling` 可在进程重启后重新拉取；附件回滚失败进入 `RestoreFailed`；`RestoreFailed` 禁止直接导入但允许重试恢复；所有导入结果都会清理 staging；目标为 `CubeDb` 的数据库升级/比对与 FollowUp 共用维护租约。验证结果：聚焦测试均完成先失败后通过，`dotnet test DataSync.sln --no-restore -p:UseAppHost=false` 当前通过 98 项测试。

## 新增或修改接口的推荐步骤

1. 明确医院项目编码：`CYYY`、`SJT`、`LHYY` 或新增项目。
2. 明确数据方向：
   - 医院主动推送到我们。
   - 我们主动采集医院数据湖/数据库。
   - 我们向医院推送数据或文件。
   - 医院调用我们提供的数据服务。
3. 明确数据形态：JSON、XML、数据库表/视图、PDF/DOC/DICOM、压缩包、混合结构。
4. 若是主动采集，优先检查或新增 `DataSync.CYYY` 采集源和同步任务。
5. 若要写入 ntcare，优先在 `DataSync.LHYY.V2` 配置接口、匹配规则、字段映射、过滤规则、幂等规则。
6. 若通用处理器无法表达业务，新增或扩展处理器，并在 `MessageExecutionService` 或配置中明确分发方式。
7. 数据库结构变更必须补对应 SQL：
   - `DataSync.CYYY`：`Migrations\yyyy-MM\yyyy-MM-dd_变更说明.sql`。
   - `DataSync.LHYY.V2`：`Scripts\yyyyMM\yyyyMMdd.sql`，同一天追加到同一文件。
8. 更新本文件，记录新增接口编码、数据来源、处理器、映射目标、业务规则和验证结果。

## 安全与配置规则

- 不要在说明、提交或技能记录中展开真实数据库密码、Token、API Key、客户端密钥或医院内网地址。
- 当前项目配置文件中存在明文连接串和 LLM Key，后续应迁移到 user secrets、环境变量或部署平台密钥管理。
- 分析数据库时可从项目配置读取连接串，但输出必须脱敏。
- 涉及患者数据时，避免输出患者姓名、身份证、电话、病案号等可识别信息。

## 待关注问题

- `CYYY` 项目当前 ESB 消息大量未匹配，后续需核对当前项目上下文、`serverCode` 匹配规则和 `ApiPushService` 报文结构。
- `dgs_report` 的推送类型和目标格式存在不一致迹象，启用前必须重新确认。
- 文件/报告/PDF/CA/归档接口目前更多体现在文档和项目边界中，完整实现程度需要按具体代码和需求再次确认。
- `DataSync.CYYY` 与 `DataSync.LHYY.V2` 当前通过 `/api/esb` 间接串联；如果未来要求 CYYY 直接通过 Bio.Core 写入，应新增明确的 Bio.Core 推送通道并记录在此。
