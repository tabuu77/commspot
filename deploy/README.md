# Setting up the CommSpot VPS (Ubuntu 24.04)

Takes about 15 minutes. Keep your current SSH session open the whole time.

## 1. Put your SSH key on the VPS (from your PC)

```bash
# Skip the first line if you already have ~/.ssh/id_ed25519
ssh-keygen -t ed25519
ssh-copy-id root@<VPS-IP>
```

## 2. Run the hardening script (on the VPS, as root)

```bash
ssh root@<VPS-IP>
apt-get update && apt-get install -y git
git clone https://github.com/tabuu77/commspot.git /root/commspot
bash /root/commspot/deploy/harden-vps.sh <your-admin-name>
```

The script stops halfway and asks you to test logging in as your new admin user in a
**second terminal**. Only type `yes` once that works. After this, root login and
passwords are disabled, and you log in with `ssh <your-admin-name>@<VPS-IP>`.

> Private repo? Use a GitHub deploy key or copy the `deploy/` folder over with `scp -r`.

## 3. DNS in Cloudflare

Add two **A records** pointing at the VPS IP, both **DNS only (grey cloud)**:

| Name | Purpose |
|---|---|
| `commspot.net` (`@`) | Download page and updates |
| `voice` | App API and voice |

Caddy gets HTTPS certificates automatically once DNS resolves.

## 4. Hostinger panel (optional extra layer)

In hPanel → VPS → **Firewall**, allow only 22/tcp, 443/tcp, and 50000/udp. That way
traffic is filtered before it even reaches the server.

## 5. Backups

Create `/etc/commspot/backup.env` (see the top of `backup.sh` for the format), then:

```bash
sudo chmod 600 /etc/commspot/backup.env
sudo systemctl enable --now commspot-backup.timer
sudo /opt/commspot-deploy/backup.sh   # first run, to confirm it works
```

Store the `RESTIC_PASSWORD` in your password manager. Without it, backups can't be restored.

## 6. Check it from the outside

On GitHub: **Actions → security-scan → Run workflow**. It should pass with
only the ports listed in `docs/ATTACK_SURFACE.md` open.
