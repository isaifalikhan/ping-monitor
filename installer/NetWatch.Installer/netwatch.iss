; NetWatch installer (Inno Setup 6).
; Built by build.ps1:  ISCC /DAppVersion=1.0.0 /DSourceDir=..\..\artifacts\publish /DDocsDir=..\..\docs netwatch.iss
;
; Install:    copies NetWatch.Service.exe, creates the "NetWatch" Windows service (delayed auto-start, restart on
;             failure), inbound firewall rules for the web console (TCP 8443), SNMP traps (UDP 162) and syslog
;             (UDP/TCP 514), the "NetWatch" event-log source, C:\ProgramData\NetWatch with restricted ACL, and a
;             Start-menu shortcut to the dashboard.
; Upgrade:    run a newer installer; the service is stopped, the exe replaced and the service restarted.
;             Databases, settings, certificates and logs in C:\ProgramData\NetWatch are kept.
; Uninstall:  removes the service, firewall rules and program files. Data is kept unless the administrator
;             chooses to delete it.

#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish"
#endif
#ifndef DocsDir
  #define DocsDir "..\..\docs"
#endif

#define ServiceName "NetWatch"
#define ServiceDisplayName "NetWatch Monitoring Service"
#define HttpsPort "8443"

[Setup]
AppId={{7E3F2B4A-5C1D-4E8A-9B6F-2D4C8A1E3F57}
AppName=NetWatch
AppVersion={#AppVersion}
AppVerName=NetWatch {#AppVersion}
AppPublisher=NetWatch
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\NetWatch
DefaultGroupName=NetWatch
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=NetWatch-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayName=NetWatch
UninstallDisplayIcon={app}\NetWatch.Service.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\NetWatch.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DocsDir}\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist

[Dirs]
Name: "{commonappdata}\NetWatch"; Flags: uninsneveruninstall

[INI]
Filename: "{app}\NetWatch Dashboard.url"; Section: "InternetShortcut"; Key: "URL"; String: "https://localhost:{#HttpsPort}/"

[Icons]
Name: "{group}\NetWatch Dashboard"; Filename: "{app}\NetWatch Dashboard.url"
Name: "{group}\NetWatch documentation"; Filename: "{app}\docs"
Name: "{group}\NetWatch data folder"; Filename: "{commonappdata}\NetWatch"
Name: "{group}\Uninstall NetWatch"; Filename: "{uninstallexe}"

[Registry]
; Event-log source for the optional "Windows Event Log" notification channel (message file of the .NET Framework,
; present on every supported Windows version).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\NetWatch"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"; Flags: uninsdeletekey

[UninstallDelete]
Type: files; Name: "{app}\NetWatch Dashboard.url"

[Code]
var
  FreshInstall: Boolean;

function Run(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('Run: %s %s -> %d', [FileName, Params, Code]));
  Result := Code;
end;

function Sc(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params);
end;

function Netsh(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\netsh.exe'), Params);
end;

function ServiceExists(): Boolean;
begin
  // sc query returns 1060 when the service does not exist.
  Result := Sc('query {#ServiceName}') = 0;
end;

procedure StopService();
begin
  if ServiceExists() then
  begin
    // "net stop" waits until the service has stopped.
    Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  end;
  // Make sure no instance keeps the exe locked (e.g. one started manually from a console).
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NetWatch.Service.exe');
end;

procedure RemoveFirewallRules();
begin
  Netsh('advfirewall firewall delete rule name="NetWatch HTTPS"');
  Netsh('advfirewall firewall delete rule name="NetWatch SNMP traps"');
  Netsh('advfirewall firewall delete rule name="NetWatch syslog UDP"');
  Netsh('advfirewall firewall delete rule name="NetWatch syslog TCP"');
end;

procedure AddFirewallRules();
var
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\NetWatch.Service.exe');
  RemoveFirewallRules();
  // Web console, SNMP trap receiver and syslog receiver; all bound to the NetWatch executable only.
  Netsh('advfirewall firewall add rule name="NetWatch HTTPS" dir=in action=allow protocol=TCP localport={#HttpsPort} program="' + ExePath + '" profile=any');
  Netsh('advfirewall firewall add rule name="NetWatch SNMP traps" dir=in action=allow protocol=UDP localport=162 program="' + ExePath + '" profile=any');
  Netsh('advfirewall firewall add rule name="NetWatch syslog UDP" dir=in action=allow protocol=UDP localport=514 program="' + ExePath + '" profile=any');
  Netsh('advfirewall firewall add rule name="NetWatch syslog TCP" dir=in action=allow protocol=TCP localport=514 program="' + ExePath + '" profile=any');
end;

procedure SecureDataFolder();
var
  Dir: String;
begin
  // Only SYSTEM (the service account) and local Administrators may read databases, certificates and logs.
  Dir := ExpandConstant('{commonappdata}\NetWatch');
  ForceDirectories(Dir);
  Run(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /C /Q');
end;

procedure InstallOrUpdateService();
var
  BinPath: String;
begin
  BinPath := 'binPath= "\"' + ExpandConstant('{app}\NetWatch.Service.exe') + '\""';
  if ServiceExists() then
    Sc('config {#ServiceName} ' + BinPath + ' start= delayed-auto DisplayName= "{#ServiceDisplayName}"')
  else
    Sc('create {#ServiceName} ' + BinPath + ' start= delayed-auto DisplayName= "{#ServiceDisplayName}"');
  Sc('description {#ServiceName} "NetWatch infrastructure monitoring: collectors, alerting and the web console (HTTPS port {#HttpsPort})."');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  FreshInstall := not ServiceExists();
  StopService();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PasswordFile: String;
  I: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    SecureDataFolder();
    InstallOrUpdateService();
    AddFirewallRules();
    Run(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');

    if FreshInstall then
    begin
      // The service creates the initial admin password on its first start; wait for it (max ~60 s).
      PasswordFile := ExpandConstant('{commonappdata}\NetWatch\initial-admin-password.txt');
      for I := 1 to 60 do
      begin
        if FileExists(PasswordFile) then Break;
        Sleep(1000);
      end;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
begin
  if CurPageID = wpFinished then
  begin
    Text := 'NetWatch is installed and the "NetWatch" service is running.' + #13#10#13#10 +
            'Dashboard: https://' + GetComputerNameString() + ':{#HttpsPort}/' + #13#10#13#10;
    if FreshInstall then
      Text := Text + 'Sign in as "admin" with the one-time password stored in:' + #13#10 +
              ExpandConstant('{commonappdata}\NetWatch\initial-admin-password.txt') + #13#10 +
              '(readable by Administrators only; delete it after the first sign-in).' + #13#10#13#10;
    Text := Text + 'The browser will warn about the self-signed certificate until you install an internal-CA certificate (see the installation guide).';
    WizardForm.FinishedLabel.Caption := Text;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService();
    if ServiceExists() then
      Sc('delete {#ServiceName}');
    RemoveFirewallRules();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\NetWatch');
    if DirExists(DataDir) and (not UninstallSilent()) then
    begin
      if MsgBox('Also delete all NetWatch data?' + #13#10#13#10 +
                DataDir + #13#10#13#10 +
                'This removes the databases (devices, alerts, history), settings, certificates, backups and logs. ' +
                'This cannot be undone. Choose "No" to keep the data for a later re-installation.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
