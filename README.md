# CommSpot

A self-hosted voice and text chat app for hanging out with friends.

- **Client:** desktop app for Windows and Linux (C#, .NET 10, Avalonia)
- **Server:** ASP.NET Core on Linux or Windows, behind the shared nginx + Cloudflare (HTTPS)
- **Voice:** Opus audio over encrypted UDP (AES-256-GCM)

Security comes first. Read these before changing anything:

- [docs/SECURITY_DESIGN.md](docs/SECURITY_DESIGN.md): threat model and defenses
- [docs/ATTACK_SURFACE.md](docs/ATTACK_SURFACE.md): every exposed port and endpoint
- [SECURITY.md](SECURITY.md): how to report a vulnerability

Server setup: [deploy/README.md](deploy/README.md)
