; Windows installer: datapeach-windows-x64-setup.exe (Inno Setup 6). Per user, no administrator: installs the same portable exe
; into %LOCALAPPDATA%\Programs\DataPeach, adds it to the Start menu (so Windows search finds it) and to
; Settings > Apps with its uninstaller. Settings in %APPDATA%\peachapps\DataPeach are kept on uninstall.
; Build after build.cmd:  iscc /DAppVersion=1.2.3 installer\DataPeach.iss   ->  dist\datapeach-windows-x64-setup.exe
; The app updates itself by running a newer setup with /SILENT; installed.txt tells it that it was installed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{63454123-D8C3-4461-B36D-AF7F39A56DAF}
AppName=DataPeach
AppVersion={#AppVersion}
AppVerName=DataPeach {#AppVersion}
AppPublisher=DataPeach
AppPublisherURL=https://github.com/rileydev23/datapeach
AppSupportURL=https://github.com/rileydev23/datapeach/issues
AppUpdatesURL=https://github.com/rileydev23/datapeach/releases
DefaultDirName={localappdata}\Programs\DataPeach
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=datapeach-windows-x64-setup
SetupIconFile=..\src\DataPeach\Assets\app.ico
UninstallDisplayIcon={app}\DataPeach.exe
UninstallDisplayName=DataPeach
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running DataPeach before replacing it (the app also exits by itself when it updates).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\DataPeach.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installed.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\DataPeach"; Filename: "{app}\DataPeach.exe"
Name: "{userdesktop}\DataPeach"; Filename: "{app}\DataPeach.exe"; Tasks: desktopicon

[Run]
; Interactive install: a "Launch DataPeach" checkbox. Silent install (an update from the app): reopen it right away.
Filename: "{app}\DataPeach.exe"; Description: "{cm:LaunchProgram,DataPeach}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\DataPeach.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; Left behind by an update that replaced the exe in place.
Type: files; Name: "{app}\DataPeach.exe.old"
Type: files; Name: "{app}\DataPeach.exe.new"
Type: dirifempty; Name: "{app}"
