# 总体架构与链路路由

## 系统定位

DataSync 是面向多医院、多来源和多协议的 ntcare 集成适配平台。它将医院侧数据转换为 Bio.Core/ntcare 能识别的患者、事件、表单、题目、子卡或目标表数据。

数据方向包括：医院主动调用 DataSync；DataSync 定时采集医院数据湖或数据库；DataSync 接收医院文件；DataSync 向医院推送数据；DataSync 在两个应用间转交后写入 ntcare。

## 项目职责

- `DataSync.Common`：面向 .NET 9/10 的共享协议和基础能力，包括 OCR 契约与 FollowUp 医院包协议；不承载应用编排。
- `DataSync.CYYY`：主动采集与同步编排。采集数据湖、SQL Server、Oracle、MySQL、Doris（MySQL 协议）等来源，落本地数据池，生成待处理队列，再推送 API 或 PostgreSQL。
- `DataSync.LHYY.V2`：统一接收、接口识别、配置映射、消息处理及 Bio.Core/目标表写入；同时承载医院包校验、导入、恢复和数据库运维入口。
- 两个应用通过 DataSync 管理库共享配置/状态；当前常规 ntcare 路径是 CYYY 推送 `/api/esb`，再由 LHYY 写入产品库。

## 主链路选择

| 任务 | 首要入口 | 继续读取 |
|---|---|---|
| 主动采集、补数据、任务队列、API 推送 | `DataSync.CYYY` | `cyyy-ingestion.md` |
| 医院 JSON/SOAP 推送、识别、映射、幂等 | `DataSync.LHYY.V2` | `lhyy-esb.md` |
| PDF、OCR、医院接口文档、归档 | Common + LHYY | `interfaces-documents-and-ocr.md` |
| FollowUp 云端到医院数据包 | Common + CYYY + LHYY | `followup-hospital-sync.md` |
| 数据库升级、归档优化、维护互斥 | LHYY | `operations-and-risks.md` |
| 医院端离线出包/安装/回滚 | `deploy/s7-followup-hospital` | `datasync-offline-release` |

## 稳定边界

- CYYY 不引用 Bio.Core，通常不直接写 ntcare；`DatabasePushService` 是独立 PostgreSQL 直写通道，不代表当前启用任务都采用它。
- LHYY 的普通统一处理链以 JSON 为中心；SOAP 入口先把 XML 转为 JSON，再复用同一处理链。
- 医院包协议位于 `DataSync.Common/FollowUp`，不是对 FollowUp 仓库程序集的引用。
- 运行配置、数据库记录数、启用任务和现场连接均为动态状态，回答前必须实时检查并脱敏。

## 变更影响判断

修改解决方案成员、应用入口、共享协议、依赖方向或主分发路径时更新本文件。仅新增内部实现类且职责和入口未变化时不更新。
