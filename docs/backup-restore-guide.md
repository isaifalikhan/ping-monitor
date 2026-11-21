# Backup and restore guide

## What is backed up

One zip per backup, `netwatch-backup-YYYYMMDD-HHMMSS[-manual|-portable].zip`, with consistent copies of the
three databases (taken while NetWatch keeps running), `appsettings.json`, the certificate folder and a
`manifest.json` with a SHA-256 checksum of every file.

| Database | Content |
|---|---|
| `netwatch.db` | devices, credentials (encrypted), rules, alerts, users, settings, audit, schedules |
| `events.db` | device events, syslog, traps, Windows and DC security events |
| `metrics.db` | performance history (can be left out; usually the largest file) |

## Automatic backups

*Settings → System status → Backups*: daily at **02:00 PKT**, newest **14** kept (both editable). Store the
backups on another disk or a share (*Folder*; the service account needs write access, NetWatch checks it when
you save). The self-check raises an alert when the newest backup is older than 36 hours or the last one failed.
**Back up now** makes one immediately. Downloads are audited: a backup contains the user database and the
encrypted credentials.

## Credentials and a new server

Credential secrets are encrypted with Windows DPAPI for this server only. A normal backup restored on **another
server** therefore has unreadable credentials (they must be re-entered).

**Back up for another server…** asks for a password (at least 12 characters) and adds
`credentials.portable.json` to the backup: every secret encrypted with AES-256-GCM under a key derived from that
password (PBKDF2-SHA256, 600,000 iterations). The password is not stored anywhere — keep it with the backup
procedure. Restoring with the password re-encrypts the credentials for the new server.

## Restore from the console

*Backups* list → **Restore** → type `RESTORE` → (portable backups) the password. NetWatch checks the backup
(manifest, every checksum, the password) and changes nothing if anything is wrong. It then stops; the Windows
service restarts by itself within a minute (installer recovery settings) and restores the backup **before**
opening any database. The current files are kept in `C:\ProgramData\NetWatch\before-restore-<date>` (delete it
when satisfied). The audit trail of the restored database shows *BackupRestored* (or *RestoreFailed* with the
reason) and who requested it.

## Restore from the command line

```powershell
Stop-Service NetWatch
& "C:\Program Files\NetWatch\NetWatch.Service.exe" --restore "D:\Backups\netwatch-backup-20261121-020000-portable.zip" --restore-password
Start-Service NetWatch
```

`--restore-password` asks for the portable-credential password (omit it for a normal backup). The command
refuses to run while NetWatch runs on the same data folder.

## Moving to a new server

1. Install the same or a newer NetWatch version on the new server.
2. Restore a **portable** backup with its password (console or command line).
3. Certificate: a self-signed certificate is not portable; delete `C:\ProgramData\NetWatch\certs\` before the
   first start to get a new one, or import your internal-CA certificate (*Settings → System status → HTTPS
   certificate*).
4. Check *Settings → Notifications* (SMTP password) and run *Settings → Pilot tools* tests.
