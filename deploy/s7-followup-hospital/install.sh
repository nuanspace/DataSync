#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

command -v docker >/dev/null 2>&1 || { echo "未找到 docker。" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "未找到 Docker Compose v2。" >&2; exit 1; }

if [[ ! -f manifest/SHA256SUMS.txt ]]; then
  echo "缺少 manifest/SHA256SUMS.txt。" >&2
  exit 1
fi

sha256sum -c manifest/SHA256SUMS.txt

if [[ ! -f .env ]]; then
  cp .env.example .env
fi
chmod 0600 .env

set -a
# shellcheck disable=SC1091
source .env
set +a

data_root="${DATA_ROOT:-/data/s7-followup}"
cyyy_uid="${CYYY_CONTAINER_UID:-1654}"

install -d -m 0750 "$data_root"
install -d -m 0750 "$data_root/packages" "$data_root/logs" "$data_root/logs/cyyy" "$data_root/logs/lhyy"
install -d -m 0700 "$data_root/datasync-db" "$data_root/cube-db" "$data_root/staging" "$data_root/backups" "$data_root/uploads"
install -d -m 0700 "$data_root/secrets" "$data_root/secrets/cyyy" "$data_root/secrets/lhyy"

if [[ ! -f config/cyyy/appsettings.Production.json ]]; then
  cp config/cyyy/appsettings.Production.json.example config/cyyy/appsettings.Production.json
fi
if [[ ! -f config/lhyy/appsettings.Production.json ]]; then
  cp config/lhyy/appsettings.Production.json.example config/lhyy/appsettings.Production.json
fi

for secret in datasync_db_password cube_db_password; do
  if [[ ! -f "secrets/$secret" ]]; then
    install -m 0600 /dev/null "secrets/$secret"
  fi
done

chmod 0600 config/cyyy/appsettings.Production.json config/lhyy/appsettings.Production.json
chmod 0600 secrets/datasync_db_password secrets/cube_db_password
chown -R "$cyyy_uid:$cyyy_uid" "$data_root/packages" "$data_root/logs/cyyy" "$data_root/secrets/cyyy"
chown "$cyyy_uid:$cyyy_uid" config/cyyy/appsettings.Production.json

for image_tar in images/*.tar; do
  [[ -f "$image_tar" ]] || { echo "没有找到镜像 tar。" >&2; exit 1; }
  docker load -i "$image_tar"
done

echo "医院端安装文件和镜像已准备完成。"
echo "下一步：填写 .env、两个 appsettings.Production.json 和 secrets/*，然后只启动数据库并恢复基础库。"
