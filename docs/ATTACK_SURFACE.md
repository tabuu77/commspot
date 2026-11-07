# CommSpot Attack Surface Map

**Rule:** any PR that opens a port, adds an endpoint, adds a hub method, or
accepts new input must update this file. The weekly scan reads the
"Expected open ports" lines below and fails on anything extra.

Last reviewed: 2026-11-07 (Step 0, no app code yet)

## Expected open ports (internet-facing)

<!-- scan-allowlist: the workflow parses these two lines; keep the format -->
TCP: 22,443
UDP: 50000

| Port | Service | Exposure | Auth | Notes |
|---|---|---|---|---|
| 22/tcp | OpenSSH | Internet | SSH key only | ufw rate limit + fail2ban |
| 443/tcp | Caddy (TLS) | Internet | TLS; app auth behind it | Reverse proxy to 127.0.0.1:5000 |
| 50000/udp | Voice relay | Internet | AES-GCM per session | Not built yet (Step 1) |

## Internal only (must NOT be reachable from outside)

| Port | Service | Bound to |
|---|---|---|
| 5000/tcp | CommSpot ASP.NET Core | 127.0.0.1 |
| 2019/tcp | Caddy admin API | 127.0.0.1 (Caddy default) |

## HTTP endpoints (via 443)

| Host / path | Method | Auth | Rate limit | Added in |
|---|---|---|---|---|
| commspot.net `/` | GET | Public, static download page | Caddy defaults | Step 0 |
| commspot.net `/download/*` | GET | Public, static launcher + SHA-256 | Caddy defaults | Step 0 |
| commspot.net `/updates/*` | GET | Public, static; integrity via signed manifest | Caddy defaults | Step 0 |

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
