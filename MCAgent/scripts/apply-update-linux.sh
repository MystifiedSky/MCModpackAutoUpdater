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

# Resolve links before comparing paths. The normal self-update staging directory is
# inside TARGET_DIR/updates/staged, so that subtree must survive --delete.
STAGING_DIR="$(realpath -e -- "$STAGING_DIR")"
TARGET_DIR="$(realpath -m -- "$TARGET_DIR")"
if [[ "$TARGET_DIR" == "/" || "$STAGING_DIR" == "/" || "$STAGING_DIR" == "$TARGET_DIR" ]]; then
  echo "Refusing unsafe staging or target directory." >&2
  exit 1
fi
if [[ "$TARGET_DIR" == "$STAGING_DIR"/* ]]; then
  echo "Target directory cannot be inside staging." >&2
  exit 1
fi
if [[ "$STAGING_DIR" == "$TARGET_DIR"/* && "$STAGING_DIR" != "$TARGET_DIR"/updates/* ]]; then
  echo "Staging inside target must be under the protected updates directory." >&2
  exit 1
fi

# Excludes also protect destination files from rsync --delete. Keep deployment
# credentials and durable journals outside publish assets, ideally in /state.
rsync_filters=(
  --exclude=/updates/
  --exclude=/state/
  --exclude=/private/
  --exclude='/appsettings.*.json'
  --exclude='**/appsettings.*.json'
  --exclude='/agent-command-state*.json'
  --exclude='**/agent-command-state*.json'
  --exclude='/agent-command-state*.json.tmp'
  --exclude='**/agent-command-state*.json.tmp'
  --exclude=/agent-command-state*.json.lock
  --exclude='**/agent-command-state*.json.lock'
)

# Optional colon-separated paths relative to TARGET_DIR for custom journal or
# configuration locations. Refuse traversal and absolute values before rsync.
if [[ -n "${MC_AGENT_PRESERVE_PATHS:-}" ]]; then
  IFS=':' read -r -a extra_preserved <<< "$MC_AGENT_PRESERVE_PATHS"
  for preserved in "${extra_preserved[@]}"; do
    if [[ -z "$preserved" || "$preserved" == /* || "$preserved" == *'..'* || "$preserved" == *'*'* || "$preserved" == *'?'* ]]; then
      echo "Invalid MC_AGENT_PRESERVE_PATHS entry: $preserved" >&2
      exit 1
    fi
    rsync_filters+=("--exclude=/$preserved" "--exclude=/${preserved%/}.tmp" "--exclude=/${preserved%/}.lock")
  done
fi

# Give the current agent process a moment to flush and exit if needed.
sleep 2

mkdir -p "$TARGET_DIR"
rsync -a --delete "${rsync_filters[@]}" "$STAGING_DIR"/ "$TARGET_DIR"/

systemctl restart "$SERVICE_NAME"
