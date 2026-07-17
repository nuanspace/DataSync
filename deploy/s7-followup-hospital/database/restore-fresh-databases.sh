#!/usr/bin/env bash
set -euo pipefail

target="${1:-}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if [[ "$target" != "datasync" && "$target" != "cube" ]]; then
  echo "用法：bash database/restore-fresh-databases.sh datasync|cube" >&2
  exit 2
fi

[[ -f .env ]] || { echo "缺少 .env，请先运行 install.sh 并填写配置。" >&2; exit 1; }
set -a
# shellcheck disable=SC1091
source .env
set +a

if [[ "$target" == "datasync" ]]; then
  container="s7-followup-datasync-db"
  database="${DATASYNC_DB_NAME:-datasync}"
  user="${DATASYNC_DB_USER:-postgres}"
  password_file="$root/secrets/datasync_db_password"
  dump_pattern="$root/database/datasync-base-*.dump"
else
  container="s7-followup-cube-db"
  database="${CUBE_DB_NAME:-cube}"
  user="${CUBE_DB_USER:-postgres}"
  password_file="$root/secrets/cube_db_password"
  dump_pattern="$root/database/cube-base-*.dump"
fi

[[ -s "$password_file" ]] || { echo "密码文件为空：$password_file" >&2; exit 1; }
mapfile -t dumps < <(compgen -G "$dump_pattern" || true)
[[ "${#dumps[@]}" -eq 1 ]] || { echo "必须且只能找到一个 $target 基础 dump，当前 ${#dumps[@]} 个。" >&2; exit 1; }
dump_file="${dumps[0]}"

docker inspect "$container" >/dev/null 2>&1 || { echo "数据库容器不存在：$container" >&2; exit 1; }
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$container")" == "healthy" ]] || { echo "数据库容器未健康：$container" >&2; exit 1; }

password="$(tr -d '\r\n' < "$password_file")"
table_count="$(docker exec -e PGPASSWORD="$password" "$container" psql -XAt --username "$user" --dbname "$database" --set ON_ERROR_STOP=on --command "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema');")"
[[ "$table_count" == "0" ]] || { echo "目标数据库不是空库（用户表=$table_count），拒绝覆盖。" >&2; exit 1; }

remote_dump="/tmp/s7-${target}-base.dump"
cleanup() { docker exec "$container" rm -f "$remote_dump" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker cp "$dump_file" "$container:$remote_dump"
docker exec -e PGPASSWORD="$password" "$container" pg_restore --list "$remote_dump" >/dev/null
docker exec -e PGPASSWORD="$password" "$container" pg_restore \
  --exit-on-error \
  --no-owner \
  --no-privileges \
  --username "$user" \
  --dbname "$database" \
  "$remote_dump"

echo "$target 基础库恢复完成：$(basename "$dump_file")"
