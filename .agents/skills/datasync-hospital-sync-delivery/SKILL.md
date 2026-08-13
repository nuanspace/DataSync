---
name: datasync-hospital-sync-delivery
description: DataSync 的 FollowUp 医院数据包交付流程。适用于协议、DMZ 拉包、验签解密、结构校验、目标适配、导入、附件、ACK、恢复、CubeDb 门禁及相关跨 CYYY/LHYY/Common 修改或排障；要求先分析多种失败路径并完成分级验证。
---

# 医院数据回传交付

本技能用于高风险跨模块链路。开始前应用 `karpathy-guidelines`，但不得修改超出用户授权的文件或隐含执行现场导入、恢复、数据库操作、发布、提交或推送。

## 最小上下文

1. 完整阅读 [references/delivery-workflow.md](references/delivery-workflow.md)。
2. 阅读 `../datasync-business-logic/references/followup-hospital-sync.md` 获取协议和安全不变量。
3. 只在需要离线交付时再使用 `datasync-offline-release`，不要提前加载发布文档。

## 开始前

- 明确目标端：DMZ、CYYY、LHYY、DataSyncDb、CubeDb、附件存储或 NTCare 页面。
- 列出 2–4 个合理原因或实现方案，以协议、持久状态和真实业务流程证据排序。
- 明确成功标准、失败状态、幂等边界、不可逆动作、备份和回滚入口。
- 读取对应测试和邻近实现；不得以文档推断当前代码。

## 完成前

- 先运行受影响的聚焦测试，再运行解决方案测试和 Release 构建。
- 协议、安全门禁、包链、恢复或附件变更必须验证失败路径和重复执行。
- 涉及跨端行为时，检查 Common 契约、CYYY 状态、LHYY 状态及用户可见结果的一致性。
- 运行知识影响检测，更新稳定规则而非追加任务流水。
- 只报告已实际验证的结论，明确未连接现场、未真实导入或未执行恢复的边界。
