#!/usr/bin/env bash
# CommSpot VPS hardening for Ubuntu 24.04 LTS.
# Run as root:  bash harden-vps.sh <admin-username>
# Safe to re-run. Refuses to lock SSH down until key login is confirmed.
set -euo pipefail

ADMIN_USER="${1:-}"
VOICE_UDP_PORT=50000
DOMAIN="voice.commspot.net"

say()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root (sudo bash $0 <admin-username>)."
[[ -n "$ADMIN_USER" ]] || die "Usage: bash $0 <admin-username>"
[[ "$ADMIN_USER" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "Invalid username."
. /etc/os-release
[[ "$ID" == "ubuntu" && "$VERSION_ID" == "24.04" ]] || die "This script targets Ubuntu 24.04 (found $PRETTY_NAME)."

# ---------------------------------------------------------------- 1. admin user
say "1/9 Admin user '$ADMIN_USER' with sudo and SSH key"
if ! id "$ADMIN_USER" &>/dev/null; then
  adduser --disabled-password --gecos "" "$ADMIN_USER"
fi
usermod -aG sudo "$ADMIN_USER"
if passwd -S "$ADMIN_USER" | awk '{print $2}' | grep -qE '^(L|NP)$'; then
  echo "Set a sudo password for $ADMIN_USER (used for sudo only, never for SSH):"
  passwd "$ADMIN_USER"
fi

ADMIN_HOME=$(getent passwd "$ADMIN_USER" | cut -d: -f6)
install -d -m 700 -o "$ADMIN_USER" -g "$ADMIN_USER" "$ADMIN_HOME/.ssh"
AUTH_KEYS="$ADMIN_HOME/.ssh/authorized_keys"
if [[ ! -s "$AUTH_KEYS" && -s /root/.ssh/authorized_keys ]]; then
  cp /root/.ssh/authorized_keys "$AUTH_KEYS"
fi
touch "$AUTH_KEYS"; chown "$ADMIN_USER:$ADMIN_USER" "$AUTH_KEYS"; chmod 600 "$AUTH_KEYS"
grep -qE '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-|sk-)' "$AUTH_KEYS" || die \
"No SSH public key for $ADMIN_USER.
On your PC run:  ssh-copy-id $ADMIN_USER@<vps-ip>   (or paste your .pub key into $AUTH_KEYS)
then re-run this script."

# ---------------------------------------------------------- 2. lockout guard
say "2/9 Lockout check"
cat <<EOF
Open a NEW terminal on your PC and run:
    ssh $ADMIN_USER@$(hostname -I | awk '{print $1}')
then run 'sudo true' there. Keep THIS session open.
EOF
read -rp "Did key login AND sudo work? Type 'yes' to continue: " ok
[[ "$ok" == "yes" ]] || die "Stopped before changing SSH. Nothing locked down."

# ------------------------------------------------------------- 3. packages
say "3/9 Updating system and installing security tools"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get -y -q full-upgrade
apt-get -y -q install ufw fail2ban unattended-upgrades apt-listchanges \
  debian-keyring debian-archive-keyring apt-transport-https curl gnupg

# ------------------------------------------------------------------ 4. SSH
say "4/9 Hardening SSH (keys only, no root login)"
# 00- prefix so it wins over cloud-init's 50-cloud-init.conf (first match wins).
cat > /etc/ssh/sshd_config.d/00-commspot-hardening.conf <<EOF
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey
PermitEmptyPasswords no
MaxAuthTries 3
LoginGraceTime 30
X11Forwarding no
AllowAgentForwarding no
AllowTcpForwarding no
AllowUsers $ADMIN_USER
ClientAliveInterval 300
ClientAliveCountMax 2
EOF
sshd -t || die "sshd config test failed; not reloading."
systemctl reload ssh

# -------------------------------------------------------------- 5. firewall
say "5/9 Firewall: allow only 22/tcp (rate limited), 443/tcp, $VOICE_UDP_PORT/udp"
ufw --force reset >/dev/null
ufw default deny incoming
ufw default allow outgoing
ufw limit 22/tcp comment 'SSH'
ufw allow 443/tcp comment 'Caddy HTTPS'
ufw allow "$VOICE_UDP_PORT"/udp comment 'CommSpot voice'
ufw --force enable

# -------------------------------------------------------------- 6. fail2ban
say "6/9 fail2ban for SSH"
cat > /etc/fail2ban/jail.d/commspot.local <<'EOF'
[DEFAULT]
bantime  = 1h
bantime.increment = true
findtime = 10m
maxretry = 4
backend  = systemd

[sshd]
enabled = true
EOF
systemctl enable --now fail2ban
systemctl restart fail2ban

# ------------------------------------------------------ 7. automatic updates
say "7/9 Automatic security updates"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
cat > /etc/apt/apt.conf.d/52commspot-unattended <<'EOF'
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:00";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
EOF
systemctl enable --now unattended-upgrades

# ---------------------------------------------------------------- 8. kernel
say "8/9 Kernel network hardening"
cat > /etc/sysctl.d/99-commspot.conf <<'EOF'
net.ipv4.tcp_syncookies = 1
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.default.accept_redirects = 0
net.ipv6.conf.all.accept_redirects = 0
net.ipv6.conf.default.accept_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.conf.all.accept_source_route = 0
net.ipv6.conf.all.accept_source_route = 0
net.ipv4.conf.all.log_martians = 1
net.ipv4.icmp_echo_ignore_broadcasts = 1
kernel.kptr_restrict = 2
kernel.dmesg_restrict = 1
kernel.yama.ptrace_scope = 1
fs.protected_symlinks = 1
fs.protected_hardlinks = 1
EOF
sysctl --system >/dev/null

# ------------------------------------------------- 9. Caddy + service account
say "9/9 Caddy (HTTPS) and the locked-down 'commspot' service account"
if ! command -v caddy &>/dev/null; then
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
    | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
    > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -q && apt-get -y -q install caddy
fi
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$SCRIPT_DIR/Caddyfile" ]]; then
  install -m 644 "$SCRIPT_DIR/Caddyfile" /etc/caddy/Caddyfile
  caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
  systemctl reload caddy || systemctl restart caddy
fi

id commspot &>/dev/null || useradd --system --home /opt/commspot --shell /usr/sbin/nologin commspot
install -d -m 750 -o root -g commspot /opt/commspot
install -d -m 750 -o root -g commspot /etc/commspot
install -d -m 700 -o commspot -g commspot /var/lib/commspot
install -d -m 755 -o root -g root /srv/commspot/www /srv/commspot/www/download /srv/commspot/www/updates

# Backup tooling (timer is enabled only once /etc/commspot/backup.env exists).
apt-get -y -q install restic
install -d -m 750 -o root -g root /opt/commspot-deploy
install -m 750 "$SCRIPT_DIR/backup.sh" /opt/commspot-deploy/backup.sh
install -m 644 "$SCRIPT_DIR/commspot-backup.service" "$SCRIPT_DIR/commspot-backup.timer" /etc/systemd/system/
systemctl daemon-reload
if [[ -f /etc/commspot/backup.env ]]; then
  systemctl enable --now commspot-backup.timer
else
  echo "Backups: create /etc/commspot/backup.env (see deploy/README.md), then: systemctl enable --now commspot-backup.timer"
fi

say "Done. Summary"
ufw status verbose
echo
echo "SSH:      key-only login as '$ADMIN_USER'; root login disabled."
echo "Next:     in Cloudflare, point commspot.net and $DOMAIN (DNS only, grey cloud) at this server's IP."
