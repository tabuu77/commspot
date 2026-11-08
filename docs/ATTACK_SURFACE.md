# CommSpot Attack Surface Map

**Rule:** any PR that opens a port, adds an endpoint, adds a hub method, or
accepts new input must update this file. The weekly scan reads the
"Expected open ports" lines below and fails on anything extra.

Last reviewed: 2026-11-07 (Step 1 voice MVP)

The VPS is shared; ports owned by Metal Covenant are listed so the scan knows about them. See [SHARED_VPS.md](SHARED_VPS.md).

## Expected open ports (internet-facing)

<!-- scan-allowlist: the workflow parses these two lines; keep the format -->
TCP: 22
UDP: 7777,7778,50000

| Port | Service | Exposure | Auth | Notes |
|---|---|---|---|---|
| 22/tcp | OpenSSH (shared) | Internet | SSH key only | fail2ban |
| 80, 443/tcp | nginx (shared) | **Cloudflare IPs only** | TLS; app auth behind it | Not in the scan allowlist because direct scans must see it closed |
| 7777/udp | Metal Covenant game | Internet | Game's own | Not CommSpot's |
| 7778/udp | Metal Covenant test | Internet, only during tests | Game's own | Not CommSpot's |
| 50000/udp | CommSpot voice relay | Internet | AES-GCM per session | Drops in order: bad size/header, unknown session, over 60 pkt/s (voice) or 1500 pkt/s and not sharing (screen share), failed auth, replay. Max 1400 B. Screen share packets go only to members watching that sharer |

## Internal only (must NOT be reachable from outside)

| Port | Service | Bound to |
|---|---|---|
| 5100/tcp | CommSpot ASP.NET Core | 127.0.0.1 |
| 8001/tcp | Metal Covenant mcadmin | 127.0.0.1 |

## HTTP endpoints (via 443)

| Host / path | Method | Auth | Rate limit | Added in |
|---|---|---|---|---|
| commspot.net `/hub` | WebSocket (SignalR) | Shared access key on `Join` | nginx 10 r/s per IP; 5 failed joins per IP per 15 min; 32 KB max message | Step 1 |
| commspot.net `/api/health` | GET | Public, returns `ok` | nginx 10 r/s per IP | Step 1 |
| commspot.net `/` | GET | Public, static download page | nginx defaults | Step 0 |
| commspot.net `/download/*` | GET | Public, static launcher + SHA-256 | nginx defaults | Step 0 |
| commspot.net `/updates/*` | GET | Public, static; integrity via signed manifest | nginx defaults | Step 0 |

## SignalR hub methods

| Hub | Method | Auth | Authorization check | Added in |
|---|---|---|---|---|
| `/hub` | `Join(AccessKey, DisplayName)` | Shared access key (constant-time compare) | Name validated (1-32 chars, letters/digits/space/-_.); room max 10 | Step 1 |
| `/hub` | `SetMuted(bool)` | Must have joined | Only changes the caller's own state | Step 1 |
| `/hub` | `SetSharing(bool)` | Must have joined | Only the caller's own state; stopping clears everyone watching them | v0.4 |
| `/hub` | `Watch(int userId)` | Must have joined | Only a member who is sharing (and not yourself); else 0 | v0.4 |

## Client-side inputs (things a malicious server or peer can send to the client)

| Input | Validation | Added in |
|---|---|---|
| `manifest.json` + `.sig` (GitHub latest release) | Ed25519 verify against 2 pinned keys, no downgrade, HTTPS + host allowlist on every redirect, 64 KB cap | v0.2 |
| Update archive | Signed size + SHA-256, only the app exe allowed inside, 300 MB cap | v0.2 |
| Downloaded app files | SHA-256 must match signed manifest | Step 0 (design) |
| Hub events (`MemberJoined/Left/Muted`) | Typed records; names shown as plain text | Step 1 |
| Screen share frames (JPEG) from server | AES-GCM auth, replay window, fragment bounds (max 1024 x 1359 B), newest-frame-only reassembly, decoded off the UI thread, max 4096 px per side, malformed frames skipped | v0.4 |
| Voice packets from server | AES-GCM auth (server direction), replay window, size limit, then Opus decode | Step 1 |
| Global key state (push-to-talk) | Only the bound key is read; local only, never sent | Step 3 (early) |
| Server address typed by user | `https://` only; plain `http://` allowed for loopback only | Step 1 |

## Third-party dependencies

| Package | Purpose | License |
|---|---|---|
| Microsoft.AspNetCore.SignalR.Client | Client hub connection | MIT |
| Avalonia (+ Themes.Fluent) | Desktop UI | MIT |
| PortAudioSharp2 | Mic and speaker I/O (bundles PortAudio) | Apache-2.0 / MIT |
| Concentus | Opus codec (managed) | BSD-3-Clause |
| BouncyCastle.Cryptography | Ed25519 update signatures | MIT |
| xunit (tests only) | Tests | Apache-2.0 |
