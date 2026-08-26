; Inno Setup Script for FastExplorer
#define MyAppName "FastExplorer"

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppPublisher "FastExplorer"
#define MyAppExeName "FastExplorer.exe"

#ifndef AppArch
#define AppArch "x64"
#endif

#ifndef OutputBaseFilename
#define OutputBaseFilename "FastExplorer_Setup_v" + MyAppVersion
#endif

[Setup]
AppId={{D8C9A3F1-5274-4C2E-9C8F-7C91B4C72E60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=dist
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=icon.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible arm64
PrivilegesRequired=admin

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "bin\Release\net10.0-windows10.0.19041.0\win-{#AppArch}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\FastExplorer"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"; ValueType: string; ValueName: "DisabledHotkeys"; ValueData: "E"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillRunningProcesses();
var
  ResultCode: Integer;
begin
  Exec('schtasks.exe', '/end /tn "FastExplorer_Background"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 注意: /t フラグを付けると、FastExplorer から起動されたインストーラー自身まで子プロセスとして強制終了されてしまうため /t は使用しない
  Exec('cmd.exe', '/c taskkill.exe /f /im FastExplorer.exe /im FastExplorerWatcher.exe >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('powershell.exe', '-NoProfile -NonInteractive -Command "Get-Process -Name FastExplorer,FastExplorerWatcher -ErrorAction SilentlyContinue | Stop-Process -Force"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

procedure CleanStartupShortcuts();
var
  StartupLnk: String;
begin
  StartupLnk := ExpandConstant('{userstartup}\FastExplorer.lnk');
  if FileExists(StartupLnk) then
    DeleteFile(StartupLnk);
  StartupLnk := ExpandConstant('{commonstartup}\FastExplorer.lnk');
  if FileExists(StartupLnk) then
    DeleteFile(StartupLnk);
end;

procedure CleanLegacyRegistryKeys();
begin
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Folder\shell\open');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Folder\shell\explore');
end;

procedure CleanLegacyAppFolders();
var
  FindRec: TFindRec;
  AppDir, SubDirName, FullSubPath: String;
begin
  // {app} 配下の不要な非対応言語フォルダー（af-ZA, ar-SA等）を削除
  AppDir := ExpandConstant('{app}');
  if (AppDir <> '') and DirExists(AppDir) then
  begin
    if FindFirst(AppDir + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
             (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            SubDirName := Lowercase(FindRec.Name);
            if (SubDirName <> 'assets') and
               (SubDirName <> 'ja-jp') and
               (SubDirName <> 'ja') and
               (SubDirName <> 'en-us') and
               (SubDirName <> 'en') then
            begin
              FullSubPath := AppDir + '\' + FindRec.Name;
              DelTree(FullSubPath, True, True, True);
            end;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  KillRunningProcesses();
  CleanStartupShortcuts();
  CleanLegacyRegistryKeys();
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningProcesses();
  CleanStartupShortcuts();
  CleanLegacyRegistryKeys();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningProcesses();
  CleanStartupShortcuts();
  CleanLegacyRegistryKeys();
  Result := True;
end;

procedure RegisterScheduledTask();
var
  ResultCode: Integer;
  WatcherPath, XmlPath, XmlContent: String;
begin
  WatcherPath := ExpandConstant('{app}\FastExplorerWatcher.exe');
  XmlPath := ExpandConstant('{tmp}\FastExplorer_Task.xml');

  XmlContent := 
    '<?xml version="1.0" encoding="UTF-16"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #13#10 +
    '  <RegistrationInfo>' + #13#10 +
    '    <Description>FastExplorer Background Resident for Instant Launch</Description>' + #13#10 +
    '  </RegistrationInfo>' + #13#10 +
    '  <Triggers>' + #13#10 +
    '    <LogonTrigger>' + #13#10 +
    '      <Enabled>true</Enabled>' + #13#10 +
    '    </LogonTrigger>' + #13#10 +
    '  </Triggers>' + #13#10 +
    '  <Principals>' + #13#10 +
    '    <Principal id="Author">' + #13#10 +
    '      <LogonType>InteractiveToken</LogonType>' + #13#10 +
    '      <RunLevel>LeastPrivilege</RunLevel>' + #13#10 +
    '    </Principal>' + #13#10 +
    '  </Principals>' + #13#10 +
    '  <Settings>' + #13#10 +
    '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>' + #13#10 +
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' + #13#10 +
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' + #13#10 +
    '    <AllowHardTerminate>true</AllowHardTerminate>' + #13#10 +
    '    <StartWhenAvailable>false</StartWhenAvailable>' + #13#10 +
    '    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>' + #13#10 +
    '    <IdleSettings>' + #13#10 +
    '      <StopOnIdleEnd>false</StopOnIdleEnd>' + #13#10 +
    '      <RestartOnIdle>false</RestartOnIdle>' + #13#10 +
    '    </IdleSettings>' + #13#10 +
    '    <AllowStartOnDemand>true</AllowStartOnDemand>' + #13#10 +
    '    <Enabled>true</Enabled>' + #13#10 +
    '    <Hidden>false</Hidden>' + #13#10 +
    '    <RunOnlyIfIdle>false</RunOnlyIfIdle>' + #13#10 +
    '    <WakeToRun>false</WakeToRun>' + #13#10 +
    '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>' + #13#10 +
    '    <Priority>7</Priority>' + #13#10 +
    '  </Settings>' + #13#10 +
    '  <Actions Context="Author">' + #13#10 +
    '    <Exec>' + #13#10 +
    '      <Command>' + WatcherPath + '</Command>' + #13#10 +
    '    </Exec>' + #13#10 +
    '  </Actions>' + #13#10 +
    '</Task>';

  SaveStringToFile(XmlPath, XmlContent, False);
  Exec('schtasks.exe', '/create /tn "FastExplorer_Background" /xml "' + XmlPath + '" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(XmlPath);

  // インストール完了直後に Watcher を起動して Win+E 即応開始
  Exec(WatcherPath, '', '', SW_HIDE, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    KillRunningProcesses();
    CleanStartupShortcuts();
    CleanLegacyRegistryKeys();
    CleanLegacyAppFolders();
  end
  else if CurStep = ssPostInstall then
  begin
    CleanLegacyRegistryKeys();
    CleanLegacyAppFolders();
    RegisterScheduledTask();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunningProcesses();
    CleanStartupShortcuts();
    CleanLegacyRegistryKeys();
    CleanLegacyAppFolders();
    Exec('schtasks.exe', '/delete /tn "FastExplorer_Background" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
