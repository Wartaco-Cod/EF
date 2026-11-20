; Inno Setup script for the Templar Knights game (players' PCs).
; 1. python tools/release/make_release.py <version>
; 2. Open this file in Inno Setup (https://jrsoftware.org/isinfo.php) and press Compile.
; The setup installs the game and launcher, makes Start menu and desktop shortcuts to the launcher,
; and asks for your server's address so the launcher knows where to update from.

#define AppName "Templar Knights"
#define AppVersion "0.8.2"
#define AppExe "TemplarKnightsLauncher.exe"

[Setup]
AppId={{8C5D3A3E-6F0B-4B8E-9C1E-5A1F2B7D7E11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Taco Factory
DefaultDirName={autopf}\Templar Knights
DefaultGroupName=Templar Knights
OutputDir=..\release
OutputBaseFilename=TemplarKnightsSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\TemplarKnights.exe

[Files]
Source: "..\release\TemplarKnights\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Templar Knights"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Templar Knights"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Code]
var ServerPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpSelectDir, 'Your server', 'Where is the Templar Knights server?',
    'Enter the address of the server you play on (for example play.example.com or 203.0.113.7). The launcher will update the game from it.');
  ServerPage.Add('Server address:', False);
  ServerPage.Values[0] := '127.0.0.1';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\launcher.json'),
      '{ "Server": "' + ServerPage.Values[0] + '", "UpdatePort": 7778 }', False);
end;

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start Templar Knights"; Flags: nowait postinstall skipifsilent
