# CommSpot Security Design

Status: Step 0 baseline (2026-11-07). Every feature PR must keep this doc and
[ATTACK_SURFACE.md](ATTACK_SURFACE.md) accurate.

Goal: defense in depth. No single mistake should expose users, their
messages, their voice, or the server.

## 1. What we protect

| Asset | Why it matters |
|---|---|
| Account credentials | Takeover of a user's identity |
| Voice and chat content | Privacy of conversations |
| Session tokens and voice keys | Impersonation, eavesdropping |
| The VPS itself | Full compromise of everything above |
| The release binaries and update signing key | A poisoned update attacks every user |

## 2. Who we defend against

1. **Internet scanners and bots**: constant SSH brute force, port scans, exploit spray.
2. **Network attackers**: anyone on the path (public Wi-Fi, ISP) who sniffs or tampers with traffic.
3. **Malicious users**: a logged-in account that sends malformed packets, floods, or tries to reach rooms it wasn't invited to.
4. **Supply chain**: a vulnerable or hijacked NuGet package or GitHub Action.

Out of scope for now: a fully compromised server operator reading voice (that
needs end-to-end encryption, see section 9) and large DDoS (needs an upstream provider).

## 3. Network layout

```
 Client ──TLS──▶ Cloudflare (orange) ──TLS──▶ nginx (443, Cloudflare IPs only) ──▶ 127.0.0.1:5100 CommSpot server
 Client ──AES-GCM UDP (50000/udp) via voice.commspot.net (grey cloud)──▶ CommSpot voice relay
 Admin  ──SSH key only (22/tcp)──▶ VPS
```

- The VPS is **shared with Metal Covenant** (see [SHARED_VPS.md](SHARED_VPS.md)). CommSpot only adds rules and files and never replaces shared config.
- The app's HTTP port binds to **localhost only**. The shared nginx is the only web entry point, and ufw lets only Cloudflare IPs reach 80/443.
- `commspot.net` is **proxied (orange cloud)**. Cloudflare SSL mode must be **Full (strict)**. nginx gets a Let's Encrypt cert through certbot.
- `voice.commspot.net` is **DNS-only (grey cloud)**, because Cloudflare's free proxy can't carry UDP. The VPS IP is already public through the game server, so the box is hardened accordingly.

## 4. Transport security

- **Control channel** (login, chat, presence): HTTPS and WSS through Cloudflare and nginx. TLS 1.2 is the minimum and TLS 1.3 is preferred. HSTS is on.
- **Voice (UDP):**
  - The server gives each client a fresh random 256-bit key and a session ID when it joins a voice channel. The key travels only over the TLS channel.
  - Packet: `[version 1B][sessionId 4B][counter 8B][AES-256-GCM ciphertext + 16B tag]`. The header is authenticated data.
  - The nonce comes from the direction plus a 64-bit counter, so a nonce is never reused with the same key.
  - Replay protection: a sliding window of 1024 packets. Old or duplicate counters are dropped.
  - Packets that fail authentication are dropped **before** any parsing or decoding.
  - Keys rotate on every rejoin and are wiped on disconnect.
- The server decrypts and re-encrypts per recipient (hop-by-hop, not end-to-end).
- **Screen sharing** uses the same keys and packet format with header version 2 (authenticated). Frames are JPEG, split into fragments. The server never decodes them: it forwards them only to members who chose to watch that sharer, only while the sharer is live, and limits each sharer to 1500 packets/s on its own token bucket, so video can't starve voice. Capture is Windows GDI (screens, or one window via PrintWindow); nothing is captured unless the user picks a source and clicks Go Live.

## 5. Accounts and sessions

**Step 1 (voice MVP, now):** there are no accounts yet. One shared access key
(`COMMSPOT_AccessKey`, 32 random characters in `/etc/commspot/secrets.env`) lets
a client join the single voice room. It is compared in constant time, failed joins
are limited to 5 per IP per 15 minutes, display names are validated, and the room
holds at most 10 people. The client never saves the key to disk. Everything below
replaces this in Step 2.

- Passwords are hashed with **Argon2id** (memory ≥ 64 MB, iterations ≥ 3) and a unique salt. Plaintext is never logged.
- Passwords must be at least 12 characters and are checked against a common-password list.
- Login limits: 5 attempts per account per 15 minutes, plus a per-IP limit. Locked-out and wrong-password responses look identical.
- **Access tokens** last 15 minutes. **Refresh tokens** last 30 days, rotate on each use, are stored only as hashes, and reuse revokes the whole family.
- Servers are **invite-only**. Invites expire and have a use limit.
- Every hub method checks that the caller is a member of the target server and channel. The client is never trusted for this.

## 6. Application hardening

- Runtime: .NET 10 (LTS, supported to Nov 2028). .NET 8 support ends 2026-11-10.
- Strict size limits: SignalR messages ≤ 32 KB, voice packets ≤ 1400 bytes, chat messages ≤ 4000 characters.
- Per-user and per-IP rate limits on every endpoint and on voice packets (60 packets/s per voice session, token bucket).
- All input is validated server-side. Queries are parameterized (EF Core). There's no dynamic SQL.
- Errors are generic. There are no stack traces in responses.
- Security events (logins, lockouts, invite use, auth failures) are logged without secrets.
- Runs as the `commspot` system user with systemd sandboxing and resource caps (see `deploy/commspot.service`).
- Client push-to-talk polls the state of the **one bound key** (GetAsyncKeyState on Windows, XQueryKeymap on X11). No keyboard hooks, nothing is recorded; all keys are scanned only while the user is picking a key.
- Secrets live in `/etc/commspot/secrets.env` (mode 600) and never in git.

## 7. VPS hardening

On the current shared VPS, the baseline below was already applied by Metal Covenant's hardening (Nov 5). CommSpot's additions come from `deploy/setup-shared-vps.sh`. `deploy/harden-vps.sh` is only for rebuilding on a **fresh, dedicated** server (DRP), and it refuses to run on the shared box.

- SSH: keys only, no root login, no passwords, MaxAuthTries 3. It refuses to run if no SSH key is installed, so you can't lock yourself out.
- Firewall (ufw): default deny incoming. 22/tcp, 80+443/tcp from Cloudflare only, and each project's own UDP ports.
- fail2ban on SSH.
- Unattended security upgrades with automatic reboot at 04:00 when needed.
- Kernel network hardening via sysctl (SYN cookies, no redirects, reverse-path filtering).

## 8. Supply chain and continuous checks

- **Dependabot** for NuGet and GitHub Actions, weekly.
- The CI audit fails on any known-vulnerable package (`dotnet list package --vulnerable --include-transitive`).
- **Gitleaks** secret scanning on every push and PR.
- **Weekly external scan** (`.github/workflows/security-scan.yml`):
  - Port-scans the VPS from the internet and **fails if any port is open that isn't in ATTACK_SURFACE.md**.
  - Checks the TLS configuration (testssl.sh) of the public site.
- Third-party GitHub Actions are pinned to full commit SHAs.
- Releases ship through the signed manifest (section 9).

## 9. Distribution and updates

**Now (v0.2+): the app updates itself.** On start and every 6 hours it fetches
`manifest.json` and `manifest.json.sig` from the latest GitHub release.
- The manifest lists version, download URL, SHA-256 and size per platform, and is signed with **Ed25519** by the release workflow.
- The app pins two public keys (current and next, in `UpdateKeys.cs`). An unsigned or wrongly signed manifest is rejected, and so is any version that isn't newer (no downgrades).
- Every redirect hop must be HTTPS on github.com or its download CDN. The download must match the signed size and SHA-256, and the archive may contain only the app executable.
- The update is swapped in by renaming the running exe to `.old`. If the swap fails, the old exe is put back. Nothing is installed until all checks pass.
- **Keys:** the current private key lives only in the GitHub secret `UPDATE_SIGNING_KEY`. The next key is kept offline on the owner's PC and is used only to rotate. The VPS never holds a signing key.

The original plan below (separate launcher, files served from commspot.net) is for Step 4.

Modeled on how Discord ships: a small launcher, then the app downloads itself.

- `https://commspot.net/` serves a static download page with the **launcher** (a few MB) and its SHA-256.
- `https://commspot.net/updates/manifest.json` lists every app file: `path`, `size`, `sha256`, plus `version` and `minLauncherVersion`.
- `manifest.json.sig` is an **Ed25519 signature** over the exact manifest bytes.
  - The launcher has the public key built in. It refuses any manifest that fails verification or whose version is lower than the installed one (no downgrade attacks).
  - The launcher pins **two** public keys, *current* and *next*, so a leaked key can be rotated without reinstalling (see DISASTER_RECOVERY.md, scenario D).
  - The **private signing key never touches the VPS.** It lives offline on the developer's PC (backed up encrypted). Owning the server alone can't push a malicious update.
- Files are downloaded to a temp folder, checked against the SHA-256 in the signed manifest, and only then swapped into place atomically. Any mismatch aborts the whole update.
- File paths in the manifest are validated: relative only, no `..`, no absolute paths, and an allowlist of extensions. A bad manifest can't write outside the install folder.
- Updates are HTTPS only. The launcher pins the domain and rejects redirects to other hosts.
- The update folder on the VPS is static, read-only content. There's no upload endpoint on the server, and releases are pushed over SSH.

## 10. Known gaps and roadmap

| Gap | Plan |
|---|---|
| Voice isn't end-to-end encrypted | Evaluate MLS-based E2EE after the feature MVP |
| No DDoS protection for UDP | Rate limits for now; upstream filtering if needed |
| Launcher .exe not Authenticode-signed (SmartScreen warning) | Code-signing certificate before sharing beyond testers |
| Single VPS | Nightly encrypted off-site backups + rebuild runbook in [DISASTER_RECOVERY.md](DISASTER_RECOVERY.md) |
