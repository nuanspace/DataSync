#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

[[ -f .env ]] || { echo "缺少 .env。" >&2; exit 1; }
set -a
# shellcheck disable=SC1091
source .env
set +a

[[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]] || {
  echo "verify-fresh-databases.sh 只允许在 fresh-cube 模式执行。" >&2
  exit 1
}

query() {
  local container="$1" user="$2" database="$3" container_password_file="$4" sql="$5"
  docker exec "$container" sh -c \
    'export PGPASSWORD="$(tr -d "\r\n" < "$1")"; shift; exec "$@"' \
    sh "$container_password_file" \
    psql -XAt --username "$user" --dbname "$database" --set ON_ERROR_STOP=on --command "$sql"
}

check_table() {
  local container="$1" user="$2" database="$3" password_file="$4" table="$5"
  local exists
  exists="$(query "$container" "$user" "$database" "$password_file" "SELECT to_regclass('$table') IS NOT NULL;")"
  [[ "$exists" == "t" ]] || { echo "缺少表：$table" >&2; exit 1; }
}

check_columns() {
  local container="$1" user="$2" database="$3" password_file="$4" schema="$5" table="$6"
  shift 6
  local column exists
  for column in "$@"; do
    exists="$(query "$container" "$user" "$database" "$password_file" "
      SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = '$schema' AND table_name = '$table' AND column_name = '$column');")"
    [[ "$exists" == "t" ]] || { echo "缺少字段：$schema.$table.$column" >&2; exit 1; }
  done
}

ds_container="s7-followup-datasync-db"
ds_user="${DATASYNC_DB_USER:-postgres}"
ds_database="${DATASYNC_DB_NAME:-datasync}"
ds_password="/run/secrets/datasync_db_password"

cube_container="s7-followup-cube-db"
cube_user="${CUBE_DB_USER:-postgres}"
cube_database="${CUBE_DB_NAME:-cube}"
cube_password="/run/secrets/cube_db_password"

[[ -s "$root/secrets/datasync_db_password" ]] || { echo "DataSyncDb 密码文件为空。" >&2; exit 1; }
[[ -s "$root/secrets/cube_db_password" ]] || { echo "CubeDb 密码文件为空。" >&2; exit 1; }

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

vector_installed="$(query "$cube_container" "$cube_user" "$cube_database" "$cube_password" "
  SELECT EXISTS (
    SELECT 1 FROM pg_extension extension
    INNER JOIN pg_namespace namespace ON namespace.oid = extension.extnamespace
    WHERE extension.extname='vector' AND namespace.nspname='form');")"
[[ "$vector_installed" == "t" ]] || { echo "Cube 缺少 form schema 下的 vector 扩展。" >&2; exit 1; }

for table in public.patient care.patient_event form.form_project datasync.followup_patient_source_map public.patient_data_scope_map; do
  check_table "$cube_container" "$cube_user" "$cube_database" "$cube_password" "$table"
done

check_columns "$cube_container" "$cube_user" "$cube_database" "$cube_password" \
  datasync followup_patient_source_map \
  patient_id original_source_type hospital_code first_package_id last_package_id created_at updated_at
check_columns "$cube_container" "$cube_user" "$cube_database" "$cube_password" \
  public patient_data_scope_map \
  id created_time patient_id hospital_id department_id ward_id project_id

echo "数据库验证通过：DataSync表=$datasync_tables，Cube表=$cube_tables，运行历史=0。"
