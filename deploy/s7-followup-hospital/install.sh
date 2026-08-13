#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

command -v docker >/dev/null 2>&1 || { echo "未找到 docker。" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "未找到 Docker Compose v2。" >&2; exit 1; }

if [[ ! -f .env ]]; then
  cp .env.example .env
fi
chmod 0600 .env

set -a
# shellcheck disable=SC1091
source .env
set +a

case "${DEPLOYMENT_MODE:-external-cube}" in
  external-cube|fresh-cube) ;;
  *) echo "DEPLOYMENT_MODE 只允许 external-cube 或 fresh-cube。" >&2; exit 1 ;;
esac

for manifest_file in manifest/package-manifest.json manifest/FILES.csv manifest/SHA256SUMS.txt; do
  [[ -f "$manifest_file" ]] || { echo "缺少交付清单：$manifest_file" >&2; exit 1; }
done

while IFS=, read -r csv_path _ csv_required _; do
  path="${csv_path#\"}"
  path="${path%\"}"
  required_for="${csv_required#\"}"
  required_for="${required_for%\"}"
  if [[ "$required_for" == "all" || "$required_for" == "${DEPLOYMENT_MODE:-external-cube}" ]]; then
    [[ -f "$path" ]] || { echo "当前模式缺少必需文件：$path" >&2; exit 1; }
  fi
done < <(tail -n +2 manifest/FILES.csv)

# 完整包校验全部文件；按 FILES.csv 独立取用时允许缺少另一模式和 packager 制品。
sha256sum --ignore-missing -c manifest/SHA256SUMS.txt

# shellcheck disable=SC1091
source "$root/deployment-mode.sh"
load_deployment_mode
validate_ntcare_uploads_path

data_root="${DATA_ROOT:-/data/s7-followup}"
cyyy_uid="${CYYY_CONTAINER_UID:-1654}"

install -d -m 0750 "$data_root"
install -d -m 0750 "$data_root/packages" "$data_root/logs" "$data_root/logs/cyyy" "$data_root/logs/lhyy"
install -d -m 0700 "$data_root/datasync-db" "$data_root/staging" "$data_root/backups"
install -d -m 0700 "$data_root/secrets" "$data_root/secrets/cyyy" "$data_root/secrets/lhyy"
if [[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]]; then
  install -d -m 0700 "$data_root/cube-db"
fi

if [[ ! -f config/cyyy/appsettings.Production.json ]]; then
  cp config/cyyy/appsettings.Production.json.example config/cyyy/appsettings.Production.json
fi
if [[ ! -f config/lhyy/appsettings.Production.json ]]; then
  if [[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]]; then
    cp config/lhyy/appsettings.Production.fresh-cube.json.example config/lhyy/appsettings.Production.json
  else
    cp config/lhyy/appsettings.Production.json.example config/lhyy/appsettings.Production.json
  fi
fi

required_secrets=(datasync_db_password)
if [[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]]; then
  required_secrets+=(cube_db_password)
fi
for secret in "${required_secrets[@]}"; do
  if [[ ! -f "secrets/$secret" ]]; then
    install -m 0600 /dev/null "secrets/$secret"
  fi
done

chmod 0600 config/cyyy/appsettings.Production.json config/lhyy/appsettings.Production.json
chmod 0600 "${required_secrets[@]/#/secrets/}"
chown -R "$cyyy_uid:$cyyy_uid" "$data_root/packages" "$data_root/logs/cyyy" "$data_root/secrets/cyyy"
chown "$cyyy_uid:$cyyy_uid" config/cyyy/appsettings.Production.json

for image_tar in images/*.tar; do
  [[ -f "$image_tar" ]] || { echo "没有找到镜像 tar。" >&2; exit 1; }
  docker load -i "$image_tar"
done
validate_ntcare_uploads_container_contract

echo "医院端安装文件和镜像已准备完成（模式：${DEPLOYMENT_MODE:-external-cube}）。"
if [[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]]; then
  echo "下一步：填写配置和 secrets/*，启动两个数据库，恢复并校验两个基础库。"
else
  echo "下一步：填写配置和 DataSyncDb 密码；现有 CubeDb 不需要包内密码、镜像或 dump。"
fi
