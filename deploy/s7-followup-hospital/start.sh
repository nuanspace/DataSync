#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

[[ -s secrets/datasync_db_password ]] || { echo "datasync_db_password 未填写。" >&2; exit 1; }
[[ -f config/cyyy/appsettings.Production.json ]] || { echo "缺少 CYYY 生产配置。" >&2; exit 1; }
[[ -f config/lhyy/appsettings.Production.json ]] || { echo "缺少 LHYY 生产配置。" >&2; exit 1; }

# shellcheck disable=SC1091
source "$root/deployment-mode.sh"
load_deployment_mode
if [[ "${DEPLOYMENT_MODE:-external-cube}" == "fresh-cube" ]]; then
  [[ -s secrets/cube_db_password ]] || { echo "fresh-cube 模式下 cube_db_password 未填写。" >&2; exit 1; }
fi

if grep -R -n '<填写' config/cyyy/appsettings.Production.json config/lhyy/appsettings.Production.json; then
  echo "生产配置仍有占位符。" >&2
  exit 1
fi

s7_compose config --quiet
if [[ "${DEPLOYMENT_MODE:-external-cube}" == "external-cube" ]]; then
  echo "正在对现有 CubeDb 执行只读兼容性检查……"
  s7_compose run --rm --no-deps datasync-lhyy-v2 cube-compat-check
fi
s7_compose up -d
s7_compose ps
