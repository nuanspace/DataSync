#!/bin/sh
set -eu

# =========================
# 现场实施配置区
# =========================

# ---- 必填：数据库升级配置 ----

# 新版程序 Docker 镜像包路径。脚本与镜像包放同一目录时可保持默认值。
IMAGE_PACKAGE='./datasync-lhyy-v2.tar'

# 新版程序镜像标签。留空时脚本会从 docker load 输出自动识别。
# 镜像包包含多个标签或跳过 docker load 时必须填写。
APP_IMAGE=''

# 备份目录。数据库备份和旧程序镜像备份会放到这里。注意：即便不进行备份，这里也需要一个有效目录
BACKUP_ROOT='/opt/datasync-lhyy-v2/backups'

# 目标平台库连接串。现场必须修改。
TARGET_CONNECTION_STRING='Host=数据库容器名或地址;Port=5432;Database=数据库名;Username=数据库用户;Password=数据库密码;MaxPoolSize=500;ConnectionLifeTime=15'

# 如果数据库是通过容器连接的，这里需要填写容器所在网络，命令行如下：
# docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' 数据库容器名
# 如果自动识别失败，或数据库连接串使用了特殊网络中的容器名，再按现场实际填写。
DOCKER_NETWORK=''

# 数据库 Host 是否为 Docker 容器名：auto=自动判断，1=是，0=不是。
# 如果 Host 是可解析的 DNS 域名但不是容器名，请设置为 0。
DB_HOST_IS_CONTAINER='auto'

# pg_dump 镜像。
PG_DUMP_IMAGE='postgres:16-alpine'

# 是否加载新版程序镜像：1=执行 docker load，0=跳过。
LOAD_APP_IMAGE='1'

# 是否由脚本更新应用服务：1=停止并重建应用服务，0=只执行数据库备份、升级、迁移和校验。
UPDATE_APP_SERVICE='1'

# UPDATE_APP_SERVICE=0 时必须设置为 1，表示 app 已由专人停止或已确认维护窗口。
APP_STOP_CONFIRMED='0'

# 是否执行数据库备份：1=执行 pg_dump 备份，0=跳过。
# 如果设置为 0，必须把 BACKUP_CONFIRMED 改成 1，表示已由外部完成备份。
BACKUP_DATABASE='1'
BACKUP_CONFIRMED='0'

# 每批迁移消息数。
BATCH_SIZE='50000'

# 热表保留天数。留空时使用数据库配置 MessageHotRetentionDays；配置不存在时程序默认 30 天。
HOT_DAYS=''

# 是否先做一次迁移预演：1=执行 dry-run，0=跳过。
RUN_DRY_RUN_FIRST='1'

# 程序内连接名。一般不用改。
CONNECTION_NAME='DataSyncDb'

# ---- 仅 UPDATE_APP_SERVICE=1 时需要关注：应用服务配置 ----

# 服务管理方式：compose。
# 非 compose 场景请设置 UPDATE_APP_SERVICE=0，由 app 专人按现场原参数重建容器。
SERVICE_MODE='compose'

# UPDATE_APP_SERVICE=0 且需要参考当前容器信息时使用。
APP_CONTAINER_NAME='datasync-lhyy-v2'

# SERVICE_MODE=compose 时使用。compose 文件或 .env 中的服务镜像应已指向 APP_IMAGE。
COMPOSE_FILE='/opt/datasync-lhyy/docker-compose.yml'
COMPOSE_SERVICE='datasync-lhyy-v2'

# 是否校验 compose 服务镜像：1=校验 COMPOSE_SERVICE 的 image 必须等于 COMPOSE_EXPECTED_IMAGE，0=跳过。
# COMPOSE_EXPECTED_IMAGE 留空时默认使用 APP_IMAGE。
COMPOSE_VALIDATE_IMAGE='1'
COMPOSE_EXPECTED_IMAGE=''

# 是否备份当前应用镜像：1=执行 docker save 备份，0=跳过。
BACKUP_APP_IMAGE='1'

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

validate_connection_string() {
    case "$TARGET_CONNECTION_STRING" in
        *数据库容器名或地址*|*数据库地址*|*数据库名*|*数据库用户*|*数据库密码*)
            die 'TARGET_CONNECTION_STRING 仍包含占位文本，请先改成现场真实数据库连接串。'
            ;;
    esac

    [ -n "$(conn_value Host)" ] || die '连接串缺少 Host。'
    [ -n "$(conn_value Database)" ] || die '连接串缺少 Database。'
    [ -n "$(conn_value Username)" ] || die '连接串缺少 Username。'
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

is_probably_ip_or_localhost() {
    value="$1"

    case "$value" in
        localhost|127.*|0.0.0.0|host.docker.internal)
            return 0
            ;;
    esac

    printf '%s' "$value" | grep -Eq '^[0-9]+(\.[0-9]+){3}$'
}

current_app_container_id() {
    case "$SERVICE_MODE" in
        compose)
            docker compose -f "$COMPOSE_FILE" ps -a -q "$COMPOSE_SERVICE" 2>/dev/null || true
            ;;
        docker)
            docker ps -a --filter "name=^/${APP_CONTAINER_NAME}$" --format '{{.ID}}' | head -n 1
            ;;
        *)
            printf ''
            ;;
    esac
}

resolve_docker_network() {
    if [ -n "$DOCKER_NETWORK" ]; then
        printf '%s' "$DOCKER_NETWORK"
        return
    fi

    if [ "$SERVICE_MODE" != 'compose' ]; then
        printf ''
        return
    fi

    container_id="$(current_app_container_id)"
    if [ -z "$container_id" ]; then
        printf ''
        return
    fi

    docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' "$container_id" |
        sed '/^$/d' |
        head -n 1
}

require_effective_network_if_needed() {
    effective_network="$1"
    db_host="$(resolve_db_host)"

    if [ -n "$effective_network" ] || [ "$DB_HOST_IS_CONTAINER" = '0' ]; then
        return
    fi

    if [ "$DB_HOST_IS_CONTAINER" = '1' ]; then
        die "无法自动获取 Docker 网络，且 DB_HOST_IS_CONTAINER=1。请在配置区填写 DOCKER_NETWORK。"
    fi

    if [ "$DB_HOST_IS_CONTAINER" != 'auto' ]; then
        die 'DB_HOST_IS_CONTAINER 只能设置为 auto、1 或 0。'
    fi

    if is_probably_ip_or_localhost "$db_host"; then
        return
    fi

    die "无法自动获取 Docker 网络，且数据库 Host=$db_host 看起来可能是容器名。若它是普通 DNS 域名，请设置 DB_HOST_IS_CONTAINER=0；若它是容器名，请填写 DOCKER_NETWORK。"
}

compose_expected_image() {
    if [ -n "$COMPOSE_EXPECTED_IMAGE" ]; then
        printf '%s' "$COMPOSE_EXPECTED_IMAGE"
        return
    fi

    printf '%s' "$APP_IMAGE"
}

compose_service_image() {
    docker compose -f "$COMPOSE_FILE" config |
        awk -v service="$COMPOSE_SERVICE" '
            $1 == service ":" {
                in_service=1
                next
            }
            in_service && $0 ~ /^  [^ ].*:$/ {
                exit
            }
            in_service && $1 == "image:" {
                print $2
                exit
            }
        '
}

validate_compose_image() {
    if [ "$UPDATE_APP_SERVICE" != '1' ] || [ "$SERVICE_MODE" != 'compose' ] || [ "$COMPOSE_VALIDATE_IMAGE" != '1' ]; then
        return
    fi

    expected_image="$(compose_expected_image)"
    actual_image="$(compose_service_image)"

    [ -n "$actual_image" ] || die "compose 服务未配置 image，无法确认应用镜像：$COMPOSE_SERVICE"
    [ "$actual_image" = "$expected_image" ] ||
        die "compose 服务镜像不匹配：$COMPOSE_SERVICE 当前为 $actual_image，期望为 $expected_image。请先修改 docker-compose.yml 或 .env。"
}

require_ready() {
    command -v docker >/dev/null 2>&1 || die '未找到 docker 命令。'

    [ -n "$TARGET_CONNECTION_STRING" ] || die '请先设置 TARGET_CONNECTION_STRING。'
    [ -n "$BATCH_SIZE" ] || die '请先设置 BATCH_SIZE。'
    [ -n "$CONNECTION_NAME" ] || die '请先设置 CONNECTION_NAME。'

    if [ "$LOAD_APP_IMAGE" = '1' ]; then
        [ -f "$IMAGE_PACKAGE" ] || die "未找到新版程序镜像包：$IMAGE_PACKAGE"
    else
        [ -n "$APP_IMAGE" ] || die 'LOAD_APP_IMAGE=0 时必须设置 APP_IMAGE。'
    fi

    if [ "$UPDATE_APP_SERVICE" != '1' ] && [ "$APP_STOP_CONFIRMED" != '1' ]; then
        die 'UPDATE_APP_SERVICE=0 时，请先确认 app 已由专人停止或已进入维护窗口，并设置 APP_STOP_CONFIRMED=1。'
    fi

    if [ "$UPDATE_APP_SERVICE" = '1' ]; then
        case "$SERVICE_MODE" in
            compose)
                [ -f "$COMPOSE_FILE" ] || die "compose 文件不存在：$COMPOSE_FILE"
                [ -n "$COMPOSE_SERVICE" ] || die '请先设置 COMPOSE_SERVICE。'
                docker compose -f "$COMPOSE_FILE" config >/dev/null || die "compose 文件校验失败：$COMPOSE_FILE"
                ;;
            docker)
                die 'UPDATE_APP_SERVICE=1 时不支持 SERVICE_MODE=docker，因为脚本无法按现场原 docker run 参数自动重建容器。非 Compose 场景请设置 UPDATE_APP_SERVICE=0 并设置 APP_STOP_CONFIRMED=1，由 app 专人重建容器。'
                ;;
            none)
                die 'UPDATE_APP_SERVICE=1 时不能使用 SERVICE_MODE=none。如 app 由专人升级，请设置 UPDATE_APP_SERVICE=0 并设置 APP_STOP_CONFIRMED=1。'
                ;;
            *)
                die "未知 SERVICE_MODE：$SERVICE_MODE"
                ;;
        esac
    fi

    if [ "$BACKUP_DATABASE" != '1' ] && [ "$BACKUP_CONFIRMED" != '1' ]; then
        die '当前配置跳过数据库备份，请先完成外部备份并设置 BACKUP_CONFIRMED=1。'
    fi

    validate_connection_string

    effective_network="$(resolve_docker_network)"
    if [ -n "$effective_network" ]; then
        docker network inspect "$effective_network" >/dev/null 2>&1 || die "Docker 网络不存在：$effective_network"
    fi
    require_effective_network_if_needed "$effective_network"
}

load_app_image() {
    if [ "$LOAD_APP_IMAGE" != '1' ]; then
        log '跳过新版程序镜像加载'
        docker image inspect "$APP_IMAGE" >/dev/null 2>&1 || die "本机未找到新版程序镜像：$APP_IMAGE"
        return
    fi

    log "加载新版程序镜像：$IMAGE_PACKAGE"
    load_output="$(docker load -i "$IMAGE_PACKAGE")"
    printf '%s\n' "$load_output"

    if [ -z "$APP_IMAGE" ]; then
        loaded_images="$(printf '%s\n' "$load_output" | sed -n 's/^Loaded image: //p')"
        loaded_count="$(printf '%s\n' "$loaded_images" | sed '/^$/d' | wc -l | tr -d ' ')"

        if [ "$loaded_count" = '1' ]; then
            APP_IMAGE="$(printf '%s\n' "$loaded_images" | sed '/^$/d' | head -n 1)"
            log "自动识别新版程序镜像：$APP_IMAGE"
        else
            die '无法从镜像包自动识别唯一 APP_IMAGE。请执行 docker load -i 镜像包查看 Loaded image，并在配置区填写 APP_IMAGE。'
        fi
    fi

    docker image inspect "$APP_IMAGE" >/dev/null 2>&1 || die "镜像包加载后未找到 APP_IMAGE：$APP_IMAGE，请确认镜像标签。"
}

backup_app_image() {
    if [ "$UPDATE_APP_SERVICE" != '1' ]; then
        log '跳过当前应用镜像备份：UPDATE_APP_SERVICE=0'
        return
    fi

    if [ "$BACKUP_APP_IMAGE" != '1' ]; then
        log '跳过当前应用镜像备份'
        return
    fi

    container_id="$(current_app_container_id)"
    if [ -z "$container_id" ]; then
        log '未找到当前应用容器，跳过应用镜像备份'
        return
    fi

    current_image="$(docker inspect -f '{{.Image}}' "$container_id")"
    [ -n "$current_image" ] || die '无法读取当前应用镜像。'

    mkdir -p "$BACKUP_ROOT"
    backup_file="$BACKUP_ROOT/old-datasync-lhyy-v2-$(date +%Y%m%d_%H%M%S).tar"
    log "备份当前应用镜像：$current_image"
    docker save -o "$backup_file" "$current_image"
    [ -s "$backup_file" ] || die "应用镜像备份失败或备份文件为空：$backup_file"
    log "应用镜像备份完成：$backup_file"
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

    effective_network="$(resolve_docker_network)"

    set -- docker run --rm
    if [ -n "$effective_network" ]; then
        set -- "$@" --network "$effective_network"
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
    if [ "$UPDATE_APP_SERVICE" != '1' ]; then
        log '跳过服务停止：UPDATE_APP_SERVICE=0'
        return
    fi

    case "$SERVICE_MODE" in
        compose)
            log "停止 compose 服务：$COMPOSE_SERVICE"
            docker compose -f "$COMPOSE_FILE" stop "$COMPOSE_SERVICE"
            ;;
        docker)
            if docker ps -a --format '{{.Names}}' | grep -qx "$APP_CONTAINER_NAME"; then
                log "停止应用容器：$APP_CONTAINER_NAME"
                docker stop "$APP_CONTAINER_NAME" >/dev/null
            else
                log "应用容器不存在，跳过停止：$APP_CONTAINER_NAME"
            fi
            ;;
        none)
            log '跳过服务停止：SERVICE_MODE=none'
            ;;
    esac
}

run_tool() {
    verb="$1"
    shift
    container_name="datasync-lhyy-v2-deploy-$verb"
    effective_network="$(resolve_docker_network)"

    docker rm -f "$container_name" >/dev/null 2>&1 || true
    if [ -n "$effective_network" ]; then
        docker run --rm --name "$container_name" \
            --network "$effective_network" \
            -e "ASPNETCORE_ENVIRONMENT=Production" \
            -e "ConnectionStrings__${CONNECTION_NAME}=${TARGET_CONNECTION_STRING}" \
            "$APP_IMAGE" \
            message-archive "$verb" --connection "$CONNECTION_NAME" "$@"
    else
        docker run --rm --name "$container_name" \
            -e "ASPNETCORE_ENVIRONMENT=Production" \
            -e "ConnectionStrings__${CONNECTION_NAME}=${TARGET_CONNECTION_STRING}" \
            "$APP_IMAGE" \
            message-archive "$verb" --connection "$CONNECTION_NAME" "$@"
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
    if [ "$UPDATE_APP_SERVICE" != '1' ]; then
        log '跳过服务启动：UPDATE_APP_SERVICE=0'
        return
    fi

    case "$SERVICE_MODE" in
        compose)
            log "使用新版镜像重建并启动 compose 服务：$COMPOSE_SERVICE"
            docker compose -f "$COMPOSE_FILE" up -d --no-deps --force-recreate "$COMPOSE_SERVICE"
            ;;
        docker)
            die "SERVICE_MODE=docker 不自动重建容器。请使用镜像 $APP_IMAGE 按现场原 docker run 参数重建应用容器：$APP_CONTAINER_NAME"
            ;;
        none)
            log '跳过服务启动：SERVICE_MODE=none'
            ;;
    esac
}

log 'LHYY V2 ESB 消息性能优化部署开始'
echo "目标连接：$(mask_connection_string)"
echo "新版镜像包：$IMAGE_PACKAGE"
echo "备份目录：$BACKUP_ROOT"
echo "Docker 网络配置：$DOCKER_NETWORK"
if [ "$UPDATE_APP_SERVICE" = '1' ]; then
    echo "应用服务处理：启用，服务模式=$SERVICE_MODE"
else
    echo '应用服务处理：跳过，仅执行数据库升级和迁移'
fi

require_ready
backup_app_image
load_app_image
echo "新版镜像：$APP_IMAGE"
validate_compose_image
stop_service
backup_database
run_upgrade
run_migrate
run_verify
start_service

log '部署完成，数据库升级验证通过'
