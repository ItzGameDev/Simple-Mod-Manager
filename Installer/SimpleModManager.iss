#ifndef ReleaseDir
  #define ReleaseDir "..\dist\win-x64"
#endif

[Setup]
AppId={{E362C2A4-E4B6-446F-921A-0EBFD5E49E9C}
AppName=Simple Mod Manager
AppVersion=1.2.5
AppPublisher=Simple Mod Manager contributors
VersionInfoVersion=1.2.5
DefaultDirName={localappdata}\Programs\Simple Mod Manager
DefaultGroupName=Simple Mod Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#ReleaseDir}
OutputBaseFilename=SimpleModManagerSetup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\SimpleModManager.exe
WizardStyle=modern dark windows11
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts"

[Files]
Source: "{#ReleaseDir}\SimpleModManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Simple Mod Manager"; Filename: "{app}\SimpleModManager.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Simple Mod Manager"; Filename: "{app}\SimpleModManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SimpleModManager.exe"; Description: "Launch Simple Mod Manager"; Flags: nowait postinstall skipifsilent

#ifdef InstallDesktopRuntime
[Code]
const
  DesktopRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe';
  DesktopRuntimeHash = 'e61b030c1673edfa3c0844502eff83198c46af9e7df70dfc6e2efc5b11ea513a';
  DesktopRuntimeFile = 'windowsdesktop-runtime-10.0.15-win-x64.exe';

var
  RuntimeDownload: TDownloadWizardPage;

function HasFramework(const Root, Framework, RuntimeFile: String): Boolean;
var
  Search: TFindRec;
  Shared: String;
  Version: Int64;
begin
  Result := False;
  Shared := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Shared + '10.0.*', Search) then
    try
      repeat
        if (Pos('-', Search.Name) = 0) and StrToVersion(Search.Name, Version) and
          FileExists(Shared + Search.Name + '\' + RuntimeFile) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Search);
    finally
      FindClose(Search);
    end;
end;

function HasDesktopRuntimeAt(const Root: String): Boolean;
begin
  Result := (Root <> '') and FileExists(AddBackslash(Root) + 'dotnet.exe') and
    HasFramework(Root, 'Microsoft.WindowsDesktop.App', 'PresentationFramework.dll') and
    HasFramework(Root, 'Microsoft.NETCore.App', 'coreclr.dll');
end;

function HasDesktopRuntime: Boolean;
var
  Root: String;
begin
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64',
    'InstallLocation', Root) and HasDesktopRuntimeAt(Root) then
  begin
    Result := True;
    Exit;
  end;
  if IsArm64 then
    Result := HasDesktopRuntimeAt(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := HasDesktopRuntimeAt(ExpandConstant('{commonpf64}\dotnet'));
end;

procedure InitializeWizard;
begin
  RuntimeDownload := CreateDownloadPage('Downloading .NET Desktop Runtime',
    'Simple Mod Manager needs the Microsoft .NET 10 Desktop Runtime (x64).', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime then
  begin
    Log('.NET 10 Desktop Runtime is present. Skipping prerequisite download.');
    Exit;
  end;

  RuntimeDownload.Clear;
  RuntimeDownload.Add(DesktopRuntimeUrl, DesktopRuntimeFile, DesktopRuntimeHash);
  RuntimeDownload.Show;
  try
    try
      RuntimeDownload.Download;
    except
      Result := 'Could not download or verify the Microsoft .NET Desktop Runtime. Check your internet connection and try again. ' +
        GetExceptionMessage;
    end;
  finally
    RuntimeDownload.Hide;
  end;
  if Result <> '' then
    Exit;

  if not ShellExec('runas', ExpandConstant('{tmp}\' + DesktopRuntimeFile),
    '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Could not start the Microsoft .NET Desktop Runtime installer. Allow the Windows administrator prompt and try again.'
  else if (ExitCode = 3010) or (ExitCode = 1641) then
  begin
    NeedsRestart := True;
    Result := 'Restart Windows to finish installing the Microsoft .NET Desktop Runtime, then run setup again.';
  end
  else if (ExitCode <> 0) or not HasDesktopRuntime then
    Result := 'Microsoft .NET Desktop Runtime installation did not complete. Exit code: ' + IntToStr(ExitCode) + '.';
end;
#endif
