# NetWatch installation guide

NetWatch is delivered as one installer, `NetWatch-Setup-<version>.exe`. It installs one self-contained program
(`NetWatch.Service.exe`; no .NET runtime, IIS, SQL Server or other prerequisite) and registers it as the Windows
service **NetWatch**. Nothing is downloaded during installation or at run time.

## 1. Requirements

| Item | Requirement |
|---|---|
| Operating system | Windows Server 2019, 2022 or 2025 (or Windows 10 1809 / Windows 11), 64-bit |
| CPU / memory | 2 vCPU / 4 GB RAM for up to ~300 devices; 4 vCPU / 8 GB for ~1,000 devices |
| Disk | 20 GB free minimum on the data drive; see sizing below |
| Rights | Local Administrator, to run the installer |
| Browser (operator PCs) | Current Edge, Chrome or Firefox. Nothing has to be installed on operator PCs |

**Disk sizing.** Nearly all disk use is interface history. With 1-minute polling and the default retention, each
monitored interface uses about 4 MB once the history is full:

- raw samples, kept 7 days: about 0.06 MB per day (measured);
- 5-minute averages, kept 90 days: about 2 MB;
- hourly averages, kept 2 years: about 1.5 MB.

Disk use grows over the first months and then levels off:

| Monitored interfaces | Disk after 3 months | Disk after 2 years (steady state) |
|---|---|---|
| 2,000 | ~5 GB | ~8 GB |
| 10,000 | ~25 GB | ~40 GB |
| 25,000 | ~60 GB | ~100 GB |

Interfaces you mark as not monitored are not stored. You can shorten retention under *Settings → General*.

## 2. Network and firewall

| Direction | Protocol / port | From → to | Purpose |
|---|---|---|---|
| Inbound | TCP 8443 | operator PCs → NetWatch server | Web console (HTTPS). The installer creates this firewall rule |
| Inbound | UDP 162 | devices → NetWatch server | SNMP traps and informs. Firewall rule created by the installer |
| Inbound | UDP 514, TCP 514 | devices → NetWatch server | Syslog. Firewall rules created by the installer |
| Outbound | ICMP echo | NetWatch server → devices | Availability and latency |
| Outbound | UDP 161 | NetWatch server → switches, routers, servers | SNMP polling |
| Outbound | TCP 5985 (or 5986) | NetWatch server → Windows servers | WinRM (WMI over WS-Management). 5985 traffic is encrypted by Kerberos/NTLM |
| Outbound | TCP 135 + dynamic RPC | NetWatch server → Windows servers | Remote event logs; WMI over DCOM if WinRM is unavailable |
| Outbound | TCP 22 | NetWatch server → Linux servers | SSH |
| Outbound | TCP 443 | NetWatch server → vCenter / ESXi | vSphere API (read-only) |
| Outbound | TCP 636 (or 389) | NetWatch server → domain controllers | Optional: sign-in with AD accounts (LDAPS) |
| Outbound | TCP 389, 5985 (or 135 + RPC) | NetWatch server → domain controllers | DC monitoring: replication status over signed/sealed LDAP, Security/Directory Service logs, clock (WMI) |
| Inbound | TCP 5985 | domain controllers → NetWatch server | Optional: Windows Event Forwarding (WinRM) when DC events are pushed instead of read |
| Outbound | as configured | NetWatch server → SMTP relay / SIEM | Optional notification channels |

The receiver ports can be changed under *Settings → General → Syslog and SNMP trap receivers* (service restart
needed); adjust the firewall rules "NetWatch SNMP traps" and "NetWatch syslog UDP/TCP" accordingly.

On the network devices, allow SNMP from the NetWatch server's IP address only. Use an SNMP ACL or view on
Cisco, `snmp-server host`/community restrictions on HP/Aruba, and `snmp client-list` on Juniper. Configure a
read-only community or SNMPv3 user; NetWatch never writes to devices.

SNMPv3 with SHA-256 or better and AES is recommended. MD5, SHA-1 and DES remain selectable for older switches
that offer nothing else.

## 3. Install

1. Copy `NetWatch-Setup-<version>.exe` to the server (USB, or your internal file share).
2. Right-click it and choose **Run as administrator**. Accept the defaults. The program is installed in
   `C:\Program Files\NetWatch` and the data in `C:\ProgramData\NetWatch`.
3. The installer creates and starts the **NetWatch** service, sets it to *Automatic (Delayed Start)*, and makes
   it restart automatically after a failure. The final page shows the console address and where the first
   password is stored.
4. On the server, open `C:\ProgramData\NetWatch\initial-admin-password.txt` (only Administrators can read it).
5. Browse to `https://<server-name>:8443/` and sign in as `admin` with that password. You must choose a new
   password immediately. It needs at least 10 characters and 3 of: upper case, lower case, digit, symbol.
6. **Delete `initial-admin-password.txt`** after the first sign-in.

Unattended installation: `NetWatch-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=C:\Temp\nw-setup.log`

## 4. HTTPS certificate

On first start NetWatch creates a self-signed certificate. It is valid for the server's host name, FQDN,
`localhost` and its IPv4 addresses. Browsers will warn about it until it is trusted. Choose one of:

**A. Trust the self-signed certificate (simplest).** Distribute
`C:\ProgramData\NetWatch\certs\netwatch-selfsigned.cer` to operator PCs into *Trusted Root Certification
Authorities*, for example with a GPO: *Computer Configuration → Policies → Windows Settings → Security Settings
→ Public Key Policies → Trusted Root Certification Authorities → Import*.

**B. Use a certificate from your internal CA (recommended).**

- *From the machine certificate store:*
  1. Import the certificate, with its private key, into *Local Computer → Personal*.
  2. Set its thumbprint in `C:\ProgramData\NetWatch\appsettings.json`:
     ```json
     "Https": { "Port": 8443, "CertificateThumbprint": "AB12...EF" }
     ```
  3. Restart the service: `Restart-Service NetWatch`.
- *From a .pfx file:*
  1. Copy the file to `C:\ProgramData\NetWatch\certs\`.
  2. Run the following from an elevated prompt and type the PFX password when asked:
     ```
     "C:\Program Files\NetWatch\NetWatch.Service.exe" --set-certificate C:\ProgramData\NetWatch\certs\netwatch.pfx
     ```
     The password is stored encrypted with Windows DPAPI.
  3. Set `"CertificatePath"` in `appsettings.json` as printed by the command, then restart the service.

If the configured certificate cannot be found, NetWatch logs an error and falls back to the self-signed one, so
the console stays reachable.

To use another port, change `Https:Port` in `appsettings.json`, restart the service, and adjust the firewall
rule "NetWatch HTTPS".

## 5. Service account and least-privilege monitoring accounts

The service runs as **LocalSystem**. It needs no domain account itself: the accounts used to read the monitored
devices are entered as credential profiles. The data folder `C:\ProgramData\NetWatch` is restricted by the installer
to SYSTEM and local Administrators, because it holds the databases, the encrypted credentials, the TLS key and
the logs.

Use dedicated, read-only accounts for monitoring:

| Target | Account and minimal rights |
|---|---|
| Switches, routers | SNMP v2c read-only community, or (recommended) an SNMPv3 user with authPriv (SHA-256 + AES) and a read-only view. Restrict SNMP to the NetWatch server's address. Send traps (v2c or v3) and syslog to the NetWatch server |
| Windows servers | A domain user (e.g. `svc-netwatch`), **not** a domain admin. On each server (or by GPO): member of **Event Log Readers** and **Distributed COM Users**; WMI namespace `root\cimv2` and `root\default` (for pending-reboot checks): *Enable Account* + *Remote Enable* (wmimgmt.msc → WMI Control → Security). For WinRM add the account to **Remote Management Users** and enable WinRM (`winrm quickconfig` or GPO). Windows firewall rules: *Windows Remote Management (HTTP-In)*, *Remote Event Log Management*, and for the DCOM fallback *Windows Management Instrumentation (WMI-In)* |
| Domain controllers | A domain user (e.g. `svc-netwatch-dc`), **not** a domain admin, member of the built-in domain local groups **Event Log Readers** (remote Security log) and **Remote Management Users** (WinRM; or **Distributed COM Users** for DCOM), with WMI *Remote Enable* on `root\cimv2` of the DCs (GPO or wmimgmt.msc). Replication status is read over LDAP, which every authenticated user may do. Configure the advanced audit policy shown on the *Recommended audit policy* tab of the DC page and set the Security log to at least 1 GB |
| Linux servers | An unprivileged user (e.g. `netwatch`) with SSH access; password or key. No sudo is needed: NetWatch only reads `/proc`, runs `df` and `systemctl show`. The host key is recorded on first contact and verified afterwards |
| vCenter / ESXi | A vCenter user (e.g. `netwatch-ro@vsphere.local` or an AD account) with the built-in **Read-only** role assigned at the vCenter root object, *propagate to children* ticked. For a standalone ESXi host, a local user with the Read-only role. Self-signed (VMCA) certificates are pinned on first connection unless the VMCA root is trusted on the NetWatch server |
| Active Directory sign-in | None stored: users bind with their own account. Use LDAPS (636) with the DC certificate trusted on the NetWatch server |
| SMTP relay | Optional; an authenticated relay account if the relay requires it |

Device credentials are encrypted with Windows DPAPI (machine scope). They can only be decrypted on this server:
when you move NetWatch to another server, the credentials must be re-entered (see the admin guide, *Backup and
restore*).

## 6. Upgrade

Run the newer `NetWatch-Setup-<version>.exe` as administrator. The installer stops the service, replaces the
program and starts it again. On start, NetWatch upgrades its databases automatically. Devices, history, alerts,
users, settings and the certificate are kept. Take a backup first (admin guide, *Backup and restore*).

## 7. Uninstall

Use *Settings → Apps → NetWatch → Uninstall*. The service, firewall rule and program files are removed. You are
asked whether to delete the data in `C:\ProgramData\NetWatch` too; the default answer, **No**, keeps it for a
later re-installation. A silent uninstall always keeps the data.

## 8. Command-line options

Run from an elevated prompt in `C:\Program Files\NetWatch`:

| Command | Effect |
|---|---|
| `NetWatch.Service.exe --version` | Print the version |
| `NetWatch.Service.exe --reset-admin` | Give `admin` a new one-time password (printed and written to `initial-admin-password.txt`), unlock it, and require a password change. Restart the service afterwards |
| `NetWatch.Service.exe --set-certificate <pfx>` | Store the password of a PFX certificate (see section 4) |
| `NetWatch.Service.exe --console --data-dir D:\NWTest --simulate` | Run interactively against a separate data folder with ~180 simulated devices, e.g. for training. Stop the service first if both would use port 8443 |
