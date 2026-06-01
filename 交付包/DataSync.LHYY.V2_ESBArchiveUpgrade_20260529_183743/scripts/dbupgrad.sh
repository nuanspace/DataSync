#!/bin/sh
set -eu

# =========================
# 实施前配置区
# =========================

# 目标平台库连接串。现场只需要改这里。
TARGET_CONNECTION_STRING='Host=lab-postgres;Port=5432;Database=datasync_lhyy_upgrade_test;Username=postgres;Password=postgres;MaxPoolSize=500;ConnectionLifeTime=15'

# 新版程序发布目录。目录内必须包含 DataSync.LHYY.V2.dll 和 DatabaseUpgrades/。
APP_DIR='/opt/datasync-lhyy-v2/new/app'

# Docker 网络。目标数据库如果是 Docker 容器名访问，必须和本工具容器在同一网络。
DOCKER_NETWORK='datasync-net'

# .NET 运行时镜像。
DOTNET_IMAGE='mcr.microsoft.com/dotnet/aspnet:10.0'

# 程序内连接名。一般不用改，脚本会把 TARGET_CONNECTION_STRING 注入到这个连接名。
CONNECTION_NAME='DataSyncDb'

# 每批迁移消息数。
BATCH_SIZE='50000'

# 热表保留天数。留空时使用数据库配置 MessageHotRetentionDays；如果配置不存在，程序默认 30 天。
HOT_DAYS=''

# 是否先做一次迁移预演：1=执行 dry-run，0=跳过。
RUN_DRY_RUN_FIRST='1'

# 是否由脚本自动停止/启动应用容器。默认不自动处理，避免现场容器名不一致。
APP_CONTAINER_NAME='datasync-lhyy-v2'
STOP_APP_CONTAINER='0'
START_APP_CONTAINER_AFTER_SUCCESS='0'

# 当前 aspnet 运行时镜像通常不带 pg_dump，因此默认跳过工具内部备份。
# 现场必须先完成外部数据库备份，再把 BACKUP_CONFIRMED 改为 1。
SKIP_TOOL_BACKUP='1'
BACKUP_CONFIRMED='0'

# 如果 SKIP_TOOL_BACKUP=0，可设置 pg_dump 路径；留空则由程序在容器内 PATH 中查找。
PG_DUMP_PATH=''
BACKUP_STAMP='/tmp/datasync_lhyy_esb_messages_upgrade.stamp'

# =========================
# 脚本逻辑区
# =========================

log() {
    printf '\n[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"
}

mask_connection_string() {
    printf '%s' "$TARGET_CONNECTION_STRING" | sed -E 's/(Password=)[^;]*/\1******/Ig'
}

require_ready() {
    if ! command -v docker >/dev/null 2>&1; then
        echo '未找到 docker 命令。' >&2
        exit 1
    fi

    if [ -z "$TARGET_CONNECTION_STRING" ]; then
        echo '请先在脚本顶部设置 TARGET_CONNECTION_STRING。' >&2
        exit 1
    fi

    if [ ! -f "$APP_DIR/DataSync.LHYY.V2.dll" ]; then
        echo "未找到新版程序：$APP_DIR/DataSync.LHYY.V2.dll" >&2
        exit 1
    fi

    if [ ! -f "$APP_DIR/DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql" ]; then
        echo "未找到专项升级 SQL：$APP_DIR/DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql" >&2
        exit 1
    fi

    if ! docker network inspect "$DOCKER_NETWORK" >/dev/null 2>&1; then
        echo "Docker 网络不存在：$DOCKER_NETWORK" >&2
        exit 1
    fi

    if [ "$SKIP_TOOL_BACKUP" = '1' ] && [ "$BACKUP_CONFIRMED" != '1' ]; then
        echo '当前配置会跳过工具内部备份。'
        echo '请先完成外部数据库备份，然后把脚本顶部 BACKUP_CONFIRMED 改为 1 后再执行。'
        exit 1
    fi
}

run_tool() {
    verb="$1"
    shift
    container_name="datasync-lhyy-v2-dbupgrad-$verb"

    docker rm -f "$container_name" >/dev/null 2>&1 || true
    docker run --rm \
        --name "$container_name" \
        --network "$DOCKER_NETWORK" \
        -e "ASPNETCORE_ENVIRONMENT=Production" \
        -e "ConnectionStrings__${CONNECTION_NAME}=${TARGET_CONNECTION_STRING}" \
        -v "$APP_DIR:/app:ro" \
        -w /app \
        "$DOTNET_IMAGE" \
        dotnet DataSync.LHYY.V2.dll message-archive "$verb" --connection "$CONNECTION_NAME" "$@"
}

run_upgrade() {
    log '开始执行数据库结构升级'

    if [ "$SKIP_TOOL_BACKUP" = '1' ]; then
        run_tool upgrade --skip-backup
    elif [ -n "$PG_DUMP_PATH" ]; then
        run_tool upgrade --pg-dump "$PG_DUMP_PATH" --backup-stamp "$BACKUP_STAMP"
    else
        run_tool upgrade --backup-stamp "$BACKUP_STAMP"
    fi
}

run_migrate_once() {
    dry_run="$1"
    dry_run_arg=''
    if [ "$dry_run" = '1' ]; then
        dry_run_arg='--dry-run'
    fi

    if [ "$SKIP_TOOL_BACKUP" = '1' ] && [ -n "$HOT_DAYS" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --hot-days "$HOT_DAYS" $dry_run_arg --skip-backup
    elif [ "$SKIP_TOOL_BACKUP" = '1' ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" $dry_run_arg --skip-backup
    elif [ -n "$PG_DUMP_PATH" ] && [ -n "$HOT_DAYS" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --hot-days "$HOT_DAYS" $dry_run_arg --pg-dump "$PG_DUMP_PATH" --backup-stamp "$BACKUP_STAMP"
    elif [ -n "$PG_DUMP_PATH" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" $dry_run_arg --pg-dump "$PG_DUMP_PATH" --backup-stamp "$BACKUP_STAMP"
    elif [ -n "$HOT_DAYS" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --hot-days "$HOT_DAYS" $dry_run_arg --backup-stamp "$BACKUP_STAMP"
    else
        run_tool migrate --batch-size "$BATCH_SIZE" $dry_run_arg --backup-stamp "$BACKUP_STAMP"
    fi
}

run_migrate() {
    if [ "$RUN_DRY_RUN_FIRST" = '1' ]; then
        log '开始执行历史数据迁移预演'
        run_migrate_once '1'
    fi

    log '开始执行历史数据迁移'
    run_migrate_once '0'
}

run_verify() {
    log '开始执行升级结果校验'
    run_tool verify
}

stop_app_container_if_needed() {
    if [ "$STOP_APP_CONTAINER" != '1' ]; then
        return
    fi

    if [ -z "$APP_CONTAINER_NAME" ]; then
        echo '已开启 STOP_APP_CONTAINER，但 APP_CONTAINER_NAME 为空。' >&2
        exit 1
    fi

    if docker ps -a --format '{{.Names}}' | grep -qx "$APP_CONTAINER_NAME"; then
        log "停止应用容器：$APP_CONTAINER_NAME"
        docker stop "$APP_CONTAINER_NAME" >/dev/null
    else
        log "应用容器不存在，跳过停止：$APP_CONTAINER_NAME"
    fi
}

start_app_container_if_needed() {
    if [ "$START_APP_CONTAINER_AFTER_SUCCESS" != '1' ]; then
        return
    fi

    if [ -z "$APP_CONTAINER_NAME" ]; then
        echo '已开启 START_APP_CONTAINER_AFTER_SUCCESS，但 APP_CONTAINER_NAME 为空。' >&2
        exit 1
    fi

    if docker ps -a --format '{{.Names}}' | grep -qx "$APP_CONTAINER_NAME"; then
        log "启动应用容器：$APP_CONTAINER_NAME"
        docker start "$APP_CONTAINER_NAME" >/dev/null
    else
        log "应用容器不存在，跳过启动：$APP_CONTAINER_NAME"
    fi
}

log 'ESB 消息冷热归档专项升级开始'
echo "目标连接：$(mask_connection_string)"
echo "程序目录：$APP_DIR"
echo "Docker 网络：$DOCKER_NETWORK"
echo "批大小：$BATCH_SIZE"

require_ready
stop_app_container_if_needed
run_upgrade
run_migrate
run_verify
start_app_container_if_needed

log '升级完成，验证通过'
