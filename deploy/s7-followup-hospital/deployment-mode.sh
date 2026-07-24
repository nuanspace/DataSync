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

s7_compose() {
  docker compose --env-file "$root/.env" --file "$compose_file" "$@"
}
