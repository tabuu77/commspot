# CommSpot Attack Surface Map

**Rule:** any PR that opens a port, adds an endpoint, adds a hub method, or
accepts new input must update this file. The weekly scan reads the
"Expected open ports" lines below and fails on anything extra.

Last reviewed: 2026-11-07 (shared VPS with Metal Covenant, no app code yet)

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
| 50000/udp | CommSpot voice relay | Internet | AES-GCM per session | Not built yet (Step 1) |

## Internal only (must NOT be reachable from outside)

| Port | Service | Bound to |
|---|---|---|
| 5100/tcp | CommSpot ASP.NET Core | 127.0.0.1 |
| 8001/tcp | Metal Covenant mcadmin | 127.0.0.1 |

## HTTP endpoints (via 443)

| Host / path | Method | Auth | Rate limit | Added in |
|---|---|---|---|---|
| commspot.net `/hub`, `/api/*` | — | Proxied to 5100, 10 r/s per IP | nginx limit_req | Step 0 (route only) |
| commspot.net `/` | GET | Public, static download page | nginx defaults | Step 0 |
| commspot.net `/download/*` | GET | Public, static launcher + SHA-256 | nginx defaults | Step 0 |
| commspot.net `/updates/*` | GET | Public, static; integrity via signed manifest | nginx defaults | Step 0 |

## SignalR hub methods

| Hub | Method | Auth | Authorization check | Added in |
|---|---|---|---|---|
| _none yet_ | | | | |

## Client-side inputs (things a malicious server or peer can send to the client)

| Input | Validation | Added in |
|---|---|---|
| `manifest.json` + `.sig` | Ed25519 verify, no downgrade, path allowlist | Step 0 (design) |
| Downloaded app files | SHA-256 must match signed manifest | Step 0 (design) |

## Third-party dependencies

| Package | Purpose | License |
|---|---|---|
| _none yet_ | | |
