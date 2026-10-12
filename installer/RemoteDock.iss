#ifndef AppVersion
  #error AppVersion is required. Build using scripts\Build-Installer.ps1.
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{DCF0885E-5211-4432-974E-E375169A9E4F}
AppName=RemoteDock
AppVersion={#AppVersion}
AppPublisher=Morten Aasheim
AppPublisherURL=https://github.com/dizx/RemoteDock
AppSupportURL=https://github.com/dizx/RemoteDock/issues
AppUpdatesURL=https://github.com/dizx/RemoteDock/releases
AppCopyright=Copyright (c) 2026 Morten Aasheim
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Morten Aasheim
VersionInfoCopyright=Copyright (c) 2026 Morten Aasheim
VersionInfoDescription=RemoteDock Setup
DefaultDirName={autopf}\RemoteDock
DefaultGroupName=RemoteDock
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=RemoteDock-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\RemoteDock.App\Assets\RemoteDock.ico
UninstallDisplayIcon={app}\RemoteDock.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\RemoteDock"; Filename: "{app}\RemoteDock.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\RemoteDock"; Filename: "{app}\RemoteDock.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RemoteDock.exe"; Description: "{cm:LaunchProgram,RemoteDock}"; Flags: shellexec nowait postinstall skipifsilent
