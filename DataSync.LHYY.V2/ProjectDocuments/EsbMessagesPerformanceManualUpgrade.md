# LHYY V2 ESB 消息性能专项手工升级步骤

本文档用于现场按原有实施习惯手工升级：先备份，再执行数据库专项升级和历史迁移，最后替换程序镜像。

## 需要带到现场的内容

- 新版本 Docker 镜像包：`datasync-lhyy-v2.tar`
- 新版本程序发布包，包内需包含：
  - `DataSync.LHYY.V2.dll`
  - `DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql`
- 本手工升级步骤文档
- PostgreSQL 客户端工具：现场宿主机需要可执行 `pg_dump`、`pg_restore`
- 现场原有 `docker-compose.yml` 路径、服务名、数据库连接信息

## 升级前确认

1. 确认现场有维护窗口，允许短暂停止 LHYY V2 服务。
2. 确认数据库账号具备建表、建索引、建函数、建视图、插入配置和数据迁移权限。
3. 确认磁盘空间足够保存数据库备份、旧镜像备份和新镜像包。
4. 确认当前服务名，例如：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml config --services
```

以下命令中的路径和服务名请按现场实际替换。

## 1. 备份程序和数据库

备份 compose 文件：

```bash
cp /opt/datasync-lhyy/docker-compose.yml /opt/datasync-lhyy/docker-compose.yml.bak.$(date +%Y%m%d_%H%M%S)
```

如现场使用 `.env` 文件，也一并备份：

```bash
cp /opt/datasync-lhyy/.env /opt/datasync-lhyy/.env.bak.$(date +%Y%m%d_%H%M%S)
```

备份当前运行镜像：

```bash
container_id=$(docker compose -f /opt/datasync-lhyy/docker-compose.yml ps -q datasync-lhyy-v2)
old_image=$(docker inspect -f '{{.Config.Image}}' "$container_id")
docker save -o old-datasync-lhyy-v2-$(date +%Y%m%d_%H%M%S).tar "$old_image"
```

备份数据库：

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

## 2. 加载新镜像

```bash
docker load -i datasync-lhyy-v2.tar
docker images | grep datasync-lhyy-v2
```

记录新镜像标签，例如：

```text
datasync-lhyy-v2:esb-message-archive
```

## 3. 停止服务

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml stop datasync-lhyy-v2
```

## 4. 执行数据库专项升级

推荐直接使用新镜像中的 `message-archive` 工具执行升级脚本。这样可以复用程序内置校验，避免手工漏执行 SQL。

如果数据库连接串已在 compose 环境中配置：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml run --rm --no-deps datasync-lhyy-v2 \
  message-archive upgrade --connection DataSyncDb --skip-backup
```

如果需要临时指定连接串：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml run --rm --no-deps \
  -e 'ConnectionStrings__DataSyncDb=Host=数据库地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码' \
  datasync-lhyy-v2 \
  message-archive upgrade --connection DataSyncDb --skip-backup
```

## 5. 迁移历史终态消息

迁移范围是超过热保留天数的终态消息，终态包括已处理、已忽略、处理成功、处理失败等已完成状态。长期 `Pending`、`Processing`、`Failed` 不会被本步骤归档。

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml run --rm --no-deps datasync-lhyy-v2 \
  message-archive migrate --connection DataSyncDb --batch-size 50000 --skip-backup
```

如果使用临时连接串，同样增加 `-e ConnectionStrings__DataSyncDb=...`。

## 6. 校验数据库升级结果

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml run --rm --no-deps datasync-lhyy-v2 \
  message-archive verify --connection DataSyncDb
```

看到归档表、统一视图、分区函数、关键索引校验通过后，再继续替换程序镜像。

## 7. 替换程序镜像并启动

修改现场 `docker-compose.yml` 或 `.env` 中 LHYY V2 服务的镜像标签为新镜像标签。

启动服务：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml up -d datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml ps datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml logs --tail=100 datasync-lhyy-v2
```

## 8. 升级后检查

1. 打开系统页面，确认消息查询、接口配置、首页统计正常。
2. 查询近 30 天消息，确认速度明显改善。
3. 查询历史消息，确认可通过统一视图读取归档数据。
4. 观察后台日志，确认没有持续数据库异常。

## 手工还原步骤

如需回退：

1. 停止服务：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml stop datasync-lhyy-v2
```

2. 恢复旧 compose 和可选 `.env` 备份。
3. 加载旧镜像：

```bash
docker load -i old-datasync-lhyy-v2-备份时间.tar
```

4. 还原数据库：

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

5. 启动旧服务并检查日志：

```bash
docker compose -f /opt/datasync-lhyy/docker-compose.yml up -d datasync-lhyy-v2
docker compose -f /opt/datasync-lhyy/docker-compose.yml logs --tail=100 datasync-lhyy-v2
```
