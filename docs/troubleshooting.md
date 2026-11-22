# Troubleshooting guide (connection-test errors)

Run the matching test in *Settings → Pilot tools*. Each step shows the exact error; find it below.

## Ping

| Error | Cause and fix |
|---|---|
| no reply | Host down, or ICMP blocked. Allow ICMP echo from the NetWatch server (Windows: *File and Printer Sharing (Echo Request – ICMPv4-In)*) |
| cannot be resolved | DNS: use the IP address, or add the host to DNS |

## SNMP

| Error | Cause and fix |
|---|---|
| No SNMP response … within 3000 ms | Wrong community / v3 user, the device's SNMP ACL does not allow the NetWatch server, or UDP 161 blocked on a firewall. With v3 also: wrong security level |
| unknown user name / wrong digest / decryption error | SNMPv3 user, authentication or privacy password/protocol differ from the device |
| interface table is empty | The SNMP view excludes IF-MIB (`1.3.6.1.2.1.2` and `1.3.6.1.2.1.31`) |

## WinRM / WMI / Event Log

| Error | Cause and fix |
|---|---|
| access denied | Account not in *Remote Management Users* (WinRM) / *Distributed COM Users* (DCOM); no WMI *Remote Enable* on `root\cimv2`; for event logs not in *Event Log Readers* |
| The WinRM client cannot process the request / 0x80338012 | WinRM not enabled (`winrm quickconfig`) or TCP 5985 blocked |
| RPC server is unavailable (event log) | Firewall rule *Remote Event Log Management* disabled, or RPC dynamic ports blocked |
| nvidia-smi over WinRM failed | The account may run WMI queries but not open a remote shell: add it to *Remote Management Users*; or nvidia-smi is not installed (data-centre driver) |
| GPU performance counters not available | Windows Server 2016 / non-WDDM-2 driver: only inventory is shown |

## SSH

| Error | Cause and fix |
|---|---|
| Permission denied | Wrong user/password/key, or the user may not log in over SSH (`AllowUsers`) |
| host key changed | Server reinstalled or key replaced: verify, then *Reset* the key on the Server tab |
| nvidia-smi: … | NVIDIA driver not loaded (`lsmod | grep nvidia`) or the GPU failed |

## vCenter / iLO / iDRAC (HTTPS APIs)

| Error | Cause and fix |
|---|---|
| certificate changed / rejected | The API certificate was renewed or the address points elsewhere: verify, then reset the pinned certificate on the device page. For vCenter, *trust on first use* can be turned off (*Settings → General → VMware*); then only CA-trusted certificates are accepted. For iLO/iDRAC trust on first use is always on |
| Redfish login refused (401/403) | Wrong account, or the iLO/iDRAC account lacks the read-only (Login) privilege; on iDRAC check *Redfish* is enabled (*iDRAC Settings → Services*) |
| timed out | TCP 443 blocked, or the controller is busy; iLO/iDRAC answer slowly — they are read every 5 minutes with a 30 s timeout (fixed; not configurable in the console) |
| lacks permission / empty inventory (vCenter) | Assign the Read-only role at the vCenter root with *propagate to children* |

## LDAP / LDAPS

| Error | Cause and fix |
|---|---|
| directory cannot be reached / server unavailable | Port 636 (LDAPS) or 389 blocked, wrong DC name |
| certificate not issued for … / chain errors | The DC certificate must be trusted on the NetWatch server and carry the DC's FQDN; with *Offline* revocation the CA's CRL must be in the local cache (`certutil -addstore -f Root ca.crl`) |
| bound, but in none of the mapped groups | Add the user to a group mapped in *Settings → Directory* |

## SMTP

| Error | Cause and fix |
|---|---|
| Connection refused / timed out | Relay address or port wrong, firewall |
| Authentication required | Enter the relay account in *Settings → Notifications* |
| certificate errors | Trust the relay's CA on the NetWatch server, or use port 25 without TLS on a trusted network |

## Syslog / traps received

| Result | Fix |
|---|---|
| none from this source | Device sends elsewhere or with another source address; UDP/TCP 514 or UDP 162 blocked; *System status* shows a listener not "listening" (another program uses the port) |

## Service does not start

Check *Event Viewer → Windows Logs → Application* (sources **NetWatch** and **.NET Runtime**) and the newest file in
`C:\ProgramData\NetWatch\logs`.

| Message | Cause and fix |
|---|---|
| NetWatch cannot start: Access to the path … is denied | The service account (SYSTEM) cannot read the data folder or a file in it. The 2.0.0-rc1 installer caused this on every reinstall or upgrade (BUG-009). Run the current installer again, or in an administrator PowerShell: `icacls C:\ProgramData\NetWatch /setowner *S-1-5-32-544 /T /C /Q; icacls "C:\ProgramData\NetWatch\*" /reset /T /C /Q; Start-Service NetWatch` |
| Another NetWatch instance is already running on data folder … | A copy started from a console uses the same data folder: close it (`Get-Process NetWatch.Service`), then `Start-Service NetWatch` |
| Cannot listen for SNMP traps / syslog (in the log, service keeps running) | Another program holds UDP 162 or UDP/TCP 514; the web console and polling still work |

## When nothing helps

*Settings → Pilot tools → Download bundle*, with an SNMP walk of the affected devices ticked. It contains logs,
the test results, walks and configuration with every secret removed — bring it out on USB.
