# FollowUp 医院数据回传部署、验证与恢复

## 1. 组件边界

- `DataSync.CYYY`：通过 SSH forced-command 主动访问 DMZ，查询、拉取并原样保存 `.fupkg`，同时转发 LHYY 生成的 ACK。
- `DataSync.LHYY.V2`：发现已落盘包，完成验签、解密、包链和目标结构校验，备份后导入数据库与附件，并提供恢复入口。
- `DataSync.Common`：只保存三端约定的独立协议 DTO、包清单模型和包链判定代码。它不引用、不导入 `FollowUp` 仓库；两端只通过版本化 JSON/ZIP 契约协作。

两个应用必须连接同一个 DataSync 管理库，并能看到同一份包仓库。目标业务库由 LHYY 的 `CubeDb` 连接指定。

## 2. 数据库升级

原 FollowUp 文档目录中的两个 `2026-07-08` 参考脚本已按项目规则合并到内置功能脚本：

- `DataSync.LHYY.V2/Scripts/202607/20260708.sql`

可直接使用 LHYY 现有“数据库升级”页面处理：

1. 选择目标连接 `DataSyncDb`，升级方式选择“内置脚本”。
2. 点击“检查升级”，在“功能脚本处理”中确认列表包含 `Scripts\\202607\\20260708.sql`。
3. 保留自动备份，输入页面要求的确认文字并执行功能脚本。
4. 打开“FollowUp 包同步”和“FollowUp 回传导入”页面，确认不再提示管理表缺失。

内置功能脚本检查会验证数据库连接并列出项目脚本，但不执行 SQL 语法预演。页面默认先调用 `pg_dump` 备份，因此部署环境必须安装 PostgreSQL client，且不要勾选“跳过自动备份”，除非已经完成并核验手工备份。

合并脚本使用 `IF NOT EXISTS` 和条件约束创建，适合重复检查和执行；正式环境仍应先在同版本测试库演练并核对备份文件。

目标业务库还需要单独执行以下 CubeDb 专用迁移：

- `DataSync.LHYY.V2/Scripts/202607/20260722.sql`

该迁移创建 DataSync 自有的 `datasync.followup_patient_source_map` 来源映射表，并用 `original_source_type` 保存包内患者最近一次携带的原始来源。脚本在常规 `DataSyncDb` 升级链中会识别到目标业务表不存在并安全跳过；部署人员仍必须连接 `ConnectionStrings__CubeDb` 对应数据库手工执行同一文件。导入器会在备份前检查该表及关键字段，缺失时以结构待处理状态阻断导入。

本版本只接受业务契约 `followup-hospital-sync.v3`，导入器版本为 `1.2.0`，要求数据包 `minImporterVersion` 不高于 `1.2.0`。旧 v2 数据包必须撤销并由新云端服务重新生成，不允许继续导入。SSH relay、加密 envelope 和 ACK 的传输协议版本仍为 `1.0`，没有随业务契约升级。

## 3. 容器与目录

新版 Dockerfile 已包含以下运行依赖：

- CYYY：`openssh-client`，用于 `ssh`、`ssh-keygen`。
- LHYY：`postgresql-client`，用于导入前 `pg_dump` 和恢复时 `pg_restore`。

建议持久化挂载：

```text
宿主机 /data/followup/packages  -> 两个容器中的 /app/followup/packages
宿主机 /data/followup/staging   -> LHYY /app/followup/staging
宿主机 /data/followup/backups   -> LHYY /app/followup/backups
宿主机业务附件目录              -> LHYY /app/uploads
宿主机受限 secrets 目录         -> 两个容器 /app/secrets
宿主机配置目录                  -> 两个容器 /app/config
```

CYYY 容器以应用用户运行，挂载目录必须授予该用户读写权限。包仓库、staging、备份和密钥目录不得对无关用户开放。

关键配置建议用环境变量覆盖：

```text
ConnectionStrings__SyncDb
ConnectionStrings__DataSyncDb
ConnectionStrings__CubeDb
FollowUpPackageSync__Enabled
FollowUpPackageSync__PrivateKeyPath
FollowUpPackageSync__KnownHostsPath
FollowUpPackageSync__TokenFilePath
FollowUpPackageImport__PackageRoot
FollowUpPackageImport__StagingRoot
FollowUpPackageImport__BackupRoot
FollowUpPackageImport__AttachmentRoot
FollowUpPackageImport__DecryptionPrivateKeyPath
FollowUpPackageImport__CloudSigningPublicKeyPath
FollowUpPackageImport__Enabled
```

连接串、token 和私钥不得写入镜像或提交到仓库。
加密 Key ID 由当前解密私钥计算，不再单独配置，避免统一初始化后残留旧值。

## 4. 首次启用

1. 启动两个服务，但先保持 `FollowUpPackageSync__Enabled=false`、`FollowUpPackageImport__Enabled=false`，CYYY 来源配置也先不启用定时拉取。
2. 在 LHYY“FollowUp 回传导入 → 医院端统一初始化”生成并导出 `hospital-to-dmz.s7sync`。该动作同时生成 CYYY Ed25519 SSH 密钥和 LHYY RSA-3072 解密密钥，私钥都留在医院主机。
3. 按 `hospital-to-dmz → dmz-to-cloud → cloud-to-dmz → dmz-to-hospital` 四包顺序在 DMZ、FollowUp、DMZ、LHYY 页面完成定向交换；DMZ 运行期授权和医院端七项材料自动即时生效，不需要重启三个应用容器，也不要在 CYYY 单独生成密钥、手填 known-hosts 或手工复制 token。
4. 保存医院编码、DMZ 主机、端口、用户、共享包仓库和拉取周期；来源为空时页面会阻断连接诊断和拉取。保存后执行 CYYY“连接诊断”。
5. 在 LHYY 点击“一键验证”，并确认包仓库、staging、备份、附件、两类密钥和 PostgreSQL 工具全部通过。
6. 在 CYYY 页面手工“查询并拉取”一个测试包，确认 relay 清单缺少合法的 64 位十六进制外层 SHA-256 时立即拒绝，状态只在完整文件原子落盘、大小和 SHA-256 均通过后变为 `Pulled`。
7. 首次必须导入一份完整的 `Baseline` 或 `Replacement`，不能直接从历史链中间的增量包开始；在 LHYY 页面点击“发现包”，对测试包执行“校验 / 导入”。Baseline 必须二次确认；普通兼容增量包可由 Worker 自动导入。
8. 导入成功后重启 NTCare，或执行医院已有的 NTCare 缓存刷新运维流程，确认患者管理能够读取最新表单定义和答案。
9. 校验通过后设置 `FollowUpPackageSync__Enabled=true`、开启 CYYY 来源定时拉取，再设置 `FollowUpPackageImport__Enabled=true` 并重启两个 DataSync 服务。

CYYY 总开关默认为 `false`；关闭时拉取 Worker 在创建服务 Scope 和访问数据库前直接退出，页面手工配置和连接诊断仍可使用。Worker 只有在总开关、管理表、安全材料、目录和外部工具预检均通过时才工作。

## 5. 日常验证增量包

1. CYYY 页面核对 DMZ 连通、包号、序号、类型、水位范围、大小和拉取状态。序号允许有间隙；自动扫描除查询高水位后的新包外，还会合并本地 `Pending`、`Failed` 包重新拉取。
2. 需要补拉时，可直接点击某包的重拉按钮，或填写包号/增量日期范围执行条件重拉。
3. LHYY 页面点击“发现包”，再执行“校验 / 导入”。服务要求外层 SHA-256 非空，并依次验证外层 hash、待导入包号/序号/类型、RSA-PSS 签名、RSA-OAEP 密钥解包、HMAC、AES 解密、内层 checksum 路径与内容、契约版本、包链和目标数据库结构。
4. `Compatible` 自动继续；`RequiresMapping` 可在“结构处理”中保存目标表、字段和默认值映射，或标记“等待数据库升级”；`Breaking` 必须升级数据库或导入器后重试。
5. 导入前自动生成目标业务库完整备份和受影响附件备份。业务数据按清单策略幂等写入，不执行物理删除。原始包、staging 文件和导入前备份保持原始内容不变；所有包内 `public.patient` 写入目标库时统一适配为 `source_type=care`，包内原始值写入来源映射表的 `original_source_type`，其他 UUID 和患者字段保持原值。
6. `followup-hospital-sync.v3` 的 `care.patient_event` 只导出已达到表单展示条件的事件，或 `form_set_id` 为空且有关联住院/门诊资料的基础事件。表单事件要求 `is_valid` 未作废，并满足以下任一条件：状态为“已审核/已随访”；或事件类型为“预问诊/门诊签到”、`input_time` 非空且状态为“门诊结束/办理住院/入组随访/转诊”；或“转诊记录+已确认”。未达到条件的表单事件不得借由住院/门诊关联进入任何包；医院端保持已有表单事件的原始字段，对无表单住院/门诊基础事件按目标项目的 `event_type` 唯一有效定义补齐 `form_set_id`、`form_set_name` 和 `event_type_definition_id`。找不到映射或存在多个映射时整包回滚并进入结构处理，禁止写入 NTCare 无法加载的患者事件。医院端同时拒绝旧 v2 数据包；后续达到条件时，由内容快照变化将完整事件纳入下一增量包。
7. 同一个 CubeDb 事务会把回传患者登记到 `datasync.followup_patient_source_map`，再为其中涉及 EDC 的患者幂等补齐 `patient_data_scope_map`。补图同时覆盖患者事件的 `project_id` 和患者自身的 `public.patient.project_id`，因此没有事件或没有已填写表单的回传患者也可获得 EDC 数据范围；纯 FormSet 增量切换为 EDC 时，也只回填来源映射表中的回传患者，不扩展 NTCare 原生患者。非 EDC 包不会访问 `patient_data_scope_map`，缺少映射表或关键列时在备份和导入前阻断。
8. DataSync 不依赖也不调用 NTCare 新接口。数据和附件提交后，导入状态和 ACK 仍为 `Imported`，同时以 `NTCARE_RESTART_REQUIRED` 和警告日志明确提示：重启 NTCare 或执行医院既有缓存刷新运维流程。该提示不回滚已提交数据。
9. 导入成功后 LHYY 写入 `Imported` ACK，CYYY 使用稳定 `ackId` 重试转发。已成功导入的包不会再次执行，也不会被迟到失败状态降级。后续 `Incremental`、`Supplement` 和 `Replacement` 仍按相同 UUID 幂等增量写入。

映射 JSON 示例：

```json
{
  "public.source_patient": {
    "targetSchema": "care",
    "targetTable": "patient",
    "columnMappings": {
      "source_id": "id",
      "source_name": "name"
    },
    "defaultValues": {
      "tenant_id": 7
    }
  }
}
```

## 6. 恢复与重放

恢复会调用 `pg_restore --clean --if-exists` 恢复该包导入前的完整业务库，并同步恢复受影响附件，属于高危操作。导入、恢复以及目标为 `CubeDb` 的数据库升级/比对通过进程级协调器及 PostgreSQL advisory lock 共用同一套独占租约；独占期间后台消息处理不领取新消息，JSON ESB 请求返回 `AckCode=300.1` 和“系统维护中，请稍后重试”，SOAP 1.1 请求返回可重试的 `soap:Server` 故障，其他页面维护操作也会被拒绝。

1. 先停止上游 ESB 推送，设置 `FollowUpPackageImport__Enabled=false` 并重启服务。代码维护门禁可以排空当前实例中的在途消息并阻止新写入，但生产恢复仍必须先停流，避免上游持续失败或遗漏重试。
2. 核对目标包、备份文件 hash、数据库和附件路径。
3. 在 LHYY 页面输入“确认”，再点击该包“恢复”并完成二次确认。
4. 系统只允许恢复当前实际最后完成导入或恢复失败的包。回退多个包时必须按实际导入完成顺序倒序逐包恢复；不能仅按 `sequenceNo` 判断，因为迟到的 Supplement 或 Replacement 可能在更高序号包之后完成导入。
5. 恢复批次登记后，系统先在持久化 `BackupRoot/.restore-reconciliation` 目录写入未完成标记，再把包状态置为 `Restoring` 并调用实际恢复；再次点击恢复时会先在恢复专用独占租约内协调同包已有的完成标记，如果该标记证明数据库和附件已经恢复，则只补写管理状态、审计和日志并直接返回，不登记新批次，也不重复执行 `pg_restore` 或附件恢复。只有没有完成证明时，后续恢复批次才会在同一个管理库事务内把同包遗留的旧 `Running` 审计标记为“恢复进程中断”，再登记新批次。恢复成功后包状态变为 `Restored`，并写入 `lhyy.followup_package_restore_record` 和审计日志；如果数据库和附件已恢复但 DataSyncDb 暂时不可用，系统会把持久标记更新为已完成，再由后台任务持续幂等补写 `Restored`、`Completed` 和带 `restoreId` 的恢复日志，三项全部落库后才删除标记，期间原 `Restoring` 状态继续阻断普通写入。每个标记只匹配自身的恢复记录、备份记录和当前最新恢复批次；前台已明确捕获恢复失败时会在标记中保存 `RestoreError`，后台只据此补齐该次恢复的 `Failed` 审计和必要的 `RestoreFailed` 状态。没有 `RestoreError` 的当前未完成标记仍表示结果未知，后台只保留等待，不会推断结果，也不会再次恢复 CubeDb 或附件；只有出现更新恢复批次后，仍为 `Running` 的旧未知记录才会标记为“恢复进程中断”。旧批次标记不能把后发恢复状态改为成功，单个异常标记也不会阻断其他标记。页面仍返回恢复成功并提示检查 DataSync 日志，不得重复执行恢复，也不得人工删除补写标记。
6. 需要重放时，从恢复后的最早目标包开始按包链顺序执行“校验 / 导入”；`Supplement` 不推进主链，`Replacement` 仍按被替代包状态校验。
7. 导入或恢复租约会在成功、失败或取消后自动释放；进程崩溃时 PostgreSQL 会随连接断开自动释放 advisory lock。核对业务数据、附件和 ACK 后，再恢复上游推送并重新启用 Worker。

如果恢复失败，保持 Worker 关闭，不要继续导入后续包；`RestoreFailed`、进程中断遗留的 `Restoring` 或 `Importing` 都会作为持久阻断状态，使自动/手工导入、ESB/SOAP 写入、后台消息处理以及 CubeDb 数据库升级/比对停止取得普通写租约。页面对 `Imported`、`RestoreFailed`、`Restoring`、`Importing` 提供恢复入口，并禁止通过“校验 / 导入”覆盖不确定状态。核对数据库 dump、附件备份和应用日志后，可以在同一包上再次执行“恢复”；系统只使用该包已登记的导入前完整备份，只有恢复专用租约可绕过持久阻断，恢复成功后才能继续其他写入。校验/导入生成的 `verify-*` staging 目录会在本轮流程结束后自动清理，不应把 staging 作为恢复依据。

普通 CubeDb 写租约仍以 DataSyncDb 中的危险状态为准。为避免每条 ESB/SOAP/后台消息都串行访问管理库，查询结果只缓存 1 秒；所有应用内导入状态迁移会立即使缓存失效，跨进程或人工改库则最多等待该短缓存到期，不改变危险状态的判断条件。

CYYY 服务如果在包状态写为 `Pulling` 后异常退出，重启后的普通同步会重新领取该包；包文件仍须重新通过完整长度和 SHA-256 校验后才能置为 `Pulled`。

## 7. 上线验收最小集

- 合并数据库脚本经升级页执行成功，两个管理页预检通过。
- CubeDb 专用来源映射迁移已执行；确认没有误应用到 DataSyncDb。
- SSH 严格 host key 校验生效，错误 key、缺 token 和非法 shell 均被拒绝。
- 正常包可拉取、验签、解密、备份、导入并回传 ACK。
- 首次完整 Baseline/Replacement 导入后重启 NTCare，患者管理可查看回传表单；后续增量重复导入不产生重复患者或 EDC 权限记录。
- 修改 envelope、payload、签名或内层文件后均在备份前被拒绝。
- 验证序号间隙、错误前驱、Supplement、Replacement、重复导入和 ACK 重试。
- 在隔离测试库完成一次链头恢复及顺序重放，并核对数据库与附件。
- `dotnet test DataSync.sln` 与 `dotnet build DataSync.sln` 均通过。
