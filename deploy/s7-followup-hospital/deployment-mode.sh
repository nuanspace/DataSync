#!/usr/bin/env bash

load_deployment_mode() {
  [[ -f "$root/.env" ]] || { echo "缺少 .env。" >&2; return 1; }

  set -a
  # shellcheck disable=SC1091
  source "$root/.env"
  set +a

  case "${DEPLOYMENT_MODE:-external-cube}" in
    external-cube)
      compose_file="$root/docker-compose.yml"
      ;;
    fresh-cube)
      compose_file="$root/docker-compose.fresh-cube.yml"
      ;;
    *)
      echo "DEPLOYMENT_MODE 只允许 external-cube 或 fresh-cube。" >&2
      return 1
      ;;
  esac

  [[ -f "$compose_file" ]] || { echo "缺少部署文件：$compose_file" >&2; return 1; }
}

validate_ntcare_uploads_path() {
  local uploads_path="${NTCARE_UPLOADS_PATH:-}"
  [[ -n "$uploads_path" && "$uploads_path" != *'__REPLACE_WITH_NTCARE_UPLOADS_ABSOLUTE_PATH__'* ]] || {
    echo "NTCARE_UPLOADS_PATH 必须填写为 NTCare 实际 uploads 绝对路径。" >&2
    return 1
  }
  [[ "$uploads_path" == /* ]] || {
    echo "NTCARE_UPLOADS_PATH 必须是绝对路径。" >&2
    return 1
  }
  [[ -d "$uploads_path" && ! -L "$uploads_path" ]] || {
    echo "NTCARE_UPLOADS_PATH 必须是已存在且非符号链接的目录。" >&2
    return 1
  }
  [[ "$(readlink -f -- "$uploads_path")" == "$uploads_path" ]] || {
    echo "NTCARE_UPLOADS_PATH 的规范路径必须与配置值完全一致。" >&2
    return 1
  }
  [[ -r "$uploads_path" && -w "$uploads_path" ]] || {
    echo "NTCARE_UPLOADS_PATH 必须允许当前实施账号读写。" >&2
    return 1
  }
}

validate_ntcare_uploads_container_contract() {
  s7_compose run --rm --no-deps --entrypoint sh datasync-lhyy-v2 -eu -c '
    probe="$(mktemp -d /app/uploads/.datasync-hardlink-probe.XXXXXX)"
    cleanup() {
      rm -f -- "$probe/source" "$probe/claim" "$probe/published"
      rmdir -- "$probe"
    }
    trap cleanup EXIT
    printf probe > "$probe/source"
    ln "$probe/source" "$probe/claim"
    mv "$probe/claim" "$probe/published"
    test "$(stat -c %i "$probe/source")" = "$(stat -c %i "$probe/published")"
    test "$(cat "$probe/published")" = probe
  ' || {
    echo "NTCARE_UPLOADS_PATH 必须允许 LHYY 容器创建文件、同目录硬链接和原子发布。" >&2
    return 1
  }
}

s7_compose() {
  docker compose --env-file "$root/.env" --file "$compose_file" "$@"
}
