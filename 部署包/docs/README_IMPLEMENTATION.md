# LHYY V2 ESB 消息性能优化实施步骤

本文档用于现场实施 ESB 消息冷热归档与查询性能优化。实施目标是将历史终态消息从热表迁移到归档分区表，程序查询通过统一视图读取热表和归档表。

现场推荐统一执行 `deploy.sh`。脚本面向 Linux + Docker 现场：应用通过 Docker 镜像升级，数据库升级通过新版镜像内置的 `message-archive` 工具连接目标数据库完成。

## 实施包内容

- `datasync-lhyy-v2.tar`：新版程序 Docker 镜像包。
- `deploy.sh`：现场一键部署脚本，包含加载镜像、停止服务、备份、数据库升级、历史迁移、结果校验和启动服务。
- `docs/README_IMPLEMENTATION.md`：本文档。
- `docs/MANUAL_UPGRADE_REFERENCE.md`：手工升级命令参考。
- `sql/upgrade_esb_messages_archive_optimization.sql`：专项数据库升级 SQL 参考。
- `SHA256SUMS.txt`：交付包内关键文件校验值。

## 实施前确认

1. 已确认维护窗口，允许停止 LHYY V2 服务。
2. 已确认数据库账号具备建表、建索引、建函数、建视图、插入配置和迁移数据权限。
3. 已确认磁盘空间足够保存数据库备份、旧镜像备份和新版镜像包。
4. 已确认现场应用服务使用 Docker 镜像方式部署。
5. 已确认现场 `docker-compose.yml` 路径、服务名、镜像标签配置方式和数据库连接方式。
6. 如果数据库连接串使用数据库容器名，脚本会优先从当前 compose 应用容器自动识别 Docker 网络；自动识别失败时再手工填写 `DOCKER_NETWORK`。
7. 现场服务器已安装 `docker` 和 Docker Compose v2。

## 1. 上传实施包

建议放到独立目录，例如：

```bash
mkdir -p /opt/datasync-lhyy-v2/deploy
cd /opt/datasync-lhyy-v2/deploy
```

将以下文件放到该目录：

```text
datasync-lhyy-v2.tar
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
# ---- 必填：数据库升级配置 ----
IMAGE_PACKAGE='./datasync-lhyy-v2.tar'
APP_IMAGE=''
BACKUP_ROOT='/opt/datasync-lhyy-v2/backups'
TARGET_CONNECTION_STRING='Host=数据库容器名或地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码;MaxPoolSize=500;ConnectionLifeTime=15'
DOCKER_NETWORK=''
DB_HOST_IS_CONTAINER='auto'
PG_DUMP_IMAGE='postgres:16-alpine'
LOAD_APP_IMAGE='1'
UPDATE_APP_SERVICE='1'
APP_STOP_CONFIRMED='0'

# ---- 仅 UPDATE_APP_SERVICE=1 时需要关注：应用服务配置 ----
SERVICE_MODE='compose'
COMPOSE_FILE='/opt/datasync-lhyy/docker-compose.yml'
COMPOSE_SERVICE='datasync-lhyy-v2'
COMPOSE_VALIDATE_IMAGE='1'
COMPOSE_EXPECTED_IMAGE=''
```

`APP_IMAGE` 通常可以留空。脚本会执行 `docker load -i "$IMAGE_PACKAGE"`，并从输出的 `Loaded image: ...` 自动识别新版程序镜像标签。只有以下情况需要手工填写：

- `LOAD_APP_IMAGE='0'`，脚本不加载镜像包。
- 镜像包内包含多个镜像标签，脚本无法判断唯一标签。
- 现场需要使用私有仓库前缀或另一个等效镜像标签。

现场 `docker-compose.yml` 或 `.env` 中该服务的镜像标签应指向脚本最终识别或配置的新版镜像。

`TARGET_CONNECTION_STRING` 必须改成现场真实连接串，不能保留 `数据库容器名或地址`、`数据库名`、`数据库用户`、`数据库密码` 等占位文本。脚本会提前检查 `Host`、`Database`、`Username` 是否存在。

`COMPOSE_VALIDATE_IMAGE='1'` 时，脚本会在执行前校验 `COMPOSE_SERVICE` 渲染后的 `image` 是否等于期望镜像。`COMPOSE_EXPECTED_IMAGE` 留空时默认使用 `APP_IMAGE`；如果现场 compose 使用了镜像仓库前缀、私有仓库地址或其他等效标签，应把 `COMPOSE_EXPECTED_IMAGE` 填成 compose 实际渲染出的完整镜像名。

`DOCKER_NETWORK` 通常可以留空。脚本会在 `SERVICE_MODE='compose'` 时从当前应用容器自动识别 Docker 网络，并让数据库备份容器和升级工具容器加入该网络。只有以下情况需要手工填写：

- 当前应用容器不存在，脚本无法自动识别网络。
- 数据库容器不在应用容器的默认网络中。
- 现场使用了多个 Docker 网络，需要指定其中某一个网络。

如果数据库连接串使用宿主机可访问的 IP 地址，也可以保持 `DOCKER_NETWORK=''`。如果数据库连接串使用容器名，并且脚本无法自动识别 Docker 网络，脚本会停止运行并提示填写 `DOCKER_NETWORK`。

`DB_HOST_IS_CONTAINER` 用于辅助判断数据库连接串中的 `Host` 是否为 Docker 容器名：

- `auto`：默认值，脚本自动判断。IP、`localhost`、`host.docker.internal` 会按非容器名处理，其他名称会在无法识别网络时提示确认。
- `1`：明确表示 `Host` 是 Docker 容器名，无法识别网络时必须填写 `DOCKER_NETWORK`。
- `0`：明确表示 `Host` 不是 Docker 容器名，例如普通 DNS 域名 `db.example.local`，即使未识别到 Docker 网络也继续执行。

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
BACKUP_APP_IMAGE='1'
BATCH_SIZE='50000'
HOT_DAYS=''
RUN_DRY_RUN_FIRST='1'
CONNECTION_NAME='DataSyncDb'
```

`HOT_DAYS` 留空时使用数据库配置 `MessageHotRetentionDays`；配置不存在时程序默认保留近 30 天热数据。

如果现场 app 由专人单独升级，本脚本只负责数据库升级和历史数据迁移，可以设置：

```bash
UPDATE_APP_SERVICE='0'
APP_STOP_CONFIRMED='1'
```

此模式下脚本不会备份旧 app 镜像、不会停止正式 app 服务、不会重建或启动 app 服务；但仍会加载 `IMAGE_PACKAGE`，并使用新版镜像里的 `message-archive` 工具执行数据库备份、结构升级、历史迁移和校验。

设置 `UPDATE_APP_SERVICE='0'` 前必须确认 app 已由专人停止，或现场已经进入维护窗口并接受数据库升级期间旧 app 继续运行的风险。未设置 `APP_STOP_CONFIRMED='1'` 时脚本会停止运行。此模式下 `SERVICE_MODE`、`APP_CONTAINER_NAME`、`COMPOSE_FILE`、`COMPOSE_SERVICE`、`COMPOSE_VALIDATE_IMAGE`、`COMPOSE_EXPECTED_IMAGE`、`BACKUP_APP_IMAGE` 可以不配置。

当 `UPDATE_APP_SERVICE='1'` 时，`SERVICE_MODE` 只支持 `compose`。脚本不会自动按现场原 `docker run` 参数重建普通 Docker 容器；如果现场不是 Compose 管理，或 app 由专人升级，应使用 `UPDATE_APP_SERVICE='0'` 和 `APP_STOP_CONFIRMED='1'` 表达该场景。

## 3. 执行一键部署

在实施目录执行：

```bash
./deploy.sh
```

脚本会顺序执行：

1. 备份当前应用容器正在使用的旧镜像。
2. 加载新版程序镜像包。
3. 按 `SERVICE_MODE` 停止现场服务。
4. 使用 `postgres:16-alpine` 容器执行 `pg_dump` 备份目标数据库。
5. 使用新版程序镜像执行 `message-archive upgrade`，创建归档表、统一视图、分区函数和关键索引，并同步热表 identity 序列。
6. 使用新版程序镜像执行 `message-archive migrate --dry-run`，预演迁移批次。
7. 使用新版程序镜像执行 `message-archive migrate`，迁移超过热保留天数的终态消息和处理日志。
8. 使用新版程序镜像执行 `message-archive verify`，校验对象、数量、重复 ID 和索引。
9. `SERVICE_MODE=compose` 时执行 `docker compose up -d --no-deps --force-recreate`，用新版镜像重建并启动应用服务。

如果 `UPDATE_APP_SERVICE='0'`，第 1、3、9 步会跳过，只执行镜像加载、数据库备份、结构升级、迁移和校验。

成功时应看到：

```text
校验通过。
部署完成，数据库升级验证通过
```

如果现场不是 Compose 管理，而是直接 `docker run` 创建容器，建议设置 `UPDATE_APP_SERVICE='0'` 和 `APP_STOP_CONFIRMED='1'`，让脚本只负责数据库备份、结构升级、历史迁移和校验。数据库升级完成后，由 app 专人按现场原 `docker run` 参数使用新版镜像手工重建应用容器。

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

一键脚本已经执行 `message-archive verify`。如需复核，可以使用新版镜像再次执行：

```bash
docker run --rm \
  --network 现场 Docker 网络名 \
  -e 'ConnectionStrings__DataSyncDb=Host=数据库容器名或地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码' \
  datasync-lhyy-v2:esb-message-archive \
  message-archive verify --connection DataSyncDb
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
2. 恢复旧 `docker-compose.yml` 或 `.env` 中的镜像标签。
3. 如本机缺少旧镜像，使用 `deploy.sh` 备份的旧镜像 tar 执行 `docker load -i old-datasync-lhyy-v2-备份时间.tar`。
4. 使用升级前数据库备份恢复数据库。
5. 启动旧版服务。
6. 检查旧版页面和日志。

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
