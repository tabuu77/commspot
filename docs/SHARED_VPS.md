# Shared VPS Registry (srv2129622, Hostinger KVM 2)

This VPS runs **two projects**: Metal Covenant and CommSpot. Copied to
`/home/crow/SHARED-VPS.md` on the server.

**Rules for both projects**
1. Read this file before changing anything on the server, and update it after.
2. Only ever **add** firewall rules, each with a comment naming its project. Never `ufw reset`.
3. nginx is shared. Each project owns only its own site files. Always run `nginx -t` before `reload`, and never `restart`.
4. Don't restart shared pieces (nginx, ufw, ssh, docker) while the other project is deploying or running a test server. Announce it in both projects first.
5. Each project's service runs as its own system user, with memory and CPU limits.

## Ports

| Port | Proto | Owner | Exposure | What |
|---|---|---|---|---|
| 22 | tcp | shared | Internet (key only) | SSH |
| 80, 443 | tcp | shared (nginx) | Cloudflare IPs only | Web for both projects |
| 7777 | udp | Metal Covenant | Internet | Game server |
| 7778 | udp | Metal Covenant | Internet (only while test runs) | Test server |
| 8001 | tcp | Metal Covenant | 127.0.0.1 | mcadmin (Django) |
| 1985, 35803 | tcp | Metal Covenant | Blocked by ufw | Unreal server internals |
| 5100 | tcp | CommSpot | 127.0.0.1 | CommSpot API / SignalR |
| 50000 | udp | CommSpot | Internet | CommSpot voice (AES-GCM) |

## Services and users

| Service | User | Owner |
|---|---|---|
| mc2684, mc2684-test | mcserver | Metal Covenant |
| mcadmin | mcadmin | Metal Covenant |
| commspot, commspot-backup.timer | commspot | CommSpot |
| nginx, fail2ban, unattended-upgrades, certbot.timer | root | shared |

## Folders

| Path | Owner |
|---|---|
| /opt/mc2684, /opt/mc2684-test, /opt/mcadmin, /var/lib/mcadmin, /var/www/mc2684 | Metal Covenant |
| /opt/commspot, /etc/commspot, /var/lib/commspot, /srv/commspot, /opt/commspot-deploy | CommSpot |

## nginx sites

| File | Owner | Hosts |
|---|---|---|
| 00-catchall | shared | default (rejects unknown hosts) |
| mc2684, mcadmin | Metal Covenant | mc2684.net, www, admin.mc2684.net |
| commspot | CommSpot | commspot.net, www.commspot.net |

## Resources (2 vCPU, 8 GB RAM, 2 GB swap)
The game servers have priority. CommSpot is capped with `MemoryMax=1G` and `CPUQuota=50%`.

## Change log

| Date | Project | Change |
|---|---|---|
| 2026-11-07 | CommSpot | Added user/folders, ufw 50000/udp, nginx site `commspot`, 2 GB swapfile |
