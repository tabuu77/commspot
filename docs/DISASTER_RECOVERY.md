# CommSpot Disaster Recovery Plan (DRP)

Status: Step 0 baseline (2026-11-07). Review whenever the deployment changes,
and **test a full restore every 3 months** (log it at the bottom).

## 1. Targets

| Metric | Target | Meaning |
|---|---|---|
| **RTO** (time to recover) | 2 hours | From "server is gone" to friends talking again |
| **RPO** (max data loss) | 24 hours | Nightly backups; at most one day of chat history lost |

## 2. What must survive (and where it lives)

| Item | Location | Backup | Rebuildable? |
|---|---|---|---|
| Database (accounts, servers, chat) | `/var/lib/commspot/` | Nightly encrypted restic, off-site | No, so back it up |
| Server secrets (JWT key, etc.) | `/etc/commspot/secrets.env` | Nightly restic | Yes, by rotating (forces everyone to log in again) |
| nginx site, systemd units | `/etc/nginx/sites-available/commspot`, `/etc/systemd/system/commspot*` | Git repo + restic | Yes, from git |
| Website and update files | `/srv/commspot/www/` | Git releases + restic | Yes, by re-publishing |
| **Update signing key (Ed25519 private)** | Offline, on the developer's PC | **2 encrypted copies**: USB drive + password manager | **No. Losing it means users must reinstall the launcher** |
| Restic repo password | Password manager | The password manager's own backups | No |
| Source code | GitHub | Local clones | Yes |
| Domain and DNS | Cloudflare account | Registrar lock on, 2FA on | Yes, manually |

## 3. Backups

- `deploy/backup.sh`, run nightly at 03:30 by `commspot-backup.timer`:
  - Stops the app for a consistent DB snapshot (a few seconds), backs up with **restic** (AES-256 encrypted, deduplicated) to an **off-site** repository, then restarts the app.
  - Retention: 7 daily, 4 weekly, 6 monthly.
  - Runs `restic check` on Sundays.
- Off-site target: Backblaze B2, an S3 bucket, or SFTP to a home machine. **Never the same VPS.**
- Extra layer: Hostinger's own VPS snapshots. Take one before every risky change.
- Backup credentials live in `/etc/commspot/backup.env` (mode 600). The repo password is also in the password manager, because without it the backups are useless.

## 4. Scenarios and runbooks

### A. VPS dead, deleted, or Hostinger outage
1. Create a new Ubuntu 24.04 VPS (Hostinger or any provider). If Metal Covenant moves too, rebuild it first per its own docs, then use `deploy/setup-shared-vps.sh`; otherwise use `harden-vps.sh`.
2. `git clone` the repo, then run `deploy/harden-vps.sh <admin>` (see deploy/README.md).
3. Install restic, recreate `/etc/commspot/backup.env`, then `restic restore latest --target /`.
4. Install the app (Step 1+ instructions) and run `systemctl enable --now commspot`.
5. In Cloudflare, point `commspot.net` and `voice.commspot.net` at the new IP.
6. Run the **security-scan** workflow manually to confirm only the expected ports are open.

### B. VPS compromised (or suspected)
1. **Don't fix it in place.** Take a Hostinger snapshot for forensics, then power it off.
2. Rotate everything the server knew: JWT signing secret (logs everyone out), backup credentials, and SSH keys.
3. Rebuild from scratch using runbook A, restoring from a backup taken **before** the suspected intrusion.
4. Users' passwords are Argon2id-hashed, but tell both users to change them anyway.
5. Updates stay safe because the signing key was never on the server. Verify that `/srv/commspot/www/updates` matches the signed release before reopening.
6. Write down what happened and what changed in section 6.

### C. Bad update shipped (app crashes or breaks)
1. Re-sign and publish the previous good build with a **higher** version number (launchers refuse downgrades).
2. Clients pick it up on their next launch.

### D. Update signing key leaked
1. The launcher ships with **two** public keys: current and next. The next private key is stored offline separately and never used until needed.
2. Publish a manifest signed with the *next* key that revokes the old one.
3. Generate a new "next" key and release a launcher containing it.

### E. Update signing key lost (no backup)
1. Generate a new key pair and build a new launcher with the new public key.
2. Both users download the new launcher from commspot.net and verify its SHA-256 over a separate channel (e.g. a text message).

### F. Cloudflare or domain account hijacked
1. Recover the account through Cloudflare support (2FA plus a registrar lock make this unlikely).
2. Meanwhile, users can still connect by IP. TLS will fail on IP, so the launcher refuses to update, which is the safe outcome.

### G. GitHub account compromised
1. Recover the account and revoke all tokens and deploy keys.
2. Review recent commits and releases. Update files are still protected by the offline signing key.

### H. Database corruption
1. Stop the app, then `restic restore` the last good snapshot of `/var/lib/commspot/`.
2. Start the app and verify logins work.

## 5. Contacts and accounts

| Service | Used for | 2FA |
|---|---|---|
| Hostinger | VPS | Required |
| Cloudflare | DNS for commspot.net | Required |
| GitHub | Code, CI, security scans | Required |
| Backup provider | Off-site backups | Required |

## 6. Restore test and incident log

| Date | What was tested or what happened | Result | Follow-ups |
|---|---|---|---|
| | | | |
