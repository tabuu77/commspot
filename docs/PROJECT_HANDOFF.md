# CommSpot Project Handoff

## Purpose
A self-hosted Discord-style voice and text app for the owner and one friend at first.
Domain: commspot.net (Cloudflare). VPS: Hostinger KVM 2, Ubuntu 24.04.

## Decisions (don't revisit without the owner)
- **Security first:** no feature work skips SECURITY_DESIGN.md or ATTACK_SURFACE.md.
- **Stack:** C# / .NET 10 everywhere. **Avalonia** desktop client (Windows + Linux), ASP.NET Core server, SignalR for control and chat, Opus audio over AES-256-GCM UDP (port 50000), nginx (shared with Metal Covenant) + Cloudflare orange cloud for TLS on 443. VPS shared with Metal Covenant: see docs/SHARED_VPS.md.
- **Distribution:** small launcher downloaded from commspot.net, which fetches app files using a `manifest.json` with SHA-256 per file and an **Ed25519 signature** made with an offline key (current and next key pinned).
- **VPS changes** are delivered as copy-paste scripts the owner runs. Claude does not SSH in.
- Audio: PortAudioSharp2 (bundled PortAudio for Windows + Linux) and Concentus (managed Opus). Opus 48 kHz mono, 20 ms frames, 32 kbps.
- Step 1 auth is one shared access key (see SECURITY_DESIGN section 5); real accounts come in Step 2.
- Releases: push a tag `v*`; `.github/workflows/release.yml` builds server + Windows/Linux clients with SHA256SUMS. The VPS installs with `deploy/install-server.sh`.

## Status
| Step | State |
|---|---|
| 0. Security baseline (docs, hardening, scans, DRP) | **Done.** VPS set up 2026-11-07 via setup-shared-vps.sh; commspot.net live on HTTPS (Cloudflare Full strict, DNS-01 cert); external scan green (only 22/tcp open, TLS OK). Backups on hold by owner's choice |
| 1. Voice MVP (server + Avalonia client) | **Code done**, 17 tests pass (incl. end-to-end two-client voice). Client UI checked on Linux; live audio not yet tested on real hardware. Deploy with install-server.sh |
| 2. Accounts, servers, channels, text chat | Not started |
| 3. Extras (presence, push-to-talk, screen share) | Not started |
| 4. Polish, launcher/updater, installer | Not started |

## Key files
- `docs/SECURITY_DESIGN.md`: threat model and controls
- `docs/ATTACK_SURFACE.md`: port allowlist (parsed by the weekly scan)
- `docs/DISASTER_RECOVERY.md`: RTO/RPO, backups, runbooks
- `src/`: CommSpot.Protocol (packet crypto, hub contracts), CommSpot.Server, CommSpot.Client.Core (voice logic), CommSpot.Client (Avalonia UI + audio), CommSpot.Bot (test tone bot). `tests/CommSpot.Tests`
- `deploy/`: install-server.sh (installs a release), setup-shared-vps.sh (current box), harden-vps.sh (fresh box only), nginx site, systemd units, backup script
- `.github/workflows/`: `ci` (build + test), `release` (tag v*), `ci-security` (gitleaks + vulnerable deps), `security-scan` (weekly nmap + testssl)

## Access notes
- VPS: from the owner's Windows PC, `"C:\Program Files\Git\usr\bin\ssh.exe" mc2684` (user crow, passwordless sudo). The PC's Claude permission rules block writes over ssh, so server-changing commands are given to the owner as one PowerShell line.
- Cloudflare token (DNS Edit, Zone Read, Zone Settings Edit; commspot.net only; IP-locked to the VPS v4+v6) lives at `/etc/letsencrypt/commspot-cloudflare.ini`.
- DNS: `commspot.net` and `www` are proxied, and `voice` is DNS-only (UDP).

## Next steps
1. Deploy v0.1.0 to the VPS and test voice with the friend (Windows zip from the GitHub release).
2. Fix anything found in real-hardware testing, then Step 2 (accounts, servers, channels, text chat).
3. Backups: on hold; when resumed, choose off-site storage and enable `commspot-backup.timer`.
