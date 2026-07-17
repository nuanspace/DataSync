#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

[[ -f .env ]] || { echo "缺少 .env。" >&2; exit 1; }
set -a
# shellcheck disable=SC1091
source .env
set +a

query() {
  local container="$1" user="$2" database="$3" password_file="$4" sql="$5"
  local password
  password="$(tr -d '\r\n' < "$password_file")"
  docker exec -e PGPASSWORD="$password" "$container" psql -XAt --username "$user" --dbname "$database" --set ON_ERROR_STOP=on --command "$sql"
}

check_table() {
  local container="$1" user="$2" database="$3" password_file="$4" table="$5"
  local exists
  exists="$(query "$container" "$user" "$database" "$password_file" "SELECT to_regclass('$table') IS NOT NULL;")"
  [[ "$exists" == "t" ]] || { echo "缺少表：$table" >&2; exit 1; }
}

ds_container="s7-followup-datasync-db"
ds_user="${DATASYNC_DB_USER:-postgres}"
ds_database="${DATASYNC_DB_NAME:-datasync}"
ds_password="$root/secrets/datasync_db_password"

cube_container="s7-followup-cube-db"
cube_user="${CUBE_DB_USER:-postgres}"
cube_database="${CUBE_DB_NAME:-cube}"
cube_password="$root/secrets/cube_db_password"

for table in \
  cyyy.followup_package_source_config \
  cyyy.followup_package_pull_state \
  cyyy.followup_package_ack_queue \
  cyyy.followup_package_pull_log \
  lhyy.followup_package_import_state \
  lhyy.followup_package_schema_check \
  lhyy.followup_package_backup_record \
  lhyy.followup_package_restore_record \
  lhyy.followup_package_import_log; do
  check_table "$ds_container" "$ds_user" "$ds_database" "$ds_password" "$table"
done

state_rows="$(query "$ds_container" "$ds_user" "$ds_database" "$ds_password" "
  SELECT
    (SELECT count(*) FROM cyyy.followup_package_pull_state) +
    (SELECT count(*) FROM cyyy.followup_package_ack_queue) +
    (SELECT count(*) FROM cyyy.followup_package_pull_log) +
    (SELECT count(*) FROM lhyy.followup_package_import_state) +
    (SELECT count(*) FROM lhyy.followup_package_backup_record) +
    (SELECT count(*) FROM lhyy.followup_package_restore_record) +
    (SELECT count(*) FROM lhyy.followup_package_import_log);")"
[[ "$state_rows" == "0" ]] || { echo "DataSync 基础库包含运行历史（记录数=$state_rows）。" >&2; exit 1; }

datasync_tables="$(query "$ds_container" "$ds_user" "$ds_database" "$ds_password" "SELECT count(*) FROM pg_tables WHERE schemaname IN ('cyyy','lhyy');")"
[[ "$datasync_tables" -ge 9 ]] || { echo "DataSync 表数量异常：$datasync_tables" >&2; exit 1; }

cube_tables="$(query "$cube_container" "$cube_user" "$cube_database" "$cube_password" "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema');")"
[[ "$cube_tables" -ge 100 ]] || { echo "Cube 表数量异常：$cube_tables" >&2; exit 1; }

vector_installed="$(query "$cube_container" "$cube_user" "$cube_database" "$cube_password" "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname='vector');")"
[[ "$vector_installed" == "t" ]] || { echo "Cube 缺少 vector 扩展。" >&2; exit 1; }

echo "数据库验证通过：DataSync表=$datasync_tables，Cube表=$cube_tables，运行历史=0。"
