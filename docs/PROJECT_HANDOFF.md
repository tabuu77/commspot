# CommSpot Project Handoff

## Purpose
A self-hosted Discord-style voice and text app for the owner and one friend at first.
Domain: commspot.net (Cloudflare). VPS: Hostinger KVM 2, Ubuntu 24.04.

## Decisions (don't revisit without the owner)
- **Security first:** no feature work skips SECURITY_DESIGN.md or ATTACK_SURFACE.md.
- **Stack:** C# / .NET 8 everywhere. **Avalonia** desktop client (Windows + Linux), ASP.NET Core server, SignalR for control and chat, Opus audio over AES-256-GCM UDP (port 50000), nginx (shared with Metal Covenant) + Cloudflare orange cloud for TLS on 443. VPS shared with Metal Covenant: see docs/SHARED_VPS.md.
- **Distribution:** small launcher downloaded from commspot.net, which fetches app files using a `manifest.json` with SHA-256 per file and an **Ed25519 signature** made with an offline key (current and next key pinned).
- **VPS changes** are delivered as copy-paste scripts the owner runs. Claude does not SSH in.
- NAudio is Windows-only, so the Avalonia client needs a cross-platform audio library (to decide in Step 1).

## Status
| Step | State |
|---|---|
| 0. Security baseline (docs, hardening, scans, DRP) | **Done.** VPS set up 2026-11-07 via setup-shared-vps.sh; commspot.net live on HTTPS (Cloudflare Full strict, DNS-01 cert); external scan green (only 22/tcp open, TLS OK). Backups NOT enabled yet (need off-site storage + backup.env) |
| 1. Voice MVP (server + Avalonia client) | Not started |
| 2. Accounts, servers, channels, text chat | Not started |
| 3. Extras (presence, push-to-talk, screen share) | Not started |
| 4. Polish, launcher/updater, installer | Not started |

## Key files
- `docs/SECURITY_DESIGN.md`: threat model and controls
- `docs/ATTACK_SURFACE.md`: port allowlist (parsed by the weekly scan)
- `docs/DISASTER_RECOVERY.md`: RTO/RPO, backups, runbooks
- `deploy/`: setup-shared-vps.sh (current box), harden-vps.sh (fresh box only), nginx site, systemd units, backup script
- `.github/workflows/`: `ci-security` (gitleaks + vulnerable deps), `security-scan` (weekly nmap + testssl)

## Access notes
- VPS: from the owner's Windows PC, `"C:\Program Files\Git\usr\bin\ssh.exe" mc2684` (user crow, passwordless sudo). The PC's Claude permission rules block writes over ssh, so server-changing commands are given to the owner as one PowerShell line.
- Cloudflare token (DNS Edit, Zone Read, Zone Settings Edit; commspot.net only; IP-locked to the VPS v4+v6) lives at `/etc/letsencrypt/commspot-cloudflare.ini`.
- DNS: `commspot.net` and `www` are proxied, and `voice` is DNS-only (UDP).

## Next steps
1. Choose off-site backup storage and enable `commspot-backup.timer`.
2. Start Step 1: voice MVP (server + Avalonia client; pick a cross-platform audio library).
