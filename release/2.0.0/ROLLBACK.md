# NetWatch 2.0.0 — rollback to the previous version

NetWatch upgrades its databases automatically when 2.0.0 starts for the first time. **A data folder that has been
opened by 2.0.0 cannot be used by 1.x again**: 1.x does not know the 2.0 schema. Rolling back therefore means
going back to a copy of the data taken *before* the upgrade. Data collected while 2.0.0 was running is lost in a
rollback (export the reports you need first).

## 1. Before upgrading (mandatory)

Run on the NetWatch server in an elevated PowerShell:

```
Stop-Service NetWatch
$stamp = Get-Date -Format yyyyMMdd-HHmm
robocopy C:\ProgramData\NetWatch "D:\NetWatch-pre-2.0-$stamp" /E /COPYALL /R:1 /W:1
& "C:\Program Files\NetWatch\NetWatch.Service.exe" --version
Start-Service NetWatch
```

- `robocopy` must end with exit code 0–7 (8 or higher = copy failed: do not upgrade).
- Note the version printed (for example `NetWatch 1.2.0`) and keep the matching installer
  (`NetWatch-Setup-1.2.0.exe`) next to the copy.
- If the old version is 1.4.0 you can additionally use *Settings → Backups → Back up now*; 1.2.0 has no backup
  function, so the folder copy above is the only rollback point.

## 2. Upgrade

Run `NetWatch-Setup-2.0.0.exe` as administrator. Check: service Running, console opens, devices and history present,
*Settings → System status* without errors. Keep the pre-upgrade copy for at least 30 days.

## 3. Decide to roll back

Roll back only for a blocking problem (service does not start, collection stopped, data wrong). First collect a
diagnostic bundle (*Settings → Pilot tools → Diagnostic bundle*) and the log folder
`C:\ProgramData\NetWatch\logs` for analysis.

## 4. Roll back

In an elevated PowerShell:

```
Stop-Service NetWatch
Rename-Item C:\ProgramData\NetWatch "NetWatch-2.0-failed-$(Get-Date -Format yyyyMMdd-HHmm)"
```

1. Uninstall NetWatch 2.0.0 (*Settings → Apps → NetWatch → Uninstall*), answer **No** to "delete all data"
   (the folder was already renamed).
2. Put the pre-upgrade copy back:
   `robocopy "D:\NetWatch-pre-2.0-<stamp>" C:\ProgramData\NetWatch /E /COPYALL /R:1 /W:1`
3. Install the previous version: `NetWatch-Setup-1.2.0.exe` (run as administrator). It finds the data folder and
   starts with it.
4. Check: `Get-Service NetWatch` is Running; console opens; devices, users and alert history are as before the
   upgrade; graphs continue (with a gap for the 2.0 period).
5. The installer recreates the firewall rules and the service. Credentials are still valid because the copy was taken
   on the same server (DPAPI).

**Never** start 1.x on a data folder that 2.0.0 has opened, and never copy only some of the `*.db` files: always the
whole folder (databases, `-wal`/`-shm` files, `certs`, `appsettings.json`).

## 5. After a rollback

- Keep the renamed `NetWatch-2.0-failed-…` folder and the diagnostic bundle for the analysis.
- To try 2.0.0 again later, start again at section 1 (take a fresh copy).
