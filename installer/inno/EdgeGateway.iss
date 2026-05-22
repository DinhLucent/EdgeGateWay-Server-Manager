#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef StageDir
#define StageDir "..\..\output\edge-gateway\installer\stage"
#endif

#ifndef OutputDir
#define OutputDir "..\..\output\edge-gateway\installer"
#endif

#ifndef OutputBaseFilename
#define OutputBaseFilename "EdgeGatewaySetup-" + AppVersion
#endif

#ifndef SelfContained
#define SelfContained "false"
#endif

#ifndef PrerequisiteVersion
#define PrerequisiteVersion "10.0.8"
#endif

[Setup]
AppId={{7D3D8A54-7FD3-49A6-8DD9-A0707E891B1F}
AppName=EdgeGateway
AppVersion={#AppVersion}
AppPublisher=LabInfoSys
AppPublisherURL=https://localhost/
DefaultDirName={autopf}\LabInfoSys\EdgeGateway
DefaultGroupName=EdgeGateway
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\manager\EdgeGateway.Manager.exe

[Tasks]
Name: "desktopserver"; Description: "Create a desktop shortcut for EdgeGateway Server Manager"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "desktopdevice"; Description: "Create a desktop shortcut for EdgeGateway Device Manager"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostarttray"; Description: "Start EdgeGateway Tray when Windows starts"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "{#StageDir}\service\*"; DestDir: "{app}\service"; Excludes: "*.pdb,appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\manager\*"; DestDir: "{app}\manager"; Excludes: "*.pdb,appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\tray\*"; DestDir: "{app}\tray"; Excludes: "*.pdb,appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\defaults\devices.json"; DestDir: "{app}\defaults"; Flags: ignoreversion

[Icons]
Name: "{group}\EdgeGateway Server Manager"; Filename: "{app}\manager\EdgeGateway.Manager.exe"; WorkingDir: "{app}\manager"
Name: "{group}\EdgeGateway Device Manager"; Filename: "{app}\manager\EdgeGateway.Manager.exe"; Parameters: "--device-manager"; WorkingDir: "{app}\manager"
Name: "{group}\EdgeGateway Tray"; Filename: "{app}\tray\EdgeGateway.Tray.exe"; WorkingDir: "{app}\tray"
Name: "{group}\EdgeGateway Logs"; Filename: "{commonappdata}\EdgeGateway\logs"
Name: "{commondesktop}\EdgeGateway Server Manager"; Filename: "{app}\manager\EdgeGateway.Manager.exe"; WorkingDir: "{app}\manager"; Tasks: desktopserver
Name: "{commondesktop}\EdgeGateway Device Manager"; Filename: "{app}\manager\EdgeGateway.Manager.exe"; Parameters: "--device-manager"; WorkingDir: "{app}\manager"; Tasks: desktopdevice
Name: "{commonstartup}\EdgeGateway Tray"; Filename: "{app}\tray\EdgeGateway.Tray.exe"; WorkingDir: "{app}\tray"; Tasks: autostarttray

[Run]
Filename: "{app}\tray\EdgeGateway.Tray.exe"; Description: "Start EdgeGateway Tray"; Flags: nowait postinstall skipifsilent
Filename: "{app}\manager\EdgeGateway.Manager.exe"; Description: "Open EdgeGateway Server Manager"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  ServiceName = 'EdgeGateway.DeviceService';
  ServiceDisplayName = 'EdgeGateway Device Service';
  DotNetMajorVersion = '10';
  AspNetRuntimeFramework = 'Microsoft.AspNetCore.App';
  DesktopRuntimeFramework = 'Microsoft.WindowsDesktop.App';
  AspNetRuntimeFileName = 'aspnetcore-runtime-{#PrerequisiteVersion}-win-x64.exe';
  DesktopRuntimeFileName = 'windowsdesktop-runtime-{#PrerequisiteVersion}-win-x64.exe';
  AspNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/{#PrerequisiteVersion}/aspnetcore-runtime-{#PrerequisiteVersion}-win-x64.exe';
  DesktopRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{#PrerequisiteVersion}/windowsdesktop-runtime-{#PrerequisiteVersion}-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function InstallerIsSelfContained(): Boolean;
begin
  Result := CompareText('{#SelfContained}', 'true') = 0;
end;

function RuntimeInstalled(FrameworkName: string): Boolean;
var
  FindRec: TFindRec;
  FrameworkRoot: string;
begin
  FrameworkRoot := ExpandConstant('{pf64}\dotnet\shared\') + FrameworkName;
  Result := FindFirst(FrameworkRoot + '\' + DotNetMajorVersion + '.*', FindRec);
  if Result then
  begin
    FindClose(FindRec);
  end;
end;

function MissingRuntimeSummary(): string;
begin
  Result := '';
  if not RuntimeInstalled(DesktopRuntimeFramework) then
  begin
    Result := Result + '- .NET Desktop Runtime {#PrerequisiteVersion} x64' + #13#10;
  end;
  if not RuntimeInstalled(AspNetRuntimeFramework) then
  begin
    Result := Result + '- ASP.NET Core Runtime {#PrerequisiteVersion} x64' + #13#10;
  end;
end;

function InstallRuntimeInstaller(FileName: string; DisplayName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{tmp}\') + FileName,
    '/install /quiet /norestart',
    '',
    SW_SHOW,
    ewWaitUntilTerminated,
    ResultCode
  ) and ((ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638));

  if not Result then
  begin
    MsgBox(
      DisplayName + ' could not be installed. EdgeGateway setup cannot continue. Exit code: ' + IntToStr(ResultCode),
      mbCriticalError,
      MB_OK
    );
  end;
end;

function DownloadAndInstallPrerequisites(): Boolean;
var
  NeedAspNetRuntime: Boolean;
  NeedDesktopRuntime: Boolean;
  Error: string;
begin
  Result := True;
  if InstallerIsSelfContained() then
  begin
    exit;
  end;

  NeedDesktopRuntime := not RuntimeInstalled(DesktopRuntimeFramework);
  NeedAspNetRuntime := not RuntimeInstalled(AspNetRuntimeFramework);
  if not NeedDesktopRuntime and not NeedAspNetRuntime then
  begin
    exit;
  end;

  if not WizardSilent() then
  begin
    if MsgBox(
      'EdgeGateway needs these Microsoft runtime components:' + #13#10 + #13#10 +
      MissingRuntimeSummary() + #13#10 +
      'Setup can download and install them now from Microsoft, then continue.',
      mbConfirmation,
      MB_YESNO
    ) <> IDYES then
    begin
      Result := False;
      exit;
    end;
  end;

  DownloadPage.Clear;
  if NeedDesktopRuntime then
  begin
    DownloadPage.Add(DesktopRuntimeUrl, DesktopRuntimeFileName, '');
  end;
  if NeedAspNetRuntime then
  begin
    DownloadPage.Add(AspNetRuntimeUrl, AspNetRuntimeFileName, '');
  end;

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
      begin
        Log('Prerequisite download aborted by user.');
      end
      else
      begin
        Error := Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage]);
        SuppressibleMsgBox(AddPeriod(Error), mbCriticalError, MB_OK, IDOK);
      end;
      Result := False;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  if NeedDesktopRuntime then
  begin
    Result := InstallRuntimeInstaller(DesktopRuntimeFileName, '.NET Desktop Runtime');
    if not Result then
    begin
      exit;
    end;
  end;

  if NeedAspNetRuntime then
  begin
    Result := InstallRuntimeInstaller(AspNetRuntimeFileName, 'ASP.NET Core Runtime');
  end;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage('Downloading prerequisites', 'Setup is downloading Microsoft .NET runtime components.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    Result := DownloadAndInstallPrerequisites();
  end;
end;

function RunHidden(FileName: string; Parameters: string; var ResultCode: Integer): Boolean;
begin
  Result := Exec(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function RunSc(Parameters: string; var ResultCode: Integer): Boolean;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Parameters, ResultCode);
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunSc('query ' + ServiceName, ResultCode) and (ResultCode = 0);
end;

procedure StopEdgeGatewayProcesses();
var
  ResultCode: Integer;
begin
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM EdgeGateway.Tray.exe /F', ResultCode);
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM EdgeGateway.Manager.exe /F', ResultCode);
end;

procedure StopEdgeGatewayService();
var
  ResultCode: Integer;
begin
  if ServiceExists() then
  begin
    RunSc('stop ' + ServiceName, ResultCode);
    Sleep(1500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEdgeGatewayProcesses();
  StopEdgeGatewayService();
  Result := '';
end;

function EscapeJson(Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
end;

procedure SeedProgramData();
var
  ConfigDir: string;
  DatabaseDir: string;
  DevicesPath: string;
  ManagerConfigPath: string;
  DatabasePath: string;
  ConfigJson: string;
begin
  ConfigDir := ExpandConstant('{commonappdata}\EdgeGateway\config');
  DatabaseDir := ExpandConstant('{commonappdata}\EdgeGateway\database');
  ForceDirectories(ConfigDir);
  ForceDirectories(DatabaseDir);
  ForceDirectories(ExpandConstant('{commonappdata}\EdgeGateway\logs'));
  ForceDirectories(ExpandConstant('{commonappdata}\EdgeGateway\spool'));
  ForceDirectories(ExpandConstant('{commonappdata}\EdgeGateway\orders'));
  ForceDirectories(ExpandConstant('{commonappdata}\EdgeGateway\packets'));
  ForceDirectories(ExpandConstant('{commonappdata}\EdgeGateway\security'));

  DevicesPath := ConfigDir + '\devices.json';
  if not FileExists(DevicesPath) then
  begin
    CopyFile(ExpandConstant('{app}\defaults\devices.json'), DevicesPath, False);
  end;

  ManagerConfigPath := ConfigDir + '\service-manager.json';
  if not FileExists(ManagerConfigPath) then
  begin
    DatabasePath := DatabaseDir + '\edgegateway.db';
    ConfigJson :=
      '{' + #13#10 +
      '  "server": {' + #13#10 +
      '    "httpPort": 18082,' + #13#10 +
      '    "httpsPort": 0,' + #13#10 +
      '    "mainLogLevel": "Information",' + #13#10 +
      '    "channelLogLevel": "Information"' + #13#10 +
      '  },' + #13#10 +
      '  "database": {' + #13#10 +
      '    "type": "SQLite",' + #13#10 +
      '    "database": "' + EscapeJson(DatabasePath) + '"' + #13#10 +
      '  }' + #13#10 +
      '}' + #13#10;
    SaveStringToFile(ManagerConfigPath, ConfigJson, False);
  end;
end;

procedure ConfigureService();
var
  ResultCode: Integer;
  ServiceExePath: string;
begin
  ServiceExePath := ExpandConstant('{app}\service\EdgeGateway.Service.exe');

  if ServiceExists() then
  begin
    RunSc('config ' + ServiceName + ' binPath= "' + ServiceExePath + '" start= delayed-auto DisplayName= "' + ServiceDisplayName + '"', ResultCode);
  end
  else
  begin
    RunSc('create ' + ServiceName + ' binPath= "' + ServiceExePath + '" start= auto DisplayName= "' + ServiceDisplayName + '"', ResultCode);
    RunSc('config ' + ServiceName + ' start= delayed-auto', ResultCode);
  end;

  RunSc('description ' + ServiceName + ' "LabInfoSys analyzer device communication gateway."', ResultCode);
  RunSc('failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/300000', ResultCode);

  if not (RunSc('start ' + ServiceName, ResultCode) and ((ResultCode = 0) or (ResultCode = 1056))) then
  begin
    MsgBox('EdgeGateway was installed, but the Windows Service could not be started. Open EdgeGateway Server Manager or Windows Services to inspect the service state.', mbInformation, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SeedProgramData();
    ConfigureService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopEdgeGatewayProcesses();
    if ServiceExists() then
    begin
      RunSc('stop ' + ServiceName, ResultCode);
      Sleep(1500);
      RunSc('delete ' + ServiceName, ResultCode);
    end;
  end;
end;
