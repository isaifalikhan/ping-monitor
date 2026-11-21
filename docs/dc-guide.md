# Domain controller monitoring guide

NetWatch watches Windows Server domain controllers without an agent: it reads their **Security**, **Directory
Service** and **DFS Replication** logs, checks **replication** with every partner and naming context, the
**clock** against the PDC emulator, the **AD services** and the **LDAP/Kerberos/DNS ports**, and raises alerts
for brute force, lockouts, privileged group changes, a cleared log, audit-policy changes and admin logons out
of hours.

## 1. Account

Create a domain user, e.g. `svc-netwatch-dc`. It must **not** be a domain admin. Add it to these built-in domain
local groups (Active Directory Users and Computers → *Builtin*):

| Group | Needed for |
|---|---|
| **Event Log Readers** | reading the Security, Directory Service and DFS Replication logs remotely |
| **Remote Management Users** | WinRM (services, clock, disks); or **Distributed COM Users** for the DCOM fallback |

and grant WMI *Remote Enable* on `root\cimv2` of the DCs (by GPO or `wmimgmt.msc` → WMI Control → Properties →
Security). Replication status and FSMO roles are read over LDAP, which every authenticated user may do.

In NetWatch: *Settings → Credentials → Add* (type *Windows*), then add each DC as a device of type
**Domain controller** with that credential. New DCs watch NTDS, DNS, Netlogon, Kdc, DFSR and W32Time.

## 2. Audit policy (GPO)

Edit the **Default Domain Controllers Policy** (or a GPO linked to the *Domain Controllers* OU):
*Computer Configuration → Policies → Windows Settings → Security Settings → Advanced Audit Policy Configuration
→ Audit Policies*. Also enable *Security Options → Audit: Force audit policy subcategory settings … to override
audit policy category settings*.

| Category → subcategory | Setting | Events |
|---|---|---|
| Account Logon → Credential Validation | Success, Failure | 4776 |
| Account Logon → Kerberos Authentication Service | Success, Failure | 4768, 4771 |
| Account Management → User Account Management | Success, Failure | 4720, 4722, 4723, 4724, 4725, 4726, 4738, 4740, 4767 |
| Account Management → Computer Account Management | Success | 4741, 4743 |
| Account Management → Security Group Management | Success | 4728, 4729, 4732, 4733, 4756, 4757 |
| Logon/Logoff → Logon | Success, Failure | 4624, 4625 |
| Logon/Logoff → Special Logon | Success | 4672 |
| Policy Change → Audit Policy Change | Success | 4719 |
| DS Access → Directory Service Changes | Success | 5136 (needs a SACL on the objects to watch, e.g. GPOs) |
| (always logged) | – | 1102 Security log cleared |

Set the Security log size to at least **1 GB** (*Event Log → Maximum security log size*, retention *Overwrite as
needed*). The DC page shows the same list on the *Recommended audit policy* tab.

## 3. Remote reads or Windows Event Forwarding

**Remote reads (default).** NetWatch reads each DC's Security log with the account above, resuming from the
last record it read (bookmark), so nothing is missed or read twice after a restart. A DC whose log cannot be
read is retried after 1, 2, 4, 8, 16 and then every 30 minutes.

Firewall on the DCs (inbound from the NetWatch server): *Remote Event Log Management (RPC, RPC-EPMAP)*, WinRM
5985 (or DCOM/WMI), LDAP 389, Kerberos 88 and DNS 53.

**Windows Event Forwarding (WEF).** When DCs must not be read remotely, forward their Security events to the
NetWatch server:

1. On the NetWatch server: `wecutil qc` and create a *source-initiated* subscription that collects the event IDs
   of section 2 into **Forwarded Events**.
2. By GPO on the DCs: *Administrative Templates → Windows Components → Event Forwarding → Configure target
   Subscription Manager* = `Server=http://<netwatch-server>:5985/wsman/SubscriptionManager/WEC,Refresh=60`, and
   add *NETWORK SERVICE* to **Event Log Readers** on the DCs.
3. In NetWatch: *Settings → General → Domain controllers*: tick *Use Windows Event Forwarding for all DCs*, or
   list the DCs that forward under *DCs that use Event Forwarding* (the others keep being read remotely).
4. Add the NetWatch service account (LocalSystem by default) to the local **Event Log Readers** of the NetWatch
   server if the self-check reports "access denied" for Forwarded Events.

## 4. Filters

Successful logons (4624) are numerous. By default NetWatch stores interactive (2), unlock (7), RDP (10) and
cached (11) logons, skips computer accounts (`name$`) and built-in service identities, and **never alerts on
successful logons**. *Settings → General → Domain controllers* changes the logon types and lists sources
(IP, CIDR such as `10.0.20.0/24`, or workstation name) whose successful logons are not stored, e.g.
application servers. Failures are always stored.

## 5. Alerts

| Rule | Default |
|---|---|
| Failed logons for one account | ≥ 5 in 5 min (per account) |
| Failed logons from one source | ≥ 20 in 5 min (per IP/workstation) — brute force |
| Kerberos pre-authentication failures spike | ≥ 50 × 4771 in 5 min on one DC (password spraying) |
| Account locked out | any 4740 |
| Privileged group membership added | Critical — Domain Admins, Enterprise Admins, Schema Admins, Administrators, Backup Operators, Account Operators (list editable) |
| Security log cleared / audit policy changed | Critical (1102, 4719) |
| Admin logon outside working hours | 4672 outside Mon–Fri 08:00–17:00 PKT (editable) |
| New user / computer account, account deleted | 4720, 4741, 4726/4743 |
| AD replication failing | failures for 1 hour |
| AD replication stale | no success for 3 hours (editable) |
| Directory/DFS replication error | 1311, 1864, 2042, 2087, 2088, 2213, 4012 |
| DC clock skew | > 30 s (warning), > 120 s (critical; Kerberos tolerates 5 min) |
| DC service port unreachable | LDAP 389, Kerberos 88 or DNS 53 for 2 minutes |
| Service stopped | NTDS, DNS, Netlogon, Kdc, DFSR, W32Time |

## 6. The Domain controllers page

*Overview* (cards per DC with roles, clock, replication, services and ports), *Security events* (filters by
DC, event type/IDs, account, source, text and time range; saved searches; **Export CSV**), *Lockouts* (who,
from which computer, how many failures before), *Group changes* (privileged highlighted), *Replication*
(matrix DC × partner), *Recommended audit policy*.

## 7. Checking it works

*Settings → Pilot tools*: run **Remote Event Log (1 Security event)** against each DC with the DC credential,
then **WinRM / WMI query** and **Syslog / traps received** if used. Failed tests name the exact problem (access
denied → group membership; RPC unavailable → firewall).
