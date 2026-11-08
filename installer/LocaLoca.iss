#define BuildDir "..\Builds\Windows"
#define SetupIcon "Assets\LocaLoca-Installer.ico"

[Setup]
AppId={{1A6F10BC-6F72-4E6F-A481-C47C3B32A8DA}
AppName=Loca Loca
AppVersion=1.0.3
AppPublisher=Loca Loca
DefaultDirName={localappdata}\Programs\LocaLoca
DefaultGroupName=Loca Loca
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Uninstallable=yes
UninstallDisplayName=Loca Loca
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\Builds\Installer
OutputBaseFilename=LocaLoca-Setup
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\Assets\LocaLoca-Uninstaller.ico
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: checkedonce

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\tools\Install-CloudDependencies.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Assets\LocaLoca-Installer.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "Assets\LocaLoca-Uninstaller.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Loca Loca"; Filename: "{app}\LocaLoca.exe"
Name: "{autodesktop}\Loca Loca"; Filename: "{app}\LocaLoca.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LocaLoca.exe"; Description: "Запустить Loca Loca"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PowerShellPath: string;
  Parameters: string;
begin
  if CurStep = ssPostInstall then
  begin
    PowerShellPath := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
    Parameters :=
      '-NoProfile -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\tools\Install-CloudDependencies.ps1') + '"';

    WizardForm.StatusLabel.Caption :=
      'Настраиваем подключение к Supabase и проверяем компоненты первого запуска.';
    WizardForm.ProgressGauge.Style := npbstMarquee;
    WizardForm.Update;

    if not Exec(PowerShellPath, Parameters, ExpandConstant('{app}'),
      SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      RaiseException('Не удалось запустить настройку Supabase для Loca Loca.');
    end;

    if ResultCode <> 0 then
    begin
      RaiseException(
        'Не удалось настроить облачную БД. Код ошибки: ' +
        IntToStr(ResultCode) +
        '. Исправьте указанную проблему и запустите установщик повторно.'
      );
    end;

    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;
