<#
.SYNOPSIS
  Builds NetWatch: web UI, tests, self-contained single-file service exe and the Windows installer.

.DESCRIPTION
  Online build (internet-connected build PC):
      .\build.ps1

  Prepare an offline build kit (run on an internet-connected PC, then copy the whole folder incl. .\offline):
      .\build.ps1 -PrepareOffline

  Offline build inside the air-gapped network (no internet needed):
      .\build.ps1 -Offline

  Output:
      artifacts\publish\NetWatch.Service.exe      self-contained, single file, win-x64
      artifacts\HIT-SIEM-Setup-<version>.exe       installer (requires Inno Setup 6)

.PARAMETER Version
  Product version stamped into the exe and the installer (e.g. 1.0.0).
#>
[CmdletBinding()]
param(
    [string]$Version = '2.0.3',
    [string]$Configuration = 'Release',
    [switch]$Offline,
    [switch]$PrepareOffline,
    [switch]$SkipTests,
    [switch]$SkipUi,
    [switch]$SkipInstaller,
    [string]$InnoSetupCompiler,

    # Optional Authenticode signing of NetWatch.Service.exe and the installer (signtool from the Windows SDK).
    # Password: environment variable NETWATCH_SIGN_PASSWORD (never on the command line).
    [string]$SignCertificate,

    # RFC 3161 time-stamp server inside the network (optional; without it the signature ends with the certificate).
    [string]$SignTimestampUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$offlineDir = Join-Path $root 'offline'
$nugetCache = Join-Path $offlineDir 'nuget'
$npmCache = Join-Path $offlineDir 'npm-cache'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$clientApp = Join-Path $root 'src\NetWatch.Web\ClientApp'
$solution = Join-Path $root 'NetWatch.slnx'
$serviceProject = Join-Path $root 'src\NetWatch.Service\NetWatch.Service.csproj'
$installerScript = Join-Path $root 'installer\NetWatch.Installer\netwatch.iss'

# No telemetry, no first-run network checks.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_XMLDOC_MODE = 'skip'
$env:npm_config_update_notifier = 'false'
$env:npm_config_fund = 'false'
$env:npm_config_audit = 'false'

function Step([string]$name) { Write-Host "`n==> $name" -ForegroundColor Cyan }

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { return $null }
    Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Invoke-Sign([string]$file) {
    if (-not $SignCertificate) { return }
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK "Signing Tools" or omit -SignCertificate.' }
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $SignCertificate)
    if ($env:NETWATCH_SIGN_PASSWORD) { $signArgs += @('/p', $env:NETWATCH_SIGN_PASSWORD) }
    if ($SignTimestampUrl) { $signArgs += @('/tr', $SignTimestampUrl, '/td', 'SHA256') }
    $signArgs += $file
    & $signtool @signArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed (signtool exit code $LASTEXITCODE)." }
    Write-Host "    signed $file"
}

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    Write-Host "    $exe $($arguments -join ' ')" -ForegroundColor DarkGray
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "'$exe' failed with exit code $LASTEXITCODE" }
}

function Find-InnoSetup {
    if ($InnoSetupCompiler) { return $InnoSetupCompiler }
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

$restoreSource = @()
if ($Offline) {
    if (-not (Test-Path $nugetCache)) { throw "Offline NuGet cache not found at $nugetCache. Run '.\build.ps1 -PrepareOffline' on an internet-connected PC first." }
    $restoreSource = @('--source', $nugetCache)
}

# ---------------------------------------------------------------------------------------------- offline kit
if ($PrepareOffline) {
    Step 'Restoring NuGet packages into offline\nuget (including win-x64 runtime packs)'
    New-Item -ItemType Directory -Force -Path $nugetCache, $npmCache | Out-Null
    Invoke-Checked 'dotnet' @('restore', $solution, '--packages', $nugetCache)
    Invoke-Checked 'dotnet' @('restore', $serviceProject, '--packages', $nugetCache, '-r', 'win-x64', '-p:SelfContained=true')

    Step 'Filling the npm cache in offline\npm-cache'
    Push-Location $clientApp
    try { Invoke-Checked 'npm' @('ci', '--cache', $npmCache, '--prefer-online') } finally { Pop-Location }

    Write-Host "`nOffline kit ready. Copy this folder (with 'offline') into the air-gapped network together with:" -ForegroundColor Green
    Write-Host "  - the .NET 10 SDK installer (dotnet-sdk-10.0.x-win-x64.exe)"
    Write-Host "  - Node.js 20+ LTS installer (node-v2x-x64.msi)"
    Write-Host "  - Inno Setup 6 installer (innosetup-6.x.exe)"
    Write-Host "Then run: .\build.ps1 -Offline"
    return
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# ---------------------------------------------------------------------------------------------- web UI
if (-not $SkipUi) {
    Step 'Building web UI (React + Vite)'
    Push-Location $clientApp
    try {
        if ($Offline) {
            Invoke-Checked 'npm' @('ci', '--offline', '--cache', $npmCache)
        } else {
            Invoke-Checked 'npm' @('ci')
        }
        if (-not $SkipTests) { Invoke-Checked 'npm' @('test') }
        Invoke-Checked 'npm' @('run', 'build')
    } finally { Pop-Location }
} elseif (-not (Test-Path (Join-Path $clientApp 'dist\index.html'))) {
    Write-Warning 'UI build skipped and ClientApp\dist is missing: the service will run without the web console.'
}

# ---------------------------------------------------------------------------------------------- tests
if (-not $SkipTests) {
    Step 'Running unit tests'
    # 'dotnet test' has no --source option: restore first (from the offline kit when -Offline), then test without restoring.
    Invoke-Checked 'dotnet' (@('restore', $solution, "-p:Version=$Version", '-p:NetWatchLockedRestore=true') + $restoreSource)
    Invoke-Checked 'dotnet' @('test', $solution, '-c', $Configuration, "-p:Version=$Version", '-p:NetWatchLockedRestore=true', '--no-restore', '--nologo')
}

# ---------------------------------------------------------------------------------------------- publish
Step "Publishing self-contained single-file service ($Version)"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
# PublishSingleFile, IncludeNativeLibrariesForSelfExtract and EnableCompressionInSingleFile are set in
# NetWatch.Service.csproj. Do not pass them here: command-line properties are global, reach the library projects
# too, turn on the single-file analyzer there (adds Microsoft.NET.ILLink.Tasks) and break the locked restore (NU1004).
Invoke-Checked 'dotnet' (@(
    'publish', $serviceProject,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:Version=$Version",
    '-p:NetWatchLockedRestore=true',
    '-o', $publishDir,
    '--nologo') + $restoreSource)

$exe = Join-Path $publishDir 'NetWatch.Service.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
Get-ChildItem $publishDir -Exclude 'NetWatch.Service.exe' | Remove-Item -Recurse -Force
Write-Host ("    {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
Invoke-Sign $exe

# ---------------------------------------------------------------------------------------------- installer
if (-not $SkipInstaller) {
    Step 'Building installer (Inno Setup)'
    $iscc = Find-InnoSetup
    if (-not $iscc) {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found: installer skipped. Install Inno Setup 6 or pass -InnoSetupCompiler <path>.'
    } else {
        Invoke-Checked $iscc @("/DAppVersion=$Version", "/DNumericVersion=$($Version -replace '-.*$', '')", "/DSourceDir=$publishDir", "/DDocsDir=$(Join-Path $root 'docs')", "/O$artifacts", '/Q', $installerScript)
        $setup = Join-Path $artifacts "HIT-SIEM-Setup-$Version.exe"
        Invoke-Sign $setup
        Write-Host ("    {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
    }
}

Step 'Done'
