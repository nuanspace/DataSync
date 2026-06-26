# DataSync 工作空间业务逻辑记录

更新时间：2026-06-26

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

### DataSync.LHYY.V2

`DataSync.LHYY.V2` 是 .NET 10 / ASP.NET Core / MudBlazor 应用，主要承担“统一接收、配置映射、消息处理、写入 ntcare”职责。

核心职责：

- 提供统一 ESB 接收入口 `POST /api/esb`。
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

- 现有统一处理链路以 JSON 为中心：`EsbController` 读取请求体后交给 `EsbReceiverService`，由 `MessageJsonHelper` 解析为 `JToken`，再通过 JSONPath、字段映射、字典和值表达式完成数据抽取和转换。
- 当前没有独立的通用报文转换模块，也没有已落地的 XML 转 JSON 服务或接口适配层。
- 代码中的 XML 处理主要用于项目文档预览、Word OpenXML 解析和配置导出，不属于医院业务报文转换链路。

通用 OCR 转换链路：

- 2026-06-26 新增 `DataSync.Common` 通用辅助类库，当前提供 PDF OCR 转换能力，供 `DataSync.CYYY` 和 `DataSync.LHYY.V2` 引用；新增能力默认不改变现有接口处理逻辑。
- OCR 公共服务接口为 `IOcrConversionService`，输入为 `OcrSource`，支持 `FilePath`、`Url`、`Base64` 三种 PDF 来源；输出为 `OcrDocumentResult`，包含 `FullText`、`Pages`、顶层聚合 `TextItems`、`ExtractedFields`、`Metadata` 等标准 JSON 结构，单个文本块包含页码和坐标信息。
- OCR 运行实现采用 Linux 容器优先方案：`pdftoppm` 渲染 PDF，`tesseract` CLI 执行识别；程序运行时不联网下载 traineddata，部署镜像需要预装 `tesseract-ocr`、`tesseract-ocr-chi-sim`、`poppler-utils`。
- `DataSync.LHYY.V2` 新增 `OcrMessageProcessor`，仅当接口配置为 `handler_type=1` 且 `handler_name='OcrMessageProcessor'` 时启用；处理器先按 OCR profile 解析 PDF，再把 OCR 结果挂到原消息 JSON 的 `Ocr` 节点，然后继续调用 `GenericMessageProcessor` 复用现有字段映射和 Bio.Core 写入。
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

- 当前代码主链路以 JSON ESB 为主，生命体征文档代表医院要求我们按 XML/WebService 交换数据的场景。
- 若实现此类接口，需要明确是新增推送通道、适配器，还是转成 `/api/esb` 内部 JSON 后复用现有映射。

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
