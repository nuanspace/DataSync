DataSync.LHYY.V2 ESB 消息性能优化交付包

推荐执行步骤：
1. 将 DataSync.LHYY.V2.publish.zip 和 deploy.sh 上传到现场同一目录。
2. 编辑 deploy.sh 顶部配置区，设置数据库连接、程序目录、Docker 网络和服务管理方式。
3. 执行 chmod +x deploy.sh && ./deploy.sh。
4. 按 docs/README_IMPLEMENTATION.md 完成页面和业务验证。

包内文件：
- DataSync.LHYY.V2.publish.zip：新版程序发布包。
- deploy.sh：一键部署脚本，包含停服务、数据库备份、更新程序、结构升级、数据迁移、校验和启动服务。
- docs/README_IMPLEMENTATION.md：现场实施步骤。
- docs/MANUAL_UPGRADE_REFERENCE.md：手工命令参考。
- sql/upgrade_esb_messages_archive_optimization.sql：专项数据库升级 SQL 参考。
- SHA256SUMS.txt：关键文件 SHA256 校验值。
