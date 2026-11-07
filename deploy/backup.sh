#!/usr/bin/env bash
# Nightly encrypted off-site backup of CommSpot (see docs/DISASTER_RECOVERY.md).
# Needs /etc/commspot/backup.env (mode 600) with, for example:
#   RESTIC_REPOSITORY=s3:s3.us-west-000.backblazeb2.com/commspot-backups
#   RESTIC_PASSWORD=<long random password, also stored in your password manager>
#   AWS_ACCESS_KEY_ID=...
#   AWS_SECRET_ACCESS_KEY=...
set -euo pipefail

ENV_FILE=/etc/commspot/backup.env
[[ -r "$ENV_FILE" ]] || { echo "Missing $ENV_FILE" >&2; exit 1; }
[[ "$(stat -c %a "$ENV_FILE")" == "600" ]] || { echo "$ENV_FILE must be mode 600" >&2; exit 1; }
set -a
# shellcheck source=/dev/null
. "$ENV_FILE"
set +a

PATHS=(/var/lib/commspot /etc/commspot /etc/caddy /srv/commspot/www /etc/systemd/system)

restic snapshots >/dev/null 2>&1 || restic init

# Brief stop for a consistent database copy; always restart, even on failure.
was_active=0
if systemctl is-active --quiet commspot; then was_active=1; systemctl stop commspot; fi
trap '[[ $was_active -eq 1 ]] && systemctl start commspot' EXIT

restic backup --tag nightly --exclude-caches "${PATHS[@]}"

if [[ $was_active -eq 1 ]]; then systemctl start commspot; was_active=0; fi

restic forget --prune --keep-daily 7 --keep-weekly 4 --keep-monthly 6
if [[ "$(date +%u)" == "7" ]]; then restic check; fi
echo "Backup OK $(date -Is)"
