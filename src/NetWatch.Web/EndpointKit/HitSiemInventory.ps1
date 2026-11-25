<#
.SYNOPSIS
    HIT SIEM endpoint inventory (agentless). Run by a GPO scheduled task as SYSTEM every 30 minutes.

.DESCRIPTION
    Reads hardware, OS, processes, services, installed software, updates and patch state, firewall and
    antivirus state, logged-on users, local administrators, listening ports and connections, USB devices,
    startup programs and shares from this machine with local WMI/CIM and the registry, and sends them to
    HIT SIEM over HTTPS. Nothing is installed; no inbound port is opened.

    Configuration: hitsiem-endpoint.json next to this script (server URL, certificate thumbprint, enrollment key).
    State: HKLM\SOFTWARE\HIT SIEM\Endpoint (machine ID and token; readable by SYSTEM and Administrators only).

    Windows PowerShell 5.1 (Windows 10 / Server 2016 and later); Windows 8.1 / Server 2012 R2 with WMF 5.1.

.PARAMETER ConfigPath
    Path of hitsiem-endpoint.json. Default: next to this script.

.PARAMETER NoDelay
    Do not wait the random start delay (0-120 s) that spreads the load of many machines.

.PARAMETER OutFile
    Also write the JSON document to this file (for checking what is sent).

.PARAMETER DryRun
    Collect and write -OutFile, but do not send.

.PARAMETER StateKey
    Registry key for the machine ID and token. Default HKLM:\SOFTWARE\HIT SIEM\Endpoint; only for testing
    without administrator rights use a key under HKCU: (that test then enrolls as a separate machine).
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$NoDelay,
    [string]$OutFile,
    [switch]$DryRun,
    [string]$StateKey = 'HKLM:\SOFTWARE\HIT SIEM\Endpoint'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ScriptVersion = '1.0.0'
$started = Get-Date

if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'hitsiem-endpoint.json' }
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$collectCommandLines = $true
$collectConnections = $true
if ($null -ne $config.collectCommandLines) { $collectCommandLines = [bool]$config.collectCommandLines }
if ($null -ne $config.collectConnections) { $collectConnections = [bool]$config.collectConnections }

$errors = [ordered]@{}

function Iso($d) {
    if ($null -eq $d) { return $null }
    try { return ([datetime]$d).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } catch { return $null }
}

function Section([string]$name, [scriptblock]$block) {
    try { return & $block }
    catch {
        $script:errors[$name] = ($_.Exception.Message -replace '\s+', ' ').Trim()
        return $null
    }
}

function Arr($x) { if ($null -eq $x) { return ,@() } return ,@($x) }

function Str($x, [int]$max = 1024) {
    if ($null -eq $x) { return $null }
    $s = [string]$x
    if ($s.Length -gt $max) { $s = $s.Substring(0, $max) }
    return $s.Trim()
}

if (-not $NoDelay -and -not $DryRun) { Start-Sleep -Seconds (Get-Random -Minimum 0 -Maximum 120) }

# ------------------------------------------------------------------ state (machine ID and token)
function Get-State {
    if (-not (Test-Path $StateKey)) {
        $key = New-Item -Path $StateKey -Force
        if ($StateKey -like 'HKLM:*') { try {
            # SYSTEM and Administrators only: the token identifies this machine to HIT SIEM.
            $acl = New-Object System.Security.AccessControl.RegistrySecurity
            $acl.SetAccessRuleProtection($true, $false)
            foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
                $id = New-Object System.Security.Principal.SecurityIdentifier $sid
                $rule = New-Object System.Security.AccessControl.RegistryAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                $acl.AddAccessRule($rule)
            }
            Set-Acl -Path $StateKey -AclObject $acl
        } catch { $script:errors['state'] = "ACL: $($_.Exception.Message)" } }
    }
    $p = Get-ItemProperty -Path $StateKey -ErrorAction SilentlyContinue
    if (-not $p.MachineId) {
        Set-ItemProperty -Path $StateKey -Name MachineId -Value ([guid]::NewGuid().ToString())
        $p = Get-ItemProperty -Path $StateKey
    }
    return $p
}

# ------------------------------------------------------------------ collection
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem

$system = Section 'system' {
    $bios = Get-CimInstance Win32_BIOS
    $cpu = @(Get-CimInstance Win32_Processor)
    $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
    $build = [string]$os.BuildNumber
    if ($cv.UBR) { $build = "$build.$($cv.UBR)" }
    $secureBoot = $null
    try { $secureBoot = [bool](Confirm-SecureBootUEFI) } catch { }
    $tpm = $null
    try { $t = Get-CimInstance -Namespace root\cimv2\Security\MicrosoftTpm -ClassName Win32_Tpm -ErrorAction Stop; $tpm = [bool]$t } catch { }
    $fqdn = $env:COMPUTERNAME
    if ($cs.PartOfDomain -and $cs.Domain) { $fqdn = "$($env:COMPUTERNAME).$($cs.Domain)".ToLowerInvariant() }
    [ordered]@{
        computerName     = $env:COMPUTERNAME
        domain           = $(if ($cs.PartOfDomain) { $cs.Domain } else { $null })
        fqdn             = $fqdn
        partOfDomain     = [bool]$cs.PartOfDomain
        manufacturer     = Str $cs.Manufacturer 100
        model            = Str $cs.Model 100
        serialNumber     = Str $bios.SerialNumber 100
        biosVersion      = Str $bios.SMBIOSBIOSVersion 100
        osName           = Str $os.Caption 200
        osVersion        = [string]$os.Version
        osBuild          = $build
        osDisplayVersion = $(if ($cv.DisplayVersion) { $cv.DisplayVersion } else { $cv.ReleaseId })
        osArchitecture   = [string]$os.OSArchitecture
        productType      = [int]$os.ProductType
        installDateUtc   = Iso $os.InstallDate
        lastBootUtc      = Iso $os.LastBootUpTime
        cpuName          = Str ($cpu | Select-Object -First 1).Name 200
        cpuCores         = [int](($cpu | Measure-Object NumberOfCores -Sum).Sum)
        cpuLogical       = [int](($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
        cpuLoadPct       = [double](($cpu | Measure-Object LoadPercentage -Average).Average)
        memoryTotalBytes = [long]$cs.TotalPhysicalMemory
        memoryFreeBytes  = [long]$os.FreePhysicalMemory * 1024
        timeZone         = (Get-CimInstance Win32_TimeZone).Caption
        secureBoot       = $secureBoot
        tpmPresent       = $tpm
        consoleUser      = $cs.UserName
    }
}
if ($null -eq $system) { $system = [ordered]@{ computerName = $env:COMPUTERNAME } }

$adapters = Section 'adapters' {
    foreach ($a in @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = TRUE')) {
        [ordered]@{
            description = Str $a.Description 200
            mac         = $a.MACAddress
            ips         = Arr $a.IPAddress
            gateways    = Arr $a.DefaultIPGateway
            dns         = Arr $a.DNSServerSearchOrder
            dhcp        = [bool]$a.DHCPEnabled
        }
    }
}

$disks = Section 'disks' {
    $bitlocker = @{}
    try {
        foreach ($v in @(Get-CimInstance -Namespace root\cimv2\Security\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -ErrorAction Stop)) {
            $bitlocker[[string]$v.DriveLetter] = $(if ($v.ProtectionStatus -eq 1) { 'On' } else { 'Off' })
        }
    } catch { }
    foreach ($d in @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType = 3')) {
        [ordered]@{
            drive      = $d.DeviceID
            label      = Str $d.VolumeName 100
            fileSystem = $d.FileSystem
            sizeBytes  = [long]$d.Size
            freeBytes  = [long]$d.FreeSpace
            bitLocker  = $bitlocker[[string]$d.DeviceID]
        }
    }
}

$processInfo = @{}
$processes = Section 'processes' {
    $owners = @{}
    $meta = @{}
    try {
        foreach ($p in @(Get-Process -IncludeUserName -ErrorAction Stop)) { $owners[$p.Id] = $p.UserName; $meta[$p.Id] = $p }
    } catch {
        foreach ($p in @(Get-Process)) { $meta[$p.Id] = $p }
    }
    foreach ($w in @(Get-CimInstance Win32_Process)) {
        $id = [int]$w.ProcessId
        $p = $meta[$id]
        $script:processInfo[$id] = $w.Name
        $cpuSec = $null
        $company = $null
        if ($p) {
            try { $cpuSec = [math]::Round($p.TotalProcessorTime.TotalSeconds, 1) } catch { }
            try { $company = $p.Company } catch { }
        }
        [ordered]@{
            pid         = $id
            parentPid   = [int]$w.ParentProcessId
            name        = $w.Name
            path        = Str $w.ExecutablePath
            commandLine = $(if ($collectCommandLines) { Str $w.CommandLine 2048 } else { $null })
            user        = $owners[$id]
            startUtc    = Iso $w.CreationDate
            cpuSec      = $cpuSec
            memoryBytes = [long]$w.WorkingSetSize
            company     = Str $company 200
        }
    }
}

$services = Section 'services' {
    foreach ($s in @(Get-CimInstance Win32_Service)) {
        [ordered]@{
            name        = $s.Name
            displayName = Str $s.DisplayName 256
            state       = $s.State
            startMode   = $s.StartMode
            account     = $s.StartName
            path        = Str $s.PathName
            pid         = [int]$s.ProcessId
        }
    }
}

$software = Section 'software' {
    $roots = @(
        @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Scope = 'Machine'; Arch = '64-bit' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; Scope = 'Machine'; Arch = '32-bit' }
    )
    # Per-user installs of the users whose profile is loaded (logged on).
    foreach ($u in @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^S-1-5-21-[\d-]+$' })) {
        $userName = $u.PSChildName
        try { $userName = (New-Object System.Security.Principal.SecurityIdentifier $u.PSChildName).Translate([System.Security.Principal.NTAccount]).Value } catch { }
        $roots += @{ Path = "Registry::HKEY_USERS\$($u.PSChildName)\Software\Microsoft\Windows\CurrentVersion\Uninstall"; Scope = "User:$userName"; Arch = $null }
    }
    $seen = @{}
    foreach ($r in $roots) {
        if (-not (Test-Path $r.Path)) { continue }
        foreach ($k in @(Get-ChildItem $r.Path -ErrorAction SilentlyContinue)) {
            $v = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if (-not $v.DisplayName -or $v.SystemComponent -eq 1 -or $v.ParentKeyName -or $v.ReleaseType -match 'Update|Hotfix') { continue }
            $key = "$($v.DisplayName)|$($v.DisplayVersion)|$($r.Scope)"
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            [ordered]@{
                name        = Str $v.DisplayName 300
                version     = Str $v.DisplayVersion 100
                publisher   = Str $v.Publisher 200
                installDate = Str $v.InstallDate 20
                scope       = $r.Scope
                arch        = $r.Arch
            }
        }
    }
}

$hotfixes = Section 'hotfixes' {
    foreach ($h in @(Get-CimInstance Win32_QuickFixEngineering)) {
        $installed = $null
        if ($h.InstalledOn) { $installed = Iso $h.InstalledOn }
        [ordered]@{ id = $h.HotFixID; description = $h.Description; installedUtc = $installed }
    }
}

$patches = Section 'patches' {
    $result = [ordered]@{ pendingReboot = $false; missingSecurity = $null; missingCritical = $null; missingOther = $null; missing = @(); lastSearchUtc = $null; lastInstallUtc = $null; wsusServer = $null; error = $null }
    if ((Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
        ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue).PendingFileRenameOperations)) {
        $result.pendingReboot = $true
    }
    $result.wsusServer = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -ErrorAction SilentlyContinue).WUServer
    try {
        $au = New-Object -ComObject Microsoft.Update.AutoUpdate
        $result.lastSearchUtc = Iso $au.Results.LastSearchSuccessDate
        $result.lastInstallUtc = Iso $au.Results.LastInstallationSuccessDate
    } catch { }
    try {
        # Offline search: reads the result of Windows Update's own last scan (WSUS or Microsoft Update), no network.
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $false
        $found = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
        $sec = 0; $crit = 0; $other = 0; $list = @()
        foreach ($u in $found.Updates) {
            $cats = @($u.Categories | ForEach-Object { $_.Name })
            $category = 'Other'
            if ($cats -contains 'Security Updates') { $sec++; $category = 'Security Updates' }
            elseif ($cats -contains 'Critical Updates') { $crit++; $category = 'Critical Updates' }
            else { $other++ ; if ($cats.Count -gt 0) { $category = $cats[0] } }
            if ($list.Count -lt 200) {
                $kb = $null
                if ($u.KBArticleIDs.Count -gt 0) { $kb = "KB$($u.KBArticleIDs.Item(0))" }
                $list += [ordered]@{ title = Str $u.Title 300; kb = $kb; severity = $u.MsrcSeverity; category = $category }
            }
        }
        $result.missingSecurity = $sec; $result.missingCritical = $crit; $result.missingOther = $other; $result.missing = $list
    } catch { $result.error = ($_.Exception.Message -replace '\s+', ' ').Trim() }
    $result
}

$firewall = Section 'firewall' {
    try {
        foreach ($f in @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)) {
            [ordered]@{ name = [string]$f.Name; enabled = [bool]($f.Enabled -eq 'True' -or $f.Enabled -eq 1 -or $f.Enabled -eq $true); defaultInbound = [string]$f.DefaultInboundAction; defaultOutbound = [string]$f.DefaultOutboundAction }
        }
    } catch {
        # Fallback without the NetSecurity module.
        $text = netsh advfirewall show allprofiles state
        $profile = $null
        foreach ($line in $text) {
            if ($line -match '^(Domain|Private|Public) Profile') { $profile = $Matches[1] }
            elseif ($profile -and $line -match '^State\s+(ON|OFF)') { [ordered]@{ name = $profile; enabled = ($Matches[1] -eq 'ON') }; $profile = $null }
        }
    }
}

$antivirus = Section 'antivirus' {
    $products = @()
    try {
        foreach ($p in @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop)) {
            # productState: byte 2 = 0x10 enabled, byte 3 = 0x00 signatures up to date.
            $state = [int]$p.productState
            $products += [ordered]@{ name = $p.displayName; enabled = ((($state -shr 8) -band 0xFF) -band 0x10) -ne 0; upToDate = (($state -band 0xFF) -eq 0) }
        }
    } catch { } # Not present on Windows Server.
    $defender = $null
    try {
        $m = Get-MpComputerStatus -ErrorAction Stop
        $defender = [ordered]@{
            serviceEnabled     = [bool]$m.AMServiceEnabled
            realTimeProtection = [bool]$m.RealTimeProtectionEnabled
            signatureUtc       = Iso $m.AntivirusSignatureLastUpdated
            signatureAgeDays   = [int]$m.AntivirusSignatureAge
            signatureVersion   = [string]$m.AntivirusSignatureVersion
            engineVersion      = [string]$m.AMEngineVersion
            tamperProtected    = $m.IsTamperProtected
            lastQuickScanUtc   = Iso $m.QuickScanEndTime
            lastFullScanUtc    = Iso $m.FullScanEndTime
        }
    } catch { }
    [ordered]@{ products = $products; defender = $defender }
}

$sessions = Section 'sessions' {
    $list = @()
    $out = $null
    try { $out = & quser.exe 2>$null } catch { }
    foreach ($line in @($out | Select-Object -Skip 1)) {
        # USERNAME SESSIONNAME ID STATE IDLE LOGON-TIME (SESSIONNAME is empty for disconnected sessions)
        $t = ($line -replace '^[\s>]+', '') -split '\s{2,}'
        if ($t.Count -ge 5) {
            $logon = $null
            try { $logon = Iso ([datetime]::Parse($t[-1])) } catch { }
            $session = $null; $state = $t[2]
            if ($t.Count -ge 6) { $session = $t[1]; $state = $t[3] }
            $list += [ordered]@{ user = $t[0]; session = $session; state = $state; logonUtc = $logon }
        }
    }
    if ($list.Count -eq 0 -and $cs.UserName) { $list += [ordered]@{ user = $cs.UserName; session = 'console'; state = 'Active'; logonUtc = $null } }
    $list
}

$localAdmins = Section 'localAdmins' {
    $group = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
    $adsi = [ADSI]"WinNT://$env:COMPUTERNAME/$group,group"
    foreach ($m in @($adsi.psbase.Invoke('Members'))) {
        $path = $m.GetType().InvokeMember('ADsPath', 'GetProperty', $null, $m, $null)
        $class = $m.GetType().InvokeMember('Class', 'GetProperty', $null, $m, $null)
        # WinNT://DOMAIN/name or WinNT://DOMAIN/COMPUTER/name (local); orphaned SIDs come back as WinNT://S-1-5-...
        $parts = $path -replace '^WinNT://', '' -split '/'
        if ($parts.Count -ge 3) { $name = "$($parts[1])\$($parts[2])"; $source = 'Local' }
        elseif ($parts.Count -eq 2) { $name = "$($parts[0])\$($parts[1])"; $source = $(if ($parts[0] -eq $env:COMPUTERNAME) { 'Local' } else { 'Domain' }) }
        else { $name = $parts[0]; $source = 'Unknown' }
        [ordered]@{ name = $name; type = $(if ($class -eq 'Group') { 'Group' } else { 'User' }); source = $source }
    }
}

$localUsers = Section 'localUsers' {
    foreach ($u in @(Get-CimInstance Win32_UserAccount -Filter 'LocalAccount = TRUE')) {
        [ordered]@{ name = $u.Name; enabled = -not [bool]$u.Disabled; passwordRequired = [bool]$u.PasswordRequired }
    }
}

$listening = Section 'listening' {
    try {
        foreach ($c in @(Get-NetTCPConnection -State Listen -ErrorAction Stop)) {
            [ordered]@{ protocol = 'TCP'; address = $c.LocalAddress; port = [int]$c.LocalPort; pid = [int]$c.OwningProcess; process = $processInfo[[int]$c.OwningProcess] }
        }
        foreach ($c in @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $_.LocalAddress -in '0.0.0.0', '::' })) {
            [ordered]@{ protocol = 'UDP'; address = $c.LocalAddress; port = [int]$c.LocalPort; pid = [int]$c.OwningProcess; process = $processInfo[[int]$c.OwningProcess] }
        }
    } catch {
        foreach ($line in (netstat -ano | Select-String 'LISTENING')) {
            $t = ($line.ToString().Trim()) -split '\s+'
            $port = [int]($t[1] -replace '^.*:', '')
            [ordered]@{ protocol = 'TCP'; address = ($t[1] -replace ':\d+$', ''); port = $port; pid = [int]$t[-1]; process = $processInfo[[int]$t[-1]] }
        }
    }
}

$connections = $null
if ($collectConnections) {
    $connections = Section 'connections' {
        foreach ($c in @(Get-NetTCPConnection -State Established -ErrorAction Stop | Where-Object { $_.RemoteAddress -notin '127.0.0.1', '::1' } | Select-Object -First 1000)) {
            [ordered]@{ local = $c.LocalAddress; localPort = [int]$c.LocalPort; remote = $c.RemoteAddress; remotePort = [int]$c.RemotePort; state = [string]$c.State; pid = [int]$c.OwningProcess; process = $processInfo[[int]$c.OwningProcess] }
        }
    }
}

$usb = Section 'usb' {
    $present = @{}
    foreach ($d in @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -like 'USB\*' -or $_.PNPDeviceID -like 'USBSTOR\*' })) {
        $present[$d.PNPDeviceID] = $true
        [ordered]@{ name = Str $d.Name 200; deviceId = $d.PNPDeviceID; class = $d.PNPClass; storage = ($d.PNPDeviceID -like 'USBSTOR\*'); present = $true; serial = $null }
    }
    # USB storage history: every stick or disk ever connected (also ones plugged in and out between runs).
    if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Enum\USBSTOR') {
        foreach ($model in @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\USBSTOR' -ErrorAction SilentlyContinue)) {
            foreach ($inst in @(Get-ChildItem $model.PSPath -ErrorAction SilentlyContinue)) {
                $id = "USBSTOR\$($model.PSChildName)\$($inst.PSChildName)"
                if ($present.ContainsKey($id)) { continue }
                $friendly = (Get-ItemProperty $inst.PSPath -ErrorAction SilentlyContinue).FriendlyName
                [ordered]@{ name = Str $friendly 200; deviceId = $id; class = 'DiskDrive'; storage = $true; present = $false; serial = ($inst.PSChildName -replace '&\d$', '') }
            }
        }
    }
}

$startup = Section 'startup' {
    foreach ($s in @(Get-CimInstance Win32_StartupCommand)) {
        [ordered]@{ name = Str $s.Name 200; command = Str $s.Command; location = $s.Location; user = $s.User }
    }
}

$shares = Section 'shares' {
    foreach ($s in @(Get-CimInstance Win32_Share)) { [ordered]@{ name = $s.Name; path = $s.Path; description = Str $s.Description 200 } }
}

$report = [ordered]@{
    schema        = 1
    scriptVersion = $ScriptVersion
    collectedUtc  = Iso (Get-Date)
    durationSec   = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    system        = $system
    adapters      = Arr $adapters
    disks         = Arr $disks
    processes     = Arr $processes
    services      = Arr $services
    software      = Arr $software
    hotfixes      = Arr $hotfixes
    patches       = $patches
    firewall      = Arr $firewall
    antivirus     = $antivirus
    sessions      = Arr $sessions
    localAdmins   = Arr $localAdmins
    localUsers    = Arr $localUsers
    listening     = Arr $listening
    connections   = Arr $connections
    usb           = Arr $usb
    startup       = Arr $startup
    shares        = Arr $shares
    errors        = $errors
}

$json = ConvertTo-Json -InputObject $report -Depth 6 -Compress
if ($OutFile) { [System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding $false)) }
if ($DryRun) { return }

# ------------------------------------------------------------------ send (HTTPS, gzip, certificate pinning)
$state = Get-State
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not ('HitSiemPin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
public static class HitSiemPin {
    public static string Thumbprint;
    public static bool Check(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) {
        if (errors == SslPolicyErrors.None) return true;
        if (String.IsNullOrEmpty(Thumbprint) || cert == null) return false;
        var c2 = new X509Certificate2(cert);
        using (var sha = SHA256.Create()) {
            var sha256 = BitConverter.ToString(sha.ComputeHash(c2.RawData)).Replace("-", "");
            var want = Thumbprint.Replace(" ", "").Replace(":", "");
            return String.Equals(sha256, want, StringComparison.OrdinalIgnoreCase) || String.Equals(c2.Thumbprint, want, StringComparison.OrdinalIgnoreCase);
        }
    }
}
'@
}
[HitSiemPin]::Thumbprint = [string]$config.thumbprint

$bytes = [Text.Encoding]::UTF8.GetBytes($json)
$ms = New-Object IO.MemoryStream
$gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress)
$gz.Write($bytes, 0, $bytes.Length)
$gz.Close()
$body = $ms.ToArray()

$url = ([string]$config.server).TrimEnd('/') + '/api/endpoints/report'
$request = [Net.HttpWebRequest]::Create($url)
$request.Method = 'POST'
$request.ContentType = 'application/json'
$request.Timeout = 120000
$request.ServerCertificateValidationCallback = [Delegate]::CreateDelegate([Net.Security.RemoteCertificateValidationCallback], [HitSiemPin].GetMethod('Check'))
$request.Headers.Add('Content-Encoding', 'gzip')
$request.Headers.Add('X-NetWatch-Request', '1')
$request.Headers.Add('X-HitSiem-Machine', [string]$state.MachineId)
if ($state.Token) { $request.Headers.Add('X-HitSiem-Token', [string]$state.Token) }
else { $request.Headers.Add('X-HitSiem-Key', [string]$config.enrollmentKey) }
$request.ContentLength = $body.Length

try {
    $stream = $request.GetRequestStream()
    $stream.Write($body, 0, $body.Length)
    $stream.Close()
    $response = $request.GetResponse()
    $reader = New-Object IO.StreamReader($response.GetResponseStream())
    $answer = $reader.ReadToEnd() | ConvertFrom-Json
    $response.Close()
    if ($answer.token) { Set-ItemProperty -Path $StateKey -Name Token -Value ([string]$answer.token) }
    Set-ItemProperty -Path $StateKey -Name LastResult -Value "OK $(Iso (Get-Date)) $($answer.message)"
    Write-Output "HIT SIEM: $($answer.message)"
} catch {
    $ex = $_.Exception
    while ($ex.InnerException -and -not ($ex -is [Net.WebException])) { $ex = $ex.InnerException }
    $text = $ex.Message
    if ($ex -is [Net.WebException] -and $ex.Response) {
        try { $text = "$([int]$ex.Response.StatusCode) " + (New-Object IO.StreamReader($ex.Response.GetResponseStream())).ReadToEnd() } catch { }
    }
    Set-ItemProperty -Path $StateKey -Name LastResult -Value "FAILED $(Iso (Get-Date)) $text"
    Write-Error "HIT SIEM: $text"
    exit 1
}
