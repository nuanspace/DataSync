#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

[[ -s secrets/datasync_db_password ]] || { echo "datasync_db_password 未填写。" >&2; exit 1; }
[[ -s secrets/cube_db_password ]] || { echo "cube_db_password 未填写。" >&2; exit 1; }
[[ -f config/cyyy/appsettings.Production.json ]] || { echo "缺少 CYYY 生产配置。" >&2; exit 1; }
[[ -f config/lhyy/appsettings.Production.json ]] || { echo "缺少 LHYY 生产配置。" >&2; exit 1; }

if grep -R -n '<填写' config/cyyy/appsettings.Production.json config/lhyy/appsettings.Production.json; then
  echo "生产配置仍有占位符。" >&2
  exit 1
fi

docker compose config --quiet
docker compose up -d
docker compose ps
