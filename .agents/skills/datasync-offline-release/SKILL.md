---
name: datasync-offline-release
description: DataSync 医院端离线交付流程。适用于生成或检查 s7-followup-hospital 离线包、镜像与基础库汇集、安装、升级、fresh/existing 双模式部署、配置/密钥挂载、发布验收和回滚；任何真实发布或现场写操作都必须取得明确授权。
---

# DataSync 离线发布

离线发布是高风险任务。只读分析和预检可直接执行；生成正式包、上传、安装、升级、重启、数据库恢复或现场修改必须符合用户授权范围。

## 使用

1. 完整阅读 [references/offline-release.md](references/offline-release.md)。
2. 若发布包含 FollowUp 医院数据包能力，再读取 `../datasync-business-logic/references/followup-hospital-sync.md`。
3. 从实际 Git、环境示例、脚本参数和镜像清单获取版本，不复用知识库中的动态值。
4. 执行前记录目标医院/主机、部署模式、镜像、基础库来源、附件路径、备份和回滚方案。

## 门禁

- 未明确要求真实发布时，停在生成计划、只读检查或本地预检。
- 不读取或输出 secrets 的值，只确认文件存在、权限和挂载目标。
- 不从模板目录直接安装；只使用发布脚本生成并回验的成品包。
- 验证清单、镜像、基础 dump、行尾、路径、挂载和回滚材料后才交付。
- 完成后更新稳定发布契约，不把当次主机、版本号、耗时或逐命令日志写入知识库。
