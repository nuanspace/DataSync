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
FollowUpPackageImport__EncryptionKeyId
FollowUpPackageImport__Enabled
```

连接串、token 和私钥不得写入镜像或提交到仓库。

## 4. 首次启用

1. 启动两个服务，但先保持 `FollowUpPackageSync__Enabled=false`、`FollowUpPackageImport__Enabled=false`，CYYY 来源配置也先不启用定时拉取。
2. 在 CYYY“FollowUp 包同步”页面生成 Ed25519 密钥，只把公钥交给 DMZ 配置动态授权；导入经人工核对的 DMZ host key 到 known-hosts，并将设备 token 写入受限 token 文件。
3. 保存医院编码、DMZ 主机、端口、用户、共享包仓库和拉取周期，执行“连接诊断”。
4. 在 LHYY“FollowUp 回传导入”页面生成院内 RSA-3072 密钥，只把 `.public.pem` 公钥交给 FollowUp 云端；将云端 RSA 验签公钥粘贴到页面保存，并配置对应 `EncryptionKeyId`。
5. 确认 LHYY 页面中包仓库、staging、备份、附件、两类密钥和 PostgreSQL 工具全部通过。
6. 在 CYYY 页面手工“查询并拉取”一个测试包，确认状态只在完整文件原子落盘、大小和 SHA-256 均通过后变为 `Pulled`。
7. 在 LHYY 页面点击“发现包”，对测试包执行“校验 / 导入”。Baseline 必须二次确认；普通兼容增量包可由 Worker 自动导入。
8. 校验通过后设置 `FollowUpPackageSync__Enabled=true`、开启 CYYY 来源定时拉取，再设置 `FollowUpPackageImport__Enabled=true` 并重启两个服务。

CYYY 总开关默认为 `false`；关闭时拉取 Worker 在创建服务 Scope 和访问数据库前直接退出，页面手工配置和连接诊断仍可使用。Worker 只有在总开关、管理表、安全材料、目录和外部工具预检均通过时才工作。

## 5. 日常验证增量包

1. CYYY 页面核对 DMZ 连通、包号、序号、类型、水位范围、大小和拉取状态。序号允许有间隙；自动扫描除查询高水位后的新包外，还会合并本地 `Pending`、`Failed` 包重新拉取。
2. 需要补拉时，可直接点击某包的重拉按钮，或填写包号/增量日期范围执行条件重拉。
3. LHYY 页面点击“发现包”，再执行“校验 / 导入”。服务依次验证外层 hash、RSA-PSS 签名、RSA-OAEP 密钥解包、HMAC、AES 解密、内层 checksum、契约版本、包链和目标数据库结构。
4. `Compatible` 自动继续；`RequiresMapping` 可在“结构处理”中保存目标表、字段和默认值映射，或标记“等待数据库升级”；`Breaking` 必须升级数据库或导入器后重试。
5. 导入前自动生成目标业务库完整备份和受影响附件备份。业务数据按清单策略幂等写入，不执行物理删除。
6. 导入成功后 LHYY 写入 `Imported` ACK，CYYY 使用稳定 `ackId` 重试转发。已成功导入的包不会再次执行，也不会被迟到失败状态降级。

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
4. 系统只允许恢复当前已导入链头。回退多个包时必须按序号从大到小逐包恢复，防止跳过中间包破坏链状态。
5. 恢复成功后包状态变为 `Restored`，并写入 `lhyy.followup_package_restore_record` 和审计日志。
6. 需要重放时，从恢复后的最早目标包开始按包链顺序执行“校验 / 导入”；`Supplement` 不推进主链，`Replacement` 仍按被替代包状态校验。
7. 导入或恢复租约会在成功、失败或取消后自动释放；进程崩溃时 PostgreSQL 会随连接断开自动释放 advisory lock。核对业务数据、附件和 ACK 后，再恢复上游推送并重新启用 Worker。

如果恢复失败，保持 Worker 关闭，不要继续导入后续包；`RestoreFailed` 会作为持久阻断状态使自动导入 Worker 停止领取所有后续包，并禁止通过“校验 / 导入”覆盖失败状态。核对数据库 dump、附件备份和应用日志后，可以在同一包上再次执行“恢复”；只有恢复成功，或人工完成处置并明确解除失败状态后，才能恢复自动导入。校验/导入生成的 `verify-*` staging 目录会在本轮流程结束后自动清理，不应把 staging 作为恢复依据。

CYYY 服务如果在包状态写为 `Pulling` 后异常退出，重启后的普通同步会重新领取该包；包文件仍须重新通过完整长度和 SHA-256 校验后才能置为 `Pulled`。

## 7. 上线验收最小集

- 合并数据库脚本经升级页执行成功，两个管理页预检通过。
- SSH 严格 host key 校验生效，错误 key、缺 token 和非法 shell 均被拒绝。
- 正常包可拉取、验签、解密、备份、导入并回传 ACK。
- 修改 envelope、payload、签名或内层文件后均在备份前被拒绝。
- 验证序号间隙、错误前驱、Supplement、Replacement、重复导入和 ACK 重试。
- 在隔离测试库完成一次链头恢复及顺序重放，并核对数据库与附件。
- `dotnet test DataSync.sln` 与 `dotnet build DataSync.sln` 均通过。
