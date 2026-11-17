; Installer für GlassBar (Inno Setup 6)
; Bauen: installer\build.ps1 (veröffentlicht, packt das Zip und ruft ISCC mit /DAppVersion=... auf)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\GlassBar-" + AppVersion
#endif

[Setup]
AppId={{6B0F3C2E-8F4D-4C55-9C2B-3E1F7A9D4B21}
AppName=GlassBar
AppVersion={#AppVersion}
AppVerName=GlassBar {#AppVersion}
AppPublisher=Xardas1906
AppPublisherURL=https://github.com/Xardas1906/GlassBar
AppSupportURL=https://github.com/Xardas1906/GlassBar/issues
AppUpdatesURL=https://github.com/Xardas1906/GlassBar/releases
DefaultDirName={autopf}\GlassBar
DefaultGroupName=GlassBar
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\publish
OutputBaseFilename=GlassBar-Setup-{#AppVersion}
SetupIconFile=..\GlassBar\Assets\GlassBar.ico
UninstallDisplayIcon={app}\GlassBar.exe
UninstallDisplayName=GlassBar
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.Autostart=Mit Windows starten
en.Autostart=Start with Windows
de.LaunchNow=GlassBar jetzt starten
en.LaunchNow=Start GlassBar now
de.NeedRuntime=GlassBar braucht die .NET 10 Desktop Runtime. Sie wird jetzt von Microsoft heruntergeladen und installiert.
en.NeedRuntime=GlassBar needs the .NET 10 Desktop Runtime. It will now be downloaded from Microsoft and installed.
de.RuntimeFailed=Die .NET 10 Desktop Runtime konnte nicht installiert werden. Bitte von https://dotnet.microsoft.com/download/dotnet/10.0 installieren.
en.RuntimeFailed=The .NET 10 Desktop Runtime could not be installed. Please install it from https://dotnet.microsoft.com/download/dotnet/10.0.
de.StillRunning=GlassBar läuft noch. Bitte mit Rechtsklick aufs Dock › „GlassBar beenden“ schließen und dann „Wiederholen“ wählen.
en.StillRunning=GlassBar is still running. Please close it (right-click the dock › Quit GlassBar) and choose Retry.
de.DeleteSettings=Auch deine GlassBar-Einstellungen (angeheftete Programme, Hintergrund usw.) löschen?
en.DeleteSettings=Also delete your GlassBar settings (pinned apps, wallpaper settings, etc.)?

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassBar"; Filename: "{app}\GlassBar.exe"
Name: "{autodesktop}\GlassBar"; Filename: "{app}\GlassBar.exe"; Tasks: desktopicon

[Run]
; Autostart für den angemeldeten Nutzer (nicht für das Admin-Konto des Setups)
Filename: "{app}\GlassBar.exe"; Parameters: "--autostart on"; Flags: runasoriginaluser waituntilterminated; Tasks: autostart
Filename: "{app}\GlassBar.exe"; Parameters: "--autostart off"; Flags: runasoriginaluser waituntilterminated; Tasks: not autostart
Filename: "{app}\GlassBar.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall runasoriginaluser

[UninstallRun]
Filename: "{app}\GlassBar.exe"; Parameters: "--autostart off"; Flags: waituntilterminated runhidden; RunOnceId: "RemoveAutostart"

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(Access: Cardinal; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function OpenMutex(Access: Cardinal; Inherit: Boolean; Name: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function SetEvent(H: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(H: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function GlassBarRunning(): Boolean;
var
  H: THandle;
begin
  H := OpenMutex(SYNCHRONIZE, False, 'GlassBar_SingleInstance');
  Result := H <> 0;
  if Result then CloseHandle(H);
end;

{ Laufendes GlassBar sauber beenden (gibt dabei die Windows-Taskleiste zurück). }
function QuitGlassBar(): Boolean;
var
  H: THandle;
  I: Integer;
begin
  Result := True;
  if not GlassBarRunning() then Exit;
  H := OpenEvent(EVENT_MODIFY_STATE, False, 'GlassBar_Quit');
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
  end;
  for I := 1 to 100 do
  begin
    if not GlassBarRunning() then Exit;
    Sleep(100);
  end;
  Result := not GlassBarRunning();
end;

function EnsureGlassBarClosed(): Boolean;
begin
  Result := QuitGlassBar();
  while not Result do
  begin
    if MsgBox(CustomMessage('StillRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then Exit;
    Result := QuitGlassBar();
  end;
end;

function DesktopRuntimeInstalled(): Boolean;
var
  F: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), F);
  if Result then FindClose(F);
end;

function InstallRuntime(): Boolean;
var
  Page: TDownloadWizardPage;
  Code: Integer;
begin
  Result := True;
  if DesktopRuntimeInstalled() then Exit;
  if not WizardSilent() then MsgBox(CustomMessage('NeedRuntime'), mbInformation, MB_OK);
  Page := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  Page.Clear;
  Page.Add('https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe', 'windowsdesktop-runtime-win-x64.exe', '');
  Page.Show;
  try
    try
      Page.Download;
      Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code);
    except
      Log(GetExceptionMessage);
    end;
  finally
    Page.Hide;
  end;
  Result := DesktopRuntimeInstalled();
  if not Result then MsgBox(CustomMessage('RuntimeFailed'), mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not EnsureGlassBarClosed() then
  begin
    Result := CustomMessage('StillRunning');
    Exit;
  end;
  if not InstallRuntime() then Result := CustomMessage('RuntimeFailed');
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureGlassBarClosed();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    if MsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\GlassBar'), True, True, True);
end;
