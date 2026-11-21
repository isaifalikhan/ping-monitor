# Reports guide

*Reports* (Operator and Admin; Viewers can create and preview but not schedule) builds reports as **PDF** or
**CSV**. Everything is generated on the NetWatch server; nothing leaves the network unless a schedule e-mails a
report through your own SMTP relay. Times are Pakistan time (PKT).

## Reports

| Report | Contents |
|---|---|
| Availability | Uptime % per device (ping/SNMP), outages, approximate downtime and latency; worst devices first. Day, week, month or a custom range; one group or everything |
| Alert history | Alerts by severity, rule and device; mean time to acknowledge and to resolve |
| Interface utilization | Average and peak in/out utilization of important interfaces; interfaces with most errors |
| Server capacity | CPU and memory average, peak and trend per server; volume and datastore fill with growth and days until full; **disks predicted to fill within 30 days** |
| Domain controller security | Failed logons (top accounts and sources), lockouts, privileged group changes, new/deleted accounts, admin logons outside working hours, Security log cleared and audit-policy changes |
| Device inventory | All devices with type, address, group, vendor, model, serial, firmware and status |
| VMware | ESXi hosts (state, CPU, memory, VMs), datastore capacity and provisioning, snapshots (oldest first, age, size), powered-off VMs |

"Days until full" assumes the growth seen in the period continues linearly; choose a period that reflects normal
use (e.g. the last month).

## Creating a report

1. Pick the report, the format, the period and optionally a group.
2. **Preview** shows the report on screen (tables up to 200 rows; the download has everything).
3. **Generate and download** saves the PDF or CSV.

CSV files open in Excel (UTF-8 with BOM). Values that start with `=`, `+`, `-` or `@` are prefixed with `'` so a
spreadsheet cannot run them as formulas.

## Scheduled reports

*Reports → Schedules*: daily, weekly or monthly at a PKT time, covering the previous day, 7 days or calendar
month. Files are saved under `C:\ProgramData\NetWatch\reports\YYYY-MM\` (kept 400 days) and listed on the
*Files* tab. Add e-mail recipients to have them sent through the SMTP relay of *Settings → Notifications*
(attachments up to 15 MB). A failing schedule raises the self-check alert.
