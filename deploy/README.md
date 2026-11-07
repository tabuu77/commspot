# Deploying CommSpot

## Current server: shared with Metal Covenant

The VPS (`srv2129622`, ssh alias `mc2684`, user `crow`) already has Metal Covenant's
hardening: key-only SSH, ufw, fail2ban, and auto updates. CommSpot only **adds** its pieces.
Read [docs/SHARED_VPS.md](../docs/SHARED_VPS.md) first.

```bash
ssh mc2684
git clone https://github.com/tabuu77/commspot.git ~/commspot   # or: git -C ~/commspot pull
sudo bash ~/commspot/deploy/setup-shared-vps.sh
```

What it does (all additive, safe to re-run):
- Creates the `commspot` user and its folders.
- Adds one firewall rule, `50000/udp` (voice).
- Adds a 2 GB swapfile if the box has none.
- Adds the nginx site `commspot`. It backs up the config, runs `nginx -t`, then reloads. Metal Covenant's sites are never touched.
- Gets the certificate through certbot once DNS resolves.
- Sets up backup tooling and copies the shared registry to `/home/crow/SHARED-VPS.md`.

### DNS in Cloudflare (commspot.net zone)

| Type | Name | Content | Proxy |
|---|---|---|---|
| A | `@` | VPS IP | **Proxied (orange)** |
| A | `www` | VPS IP | **Proxied (orange)** |
| A | `voice` | VPS IP | **DNS only (grey)**, needed for UDP voice |

Then in SSL/TLS → Overview, set the mode to **Full (strict)**.

Certificates use **DNS-01** (ufw blocks Let's Encrypt from reaching port 80). Save a Cloudflare API token (template "Edit zone DNS", zone commspot.net) as `dns_cloudflare_api_token=...` in `/etc/letsencrypt/commspot-cloudflare.ini` (mode 600), then re-run the setup script.

### Install or update the app server

After a release exists (push a tag like `v0.1.0`):

```bash
git -C ~/commspot pull
sudo bash ~/commspot/deploy/install-server.sh          # latest release, or pass a tag
```

It downloads the server, checks it against `SHA256SUMS`, keeps the old copy in
`/opt/commspot.prev`, creates `/etc/commspot/secrets.env` with a new access key the
first time (printed once), restarts the service and rolls back if the health check fails.

### Backups

Create `/etc/commspot/backup.env` (format at the top of `backup.sh`), `chmod 600` it,
then run `sudo systemctl enable --now commspot-backup.timer`.

## Fresh dedicated server (disaster recovery only)

`deploy/harden-vps.sh` does full hardening of a brand-new Ubuntu 24.04 box (it resets ufw).
It refuses to run on the shared server. See [docs/DISASTER_RECOVERY.md](../docs/DISASTER_RECOVERY.md).
