; Inno Setup 6 Script — Builds a single UtilitaryBTSink-Setup.exe installer
; Step 1: Run Install.bat (or build Release | x64) once
; Step 2: Open this file in Inno Setup Compiler (jrsoftware.org) and click Build -> Compile

[Setup]
AppId={{8F3B4A12-9C7D-4E51-B2A4-1D7E8F9A0C3B}
AppName=UtilitaryBTSink
AppVersion=1.0.0
DefaultDirName={localappdata}\Programs\UtilitaryBTSink
DefaultGroupName=UtilitaryBTSink
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\UtilitaryBTSink\Assets\app.ico
UninstallDisplayIcon={app}\UtilitaryBTSink.exe
OutputDir=Output
OutputBaseFilename=UtilitaryBTSink-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "autostart"; Description: "Start minimized automatically with Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\UtilitaryBTSink\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\UtilitaryBTSink"; Filename: "{app}\UtilitaryBTSink.exe"; IconFilename: "{app}\Assets\app.ico"
Name: "{autodesktop}\UtilitaryBTSink"; Filename: "{app}\UtilitaryBTSink.exe"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UtilitaryBTSink"; ValueData: """{app}\UtilitaryBTSink.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\UtilitaryBTSink.exe"; Description: "Launch UtilitaryBTSink"; Flags: nowait postinstall skipifsilent
