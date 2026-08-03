# FollowUp 医院数据回传

## 组件职责

- `DataSync.Common/FollowUp`：版本化协议 DTO、严格 envelope 解析、内容清单、包类型和包链判定。
- `DataSync.CYYY`：通过 SSH forced-command 调用 DMZ 的 `relay-health/list/pull/ack`，原样保存加密包并维护拉取与 ACK 状态。
- `DataSync.LHYY.V2`：验签、解密、结构校验、目标适配、备份、导入、ACK 生成、恢复和异常补写。

每套 CYYY/LHYY 实例和管理库只服务当前一家医院；`hospital_code` 用于身份和审计，不表示单实例多医院调度。

## CYYY 拉取不变量

- SSH 业务参数放单个 stdin JSON，不进入命令行；启用严格 host key 校验，禁用交互、PTY 和转发。
- relay 清单必须给出合法外层 SHA-256。包流写入隐藏 `.partial` 文件，同时校验大小和 hash；通过后 fsync、原子改名，最后才写 `Pulled`。
- 同医院串行拉取；云端新候选与本地 `Pending/Failed/Pulling` 合并处理。`sequenceNo` 只排序，不用来假定连续，也不能让高水位跳过失败包。
- ACK 队列 ID 是稳定 `ackId`；转发失败保留同一 ID 重试。
- 管理表、私钥、公钥、known-hosts 或 token 未就绪时 Worker 不运行。

## LHYY 校验和导入顺序

1. 核对外层大小/hash，拒绝 ZIP 多余、缺失或重复顶层条目；包身份必须与待导入状态一致。
2. 对 `envelope.json` 原始字节执行 RSA-PSS-SHA256 验签，再严格解析字段、顺序、版本和算法。
3. 以 RSA-OAEP-SHA256 解包严格 64 字节密钥材料：前 32 字节为 AES-256-CBC 密钥，后 32 字节为 HMAC-SHA256 密钥；IV 必须为 16 字节，HMAC 输入必须是 `IV + payload.bin 密文`，不得改为明文或省略 IV。
4. 外层清单 SHA-256 必须是 64 位十六进制字符串；payload 流式落临时文件并校验长度、SHA-256 和 HMAC。
5. 限制 ZIP 条目和展开总量，拒绝路径逃逸；在打开文件前验证 checksum 路径，并校验 manifest、结构文件 hash 和记录数。
6. 校验导出契约、最低导入器版本和包链：Incremental 前驱等于主链头；Supplement 不推进主链；Replacement 只替代未成功包。
7. 以 `table-manifest.json` 为唯一导入范围，按主数据、关系、业务数据排序，执行声明的引用和 upsert 策略；包中缺表不等于物理删除。
8. `Compatible` 可继续；`RequiresMapping` 等待映射或升级决策；`Breaking` 禁止自动导入。
9. 导入前完整备份 CubeDb 和会覆盖的附件。附件原子切换完成后才提交数据库事务；失败时回滚数据库并恢复附件。
10. 提交后写 `Imported` 和 ACK；审计/ACK 暂时失败不得把已成功导入降级为失败或触发重复导入。

## 动态表与附件范围

- target 动态宽表只处理系统固定字段、主键、当前医院实际关联题目和批准默认值字段；结构检查与 upsert 必须复用同一列集合。
- 表单项快照优先取包内可信文件；缺失时只有内容 hash 与当前已导入主链头一致才可回退目标库。
- 未关联字段不写入；非空异常只脱敏记录表名、字段和计数，不记录患者数据。
- 仅对“文件”或“选择”题目的受控 `ARRAY/text[] -> text` 做兼容转换，值必须是字符串/null 数组。
- 包内附件只能引用清单中的相对路径；文件题值归一为 NTCare 既有相对文件名。引用未入清单时在备份前拒绝。
- `AttachmentRoot` 必须是 NTCare 实际 uploads 的同一物理存储，支持同目录硬链接和原子重命名。LHYY 不对外提供附件静态目录，由 NTCare 自身 origin 和权限读取。

## 状态机和维护门禁

- 只有新包或可重试等待态可回到 `Pending`；人工决策、结构拒绝、导入失败、已恢复和恢复失败等状态不得被重新发现覆盖。
- 任一包为 `RestoreFailed/Restoring/Importing` 时，自动 Worker 和手工新包导入停止。
- FollowUp 导入/恢复、CubeDb 升级/比对共用维护协调器和数据库锁。JSON、SOAP、后台消息与页面重试不能在独占维护期间进入写库链路。
- 只有恢复专用租约能绕过持久危险状态；非 CubeDb 的 DataSyncDb 运维仍使用自身目标库互斥。

## 恢复不变量

- 恢复需页面输入确认并二次确认，使用该包登记的导入前数据库和附件备份。
- 只恢复按实际完成时间确定的当前链头；多包回退按实际完成顺序倒序，不按 sequenceNo 猜测。
- 实际数据库/附件恢复完成后，即使管理库审计补写失败，也不得重复执行恢复。持久 reconciliation 标记只用于幂等补写状态、记录和日志。
- 未携带明确恢复错误的未完成标记视为结果未知；不得推断成功或失败。旧标记不能完成后发的新恢复批次。
- `RestoreFailed`、附件回滚失败和存储清理失败均应保持安全栅栏，直到按既有恢复入口处理。

## 配置和存储

- CYYY 与 LHYY 共用 DataSync 管理库和包仓库；LHYY 的 `CubeDb` 指向 ntcare 业务库。
- 私钥、known-hosts、token、公钥、包仓库、staging、备份、恢复标记和附件目录必须持久化并限制权限。
- `FollowUpPackageImport.Enabled` 默认关闭，预检和测试包通过后才开启。

本链路变更必须使用 `datasync-hospital-sync-delivery`，按状态机、安全门禁、回滚和跨端契约验证，不得仅做局部编译。
