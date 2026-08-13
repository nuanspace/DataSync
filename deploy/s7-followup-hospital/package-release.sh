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

command -v docker >/dev/null 2>&1 || { echo "未找到 docker。" >&2; exit 1; }
command -v sha256sum >/dev/null 2>&1 || { echo "未找到 sha256sum。" >&2; exit 1; }
command -v pg_restore >/dev/null 2>&1 || { echo "未找到 pg_restore。" >&2; exit 1; }
[[ -f "$datasync_dump" ]] || { echo "DataSync 基础 dump 不存在：$datasync_dump" >&2; exit 1; }
[[ -f "$cube_dump" ]] || { echo "Cube 基础 dump 不存在：$cube_dump" >&2; exit 1; }
[[ -d "$docs_source" ]] || { echo "实施文档目录不存在：$docs_source" >&2; exit 1; }
[[ ! -L "$docs_source" ]] || { echo "实施文档目录不允许符号链接：$docs_source" >&2; exit 1; }
[[ -f "$release_env" ]] || { echo "发布环境文件不存在：$release_env" >&2; exit 1; }
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

validate_datasync_followup_dump() {
  local dump_path="$1" schema_sql identity_map_sql required
  if ! schema_sql="$(pg_restore --schema-only --no-owner --no-privileges --file=- "$dump_path")"; then
    echo "无法读取 DataSync 基础 dump 结构：$dump_path" >&2
    exit 1
  fi
  grep -Eq 'CREATE TABLE lhyy\.followup_patient_identity_map ' <<<"$schema_sql" \
    || { echo "DataSync 基础 dump 缺少 lhyy.followup_patient_identity_map。" >&2; exit 1; }
  identity_map_sql="$(sed -n '/^CREATE TABLE lhyy\.followup_patient_identity_map (/,/^);/p' <<<"$schema_sql")"
  for required in hospital_code source_patient_id target_patient_id source_unique_patient_id target_unique_patient_id identity_match_basis original_source_type first_package_id last_package_id created_at updated_at; do
    grep -Eq "^[[:space:]]+$required[[:space:]]" <<<"$identity_map_sql" \
      || { echo "DataSync 基础 dump 缺少字段：lhyy.followup_patient_identity_map.$required" >&2; exit 1; }
  done
  grep -Eq 'ADD CONSTRAINT uq_followup_patient_identity_map_target UNIQUE' <<<"$schema_sql" \
    || { echo "DataSync 基础 dump 缺少患者身份目标唯一约束。" >&2; exit 1; }
}

validate_cube_business_dump() {
  local dump_path="$1" schema_sql scope_map_sql required
  if ! schema_sql="$(pg_restore --schema-only --no-owner --no-privileges --file=- "$dump_path")"; then
    echo "无法读取 Cube 基础 dump 结构：$dump_path" >&2
    exit 1
  fi

  for required in \
    'CREATE TABLE public\.patient ' \
    'CREATE TABLE care\.patient_event ' \
    'CREATE TABLE form\.form_project ' \
    'CREATE TABLE public\.patient_data_scope_map ' \
    'CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA form;'; do
    grep -Eq "$required" <<<"$schema_sql" || { echo "Cube 基础 dump 缺少既有业务结构：$required" >&2; exit 1; }
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
validate_datasync_followup_dump "$datasync_dump"
validate_cube_business_dump "$cube_dump"
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
[[ "$RELEASE_VERSION" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "RELEASE_VERSION 只能包含字母、数字、点、下划线和连字符。" >&2; exit 1; }

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
  printf 'DEPLOYMENT_MODE=external-cube\n'
  printf 'CYYY_IMAGE=%s\n' "$CYYY_IMAGE"
  printf 'LHYY_IMAGE=%s\n' "$LHYY_IMAGE"
  printf 'DATASYNC_DB_IMAGE=%s\n' "$DATASYNC_DB_IMAGE"
  printf 'CUBE_DB_IMAGE=%s\n' "$CUBE_DB_IMAGE"
  awk '!/^(RELEASE_VERSION|DEPLOYMENT_MODE|CYYY_IMAGE|LHYY_IMAGE|DATASYNC_DB_IMAGE|CUBE_DB_IMAGE)=/' "$root/.env.example"
} > "$stage/.env.example"
cp "$root/docker-compose.yml" "$root/docker-compose.fresh-cube.yml" "$root/deployment-mode.sh" "$root/install.sh" "$root/start.sh" "$root/status.sh" "$root/stop.sh" "$root/README.md" "$root/package-release.sh" "$stage/"
cp "$root/config/cyyy/appsettings.Production.json.example" "$stage/config/cyyy/"
cp "$root/config/lhyy/appsettings.Production.json.example" "$stage/config/lhyy/"
cp "$root/config/lhyy/appsettings.Production.fresh-cube.json.example" "$stage/config/lhyy/"
cp "$root/database/restore-fresh-databases.sh" "$stage/database/"
cp "$root/database/verify-fresh-databases.sh" "$stage/database/"
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

docker save -o "$stage/images/datasync-cyyy-${safe_version}.tar" "$CYYY_IMAGE"
docker save -o "$stage/images/datasync-lhyy-v2-${safe_version}.tar" "$LHYY_IMAGE"
docker save -o "$stage/images/datasync-db-${safe_version}.tar" "$DATASYNC_DB_IMAGE"
docker save -o "$stage/images/cube-db-${safe_version}.tar" "$CUBE_DB_IMAGE"

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '%s' "$value"
}

csv_field() {
  local value="$1"
  value="${value//\"/\"\"}"
  printf '"%s"' "$value"
}

artifact_metadata() {
  local path="$1"
  required_for="all"
  purpose="部署文件"
  install_order="90"
  case "$path" in
    docker-compose.yml) required_for="external-cube"; purpose="现有目标数据库模式 Compose（不定义 CubeDb）"; install_order="20" ;;
    docker-compose.fresh-cube.yml) required_for="fresh-cube"; purpose="全新目标数据库模式 Compose"; install_order="20" ;;
    deployment-mode.sh|install.sh|start.sh|status.sh|stop.sh) purpose="按 DEPLOYMENT_MODE 执行的部署脚本"; install_order="30" ;;
    config/cyyy/*) purpose="CYYY 生产配置模板"; install_order="40" ;;
    config/lhyy/appsettings.Production.fresh-cube.json.example) required_for="fresh-cube"; purpose="LHYY 全新 CubeDb 配置模板"; install_order="40" ;;
    config/lhyy/*) required_for="external-cube"; purpose="LHYY 现有 CubeDb 配置模板"; install_order="40" ;;
    secrets/datasync_db_password) required_for="all"; purpose="DataSyncDb 密码占位文件"; install_order="45" ;;
    images/datasync-cyyy-*) purpose="CYYY 独立服务镜像"; install_order="10" ;;
    images/datasync-lhyy-v2-*) purpose="LHYY 独立服务镜像"; install_order="10" ;;
    images/datasync-db-*) purpose="DataSyncDb 独立数据库镜像"; install_order="10" ;;
    images/cube-db-*) required_for="fresh-cube"; purpose="全新 CubeDb 独立数据库镜像"; install_order="10" ;;
    database/datasync-base-*) purpose="DataSyncDb schema-only dump"; install_order="50" ;;
    database/cube-base-*) required_for="fresh-cube"; purpose="全新 CubeDb schema-only dump"; install_order="50" ;;
    database/restore-fresh-databases.sh) required_for="all"; purpose="DataSyncDb 恢复脚本；全新库模式也用于恢复 CubeDb"; install_order="60" ;;
    database/verify-fresh-databases.sh) required_for="fresh-cube"; purpose="全新 CubeDb 校验资产"; install_order="60" ;;
    docs/*|README.md) purpose="实施与验收文档"; install_order="80" ;;
    package-release.sh|postgres-cube/*) required_for="packager"; purpose="重新出包资产"; install_order="95" ;;
    .env.example) purpose="部署模式、镜像和端口参数模板"; install_order="35" ;;
  esac
}

{
  printf '{\n'
  printf '  "release": "%s",\n' "$(json_escape "$RELEASE_VERSION")"
  printf '  "packageType": "hospital",\n'
  printf '  "recommendedMode": "external-cube",\n'
  printf '  "supportedModes": ["external-cube", "fresh-cube"],\n'
  printf '  "containsProductionSecrets": false,\n'
  printf '  "images": {"cyyy": "%s", "lhyy": "%s", "datasyncDb": "%s", "freshCubeDb": "%s"},\n' \
    "$(json_escape "$CYYY_IMAGE")" "$(json_escape "$LHYY_IMAGE")" "$(json_escape "$DATASYNC_DB_IMAGE")" "$(json_escape "$CUBE_DB_IMAGE")"
  printf '  "catalog": "manifest/FILES.csv"\n'
  printf '}\n'
} > "$stage/manifest/package-manifest.json"

{
  printf 'path,sha256,requiredFor,purpose,order\n'
  while IFS= read -r -d '' artifact; do
    relative="${artifact#"$stage"/}"
    if [[ "$relative" == *','* || "$relative" == *'"'* || "$relative" == *$'\n'* || "$relative" == *$'\r'* ]]; then
      echo "交付文件路径不能包含逗号、双引号或换行：$relative" >&2
      exit 1
    fi
    artifact_metadata "$relative"
    csv_field "$relative"; printf ','
    csv_field "$(sha256sum "$artifact" | awk '{print $1}')"; printf ','
    csv_field "$required_for"; printf ','
    csv_field "$purpose"; printf ','
    csv_field "$install_order"; printf '\n'
  done < <(find "$stage" -type f ! -path "$stage/manifest/*" -print0 | sort -z)
} > "$stage/manifest/FILES.csv"

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
