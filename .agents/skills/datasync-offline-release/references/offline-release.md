# 医院端离线发布流程

## 输入

开始前明确：

- `existing-cube` 或 `fresh-cube` 部署模式。
- 四个镜像及唯一标签。
- 两个仅结构基础库 dump 的来源。
- 指定发布环境文件。
- NTCare uploads 在 DataSync 宿主机上的真实绝对路径。
- 现场备份、维护窗口、失败回滚和验收负责人。

任何缺项都只能进行预检，不进入现场写操作。

## 成品包生成

1. 从干净且已审核的提交生成，不把工作区临时文件带入。
2. 通过 `deploy/s7-followup-hospital/package-release.sh` 汇集批准的镜像、基础 dump、白名单数据库文件、配置示例、secrets 说明和实施文档。患者身份合并版本不得携带 CubeDb 自定义迁移；DataSync 基础 dump 必须包含 `lhyy.followup_patient_identity_map`，存量 DataSyncDb 则通过镜像内 `20260811.sql` 升级。
3. 版本及镜像变量只读取指定发布环境文件；拒绝调用进程环境变量隐式覆盖。
4. 用 `pg_restore --list` 验证 dump 只含结构，拒绝表数据、序列值和大对象。
5. 拒绝符号链接、隐藏路径、特殊文件、非文档扩展名及输出目录嵌入文档目录。
6. 生成并回验 `manifest/SHA256SUMS.txt`；镜像归档名含稳定序号。

模板目录不能直接作为交付包。

## 安装与升级

- 按成品包中的安装入口执行；先验证操作系统工具、Docker、磁盘、端口、目录、权限和镜像完整性。
- `existing-cube` 使用已有 CubeDb；`fresh-cube` 使用经验证的仅结构基础库。不得混用两种模式的环境文件和 compose。
- `fresh-cube` 与 `existing-cube` 都只使用 CubeDb 既有 NTCare 业务结构，不创建患者映射表。存量升级在停止自动任务并备份 DataSyncDb 后执行 DataSyncDb 迁移和显式旧映射迁移工具；无法迁移时在放开 Incremental 前完成 `RecoveryBaseline`。
- CYYY/LHYY 共用 DataSync 管理库和包仓库；LHYY 的 CubeDb 指向目标 ntcare 库。
- 密钥、known-hosts、token 和公钥使用持久只读挂载；不要复制到日志。
- `NTCARE_UPLOADS_PATH` 必须是已存在、非符号链接、可读写的绝对路径，并映射到 LHYY `AttachmentRoot=/app/uploads`。
- uploads 存储必须支持同目录硬链接和原子重命名。安装脚本只探测能力，不创建附件根、递归改属主或放宽权限。
- `FollowUpPackageImport.Enabled` 初始关闭；完成页面预检和测试包验证后才启用。
- `external-cube` 默认不要求 form schema 下的 vector 扩展；仅当目标环境明确启用向量能力时把 `CubeCompatibility:RequireVectorExtension` 设为 `true`。`fresh-cube` 制品继续携带并验证 pgvector，逐包字段类型检查在两种模式下都不能绕过。

## 验收

- 回验 manifest、镜像加载结果、容器状态和健康检查。
- 核对管理库/CubeDb 连接目标，禁止在摘要中打印完整连接串。
- 分别验证 CYYY 拉包预检、LHYY 密钥/目录/数据库预检和测试包处理。
- 确认 DataSyncDb 的 `lhyy.followup_patient_identity_map` 字段和唯一约束存在，CubeDb 没有新增 DDL，并用 Baseline + Incremental 验证院端已有患者复用、关联 ID 重映射和后续包映射稳定性。
- Baseline 与 Incremental 均从真实 NTCare 表单验证图片和非图片附件；文件存在或数据库字段非空不足以证明用户可见。
- 记录未执行的真实导入、恢复或生产流量验证。

## 回滚

- 应用层使用发布前镜像和环境文件回退。
- 数据库/附件回退只使用系统登记的对应导入前备份，并遵循医院包恢复状态机；不得手工拼接不一致的数据库和附件快照。
- 恢复失败保持 Worker 和写入门禁关闭，先处理安全状态再继续导入。

发布证据放本地忽略目录；仓库只更新稳定 runbook 或契约。
