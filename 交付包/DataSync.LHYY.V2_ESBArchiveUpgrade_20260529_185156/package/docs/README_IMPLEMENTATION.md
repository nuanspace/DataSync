# LHYY V2 ESB 消息性能优化实施步骤

本文档用于现场实施 ESB 消息冷热归档与查询性能优化。实施目标是将历史终态消息从热表迁移到归档分区表，程序查询通过统一视图读取热表和归档表。

现场推荐统一执行 `deploy.sh`。脚本会按顺序完成数据库备份、停止服务、替换新版程序、数据库结构升级、历史数据迁移、结果校验和服务启动。

## 实施包内容

- `DataSync.LHYY.V2.publish.zip`：新版程序发布包。
- `deploy.sh`：现场一键部署脚本，已包含数据库结构升级、历史数据迁移和结果校验逻辑。
- `docs/README_IMPLEMENTATION.md`：本文档。
- `docs/MANUAL_UPGRADE_REFERENCE.md`：手工升级命令参考。
- `sql/upgrade_esb_messages_archive_optimization.sql`：专项数据库升级 SQL 参考。
- `SHA256SUMS.txt`：交付包内关键文件校验值。

## 实施前确认

1. 已确认维护窗口，允许停止 LHYY V2 服务。
2. 已确认数据库账号具备建表、建索引、建函数、建视图、插入配置和迁移数据权限。
3. 已确认磁盘空间足够保存数据库备份、旧程序目录备份和新版程序包。
4. 已确认现场新版程序目录，例如 `/opt/datasync-lhyy-v2/app`。
5. 已确认现场 Docker 网络和数据库连接方式。如果数据库用容器名访问，升级工具容器必须加入同一个 Docker 网络。
6. 现场服务器已安装 `docker`；如果脚本负责解压新版程序，还需要安装 `unzip`。

## 1. 上传实施包

建议放到独立目录，例如：

```bash
mkdir -p /opt/datasync-lhyy-v2/deploy
cd /opt/datasync-lhyy-v2/deploy
```

将以下文件放到该目录：

```text
DataSync.LHYY.V2.publish.zip
deploy.sh
```

赋予脚本执行权限：

```bash
chmod +x deploy.sh
```

## 2. 配置 deploy.sh

编辑 `deploy.sh` 顶部配置区：

```bash
vi deploy.sh
```

必须按现场修改：

```bash
APP_PACKAGE='./DataSync.LHYY.V2.publish.zip'
APP_DIR='/opt/datasync-lhyy-v2/app'
BACKUP_ROOT='/opt/datasync-lhyy-v2/backups'
TARGET_CONNECTION_STRING='Host=数据库地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码;MaxPoolSize=500;ConnectionLifeTime=15'
DOCKER_NETWORK='现场 Docker 网络名'
SERVICE_MODE='docker'
APP_CONTAINER_NAME='datasync-lhyy-v2'
```

如果现场使用 Docker Compose 管理服务：

```bash
SERVICE_MODE='compose'
COMPOSE_FILE='/opt/datasync-lhyy/docker-compose.yml'
COMPOSE_SERVICE='datasync-lhyy-v2'
```

如果现场希望手工停启服务，只让脚本负责程序替换和数据库升级：

```bash
SERVICE_MODE='none'
```

数据库备份默认由脚本执行：

```bash
BACKUP_DATABASE='1'
BACKUP_CONFIRMED='0'
```

如果现场已经通过外部流程完成数据库备份，并且不希望脚本再次备份，必须显式确认：

```bash
BACKUP_DATABASE='0'
BACKUP_CONFIRMED='1'
```

一般不需要修改：

```bash
BATCH_SIZE='50000'
HOT_DAYS=''
RUN_DRY_RUN_FIRST='1'
CONNECTION_NAME='DataSyncDb'
```

`HOT_DAYS` 留空时使用数据库配置 `MessageHotRetentionDays`；配置不存在时程序默认保留近 30 天热数据。

## 3. 执行一键部署

在实施目录执行：

```bash
./deploy.sh
```

脚本会顺序执行：

1. `pg_dump` 备份目标数据库。
2. 按 `SERVICE_MODE` 停止现场服务。
3. 解压 `DataSync.LHYY.V2.publish.zip`，备份旧程序目录，并替换到 `APP_DIR`。
4. `message-archive upgrade`：创建归档表、统一视图、分区函数和关键索引，并同步热表 identity 序列。
5. `message-archive migrate --dry-run`：预演迁移批次。
6. `message-archive migrate`：迁移超过热保留天数的终态消息和处理日志。
7. `message-archive verify`：校验对象、数量、重复 ID 和索引。
8. 按 `SERVICE_MODE` 启动新版服务。

成功时应看到：

```text
校验通过。
部署完成，数据库升级验证通过
```

如果出现 `Cannot load library libgssapi_krb5.so.2`，但后续显示 `校验通过`，该提示不影响本次密码方式连接和升级结果。

## 4. 服务验证

Docker Compose 示例：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml ps datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml logs --tail=100 datasync-lhyy-v2
```

普通 Docker 容器示例：

```bash
docker ps --filter name=datasync-lhyy-v2
docker logs --tail=100 datasync-lhyy-v2
```

页面和业务验证：

1. 打开系统首页，确认首页统计正常。
2. 打开消息日志页面，默认查询近热保留天数数据。
3. 指定历史接收时间范围，确认可以查询归档数据。
4. 打开历史消息详情，确认报文和处理日志可查看。
5. 发送一条测试 ESB 报文，确认新消息和处理日志可以继续写入热表。

## 5. 数据库结果复核

一键脚本已经执行 `message-archive verify`。如需复核，可以在新版程序目录执行：

```bash
docker run --rm \
  --network 现场 Docker 网络名 \
  -e 'ConnectionStrings__DataSyncDb=Host=数据库地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码' \
  -v /opt/datasync-lhyy-v2/app:/app:ro \
  -w /app \
  mcr.microsoft.com/dotnet/aspnet:10.0 \
  dotnet DataSync.LHYY.V2.dll message-archive verify --connection DataSyncDb
```

也可以直接执行 SQL：

```sql
select 'messages_hot' as item, count(*) from lhyy.esb_messages
union all
select 'messages_archive', count(*) from lhyy.esb_messages_archive
union all
select 'messages_all', count(*) from lhyy.esb_messages_all
union all
select 'logs_hot', count(*) from lhyy.esb_process_log
union all
select 'logs_archive', count(*) from lhyy.esb_process_log_archive
union all
select 'logs_all', count(*) from lhyy.esb_process_log_all;

select count(*) as duplicate_message_ids
from (
  select id from lhyy.esb_messages
  intersect
  select id from lhyy.esb_messages_archive
) d;

select count(*) as duplicate_log_ids
from (
  select id from lhyy.esb_process_log
  intersect
  select id from lhyy.esb_process_log_archive
) d;
```

预期：

- `messages_hot + messages_archive = messages_all`
- `logs_hot + logs_archive = logs_all`
- `duplicate_message_ids = 0`
- `duplicate_log_ids = 0`

## 6. 回滚步骤

如果升级失败且需要回滚：

1. 停止新版服务。
2. 还原 `deploy.sh` 备份的旧程序目录，或还原旧镜像。
3. 使用升级前数据库备份恢复数据库。
4. 启动旧版服务。
5. 检查旧版页面和日志。

数据库恢复示例：

```bash
PGPASSWORD='数据库密码' pg_restore \
  --host 数据库地址 \
  --port 5432 \
  --username 数据库用户 \
  --dbname 数据库名 \
  --clean \
  --if-exists \
  --no-owner \
  --no-password \
  datasyncdb-before-esb-message-opt-备份时间.backup
```

## 注意事项

- 升级后旧版程序不应继续作为正式服务运行。旧版程序只查热表，看不到归档历史数据。
- 新版页面查询通过统一视图读取热表和归档表。
- 新消息、待处理消息、失败待重试消息仍保留在热表。
- 外部报表或第三方 SQL 如果需要查询历史消息，应改查 `lhyy.esb_messages_all` 和 `lhyy.esb_process_log_all`。
