---
name: datasync-business-logic
description: 本工作空间专用的 DataSync 业务逻辑知识技能。Use when Codex works in D:\Github\DataSync and needs to understand, explain, modify, review, or extend ntcare 与医院数据对接相关逻辑，包括 DataSync.CYYY、DataSync.LHYY.V2、ESB 接收、Bio.Core 写入、数据湖/数据库采集、接口文档、字段映射、过滤规则、消息处理、文件/报告/归档接口；修改现有业务逻辑后必须同步更新本技能的业务逻辑记录。
---

# DataSync 业务逻辑

本技能只用于 `D:\Github\DataSync` 工作空间。不要把这里的业务结论迁移成全局假设；离开本仓库时不得使用本技能作为事实来源。

## 使用流程

1. 处理本仓库业务问题前，先阅读 [references/business-logic.md](references/business-logic.md)。
2. 需要精确现状时，重新检查代码、配置和数据库；引用文件中的“当前快照”只代表记录日期。
3. 修改任何业务逻辑、接口语义、数据流、字段映射、处理器、数据库结构或医院文档约定后，同一轮同步更新 [references/business-logic.md](references/business-logic.md)。
4. 对外说明时不要泄露 `appsettings*.json`、数据库、LLM、医院接口中的明文凭据、Token、密钥或内网地址。

## 修改业务逻辑时必须同步记录

更新引用文件时至少写清：

- 影响项目：`DataSync.CYYY`、`DataSync.LHYY.V2` 或两者。
- 影响链路：医院主动推送、我们主动采集、我们向医院推送、医院调取我们数据、文件/报告归档。
- 关键表和配置：涉及的 schema、表、实体、接口编码、项目编码、处理器、映射目标。
- 数据库变更脚本：`DataSync.CYYY` 使用 `Migrations\yyyy-MM\yyyy-MM-dd_变更说明.sql`；`DataSync.LHYY.V2` 使用 `Scripts\yyyyMM\yyyyMMdd.sql`。
- 验证结果：说明自动测试、构建、手工验证或未验证原因。

## 参考资料

- 业务逻辑总览与当前记录：[references/business-logic.md](references/business-logic.md)
