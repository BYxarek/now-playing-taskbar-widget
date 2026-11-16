; Instalador do Now Playing - Taskbar Widget (per-user, sem UAC)
; Compilar: ISCC.exe setup.iss  (depois do publish self-contained do README)

#define MyAppName "Now Playing - Taskbar Widget"
#ifndef MyAppVersion
#define MyAppVersion "1.4.0"
#endif
#define MyAppPublisher "MechanicWB"
#define MyAppURL "https://github.com/BYxarek/now-playing-taskbar-widget"
#define MyAppExeName "SpotifyTaskbarWidget.exe"
#ifndef PublishDir
#define PublishDir "publish"
#endif

[Setup]
AppId={{7C1E9F42-5B8A-4D33-9C61-2F4A8E7B5D10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={userpf}\SpotifyTaskbarWidget
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=installer
OutputBaseFilename=SpotifyTaskbarWidget-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartProgram,{#MyAppName}}"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "{#MyAppExeName},*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "SpotifyTaskbarWidget"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart; Check: not IsWidgetUpdate

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillWidget"

[Code]
function IsWidgetUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WIDGETUPDATE|0}') = '1';
end;
