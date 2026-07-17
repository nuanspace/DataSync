#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"
docker compose ps
docker compose logs --tail 100 datasync-cyyy datasync-lhyy-v2
