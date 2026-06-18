DataSync.LHYY.V2 ESB 消息性能优化交付包

制作交付包：
1. 在 Windows 打包机上确认 Docker Desktop 已启动。
2. 进入 D:\Github\DataSync\部署包，双击或在命令行执行 build_deploy_package.bat。
3. 脚本会自动构建镜像，在临时目录生成 datasync-lhyy-v2.tar 和 SHA256SUMS.txt，并在当前目录生成 DataSync.LHYY.V2_ESBArchiveUpgrade_生成时间.zip。
4. datasync-lhyy-v2.tar 只作为打包过程中的临时文件使用，生成 zip 后会自动删除。
5. 将生成的 zip 上传到现场 Linux。

现场推荐执行步骤：
1. 在 Linux 上解压 DataSync.LHYY.V2_ESBArchiveUpgrade_生成时间.zip，进入解压后的目录。
2. 编辑 deploy.sh 顶部配置区，必须设置数据库连接；新版镜像标签和 Docker 网络通常可自动识别，按现场情况确认或填写。
3. 如果 app 由专人单独升级，将 UPDATE_APP_SERVICE 设置为 0，并在确认 app 已停止或已进入维护窗口后设置 APP_STOP_CONFIRMED=1；否则确认现场 docker-compose.yml 或 .env 中 LHYY V2 服务镜像标签已指向新版镜像。
4. 执行 chmod +x deploy.sh && ./deploy.sh。
5. 脚本输出“部署完成，数据库升级验证通过”即表示数据库结构升级、历史数据迁移和结果校验已通过。

注意：
- UPDATE_APP_SERVICE=1 时仅支持 SERVICE_MODE=compose。普通 docker run 管理的 app 不会由脚本自动重建，请使用 UPDATE_APP_SERVICE=0 并由 app 专人按现场原参数重建容器。
- UPDATE_APP_SERVICE=0 时，脚本只做数据库备份、结构升级、历史迁移和校验，不会停止、重建或启动正式 app 服务。
- 如果数据库连接串使用容器名，脚本会尽量自动识别 Docker 网络；无法识别时会停止运行，并提示填写 DOCKER_NETWORK。
- 如果数据库连接串使用普通 DNS 域名而不是容器名，可以设置 DB_HOST_IS_CONTAINER=0。

包内文件：
- datasync-lhyy-v2.tar：新版程序 Docker 镜像包。
- deploy.sh：一键部署脚本，包含加载镜像、停服务、备份、数据库结构升级、历史迁移、校验和启动服务。
- docs/README_IMPLEMENTATION.md：详细实施说明，仅用于需要查看完整背景或排查问题时参考。
- docs/MANUAL_UPGRADE_REFERENCE.md：手工命令参考。
- sql/upgrade_esb_messages_archive_optimization.sql：专项数据库升级 SQL 参考。
- SHA256SUMS.txt：关键文件 SHA256 校验值，仅在生成后的 zip 内提供。

制作端文件：
- build_deploy_package.bat：Windows 打包脚本，只保留在源码仓库的部署包目录，不进入最终现场 zip。

源码仓库不提交 datasync-lhyy-v2.tar。该文件由 build_deploy_package.bat 在临时目录生成并打入 zip，打包完成后自动删除。
