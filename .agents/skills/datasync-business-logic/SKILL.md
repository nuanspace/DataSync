---
name: datasync-business-logic
description: DataSync 项目业务知识路由入口。适用于理解、解释、开发或审查 CYYY 主动采集、LHYY ESB、SOAP、字段映射、Bio.Core/ntcare 写入、OCR、接口文档、数据库运维及跨模块数据链路；医院数据包导入恢复或离线发布改用对应专项 skill。
---

# DataSync 业务知识路由

仅将本技能用于 `D:\Github\DataSync`。知识是定位入口，不替代对当前代码、配置和运行状态的核验。

## 开始任务

1. 运行 `.\.codex\tools\resolve-knowledge.ps1 -Query '<任务描述>' -Paths <已知路径>`。
2. 读取最高置信度领域的 reference；领域不清时先读架构，不要一次加载全部文件。
3. 精确结论以当前代码、测试、配置和 Git 历史为准；不得把知识中的示例当作现场状态。

## 按需读取

- 项目分工、数据方向和主链路：[references/architecture-and-routing.md](references/architecture-and-routing.md)
- CYYY 数据湖/数据库采集、队列与推送：[references/cyyy-ingestion.md](references/cyyy-ingestion.md)
- LHYY ESB、SOAP、识别、映射和 ntcare 写入：[references/lhyy-esb.md](references/lhyy-esb.md)
- 医院文档、文件、OCR 和归档：[references/interfaces-documents-and-ocr.md](references/interfaces-documents-and-ocr.md)
- FollowUp 医院包协议和安全不变量：[references/followup-hospital-sync.md](references/followup-hospital-sync.md)
- 数据库运维、发布边界和已知风险：[references/operations-and-risks.md](references/operations-and-risks.md)

医院包校验、导入、ACK 或恢复任务必须同时使用 `datasync-hospital-sync-delivery`。离线出包、安装、升级或回滚必须使用 `datasync-offline-release`。

## 维护知识

任务结束前运行：

```powershell
.\.codex\tools\detect-knowledge-impact.ps1
.\.codex\tools\lint-knowledge.ps1
```

只在业务行为、流程、接口语义、安全边界、稳定入口或部署契约改变时更新对应 reference 和 `.agents/knowledge-map.yaml`。`.agents/knowledge-reviews/` 只保存已绑定 Git commit 的审计基线，不具备门禁放行能力；只有受影响领域的精确 reference 更新才能解除知识门禁，修改全局 map、Skill、review record 或其他领域文件均不能绕过。不要追加提交时间线、动态数据量、测试总数或可由 Git 还原的改动流水。

任何输出和知识文件都不得包含患者可识别信息、密码、Token、密钥、完整连接串或医院内网地址。
