#!/usr/bin/env bash
set -euo pipefail

STAGING_DIR="${1:-}"
TARGET_DIR="${2:-}"
SERVICE_NAME="${3:-mc-agent}"

if [[ -z "$STAGING_DIR" || -z "$TARGET_DIR" ]]; then
  echo "Usage: $0 <staging_dir> <target_dir> [service_name]" >&2
  exit 1
fi

if [[ ! -d "$STAGING_DIR" ]]; then
  echo "Staging directory not found: $STAGING_DIR" >&2
  exit 1
fi

# Give the current agent process a moment to flush and exit if needed.
sleep 2

mkdir -p "$TARGET_DIR"
rsync -a --delete "$STAGING_DIR"/ "$TARGET_DIR"/

systemctl restart "$SERVICE_NAME"
