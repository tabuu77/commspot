#!/usr/bin/env bash
# CommSpot setup on the VPS it SHARES with Metal Covenant.
# Additive only: never resets ufw, never edits Metal Covenant's nginx sites,
# services, ports (7777/7778/8001) or folders. Safe to re-run.
# Run from the repo root on the VPS:  sudo bash deploy/setup-shared-vps.sh
set -euo pipefail

VOICE_UDP_PORT=50000
APP_PORT=5100
SWAP_SIZE=2G
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAMP=$(date +%Y%m%d-%H%M%S)

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run with sudo."
command -v nginx >/dev/null || die "nginx not found; this script expects the shared nginx."

# ---------------------------------------------------- 0. conflict checks
say "0/7 Checking for port conflicts with other projects"
for spec in "tcp:$APP_PORT" "udp:$VOICE_UDP_PORT"; do
  proto=${spec%%:*}; port=${spec##*:}
  flag=$([[ $proto == tcp ]] && echo -tlnpH || echo -ulnpH)
  if ss "$flag" "sport = :$port" | grep -q .; then
    ss "$flag" "sport = :$port" | grep -q 'CommSpot' || die "$proto/$port already in use by another program."
  fi
done
nginx -t 2>/dev/null || die "nginx config is already failing its test; fix that first, not touching it."

# ------------------------------------------- 1. service account and folders
say "1/7 Service user and folders"
id commspot &>/dev/null || useradd --system --home /opt/commspot --shell /usr/sbin/nologin commspot
install -d -m 750 -o root -g commspot /opt/commspot /etc/commspot
install -d -m 700 -o commspot -g commspot /var/lib/commspot
install -d -m 755 -o root -g root /srv/commspot/www /srv/commspot/www/download /srv/commspot/www/updates
install -d -m 750 -o root -g root /opt/commspot-deploy
[[ -f /srv/commspot/www/index.html ]] || cat > /srv/commspot/www/index.html <<'HTML'
<!doctype html><meta charset="utf-8"><title>CommSpot</title>
<h1>CommSpot</h1><p>Coming soon.</p>
HTML
install -m 644 "$REPO_DIR/deploy/commspot.service" /etc/systemd/system/commspot.service

# ------------------------------------------------------------- 2. firewall
say "2/7 Firewall: add voice UDP $VOICE_UDP_PORT (existing rules untouched)"
ufw status | grep -q "^$VOICE_UDP_PORT/udp" || ufw allow "$VOICE_UDP_PORT"/udp comment 'commspot voice'

# --------------------------------------------------------------- 3. swap
say "3/7 Swapfile ($SWAP_SIZE) if none exists"
if ! swapon --show | grep -q .; then
  fallocate -l "$SWAP_SIZE" /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo 'vm.swappiness = 10' > /etc/sysctl.d/99-commspot-swap.conf && sysctl -q -p /etc/sysctl.d/99-commspot-swap.conf
else
  echo "Swap already present, skipping."
fi

# ---------------------------------------------------------------- 4. nginx
say "4/7 nginx site for commspot.net (backup, test, reload)"
mkdir -p "/root/nginx-backups/$STAMP" && cp -a /etc/nginx/sites-available /etc/nginx/snippets "/root/nginx-backups/$STAMP/" 2>/dev/null || true
{
  echo "# Cloudflare edge IPs (generated $STAMP). Lets logs and rate limits see real client IPs."
  for u in https://www.cloudflare.com/ips-v4 https://www.cloudflare.com/ips-v6; do
    curl -fsS "$u" | sed 's/^/set_real_ip_from /; s/$/;/'
  done
  echo "real_ip_header CF-Connecting-IP;"
} > /etc/nginx/snippets/commspot-cloudflare-realip.conf.new
grep -q set_real_ip_from /etc/nginx/snippets/commspot-cloudflare-realip.conf.new || die "Could not fetch Cloudflare IP list."
mv /etc/nginx/snippets/commspot-cloudflare-realip.conf.new /etc/nginx/snippets/commspot-cloudflare-realip.conf
# Keep certbot's 443 additions if the site was already issued a cert.
if [[ ! -f /etc/nginx/sites-available/commspot ]] || ! grep -q 'managed by Certbot' /etc/nginx/sites-available/commspot; then
  install -m 644 "$REPO_DIR/deploy/nginx/commspot.conf" /etc/nginx/sites-available/commspot
fi
ln -sf /etc/nginx/sites-available/commspot /etc/nginx/sites-enabled/commspot
if ! nginx -t; then
  rm -f /etc/nginx/sites-enabled/commspot
  die "nginx test failed; CommSpot site disabled again, other sites untouched."
fi
systemctl reload nginx

# ----------------------------------------------------------- 5. certificate
# DNS-01 via Cloudflare: ufw only admits Cloudflare IPs on 80/443, so HTTP-01 can't reach us.
say "5/7 TLS certificate (certbot DNS-01 via Cloudflare)"
CF_INI=/etc/letsencrypt/commspot-cloudflare.ini
if [[ -d /etc/letsencrypt/live/commspot.net ]]; then
  echo "Certificate already exists."
elif [[ -f "$CF_INI" ]]; then
  chmod 600 "$CF_INI"
  dpkg -s python3-certbot-dns-cloudflare &>/dev/null || apt-get -y -q install python3-certbot-dns-cloudflare
  certbot certonly --dns-cloudflare --dns-cloudflare-credentials "$CF_INI" \
    --dns-cloudflare-propagation-seconds 30 -d commspot.net -d www.commspot.net \
    --non-interactive --agree-tos --register-unsafely-without-email
  certbot install --nginx --cert-name commspot.net --redirect --non-interactive
  nginx -t && systemctl reload nginx
else
  echo "No $CF_INI yet (Cloudflare token, Edit zone DNS for commspot.net); skipping certificate."
fi

# ---------------------------------------------------------------- 6. backups
say "6/7 Backup tooling"
command -v restic >/dev/null || apt-get -y -q install restic
install -m 750 "$REPO_DIR/deploy/backup.sh" /opt/commspot-deploy/backup.sh
install -m 644 "$REPO_DIR/deploy/commspot-backup.service" "$REPO_DIR/deploy/commspot-backup.timer" /etc/systemd/system/
systemctl daemon-reload
if [[ -f /etc/commspot/backup.env ]]; then systemctl enable --now commspot-backup.timer
else echo "Backups not enabled yet: /etc/commspot/backup.env missing."; fi

# ------------------------------------------------------------ 7. registry
say "7/7 Shared VPS registry"
install -m 644 "$REPO_DIR/docs/SHARED_VPS.md" /home/crow/SHARED-VPS.md 2>/dev/null || true
chown crow:crow /home/crow/SHARED-VPS.md 2>/dev/null || true

say "Done"
ufw status numbered | sed -n '1,40p'
