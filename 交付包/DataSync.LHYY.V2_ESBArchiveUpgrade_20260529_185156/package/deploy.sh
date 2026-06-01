#!/bin/sh
set -eu

# =========================
# 现场实施配置区
# =========================

# 新版程序发布包路径。脚本与发布包放同一目录时可保持默认值。
APP_PACKAGE='./DataSync.LHYY.V2.publish.zip'

# 现场程序目录。解压后的新版程序会替换到该目录。
APP_DIR='/opt/datasync-lhyy-v2/app'

# 备份目录。数据库备份和旧程序目录备份会放到这里。
BACKUP_ROOT='/opt/datasync-lhyy-v2/backups'

# 目标平台库连接串。现场必须修改。
TARGET_CONNECTION_STRING='Host=数据库地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码;MaxPoolSize=500;ConnectionLifeTime=15'

# Docker 网络。数据库如果用容器名访问，部署和升级工具容器必须加入同一个网络。
DOCKER_NETWORK='datasync-net'

# 服务管理方式：docker、compose、none。
SERVICE_MODE='docker'

# SERVICE_MODE=docker 时使用。
APP_CONTAINER_NAME='datasync-lhyy-v2'

# SERVICE_MODE=compose 时使用。
COMPOSE_FILE='/opt/datasync-lhyy/docker-compose.yml'
COMPOSE_SERVICE='datasync-lhyy-v2'

# .NET 运行时镜像和 pg_dump 镜像。
DOTNET_IMAGE='mcr.microsoft.com/dotnet/aspnet:10.0'
PG_DUMP_IMAGE='postgres:16-alpine'

# 是否执行数据库备份：1=执行 pg_dump 备份，0=跳过。
# 如果设置为 0，必须把 BACKUP_CONFIRMED 改成 1，表示已由外部完成备份。
BACKUP_DATABASE='1'
BACKUP_CONFIRMED='0'

# 是否替换程序文件：1=解压 APP_PACKAGE 并替换 APP_DIR，0=只做数据库升级和服务启停。
UPDATE_APP_FILES='1'

# 每批迁移消息数。
BATCH_SIZE='50000'

# 热表保留天数。留空时使用数据库配置 MessageHotRetentionDays；配置不存在时程序默认 30 天。
HOT_DAYS=''

# 是否先做一次迁移预演：1=执行 dry-run，0=跳过。
RUN_DRY_RUN_FIRST='1'

# 程序内连接名。一般不用改。
CONNECTION_NAME='DataSyncDb'

# =========================
# 脚本逻辑区
# =========================

log() {
    printf '\n[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"
}

die() {
    echo "$*" >&2
    exit 1
}

mask_connection_string() {
    printf '%s' "$TARGET_CONNECTION_STRING" | sed -E 's/(Password=)[^;]*/\1******/Ig'
}

conn_value() {
    key="$1"
    printf '%s' "$TARGET_CONNECTION_STRING" |
        tr ';' '\n' |
        awk -F= -v k="$key" 'tolower($1)==tolower(k) { sub(/^[^=]*=/, ""); print; exit }'
}

resolve_db_host() {
    host="$(conn_value Host)"
    [ -n "$host" ] || host='localhost'
    printf '%s' "$host" | awk -F: '{print $1}'
}

resolve_db_port() {
    port="$(conn_value Port)"
    if [ -n "$port" ]; then
        printf '%s' "$port"
        return
    fi

    host="$(conn_value Host)"
    if printf '%s' "$host" | grep -q ':'; then
        printf '%s' "$host" | awk -F: '{print $2}'
        return
    fi

    printf '5432'
}

docker_network_args() {
    if [ -n "$DOCKER_NETWORK" ]; then
        printf '%s\n%s\n' '--network' "$DOCKER_NETWORK"
    fi
}

require_ready() {
    command -v docker >/dev/null 2>&1 || die '未找到 docker 命令。'

    if [ "$UPDATE_APP_FILES" = '1' ]; then
        command -v unzip >/dev/null 2>&1 || die '未找到 unzip 命令，请先安装 unzip。'
        [ -f "$APP_PACKAGE" ] || die "未找到新版程序发布包：$APP_PACKAGE"
    fi

    [ -n "$TARGET_CONNECTION_STRING" ] || die '请先设置 TARGET_CONNECTION_STRING。'

    if [ "$BACKUP_DATABASE" != '1' ] && [ "$BACKUP_CONFIRMED" != '1' ]; then
        die '当前配置跳过数据库备份，请先完成外部备份并设置 BACKUP_CONFIRMED=1。'
    fi

    if [ -n "$DOCKER_NETWORK" ]; then
        docker network inspect "$DOCKER_NETWORK" >/dev/null 2>&1 || die "Docker 网络不存在：$DOCKER_NETWORK"
    fi
}

backup_database() {
    if [ "$BACKUP_DATABASE" != '1' ]; then
        log '跳过数据库备份：已由外部备份确认'
        return
    fi

    db_host="$(resolve_db_host)"
    db_port="$(resolve_db_port)"
    db_name="$(conn_value Database)"
    db_user="$(conn_value Username)"
    db_password="$(conn_value Password)"

    [ -n "$db_name" ] || die '连接串缺少 Database。'
    [ -n "$db_user" ] || die '连接串缺少 Username。'

    mkdir -p "$BACKUP_ROOT"
    backup_file="datasyncdb-before-esb-message-opt-$(date +%Y%m%d_%H%M%S).backup"
    log "开始备份数据库：$db_host:$db_port/$db_name"

    set -- docker run --rm
    if [ -n "$DOCKER_NETWORK" ]; then
        set -- "$@" --network "$DOCKER_NETWORK"
    fi
    set -- "$@" \
        -e "PGPASSWORD=$db_password" \
        -v "$BACKUP_ROOT:/backup" \
        "$PG_DUMP_IMAGE" \
        pg_dump \
        --host "$db_host" \
        --port "$db_port" \
        --username "$db_user" \
        --dbname "$db_name" \
        --format c \
        --file "/backup/$backup_file" \
        --no-password

    "$@"
    [ -s "$BACKUP_ROOT/$backup_file" ] || die "数据库备份失败或备份文件为空：$BACKUP_ROOT/$backup_file"
    log "数据库备份完成：$BACKUP_ROOT/$backup_file"
}

stop_service() {
    case "$SERVICE_MODE" in
        docker)
            if docker ps -a --format '{{.Names}}' | grep -qx "$APP_CONTAINER_NAME"; then
                log "停止应用容器：$APP_CONTAINER_NAME"
                docker stop "$APP_CONTAINER_NAME" >/dev/null
            else
                log "应用容器不存在，跳过停止：$APP_CONTAINER_NAME"
            fi
            ;;
        compose)
            [ -f "$COMPOSE_FILE" ] || die "compose 文件不存在：$COMPOSE_FILE"
            log "停止 compose 服务：$COMPOSE_SERVICE"
            docker compose -f "$COMPOSE_FILE" stop "$COMPOSE_SERVICE"
            ;;
        none)
            log '跳过服务停止：SERVICE_MODE=none'
            ;;
        *)
            die "未知 SERVICE_MODE：$SERVICE_MODE"
            ;;
    esac
}

deploy_app_files() {
    if [ "$UPDATE_APP_FILES" != '1' ]; then
        log '跳过程序文件替换'
        return
    fi

    parent_dir="$(dirname "$APP_DIR")"
    app_name="$(basename "$APP_DIR")"
    stamp="$(date +%Y%m%d_%H%M%S)"
    next_dir="$parent_dir/$app_name.next.$stamp"
    old_dir="$BACKUP_ROOT/$app_name.old.$stamp"

    mkdir -p "$parent_dir" "$BACKUP_ROOT" "$next_dir"
    log "解压新版程序包：$APP_PACKAGE"
    unzip -q -o "$APP_PACKAGE" -d "$next_dir"

    [ -f "$next_dir/DataSync.LHYY.V2.dll" ] || die '发布包中未找到 DataSync.LHYY.V2.dll。'
    [ -f "$next_dir/DatabaseUpgrades/EsbMessagesPerformanceOptimization/upgrade_esb_messages_archive_optimization.sql" ] ||
        die '发布包中未找到专项升级 SQL。'

    if [ -d "$APP_DIR" ]; then
        log "备份旧程序目录：$old_dir"
        mv "$APP_DIR" "$old_dir"
    fi

    mv "$next_dir" "$APP_DIR"
    log "新版程序已部署到：$APP_DIR"
}

run_tool() {
    verb="$1"
    shift
    container_name="datasync-lhyy-v2-deploy-$verb"

    docker rm -f "$container_name" >/dev/null 2>&1 || true
    if [ -n "$DOCKER_NETWORK" ]; then
        docker run --rm --name "$container_name" \
            --network "$DOCKER_NETWORK" \
            -e "ASPNETCORE_ENVIRONMENT=Production" \
            -e "ConnectionStrings__${CONNECTION_NAME}=${TARGET_CONNECTION_STRING}" \
            -v "$APP_DIR:/app:ro" \
            -w /app \
            "$DOTNET_IMAGE" \
            dotnet DataSync.LHYY.V2.dll message-archive "$verb" --connection "$CONNECTION_NAME" "$@"
    else
        docker run --rm --name "$container_name" \
            -e "ASPNETCORE_ENVIRONMENT=Production" \
            -e "ConnectionStrings__${CONNECTION_NAME}=${TARGET_CONNECTION_STRING}" \
            -v "$APP_DIR:/app:ro" \
            -w /app \
            "$DOTNET_IMAGE" \
            dotnet DataSync.LHYY.V2.dll message-archive "$verb" --connection "$CONNECTION_NAME" "$@"
    fi
}

run_upgrade() {
    log '开始执行数据库结构升级'
    run_tool upgrade --skip-backup
}

run_migrate_once() {
    dry_run="$1"

    if [ "$dry_run" = '1' ] && [ -n "$HOT_DAYS" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --skip-backup --hot-days "$HOT_DAYS" --dry-run
        return
    fi

    if [ "$dry_run" = '1' ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --skip-backup --dry-run
        return
    fi

    if [ -n "$HOT_DAYS" ]; then
        run_tool migrate --batch-size "$BATCH_SIZE" --skip-backup --hot-days "$HOT_DAYS"
        return
    fi

    run_tool migrate --batch-size "$BATCH_SIZE" --skip-backup
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

start_service() {
    case "$SERVICE_MODE" in
        docker)
            if docker ps -a --format '{{.Names}}' | grep -qx "$APP_CONTAINER_NAME"; then
                log "启动应用容器：$APP_CONTAINER_NAME"
                docker start "$APP_CONTAINER_NAME" >/dev/null
            else
                log "应用容器不存在，无法自动启动：$APP_CONTAINER_NAME"
                log '请按现场 docker run 参数创建新版容器。'
            fi
            ;;
        compose)
            log "启动 compose 服务：$COMPOSE_SERVICE"
            docker compose -f "$COMPOSE_FILE" up -d "$COMPOSE_SERVICE"
            ;;
        none)
            log '跳过服务启动：SERVICE_MODE=none'
            ;;
    esac
}

log 'LHYY V2 ESB 消息性能优化部署开始'
echo "目标连接：$(mask_connection_string)"
echo "程序包：$APP_PACKAGE"
echo "程序目录：$APP_DIR"
echo "备份目录：$BACKUP_ROOT"
echo "Docker 网络：$DOCKER_NETWORK"
echo "服务模式：$SERVICE_MODE"

require_ready
backup_database
stop_service
deploy_app_files
run_upgrade
run_migrate
run_verify
start_service

log '部署完成，数据库升级验证通过'
