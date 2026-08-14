# LHYY ESB、映射与 ntcare 写入

## 职责与入口

`DataSync.LHYY.V2` 是 .NET 10 应用，提供统一 ESB、SOAP 适配、接口配置、异步消息处理、字段映射及 Bio.Core/目标表写入。

连接边界：

- `DataSyncDb`：DataSync 管理库，主要 schema 为 `lhyy`。
- `CubeDb`：ntcare 产品库，主要涉及 `public`、`care`、`form`、`target`。

关键入口：`EsbController`、`WebServiceController`、`EsbReceiverService`、`SoapWebServiceService`、`InterfaceRecognitionService`、`MessageProcessingService`、`MessageExecutionService`、`GenericMessageProcessor`、`GenericQuestionWriteBackProcessor`、`FieldMappingExecutor`、`FilterRuleService`、`BioCoreIntegrationService`、`DirectTargetWriteService`。

## JSON/ESB 主流程

1. `EsbController` 读取 JSON 请求体；入口支持 gzip，并有请求大小限制。
2. `EsbReceiverService` 取得接入项目上下文，先持久化原消息。
3. `InterfaceRecognitionService` 依次尝试传统交易码、`serverCode/ServerCode/tranCode/TranCode/code/Code`，最后执行配置化匹配规则。
4. 根据 `ReceiveMode` 直接处理或进入后台队列；OCR 接口因识别耗时和失败重试要求，只允许入队异步处理。
5. `MessageExecutionService` 根据 `HandlerType` 选择通用、题目/子卡回写或自定义处理器。
6. 处理器执行接口级过滤、字段级过滤、字段映射、字典转换、幂等和事件定位。
7. 通过 Bio.Core 或受控的目标表直写更新 ntcare，并写消息状态、回执和处理日志。

## SOAP 1.1

- 服务地址为 `/webservice/{serviceCode}`，WSDL 为同地址加 `?wsdl`。
- 请求参数 `INPUTPARA` 可为 CDATA、转义 XML 文本或嵌套 XML。
- `SoapWebServiceService` 按 `SOAPAction` 定位接口和项目，把业务 XML 转为 JSON并补充 `serverCode`，之后复用 ESB 主流程。
- 同一 `soap_service_code` 下 `soap_operation` 和 `soap_action` 均不得重复。
- 返回字符串包含 `RESULT_CODE` 和 `RESULT_CONTENT`；维护失败语义时保持 SOAP Fault 和可重试边界。

## 识别与过滤规则

- `esb_interface_match_rule`：同组规则 AND、组间 OR；路径含 `[]` 时可遍历数组。
- `esb_filter_rule`：`mapping_id IS NULL` 为接口级，否则为映射级；同组 AND、组间 OR，无规则默认通过。
- 数组路径下 `FilterScope.MessageCheck = 0` 表示数组中任一元素满足即让消息通过；`FilterScope.RowFilter = 1` 表示只保留满足条件的数组元素。不得互换两个数值或把行过滤解释为整条消息过滤。
- 项目专属规则优先；没有项目规则时才回退全局规则。
- 操作符包括相等、不等、包含、前后缀、集合、数值比较、空值和正则。
- 接口识别配置存在内存缓存；修改后应使用现有缓存清理能力或重启加载，不把缓存时长当作即时一致性保证。

## 消息状态和待身份绑定

- `Pending/Processing/Success/Failed/Filtered/Unmatched/PartialSuccess` 表示普通处理生命周期。
- `WaitingIdentity` 用于消息已保存但暂时无法定位患者事件的情况，不自动转为失败。
- 支持的统一事件身份组合包括住院号、患者 ID + 住院日期、患者 ID + 住院次数。
- 基础事件成功后，`EventIdentityService` upsert 身份并把匹配消息恢复为 `Pending`。
- 生命体征等先到消息应由 LHYY 接收；CYYY 仅负责后续基础事件采集，不代存实时推送。

## 映射与写入

- `MappingTarget.Patient/Event/Question/SubCard` 分别映射患者、事件、题目和子卡。
- 通用 JSONPath、数组路径、字典和值表达式由配置驱动；不要另建平行映射体系。
- 新建字典可从简单 JSON 对象导入未保存条目：key 为目标值，value 为单个“全部包含”关键词；仅接受非空字符串键值，导入替换页面当前草稿但不直接写库，编辑已有字典不提供该入口。
- 映射源为 JSON `null` 或路径不存在时按缺值处理：优先使用映射默认值，未配置默认值则跳过写入；空字符串 `""` 是明确值，继续参与现有映射和写入。
- Bio.Core 初始化或能力不足时，只有明确支持的处理器可走 `DirectTargetWriteService`；不得把降级路径推广为默认路径。
- 动态 target 表写入必须按表单元数据、系统字段和批准映射确定列范围，不能根据输入对象整行写入。

## FollowUp 包的患者身份适配

- 该路径不经普通 ESB 字段映射；`FollowUpPackageImportService` 在备份前预计算患者映射，并在 CubeDb 导入事务内加锁重算。两次结果不一致时整包失败。
- 优先复用 `unique_patient.id`；ID 不同时先按规范化身份证号，任一方身份证号缺失时才按完整的姓名、出生日期、性别三要素匹配。多候选或身份冲突不得自动选择。
- `patient` 在映射后的 `unique_id + hospital_id + project_id` 范围内唯一。复用已有院端 `patient` 时保留其现有字段，只重映射包内 patient/event/门诊/住院/动态表和 EDC 可见性关联。
- DataSyncDb 的 `lhyy.followup_patient_identity_map` 持久化云端 patient/unique_patient 到院端 ID 的映射，保证无 `patient` 行的 Incremental 和重试包仍稳定指向同一院端患者。该表仅属于 FollowUp 包导入路径；普通 JSON/SOAP ESB、后台消息和医院本地同步不得读写，也不得把 NTCare 原生患者加入 EDC 补图范围。

## 配置表

核心配置和状态表包括 `esb_integration_project`、`esb_integration_project_config`、`esb_global_config`、`esb_interface_config`、`esb_interface_match_rule`、`esb_field_mapping`、`esb_filter_rule`、`esb_idempotent_key_part`、`esb_event_identity`、`esb_messages`、`esb_message_receipt`、`esb_process_log` 和项目文档表。

配置同步把 OCR 配置和字段提取规则作为接口配置的一部分导出、预览和导入；样本消息编号属于环境本地数据，不进入同步包。OCR 自定义处理器在事件身份页面、服务端校验和后续执行上按通用映射处理器使用相同规则，不另建患者、事件或题目映射链路。

配置数量和消息数量属于动态状态，不写入知识库。数据库变更遵循 `DataSync.LHYY.V2/AGENTS.md`。

## LLM 接口约定

- LLM 调用使用 OpenAI 兼容的 `chat/completions` 协议。
- `BaseUrl` 可以配置为服务根地址、以 `/v1` 结尾的基础地址或完整的 `/chat/completions` 地址；服务根地址会自动补全 `/v1/chat/completions`。
- OpenRouter 的 `/api` 基础地址自动补全 `/api/v1/chat/completions`；其他带自定义代理路径的基础地址沿用在原路径后追加 `/chat/completions` 的规则，避免破坏 `/api/v3` 等既有服务。
- 已明确迁移到正式版本的模型别名可在调用层兼容；其他无效模型必须通过配置修正，不得任意替换为可能产生不同成本或语义的模型。
- JSON 字段中文名补全只发送字段结构和脱敏示例，相关请求与响应正文不得写入日志。

## 变更检查

修改识别顺序、receive mode、handler 分发、状态语义、规则组合、事件定位或写入目标时更新本文件。验证至少覆盖命中、不命中、重复消息、过滤、失败重试和项目隔离。
