#!/usr/bin/env bash
# Installs or updates the CommSpot server from a GitHub release, on the shared VPS.
# Additive: touches only /opt/commspot, /etc/commspot, /var/lib/commspot and the commspot unit.
# Usage:  sudo bash deploy/install-server.sh [tag]      (default: latest release)
set -euo pipefail

REPO=tabuu77/commspot
TAG=${1:-latest}
ASSET=commspot-server-linux-x64.tar.gz
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS=/etc/commspot/secrets.env

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run with sudo."
id commspot &>/dev/null || die "Run deploy/setup-shared-vps.sh first."

if [[ $TAG == latest ]]; then
  base="https://github.com/$REPO/releases/latest/download"
else
  base="https://github.com/$REPO/releases/download/$TAG"
fi

say "1/5 Downloading $ASSET ($TAG) and verifying SHA-256"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
curl -fsSL --proto '=https' -o "$work/$ASSET" "$base/$ASSET"
curl -fsSL --proto '=https' -o "$work/SHA256SUMS" "$base/SHA256SUMS"
(cd "$work" && grep " $ASSET\$" SHA256SUMS | sha256sum -c -) || die "Checksum mismatch, nothing installed."
mkdir "$work/x" && tar -C "$work/x" -xzf "$work/$ASSET"
[[ -x $work/x/CommSpot.Server ]] || die "Archive has no CommSpot.Server binary."

say "2/5 Secrets"
if [[ ! -f $SECRETS ]]; then
  key=$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 32)
  umask 027
  cat > "$SECRETS" <<EOF
COMMSPOT_AccessKey=$key
COMMSPOT_VoiceHost=voice.commspot.net
COMMSPOT_VoicePort=50000
EOF
  chown root:commspot "$SECRETS"; chmod 640 "$SECRETS"
  printf '\n\033[1;33mNew access key (share only with your friend, privately):\033[0m\n%s\n' "$key"
  echo "Show it again later with: sudo grep AccessKey $SECRETS"
else
  echo "Keeping existing $SECRETS."
fi

say "3/5 Installing to /opt/commspot (previous copy kept in /opt/commspot.prev)"
systemctl stop commspot 2>/dev/null || true
rm -rf /opt/commspot.prev
[[ -d /opt/commspot ]] && mv /opt/commspot /opt/commspot.prev
install -d -m 750 -o root -g commspot /opt/commspot
cp -a "$work/x/." /opt/commspot/
chown -R root:commspot /opt/commspot
chmod -R u=rwX,g=rX,o= /opt/commspot
chmod 750 /opt/commspot/CommSpot.Server
install -d -m 700 -o commspot -g commspot /var/lib/commspot /var/lib/commspot/.bundle
install -m 644 "$REPO_DIR/deploy/commspot.service" /etc/systemd/system/commspot.service

say "4/5 Starting the service"
systemctl daemon-reload
systemctl enable --now commspot >/dev/null 2>&1 || true
systemctl restart commspot

say "5/5 Health checks"
ok=0
for _ in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:5100/api/health >/dev/null 2>&1; then ok=1; break; fi
  sleep 1
done
if [[ $ok != 1 ]]; then
  journalctl -u commspot -n 40 --no-pager || true
  if [[ -d /opt/commspot.prev ]]; then
    echo "Rolling back to the previous version."
    rm -rf /opt/commspot && mv /opt/commspot.prev /opt/commspot && systemctl restart commspot
  fi
  die "Server did not become healthy."
fi
echo "HTTP health: OK"
ss -ulnH "sport = :50000" | grep -q . && echo "Voice UDP 50000: listening" || die "Voice UDP 50000 is not listening."
curl -fsS https://commspot.net/api/health >/dev/null 2>&1 && echo "Public https://commspot.net/api/health: OK" \
  || echo "WARNING: public health check failed (check nginx / Cloudflare)."
say "Done. CommSpot server is running."
