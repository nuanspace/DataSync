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

本次升级禁止对 CubeDb 执行 `20260722.sql`、旧版 `20260810.sql` 或其他自定义 DDL。患者身份映射改存 DataSyncDb 的 `lhyy.followup_patient_identity_map`，只需在停止自动导入并备份 DataSyncDb 后，通过数据库升级页对 `DataSyncDb` 执行 `DataSync.LHYY.V2/Scripts/202608/20260811.sql`。CubeDb 兼容检查只核对 NTCare 既有业务表、字段和读取/导入权限。

从已部署旧版本升级时，执行 DataSyncDb 迁移后、启动自动导入前，在 LHYY 镜像中运行 `followup-patient-map bootstrap --hospital-code <医院编码> --confirm-datasync-write`。工具以只读事务读取 CubeDb 的旧 `datasync.followup_patient_source_map`，兼容只有 `patient_id` 的初版以及含双端 ID 的扩展版，并幂等写入 DataSyncDb；不会修改或删除 CubeDb 旧表。若旧表不存在或没有该院记录，但 DataSyncDb 已有历史导入包，命令返回需恢复基线，此时必须生成并导入 `RecoveryBaseline` 后才能继续 Incremental。回滚到旧镜像前，必须先用当前版本恢复所有由新版完成的包，避免旧镜像无法识别 DataSyncDb 中的新映射状态。

```bash
docker compose run --rm datasync-lhyy-v2 \
  followup-patient-map bootstrap \
  --hospital-code <医院编码> \
  --confirm-datasync-write
```

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
5. 导入前自动生成目标业务库完整备份和受影响附件备份。业务数据按清单策略幂等写入，不执行物理删除。每个实际导入文件都通过同一个只读句柄校验表清单 SHA-256 并继续消费该句柄；校验后即使原路径被替换，也不会重新按路径读取未验证内容。`ARRAY/text[] → text` 的值形状也会在消费这一个已验证句柄时逐行复验，不能仅依赖此前按路径完成的结构预检。原始包、staging 文件和导入前备份保持原始内容不变。患者身份先按原 `unique_patient.id`，再按双方非空身份证，最后在任一方无身份证时按完整的姓名、出生日期、性别三要素识别；双方身份证均非空但不相等时不降级使用三要素。多重命中整包阻断且日志不记录患者明文。自然匹配复用院端 `patient.id` 时保留院端患者字段，并同步重写包内全部患者引用；原 `patient.id` 相同或院端尚无患者明细时仍按现有规则写入并适配 `source_type=care`。CubeDb 提交成功后，双端患者 ID、唯一患者 ID、匹配依据和原始来源会与 `Imported` 状态在一个 DataSyncDb 事务内写入；该事务失败时保留 `Importing` 门禁，禁止后续包越过。新版附件备份使用版本 2 清单，记录原文件存在状态和备份副本 hash；备份登记同时锚定附件清单 hash、条目数和备份总大小。登记成功后只创建一次受锚定的附件冻结快照，附件安装与明确回滚时的补偿共同复用该快照，不再重新信任可变清单或原备份目录。安装先把包内文件复制到目标同目录临时文件并校验该实际副本的 size/hash；补偿也先复制并校验实际恢复临时副本；两者通过后才原子认领当前文件，再用硬链接 create-if-absent 发布，校验后出现的路径级新版本不会被覆盖。文件系统不支持同目录硬链接时会在认领目标前安全阻断；硬链接能力探针或其他临时文件无法清理时，会在异常中保留残留路径和主操作/清理原因并进入 `RestoreFailed`，不得把敏感硬链接副本遗留当作普通失败继续。安装在发布前异常时会无覆盖放回原版本，放回失败则保留 `.claim` 并进入 `RestoreFailed`。包附件已发布但旧版本 `.claim` 清理失败时同样进入 `RestoreFailed`。已明确回滚的导入只补偿本次实际安装且认领内容仍保持包内 hash 的附件；若认领后仍无法完成补偿，则保留最后可用的包附件 `.claim` 现场并进入 `RestoreFailed`。数据库提交结果不确定时也不会自动补偿附件，而是把包置为 `RestoreFailed`，必须使用该包登记的完整数据库与附件备份执行恢复。患者主档会在事务内加 `SHARE` 锁复验，生产导入应安排低峰期，避免阻塞 NTCare 患者维护。
6. `followup-hospital-sync.v3` 的 `care.patient_event` 只导出已达到表单展示条件的事件，或 `form_set_id` 为空且有关联住院/门诊资料的基础事件。表单事件要求 `is_valid` 未作废，并满足以下任一条件：状态为“已审核/已随访”；或事件类型为“预问诊/门诊签到”、`input_time` 非空且状态为“门诊结束/办理住院/入组随访/转诊”；或“转诊记录+已确认”。未达到条件的表单事件不得借由住院/门诊关联进入任何包；医院端保持已有表单事件的原始字段，对无表单住院/门诊基础事件按目标项目的 `event_type` 唯一有效定义补齐 `form_set_id`、`form_set_name` 和 `event_type_definition_id`。找不到映射或存在多个映射时整包回滚并进入结构处理，禁止写入 NTCare 无法加载的患者事件。医院端同时拒绝旧 v2 数据包；后续达到条件时，由内容快照变化将完整事件纳入下一增量包。
7. EDC 补图只使用本包患者以及 DataSyncDb 中该医院的 FollowUp 映射目标 ID，再幂等写入 CubeDb 既有的 `patient_data_scope_map`。补图同时覆盖患者事件的 `project_id` 和患者自身的 `public.patient.project_id`，因此没有事件或没有已填写表单的回传患者也可获得 EDC 数据范围；纯 FormSet 增量切换为 EDC 时，也只回填已映射的回传患者，不扩展 NTCare 原生患者。普通 ESB 和医院本地数据导入不读写 `lhyy.followup_patient_identity_map`。非 EDC 包不会访问 `patient_data_scope_map`。
8. DataSync 不依赖也不调用 NTCare 新接口。数据和附件提交后，导入状态和 ACK 仍为 `Imported`，同时以 `NTCARE_RESTART_REQUIRED` 和警告日志明确提示：重启 NTCare 或执行医院既有缓存刷新运维流程。该提示不回滚已提交数据。
9. 导入成功后 LHYY 写入 `Imported` ACK，CYYY 使用稳定 `ackId` 重试转发。已成功导入的包不会再次执行，也不会被迟到失败状态降级。后续 `Incremental`、`Supplement` 和 `Replacement` 仍按相同 UUID 幂等增量写入。

动态表导入按当前医院实际关联表单项收敛字段范围：

- `target` 动态宽表只校验和写入 NTCare 的 31 个系统固定字段、主键、当前医院 `form.form_question` 关联字段和已批准的默认值字段。结构校验与实际 upsert 使用同一字段集合；未关联字段不会写入，也不会用包内 `null` 覆盖医院端已有值。医院范围内所有 `form_question` 行，包括 `table_name` 或 `column_name` 未绑定的行，仍原样参与内容 hash；只有同时提供有效表名和字段名的行才形成动态字段授权。已绑定行缺少 `data_type` 时仍可授权同名字段，但不得启用 `ARRAY/text[] → text` 特例。
- `form_project` 导出文件是按 `updated_at` 生成的增量子集。只要本包携带启用且未跳过的项目文件，无论是否同时携带 `form_question` 或动态答案，系统都会独立校验文件 hash、记录数、项目 ID 唯一性和包内医院归属；医院端已存在的同 ID 项目也必须属于当前医院，缺失则允许由本包新增。包内携带 `form.form_question` 文件时，系统还校验 manifest、文件 hash、内容 hash、记录数、题目 ID 唯一性和题目医院身份，并按每个题目的 `project_id` 复核所属项目：本包未覆盖的引用项目必须已在医院端存在且属于当前医院，医院端已存在的同 ID 题目及其当前所属项目也都必须属于当前医院，目标缺失则允许由本包新增。即使本包没有任何 `target` 动态答案文件，上述题目和项目校验仍必须在备份前执行。增量包因内容未变化而不携带题目文件时，必须同时满足：其 `contentHash` 与当前已导入主链头登记值一致，并且医院端按数据包 `schema-snapshot` 的源字段集合投影后，以云端同构 `to_jsonb(row)::text ORDER BY 1`、UTF-8 及 LF/CRLF 两种平台换行候选重算的实时快照 hash 也一致；目标表新增的可空或有默认值字段不会干扰校验。权威空快照也会用空内容 hash 证明医院端范围确实为空；任一 hash 缺失或不一致、Baseline 省略非空快照时均停止导入并进入结构处理。
- 包内未携带 `form.form_question` 文件、必须使用医院端实时快照的 Target/Empty 范围，会在 CubeDb 事务开始且尚未执行任何业务读写时，对 `form.form_project` 和 `form.form_question` 获取 `SHARE` 锁；写完前置关系表、首次写入 `target` 动态表之前，再按源字段投影重算 hash。任何携带项目增量的包都会建立项目写前守卫；Package 范围携带题目快照时再叠加题目守卫，并在事务首部锁定这两张表。任何包内 `form_project` 行处理前，先复核医院端已存在的同 ID 项目仍属于当前医院（缺失允许）；导入排序只把原本位于 `form_project` 之前的 `form_question` 和动态答案消费者稳定延后到项目之后，不会把项目提前越过医院、科室等既有前置基础表；写入 `form_question` 或首张动态表之前，再复核医院端既有同 ID 题目及其项目归属，并复核全部题目引用项目均已存在且属于当前医院。项目/题目安全表不得通过批准映射进出，`id`、归属和动态授权字段不得改名或使用大小写伪恒等映射；源结构、表清单和映射后的主键都必须精确为单列 `id`。携带文件的 `form_question` 必须使用精确 `Upsert`，并在备份前及事务写入前确认 `id/hospital_id/project_id/table_name/column_name/data_type` 实际可写；`form_project` 同样严格确认 `id/hospital_id` 可写，不允许普通表兼容逻辑静默过滤安全字段。仅获取这些表锁时把 `lock_timeout` 限制为 30 秒，锁成功后立即恢复为 PostgreSQL 默认值 `0`；锁保持到事务提交，防止预检与动态写入之间发生授权、题目或项目归属漂移。复验失败时整事务回滚并持久化待结构处理结果。`SHARE` 锁会与表单配置写入互斥，生产导入应安排在低峰期，避免锁等待超时或长事务影响配置维护。
- 人工映射不能跨越 `target` 动态表边界：普通业务表不得映射进入 `target`，动态表也不得映射离开 `target`。Package、Target、Empty 三种表单项范围下均禁止动态表名和字段名的非恒等映射，因为名称还与 `table_definition_id`、`column_definition_id` 共同构成 NTCare 元数据引用；动态表只允许使用 `DefaultValues` 补充目标必填字段。人工决定的源表键、源字段键、目标字段和 `DefaultValues` 字段键必须精确匹配大小写并满足安全标识符规则；重复动态 `table-manifest`、重复目标表或重复目标字段映射均返回结构复核结果，不再降级为内部异常。
- `form_question.table_name`、`column_name`、动态结构快照、NDJSON 属性名和目标物理字段按 PostgreSQL 双引号标识符语义精确匹配大小写且不得含首尾空白；`Foo` 不得授权或写入 `foo`，`" foo "` 也不得经修剪授权 `foo`。表单项绑定名还必须满足安全标识符规则 `^[A-Za-z_][A-Za-z0-9_]*$`，非法值统一进入结构处理，不得降级为内部失败。源端和医院端还必须使用一致的 PostgreSQL 数据库排序规则及兼容的 ICU/字符排序环境，确保 JSON 文本 `ORDER BY` 的跨端行序一致；当前三端模拟环境已验证 268 行快照 hash 一致。
- 未关联但非空的历史字段只记录表名、字段名和影响行数，不记录字段值或患者标识。医院关联字段缺失、不可写或类型不兼容时仍返回 `RequiresMapping` 或 `Breaking`，不得绕过。
- 仅当表单项类型为“文件”或“选择”、源字段为 `ARRAY`/`text[]` 且目标字段为 `text` 时，允许把仅含字符串或 `null` 的 JSON 数组写成 JSON 数组文本；该值形状会在预检和实际已验文件句柄逐行消费阶段各校验一次，固定表和其他类型差异继续严格校验。
- 导入器不会为此创建、删除或修改 CubeDb 表和字段，不执行 `CREATE`、`ALTER`、`DROP`，不新增迁移，也不改变 `followup-hospital-sync.v3`、导入器版本或数据包 hash。修复后的 LHYY 可对原 `RejectedSchemaMismatch` 包重新执行“校验 / 导入”，无需重新生成包。

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

恢复会先校验数据库 dump、登记的附件清单 hash/条目数、备份总大小和全部附件副本 hash，再把 database dump 和所有 `Existed=true` 的附件分别复制到 `BackupRoot` 之外、权限受限的独立临时目录，并复验实际副本 hash；数据库和附件两类输入全部冻结成功后，才调用 `pg_restore --clean --if-exists`，后续附件恢复只读取这份冻结快照。`Existed=false` 的路径会恢复为确实不存在；普通目录、链接或其他类型目录项占位时按冲突失败，不得静默标记恢复成功。硬中断遗留的临时副本不会污染受锚定的备份文件集合。附件清单只读取一次，同一份字节同时用于登记 hash 校验、条目数校验和解析；完整恢复复用数据库恢复前已经验证的条目集合，并逐项复制到目标同目录临时文件、校验临时副本 hash、再次检查路径边界和符号链接后才原子发布，避免清单删项或校验与复制间变化造成数据库/附件静默错配。新备份把 `attachment-backup.json` 放在 `attachments` 数据目录之外；旧记录恢复仍兼容旧目录内清单，但带清单 hash/条目数的新登记在恢复和清理时都必须使用外置清单，外置清单缺失不得回退到同名业务附件。旁路信任锚点也位于数据目录之外，因此同名历史附件不会被当作元数据或从总大小中排除。旧备份记录没有清单 hash/条目数时，完整恢复和人工清理都必须先通过登记大小、清单结构和全部附件副本预检；随后发布 `attachment-backup.artifact-anchor.v2.json` 并明确阻断，若同时是缺 hash 的旧版数组清单，本次会一并生成 `attachment-backup.hash-baseline.v2.json`。sidecar 发布临时文件位于精确隔离、权限受限且不参与登记大小的 `.attachment-backup-metadata-staging`，硬中断残留不会阻断后续预检。人工核对本次生成的一份或两份信任锚点后再次执行，才按锚点校验和恢复或清理。存储清理采用“先原子移动到 quarantine、再对隔离副本执行包 hash、数据库 hash/size、附件登记与全部附件 hash 校验、最后归档管理状态”的顺序；任何校验失败都会尝试无覆盖回迁，回迁失败则保留清理清单供人工恢复，未通过隔离校验不得进入 `Archived`。主操作与临时快照、sidecar 清理同时失败时会聚合保留两类异常，不允许后发清理错误覆盖原始完整性失败。备份登记路径、元数据和附件路径均拒绝越界、符号链接或重解析点；由于应用层检查不能替代操作系统目录权限，恢复期间仍必须禁止不受信任进程重命名目录或注入链接，并协调暂停已打开附件句柄的持续写入。该操作同步恢复受影响附件，属于高危操作。导入、恢复以及目标为 `CubeDb` 的数据库升级/比对通过进程级协调器及 PostgreSQL advisory lock 共用同一套独占租约；独占期间后台消息处理不领取新消息，JSON ESB 请求返回 `AckCode=300.1` 和“系统维护中，请稍后重试”，SOAP 1.1 请求返回可重试的 `soap:Server` 故障，其他页面维护操作也会被拒绝。

数据库与附件冻结快照在创建目录前后都会逐级拒绝 `BackupRoot`、系统临时目录及实际快照目录中的符号链接或重解析点，并再次校验快照目录位于 `BackupRoot` 外；即使 `TMP`、`TEMP` 或 `TMPDIR` 通过链接实际指向备份根，也会在复制任何恢复数据前阻断。

清理进程中断后，系统在回迁 quarantine 前重新拒绝链接或重解析点，回迁后还会复验规范原路径、包 SHA-256、数据库 dump hash/登记总大小、外置附件清单 hash/条目数、全部附件 hash 及旧备份人工锚点；只有隔离路径确已消失且完整内容仍与登记一致，才允许取消数据库 `Prepared` 状态并删除清理清单。任一复验失败都会保留清单和阻断状态，不能把伪造的普通文件、目录或符号链接误判为恢复成功。

后台协调器按清理清单文件隔离读取失败；单个损坏、空、无权限或暂时不可读的 JSON 会原样保留并记录含路径的错误日志，但不会阻断其他有效清单继续恢复。数据库已经归档时，只有规范原路径与 quarantine 都确实不存在才删除清单；任一原路径重新出现都会转人工处理。

1. 先停止上游 ESB 推送，设置 `FollowUpPackageImport__Enabled=false` 并重启服务。代码维护门禁可以排空当前实例中的在途消息并阻止新写入，但生产恢复仍必须先停流，避免上游持续失败或遗漏重试。
2. 核对目标包、备份文件 hash、数据库和附件路径。
3. 在 LHYY 页面输入“确认”，再点击该包“恢复”并完成二次确认。
4. 系统只允许恢复当前实际最后完成导入或恢复失败的包。回退多个包时必须按实际导入完成顺序倒序逐包恢复；不能仅按 `sequenceNo` 判断，因为迟到的 Supplement 或 Replacement 可能在更高序号包之后完成导入。
5. 恢复批次登记后，系统先在持久化 `BackupRoot/.restore-reconciliation` 目录写入未完成标记，再把包状态置为 `Restoring` 并调用实际恢复；再次点击恢复时会先在恢复专用独占租约内协调同包已有的完成标记，如果该标记证明数据库和附件已经恢复，则只补写管理状态、审计和日志并直接返回，不登记新批次，也不重复执行 `pg_restore` 或附件恢复。只有没有完成证明时，后续恢复批次才会在同一个管理库事务内把同包遗留的旧 `Running` 审计标记为“恢复进程中断”，再登记新批次。恢复成功后包状态变为 `Restored`，并写入 `lhyy.followup_package_restore_record` 和审计日志；如果数据库和附件已恢复但 DataSyncDb 暂时不可用，系统会把持久标记更新为已完成，再由后台任务持续幂等补写 `Restored`、`Completed` 和带 `restoreId` 的恢复日志，三项全部落库后才删除标记，期间原 `Restoring` 状态继续阻断普通写入。每个标记只匹配自身的恢复记录、备份记录和当前最新恢复批次；前台已明确捕获恢复失败时会在标记中保存 `RestoreError`，后台只据此补齐该次恢复的 `Failed` 审计和必要的 `RestoreFailed` 状态。没有 `RestoreError` 的当前未完成标记仍表示结果未知，后台只保留等待，不会推断结果，也不会再次恢复 CubeDb 或附件；只有出现更新恢复批次后，仍为 `Running` 的旧未知记录才会标记为“恢复进程中断”。旧批次标记不能把后发恢复状态改为成功，单个异常标记也不会阻断其他标记。页面仍返回恢复成功并提示检查 DataSync 日志，不得重复执行恢复，也不得人工删除补写标记。
如果数据库和全部附件已经恢复，仅 `BackupRoot` 外的临时快照清理失败，系统必须记录 `Restored`/`Completed` 和清理异常，页面返回“恢复已完成、需人工清理残留”，不得写为 `RestoreFailed`，也不得引导再次执行 `pg_restore`。真正的数据库或附件恢复失败仍进入 `RestoreFailed`。

恢复完成状态与患者映射回滚使用同一个 DataSyncDb 事务：删除由被恢复包首次创建的映射，并把其余映射的 `last_package_id` 回退到该包前驱；后台完成标记补写也执行同一逻辑。因此不得绕过页面直接把包状态改为 `Restored`。

6. 需要重放时，从恢复后的最早目标包开始按包链顺序执行“校验 / 导入”；`Supplement` 不推进主链，`Replacement` 仍按被替代包状态校验。
7. 导入或恢复租约会在成功、失败或取消后自动释放；取消正在执行的 `pg_dump`/`pg_restore` 时，系统会先终止子进程树并以不可取消等待确认实际退出，确认前不会释放维护租约。进程崩溃时 PostgreSQL 会随连接断开自动释放 advisory lock。核对业务数据、附件和 ACK 后，再恢复上游推送并重新启用 Worker。

如果恢复失败，或导入的数据库提交结果不确定，保持 Worker 关闭，不要继续导入后续包；系统不会在提交结果不确定时自动回退附件，而会把包置为 `RestoreFailed`。`RestoreFailed`、进程中断遗留的 `Restoring` 或 `Importing` 都会作为持久阻断状态，使自动/手工导入、ESB/SOAP 写入、后台消息处理以及 CubeDb 数据库升级/比对停止取得普通写租约。页面对 `Imported`、`RestoreFailed`、`Restoring`、`Importing` 提供恢复入口，并禁止通过“校验 / 导入”覆盖不确定状态。核对数据库 dump、附件备份和应用日志后，可以在同一包上再次执行“恢复”；系统只使用该包已登记的导入前完整备份，只有恢复专用租约可绕过持久阻断，恢复成功后才能继续其他写入。校验/导入生成的 `verify-*` staging 目录及附件安装临时文件会在本轮流程结束后自动清理，不应把 staging 或临时文件作为恢复依据。

普通 CubeDb 写租约仍以 DataSyncDb 中的危险状态为准。为避免每条 ESB/SOAP/后台消息都串行访问管理库，查询结果只缓存 1 秒；所有应用内导入状态迁移会立即使缓存失效，跨进程或人工改库则最多等待该短缓存到期，不改变危险状态的判断条件。

CYYY 服务如果在包状态写为 `Pulling` 后异常退出，重启后的普通同步会重新领取该包；包文件仍须重新通过完整长度和 SHA-256 校验后才能置为 `Pulled`。

## 7. 上线验收最小集

- 合并数据库脚本经升级页执行成功，两个管理页预检通过。
- DataSyncDb 的 `20260811.sql` 已执行，旧版患者映射已完成只读迁移或已通过 `RecoveryBaseline` 重建；CubeDb 未执行任何 DDL。
- SSH 严格 host key 校验生效，错误 key、缺 token 和非法 shell 均被拒绝。
- 正常包可拉取、验签、解密、备份、导入并回传 ACK。
- 首次完整 Baseline/Replacement 导入后重启 NTCare，患者管理可查看回传表单；后续增量重复导入不产生重复患者或 EDC 权限记录。
- 动态表只写入当前医院关联表单字段；构造未关联非空历史字段后，确认该字段未进入 SQL 写入列、目标库原值不变且日志只保留脱敏计数。另验证 `table_name` 或 `column_name` 未绑定的 `form_question` 行仍参与内容 hash 但不形成授权，缺少 `data_type` 的已绑定行只禁用 `ARRAY/text[] → text` 特例；构造题目医院正确但 `project_id` 所属项目跨医院的包，确认进入结构处理且不得授权字段。构造只含 `form_question/form_project`、不含动态答案文件的包，确认同样在备份前执行医院与项目归属校验；再构造仅含 `form_project` 增量、题目与动态答案均无导出文件的包，确认仍校验项目文件、建立事务守卫、锁表并在项目写入前拒绝目标同 ID 跨院项目。
- 验证无 `form.form_question` 文件的增量包：内容 hash 必须同时匹配当前主链头和医院端实时完整快照；任一不一致、缺失或 Baseline 非空快照缺文件时必须阻断。另在预检后修改医院端表单项，确认 Target/Empty 回退范围会以 30 秒等待上限锁定项目和题目、锁成功后恢复 `lock_timeout=0`，并在动态写入前复验。Package 同时包含题目完整快照和项目增量子集时，无论项目是否全部由增量覆盖，都要在事务首部以 `SHARE` 模式锁定 `form_project` 与 `form_question` 两表；构造医院端已存在同 ID 跨院项目或跨院题目时必须在对应写入前阻断，目标缺失的新项目或题目仍可由本包新增；确认排序只把题目/动态消费者延后到项目之后、不把项目提前越过医院或科室等基础表，全部引用项目的严格复验早于 `form_question` 或首张动态表写入。另分别构造安全表异常主键、题目非精确 `Upsert`、安全字段 generated/identity-always 或缺少 `INSERT/UPDATE` 列权限，以及 `ID → ID` 大小写伪恒等映射，确认全部在备份前进入结构处理，并在事务内二次门禁处仍会失败关闭。锁验证安排在低峰期。
- 在结构预检后替换 `form_question`、`form_project` 及普通数据文件，确认实际导入使用同一只读句柄完成 SHA-256 校验与消费：hash 不一致时整包阻断，路径在校验后被替换时仍只读取已验证句柄中的原字节。
- 验证普通表映射到 `target`、动态表映射出 `target`、Package/Target/Empty 任一范围下的动态表或字段重命名、大小写伪恒等映射、重复动态 manifest 以及重复目标映射均进入结构处理；验证动态表仅可通过 `DefaultValues` 补充目标必填字段，且非法默认值字段标识符必须在备份前进入结构处理。
- 验证“文件/选择”题目的 `ARRAY/text[] → text` 写入 JSON 数组文本；在预检完成后替换原路径并模拟已验证句柄中的非法对象/数值数组，确认实际逐行消费阶段仍阻断；其他动态类型差异和全部固定表类型差异仍按原规则阻断。
- 确认本次部署没有新增或执行 CubeDb DDL、数据库迁移，也没有改变协议、版本和原包 hash。
- 修改 envelope、payload、签名或内层文件后均在备份前被拒绝。
- 验证附件备份生成版本 2 清单并记录原文件 hash，外置元数据与同名附件互不冲突，备份记录同时保存清单 hash、条目数和总大小，且文件系统支持同目录硬链接；删除新登记的外置清单并在 `attachments` 放入同名业务附件时，恢复和存储清理都必须拒绝回退。从清单删除任一条目或破坏任一附件备份时，确认完整恢复在执行 `pg_restore` 前阻断。确认数据库 dump 与所有存在的附件备份都先冻结到 `BackupRoot` 外，源文件在冻结后变化不会影响本轮输入，硬中断残留也不改变登记大小；安装和明确回滚补偿必须复用备份登记后创建的同一冻结快照，即使原清单与附件副本同时被替换也不得信任。在 sidecar staging 留下中断临时文件同样不得改变登记大小。分别在安装临时副本、补偿临时副本和完整恢复临时副本生成后篡改，确认 size/hash 不符时均在认领或发布正式路径前阻断；把附件父目录或备份元数据替换为符号链接时也必须在读写前拒绝。验证备份后更新、安装认领后新建和补偿认领后新建三种路径级并发都不会被覆盖，安装失败只补偿本次实际安装且仍匹配包 hash 的路径。旧记录人工清理必须与恢复复用总大小、清单结构、全部附件及历史锚点校验；旧记录前置完整性失败不得提前落盘信任锚点，旧记录与旧版无 hash 清单首次完整预检后应同时生成清单登记锚点和 hash 基线并阻断，核对后第二次方可继续；只有旧记录缺登记字段时生成单一登记锚点。模拟安装认领后异常应放回原文件；模拟安装成功后旧 claim 清理失败或补偿认领后异常，应保留现场并进入人工恢复边界。验证数据库提交结果不确定时不自动补偿附件、状态进入 `RestoreFailed` 且只能执行完整恢复，并确认正常路径的附件临时文件均被清理；当主操作与清理同时失败时，异常中必须同时保留两个原因。
- 模拟清理进程在 quarantine 后中断，再分别把隔离项替换为指向外部的链接、删除隔离项并伪造规范原文件；确认系统在取消数据库准备态前拒绝链接并复验包、数据库和全部附件登记内容，失败时保留清理清单。另模拟数据库与全部附件恢复成功但临时快照删除失败，确认终态仍为 `Restored`/`Completed`、审计保留清理异常且禁止重复恢复。
- 验证序号间隙、错误前驱、Supplement、Replacement、重复导入和 ACK 重试。
- 在隔离测试库完成一次链头恢复及顺序重放，并核对数据库与附件。
- `dotnet test DataSync.sln` 与 `dotnet build DataSync.sln` 均通过。
