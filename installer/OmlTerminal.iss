; OML Terminal - Windows installer (Inno Setup 6)
; Build:  dotnet publish src\OmlTerminal.App\OmlTerminal.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish\setup-staging
;         ISCC installer\OmlTerminal.iss            -> publish\OML-Terminal-Setup-<version>.exe
; The version is read from the published OmlTerminal.exe, so bump <Version> in OmlTerminal.App.csproj only.

#define AppName "OML Terminal"
#define AppExe "OmlTerminal.exe"
#define Staging "..\publish\setup-staging"
#define AppVersion GetVersionNumbersString(Staging + "\" + AppExe)
; GetVersionNumbersString gives 0.2.0.0 - drop the trailing .0 for the file name / display
#define ShortVersion Copy(AppVersion, 1, RPos(".", AppVersion) - 1)

[Setup]
AppId={{6F1C2D8E-4B7A-4E0F-9C3D-0A5B7E2F9C41}
AppName={#AppName}
AppVersion={#ShortVersion}
AppVerName={#AppName} {#ShortVersion}
AppPublisher=OML Labs
AppPublisherURL=https://omllabs.com
AppSupportURL=https://omllabs.com
AppUpdatesURL=https://omllabs.com/downloads.html
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); the dialog lets people choose "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\publish
OutputBaseFilename=OML-Terminal-Setup-{#ShortVersion}
SetupIconFile=..\src\OmlTerminal.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "links"; Description: "Open oml-terminal:// links (OML Labs lab nodes) with OML Terminal"; GroupDescription: "Integration:"

[Files]
Source: "{#Staging}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same keys the app's own Help > "Register oml-terminal:// links" writes; removed again on uninstall.
Root: HKA; Subkey: "Software\Classes\oml-terminal"; ValueType: string; ValueName: ""; ValueData: "URL:OML Terminal"; Flags: uninsdeletekey; Tasks: links
Root: HKA; Subkey: "Software\Classes\oml-terminal"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: links
Root: HKA; Subkey: "Software\Classes\oml-terminal\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: links
Root: HKA; Subkey: "Software\Classes\oml-terminal\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: links

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Sessions, the password vault and settings live in %USERPROFILE%\.oml-terminal and are deliberately kept on
; uninstall, so reinstalling or upgrading never loses anyone's saved devices.
