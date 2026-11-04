#define BuildDir "..\Builds\Windows"

[Setup]
AppId={{1A6F10BC-6F72-4E6F-A481-C47C3B32A8DA}
AppName=Loca Loca
AppVersion=1.0.0
AppPublisher=Loca Loca
DefaultDirName={localappdata}\Programs\LocaLoca
DefaultGroupName=Loca Loca
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\Builds\Installer
OutputBaseFilename=LocaLoca-Setup
UninstallDisplayIcon={app}\LocaLoca.exe
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Loca Loca"; Filename: "{app}\LocaLoca.exe"
Name: "{autodesktop}\Loca Loca"; Filename: "{app}\LocaLoca.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LocaLoca.exe"; Description: "Запустить Loca Loca"; Flags: postinstall nowait skipifsilent
