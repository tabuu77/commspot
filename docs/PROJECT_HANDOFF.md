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
| 0. Security baseline (docs, hardening, scans, DRP) | Written, PR open. Scripts are lint-checked but **not yet run on the VPS** |
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

## Next steps
1. Owner runs `deploy/README.md` on the VPS and sets the DNS records.
2. Run the security-scan workflow manually to confirm the baseline.
3. Start Step 1: voice MVP.
