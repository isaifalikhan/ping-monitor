HIT SIEM endpoint inventory - deployment kit (agentless, GPO)
================================================================

Nothing is installed on the machines. A Group Policy creates a scheduled task that runs
HitSiemInventory.ps1 from NETLOGON as SYSTEM every {INTERVAL} minutes. The script reads the machine with
local WMI and the registry and sends the result to HIT SIEM over HTTPS:

    {SERVER}/api/endpoints/report

Files in this kit
  HitSiemInventory.ps1                 the collector (Windows PowerShell 5.1, no modules needed)
  hitsiem-endpoint.json                server address, certificate thumbprint, enrollment key
  HitSiem-Endpoints-Subscription.xml   Windows Event Forwarding subscription (live events, part 4)
  HitSiem-Endpoints-Query.xml          the same event query, for Event Viewer custom views
  README.txt                           this file

Keep hitsiem-endpoint.json like a password: with the enrollment key a machine can enroll. Enrolled
machines get their own token and keep working after the key is changed (Endpoints -> Deployment ->
Change enrollment key).


1. HIT SIEM server
------------------
- Allow inbound TCP to the HIT SIEM HTTPS port ({SERVER}) from the client networks in the
  Windows Firewall of the HIT SIEM server (the installer opens it for all networks by default).
- The machines must reach the server under the name in {SERVER}. Use the DNS name, not localhost.


2. Copy the script to NETLOGON
------------------------------
On a domain controller (or any machine, as a Domain Admin):

    New-Item -ItemType Directory \\<your-domain>\NETLOGON\HitSiem
    Copy-Item HitSiemInventory.ps1, hitsiem-endpoint.json \\<your-domain>\NETLOGON\HitSiem\

NETLOGON is readable by all domain computers and writable only by Domain Admins. Do not put the files
on a share that users can change: the script runs as SYSTEM.

Test it on one machine first, in an elevated PowerShell:

    powershell -NoProfile -ExecutionPolicy Bypass -File \\<your-domain>\NETLOGON\HitSiem\HitSiemInventory.ps1 -NoDelay

It prints "HIT SIEM: Machine enrolled, inventory received." The machine appears under Endpoints.
-DryRun -OutFile C:\Temp\inventory.json shows what is collected without sending.

If your execution policy requires signed scripts (AllSigned), sign it with your code-signing certificate:

    Set-AuthenticodeSignature HitSiemInventory.ps1 (Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert)[0]

and use -ExecutionPolicy AllSigned in step 3.


3. Group Policy: the scheduled task
-----------------------------------
Group Policy Management -> create a GPO "HIT SIEM Endpoint Inventory" and link it to the OUs of the
workstations and servers.

Edit -> Computer Configuration -> Preferences -> Control Panel Settings -> Scheduled Tasks ->
New -> Scheduled Task (At least Windows 7)

  General
    Action:              Replace
    Name:                HIT SIEM Inventory
    When running the task, use the following user account:  NT AUTHORITY\System
                         (Change User or Group -> type SYSTEM -> Check Names)
    Run whether user is logged on or not
    Run with highest privileges
    Configure for:       Windows 10

  Triggers -> New
    1) On a schedule, Daily, every 1 day, start today 00:00
       Advanced: Repeat task every {INTERVAL} minutes, for a duration of: 1 day
                 Delay task for up to (random delay): 5 minutes
                 Enabled
    2) At startup, Delay task for: 5 minutes

  Actions -> New -> Start a program
    Program/script:  %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe
    Add arguments:   -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "\\<your-domain>\NETLOGON\HitSiem\HitSiemInventory.ps1"

  Conditions
    clear "Start the task only if the computer is on AC power"

  Settings
    Allow task to be run on demand
    Run task as soon as possible after a scheduled start is missed
    Stop the task if it runs longer than: 1 hour
    If the task is already running: Do not start a new instance

Machines pick up the GPO within 90-120 minutes, or at once with "gpupdate /force". Check on a machine:

    schtasks /run /tn "HIT SIEM Inventory"
    Get-ItemProperty "HKLM:\SOFTWARE\HIT SIEM\Endpoint" | Select-Object LastResult   (as administrator)

With AppLocker or WDAC in enforced mode, allow the script (path or signature rule): the script compiles
a small certificate check with Add-Type, which Constrained Language Mode does not allow.


4. Group Policy: live events (Windows Event Forwarding)
-------------------------------------------------------
The inventory shows the state every {INTERVAL} minutes. For alerts within about a minute (malware found,
local administrator added, USB stick, suspicious command line, log cleared, new service) the machines
also forward a small set of events to the HIT SIEM server.

On the HIT SIEM server, in an elevated command prompt, from the folder of this kit:

    winrm quickconfig -q
    wecutil qc /q
    wecutil cs HitSiem-Endpoints-Subscription.xml

Allow inbound TCP 5985 (WinRM, HTTP) from the client networks on the HIT SIEM server. WEF over HTTP is
encrypted with Kerberos; the subscription accepts Domain Computers and Domain Controllers.

In the same GPO (Computer Configuration):

  Policies -> Administrative Templates -> Windows Components -> Event Forwarding
    Configure target Subscription Manager: Enabled
      Server=http://{HOST}:5985/wsman/SubscriptionManager/WEC,Refresh=60

  Policies -> Windows Settings -> Security Settings -> System Services
    Windows Remote Management (WS-Management): Automatic

  Preferences -> Control Panel Settings -> Local Users and Groups -> New -> Local Group
    Group name: Event Log Readers (built-in), Action: Update
    Members: Add -> NETWORK SERVICE   (the forwarder needs it to read the Security log)

  Policies -> Windows Settings -> Security Settings -> Advanced Audit Policy Configuration -> Audit Policies
    Detailed Tracking   -> Audit Process Creation:          Success     (4688)
    Detailed Tracking   -> Audit PNP Activity:              Success     (6416, USB devices)
    Account Management  -> Audit User Account Management:   Success     (4720, 4726)
    Account Management  -> Audit Security Group Management: Success     (4732, 4733)
    System              -> Audit Security System Extension: Success     (4697, service installed)
    Logon/Logoff        -> Audit Logon:                     Failure     (4625)

  Policies -> Administrative Templates -> System -> Audit Process Creation
    Include command line in process creation events: Enabled

Forwarded events: Security 4688 4625 4720 4726 4732 4733 4697 1102 6416, System 7045 104,
Application 11707 11724 (software installed/removed), Microsoft Defender 1116 1117 5001 5010 5012,
Windows Firewall 2003 2004 2005 2006 2033 2052 2097 2099.

Process starts (4688) are the bulk: about 1,000-3,000 per PC per day. Turn off "Store process starts"
under Endpoints -> Deployment to keep only the suspicious ones (alerts still work).

Check on the HIT SIEM server: wecutil gr HIT-SIEM-Endpoints  (lists the machines and their state).
Check in HIT SIEM: Endpoints -> Deployment shows "Forwarded endpoint events" and the last read time.


5. Troubleshooting
------------------
LastResult "FAILED ... 401 Wrong or missing enrollment key"   the key was changed: copy the new
                                                                hitsiem-endpoint.json to NETLOGON.
LastResult "FAILED ... 401 ... enrolled with a different token" the machine was reinstalled or the
                                                                registry key copied: Endpoints -> machine
                                                                -> Reset enrollment.
LastResult "FAILED ... 409 Another machine named ..."           two machines with one name; remove the
                                                                old entry in HIT SIEM.
"The underlying connection was closed / trust relationship"     the certificate thumbprint in
                                                                hitsiem-endpoint.json does not match the
                                                                HIT SIEM certificate (it changed): download
                                                                a new kit.
"Unable to connect"                                             firewall or DNS between machine and
                                                                {SERVER}.
Sections with errors are listed on the machine's page in HIT SIEM (for example "patches" when the
Windows Update service is disabled).
