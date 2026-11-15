#!/usr/bin/env bash
set -euo pipefail
[[ $# == 2 && -f $1/Dockerfile && -n $2 ]] || { echo "Usage: $0 BUILD_CONTEXT IMAGE" >&2; exit 2; }
export BUILD_CONTEXT="$(cd "$1" && pwd)"
export RELEASE_IMAGE="$2"
manifest="$(mktemp)"
trap 'rm -f -- "$manifest"' EXIT
cat >"$manifest" <<'YAML'
services:
  candidate:
    image: ${RELEASE_IMAGE}
    build:
      context: ${BUILD_CONTEXT}
YAML
docker compose -f "$manifest" build candidate
