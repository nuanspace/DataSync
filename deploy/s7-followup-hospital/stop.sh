#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"
# shellcheck disable=SC1091
source "$root/deployment-mode.sh"
load_deployment_mode
s7_compose stop
