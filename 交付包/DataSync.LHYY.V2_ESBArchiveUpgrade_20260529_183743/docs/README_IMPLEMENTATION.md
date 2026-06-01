# LHYY V2 ESB 消息性能优化实施步骤

本文档用于现场实施 ESB 消息冷热归档与查询性能优化。实施目标是将历史终态消息从热表迁移到归档分区表，程序查询通过统一视图读取热表和归档表。

## 实施包内容

- `DataSync.LHYY.V2.publish.zip`：新版程序发布包。
- `dbupgrad.sh`：数据库结构升级、历史数据迁移、结果校验一键脚本。
- `DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql`：专项数据库升级 SQL。
- `EsbMessagesPerformanceImplementationGuide.md`：本文档。

## 实施前确认

1. 已确认维护窗口，允许停止 LHYY V2 服务。
2. 已确认数据库账号具备建表、建索引、建函数、建视图、插入配置和迁移数据权限。
3. 已确认磁盘空间足够保存数据库备份、旧程序包或旧镜像备份。
4. 已确认现场新版程序目录，例如 `/opt/datasync-lhyy-v2/app`。
5. 已确认现场 Docker 网络和数据库连接方式。如果数据库用容器名访问，升级工具容器必须加入同一个 Docker 网络。

## 1. 备份现场程序和数据库

先备份现场程序目录或镜像。示例：

```bash
cp -a /opt/datasync-lhyy-v2/app /opt/datasync-lhyy-v2/app.bak.$(date +%Y%m%d_%H%M%S)
```

备份数据库。示例：

```bash
PGPASSWORD='数据库密码' pg_dump \
  --host 数据库地址 \
  --port 5432 \
  --username 数据库用户 \
  --dbname 数据库名 \
  --format c \
  --file datasyncdb-before-esb-message-opt-$(date +%Y%m%d_%H%M%S).backup \
  --no-password
```

必须确认备份文件存在且大小合理后，再继续。

## 2. 停止现场服务

Docker Compose 示例：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml stop datasync-lhyy-v2
```

普通 Docker 容器示例：

```bash
docker stop datasync-lhyy-v2
```

## 3. 更新新版程序文件

解压新版发布包到现场程序目录。示例：

```bash
mkdir -p /opt/datasync-lhyy-v2/app.new
unzip -q -o DataSync.LHYY.V2.publish.zip -d /opt/datasync-lhyy-v2/app.new
mv /opt/datasync-lhyy-v2/app /opt/datasync-lhyy-v2/app.old.$(date +%Y%m%d_%H%M%S)
mv /opt/datasync-lhyy-v2/app.new /opt/datasync-lhyy-v2/app
```

确认以下文件存在：

```bash
ls -l /opt/datasync-lhyy-v2/app/DataSync.LHYY.V2.dll
ls -l /opt/datasync-lhyy-v2/app/dbupgrad.sh
ls -l /opt/datasync-lhyy-v2/app/DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql
```

## 4. 配置数据库升级脚本

编辑 `dbupgrad.sh` 顶部配置区：

```bash
vi /opt/datasync-lhyy-v2/app/dbupgrad.sh
```

必须按现场修改：

```bash
TARGET_CONNECTION_STRING='Host=数据库地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码;MaxPoolSize=500;ConnectionLifeTime=15'
APP_DIR='/opt/datasync-lhyy-v2/app'
DOCKER_NETWORK='现场 Docker 网络名'
BACKUP_CONFIRMED='1'
```

如果希望脚本自动停启应用容器，可设置：

```bash
APP_CONTAINER_NAME='datasync-lhyy-v2'
STOP_APP_CONTAINER='1'
START_APP_CONTAINER_AFTER_SUCCESS='1'
```

如果现场不希望脚本管理服务，保持：

```bash
STOP_APP_CONTAINER='0'
START_APP_CONTAINER_AFTER_SUCCESS='0'
```

赋予执行权限：

```bash
chmod +x /opt/datasync-lhyy-v2/app/dbupgrad.sh
```

## 5. 执行数据库升级

```bash
/opt/datasync-lhyy-v2/app/dbupgrad.sh
```

脚本会顺序执行：

1. `message-archive upgrade`：创建归档表、统一视图、分区函数和关键索引，并同步热表 identity 序列。
2. `message-archive migrate --dry-run`：预演迁移批次。
3. `message-archive migrate`：迁移超过热保留天数的终态消息和处理日志。
4. `message-archive verify`：校验对象、数量、重复 ID 和索引。

成功时应看到：

```text
校验通过。
升级完成，验证通过
```

如果出现 `Cannot load library libgssapi_krb5.so.2`，但后续显示 `校验通过`，该提示不影响本次密码方式连接和升级结果。

## 6. 启动新版服务

Docker Compose 示例：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml up -d datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml ps datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml logs --tail=100 datasync-lhyy-v2
```

普通 Docker 容器示例：

```bash
docker start datasync-lhyy-v2
docker logs --tail=100 datasync-lhyy-v2
```

## 7. 数据库结果验证

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

## 8. 页面和业务验证

1. 打开系统首页，确认首页统计正常。
2. 打开消息日志页面，默认查询近热保留天数数据。
3. 指定历史接收时间范围，确认可以查询归档数据。
4. 打开历史消息详情，确认报文和处理日志可查看。
5. 发送一条测试 ESB 报文，确认新消息和处理日志可以继续写入热表。

## 9. 回滚步骤

如果升级失败且需要回滚：

1. 停止新版服务。
2. 还原旧程序目录或旧镜像。
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
