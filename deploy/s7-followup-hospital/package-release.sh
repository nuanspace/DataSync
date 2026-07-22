#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$#" -lt 4 || "$#" -gt 5 ]]; then
  echo "用法：bash package-release.sh 输出目录 DataSync基础dump Cube基础dump 文档目录 [发布环境文件]" >&2
  exit 2
fi

output="$1"
datasync_dump="$2"
cube_dump="$3"
docs_source="$4"
release_env="${5:-$root/.env.example}"
cube_v2_migration="$root/../../DataSync.LHYY.V2/Scripts/202607/20260722.sql"

command -v docker >/dev/null 2>&1 || { echo "未找到 docker。" >&2; exit 1; }
command -v sha256sum >/dev/null 2>&1 || { echo "未找到 sha256sum。" >&2; exit 1; }
command -v pg_restore >/dev/null 2>&1 || { echo "未找到 pg_restore。" >&2; exit 1; }
[[ -f "$datasync_dump" ]] || { echo "DataSync 基础 dump 不存在：$datasync_dump" >&2; exit 1; }
[[ -f "$cube_dump" ]] || { echo "Cube 基础 dump 不存在：$cube_dump" >&2; exit 1; }
[[ -d "$docs_source" ]] || { echo "实施文档目录不存在：$docs_source" >&2; exit 1; }
[[ ! -L "$docs_source" ]] || { echo "实施文档目录不允许符号链接：$docs_source" >&2; exit 1; }
[[ -f "$release_env" ]] || { echo "发布环境文件不存在：$release_env" >&2; exit 1; }
[[ -f "$cube_v2_migration" ]] || { echo "缺少 Cube v2 迁移脚本：$cube_v2_migration" >&2; exit 1; }
[[ ! -e "$output" ]] || { echo "输出目录已存在，拒绝覆盖：$output" >&2; exit 1; }
docs_source="$(cd "$docs_source" && pwd -P)"

validate_schema_only_dump() {
  local dump_path="$1"
  local dump_list
  if ! dump_list="$(pg_restore --list "$dump_path")"; then
    echo "基础 dump 不是可读取的 PostgreSQL 归档：$dump_path" >&2
    exit 1
  fi
  if grep -Eq '; [0-9]+ [0-9]+ (TABLE DATA|MATERIALIZED VIEW DATA|SEQUENCE SET|BLOB|BLOBS)( |$)' <<<"$dump_list"; then
    echo "基础 dump 包含业务数据条目，必须使用 pg_dump --schema-only 重新生成：$dump_path" >&2
    exit 1
  fi
}

validate_cube_v2_dump() {
  local dump_path="$1" schema_sql source_map_sql scope_map_sql required
  if ! schema_sql="$(pg_restore --schema-only --no-owner --no-privileges --file=- "$dump_path")"; then
    echo "无法读取 Cube 基础 dump 结构：$dump_path" >&2
    exit 1
  fi

  for required in \
    'CREATE TABLE public\.patient ' \
    'CREATE TABLE care\.patient_event ' \
    'CREATE TABLE form\.form_project ' \
    'CREATE TABLE datasync\.followup_patient_source_map ' \
    'CREATE TABLE public\.patient_data_scope_map ' \
    'CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA form;'; do
    grep -Eq "$required" <<<"$schema_sql" || { echo "Cube 基础 dump 缺少 v2 结构：$required" >&2; exit 1; }
  done

  source_map_sql="$(sed -n '/^CREATE TABLE datasync\.followup_patient_source_map (/,/^);/p' <<<"$schema_sql")"
  for required in patient_id original_source_type hospital_code first_package_id last_package_id created_at updated_at; do
    grep -Eq "^[[:space:]]+$required[[:space:]]" <<<"$source_map_sql" \
      || { echo "Cube 基础 dump 缺少字段：datasync.followup_patient_source_map.$required" >&2; exit 1; }
  done

  scope_map_sql="$(sed -n '/^CREATE TABLE public\.patient_data_scope_map (/,/^);/p' <<<"$schema_sql")"
  for required in id created_time patient_id hospital_id department_id ward_id project_id; do
    grep -Eq "^[[:space:]]+$required[[:space:]]" <<<"$scope_map_sql" \
      || { echo "Cube 基础 dump 缺少字段：public.patient_data_scope_map.$required" >&2; exit 1; }
  done
}

validate_release_docs() {
  local docs_path="$1"
  local invalid_path

  invalid_path="$(find "$docs_path" -mindepth 1 -type l -print -quit)"
  if [[ -n "$invalid_path" ]]; then
    echo "实施文档不允许符号链接：$invalid_path" >&2
    exit 1
  fi

  invalid_path="$(find "$docs_path" -mindepth 1 -name '.*' -print -quit)"
  if [[ -n "$invalid_path" ]]; then
    echo "实施文档不允许隐藏路径：$invalid_path" >&2
    exit 1
  fi

  invalid_path="$(find "$docs_path" -mindepth 1 ! -type d ! -type f -print -quit)"
  if [[ -n "$invalid_path" ]]; then
    echo "实施文档包含不支持的文件类型：$invalid_path" >&2
    exit 1
  fi

  while IFS= read -r -d '' document; do
    case "${document,,}" in
      *.md|*.pdf|*.doc|*.docx|*.xls|*.xlsx|*.ppt|*.pptx|*.txt|*.png|*.jpg|*.jpeg|*.svg) ;;
      *)
        echo "实施文档包含不支持的文件类型：$document" >&2
        exit 1
        ;;
    esac
  done < <(find "$docs_path" -type f -print0)
}

ensure_output_outside_docs() {
  local docs_path="$1"
  local output_parent_path="$2"
  case "$output_parent_path/" in
    "$docs_path/"*)
      echo "输出目录不能位于实施文档目录内：$output_parent_path" >&2
      exit 1
      ;;
  esac
}

validate_schema_only_dump "$datasync_dump"
validate_schema_only_dump "$cube_dump"
validate_cube_v2_dump "$cube_dump"
validate_release_docs "$docs_source"

unset RELEASE_VERSION CYYY_IMAGE LHYY_IMAGE DATASYNC_DB_IMAGE CUBE_DB_IMAGE
set -a
# shellcheck disable=SC1090
source "$release_env"
set +a

: "${CYYY_IMAGE:?发布环境文件缺少 CYYY_IMAGE}"
: "${LHYY_IMAGE:?发布环境文件缺少 LHYY_IMAGE}"
: "${DATASYNC_DB_IMAGE:?发布环境文件缺少 DATASYNC_DB_IMAGE}"
: "${CUBE_DB_IMAGE:?发布环境文件缺少 CUBE_DB_IMAGE}"
: "${RELEASE_VERSION:?发布环境文件缺少 RELEASE_VERSION}"

for image in "$CYYY_IMAGE" "$LHYY_IMAGE" "$DATASYNC_DB_IMAGE" "$CUBE_DB_IMAGE"; do
  docker image inspect "$image" >/dev/null 2>&1 || { echo "本机缺少待打包镜像：$image" >&2; exit 1; }
done

output_parent="$(dirname "$output")"
mkdir -p "$output_parent"
output_parent="$(cd "$output_parent" && pwd -P)"
output="$output_parent/$(basename "$output")"
ensure_output_outside_docs "$docs_source" "$output_parent"
stage="$(mktemp -d "$output_parent/.s7-followup-release.XXXXXX")"
cleanup() { [[ -d "$stage" ]] && rm -rf -- "$stage"; }
trap cleanup EXIT

mkdir -p "$stage/config/cyyy" "$stage/config/lhyy" "$stage/database" "$stage/docs" "$stage/images" "$stage/manifest" "$stage/postgres-cube" "$stage/secrets"
{
  printf 'RELEASE_VERSION=%s\n' "$RELEASE_VERSION"
  printf 'CYYY_IMAGE=%s\n' "$CYYY_IMAGE"
  printf 'LHYY_IMAGE=%s\n' "$LHYY_IMAGE"
  printf 'DATASYNC_DB_IMAGE=%s\n' "$DATASYNC_DB_IMAGE"
  printf 'CUBE_DB_IMAGE=%s\n' "$CUBE_DB_IMAGE"
  awk '!/^(RELEASE_VERSION|CYYY_IMAGE|LHYY_IMAGE|DATASYNC_DB_IMAGE|CUBE_DB_IMAGE)=/' "$root/.env.example"
} > "$stage/.env.example"
cp "$root/docker-compose.yml" "$root/install.sh" "$root/start.sh" "$root/status.sh" "$root/stop.sh" "$root/README.md" "$root/package-release.sh" "$stage/"
cp "$root/config/cyyy/appsettings.Production.json.example" "$stage/config/cyyy/"
cp "$root/config/lhyy/appsettings.Production.json.example" "$stage/config/lhyy/"
cp "$root/database/restore-fresh-databases.sh" "$stage/database/"
cp "$root/database/verify-fresh-databases.sh" "$stage/database/"
cp "$cube_v2_migration" "$stage/database/20260722-cube-v2.sql"
cp "$root/postgres-cube/Dockerfile" "$stage/postgres-cube/"
cp "$root/secrets/README.md" "$stage/secrets/"

while IFS= read -r -d '' docs_directory; do
  relative_directory="${docs_directory#"$docs_source"}"
  mkdir -p "$stage/docs$relative_directory"
done < <(find "$docs_source" -type d -print0)
while IFS= read -r -d '' document; do
  relative_document="${document#"$docs_source"/}"
  cp -- "$document" "$stage/docs/$relative_document"
done < <(find "$docs_source" -type f -print0)

safe_version="${RELEASE_VERSION//[^A-Za-z0-9._-]/_}"
cp "$datasync_dump" "$stage/database/datasync-base-${safe_version}.dump"
cp "$cube_dump" "$stage/database/cube-base-${safe_version}.dump"

while IFS= read -r -d '' script; do
  sed -i 's/\r$//' "$script"
  bash -n "$script"
done < <(find "$stage" -type f -name '*.sh' -print0)

declare -A saved_images=()
image_index=0
for image in "$CYYY_IMAGE" "$LHYY_IMAGE" "$DATASYNC_DB_IMAGE" "$CUBE_DB_IMAGE"; do
  if [[ -n "${saved_images[$image]:-}" ]]; then
    continue
  fi
  saved_images[$image]=1
  ((image_index += 1))
  image_file="$(printf 'image-%02d-%s.tar' "$image_index" "${image//[^A-Za-z0-9._-]/_}")"
  docker save -o "$stage/images/$image_file" "$image"
done

(
  cd "$stage"
  find . -type f ! -path './manifest/SHA256SUMS.txt' -print0 \
    | sort -z \
    | xargs -0 sha256sum > manifest/SHA256SUMS.txt
  sha256sum -c manifest/SHA256SUMS.txt >/dev/null
)

mv "$stage" "$output"
trap - EXIT
echo "离线部署包已生成：$output"
