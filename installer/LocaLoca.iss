#define BuildDir "..\Builds\Windows"
#define SetupIcon "Assets\LocaLoca-Installer.ico"

[Setup]
AppId={{1A6F10BC-6F72-4E6F-A481-C47C3B32A8DA}
AppName=Loca Loca
AppVersion=1.0.1
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


[UninstallDelete]
; Remove all per-user Loca Loca settings, local photos, temp images and WebView2 profile.
Type: filesandordirs; Name: "{localappdata}\LocaLoca"
; Remove the default WebView2 profile created by older installer versions.
Type: filesandordirs; Name: "{app}\LocaLoca.exe.WebView2"

[Code]
procedure AppendLogLines(const LogPath, Prefix: String; const Lines: TArrayOfString);
var
  TaggedLines: TArrayOfString;
  I: Integer;
begin
  if GetArrayLength(Lines) = 0 then
    Exit;

  SetArrayLength(TaggedLines, GetArrayLength(Lines));
  for I := 0 to GetArrayLength(Lines) - 1 do
    TaggedLines[I] := Prefix + Lines[I];

  if not SaveStringsToUTF8File(LogPath, TaggedLines, True) then
    Log('Не удалось дописать вывод PowerShell в журнал: ' + LogPath);
end;

procedure AppendLogLine(const LogPath, Line: String);
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Line;
  if not SaveStringsToUTF8File(LogPath, Lines, True) then
    Log('Не удалось дописать строку в журнал: ' + LogPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PowerShellPath: string;
  Parameters: string;
  SetupLogPath: string;
  SetupLogLines: TArrayOfString;
  PowerShellOutput: TExecOutput;
  ErrorMessage: string;
  I: Integer;
  ExecSucceeded: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    SetupLogPath := ExpandConstant('{localappdata}\LocaLoca\Logs\CloudSetup.log');
    if not ForceDirectories(ExtractFileDir(SetupLogPath)) then
      RaiseException('Не удалось создать каталог журнала: ' + ExtractFileDir(SetupLogPath));

    DeleteFile(SetupLogPath);
    SetArrayLength(SetupLogLines, 1);
    SetupLogLines[0] := 'Loca Loca installer: PowerShell setup started.';
    if not SaveStringsToUTF8File(SetupLogPath, SetupLogLines, False) then
      RaiseException('Не удалось создать диагностический журнал: ' + SetupLogPath + #13#10 +
        'Проверь права записи в каталог LocalAppData.');

    Parameters :=
      '-NoProfile -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\tools\Install-CloudDependencies.ps1') +
      '" -CloudApiUrl "https://localoca-api.onrender.com" -LogPath "' +
      SetupLogPath + '"';

    AppendLogLine(SetupLogPath, 'PowerShell: ' + PowerShellPath);
    AppendLogLine(SetupLogPath, 'Script: ' + ExpandConstant('{app}\tools\Install-CloudDependencies.ps1'));
    AppendLogLine(SetupLogPath, 'Working directory: ' + ExpandConstant('{app}'));

    WizardForm.StatusLabel.Caption :=
      'Настраиваем облачное подключение Loca Loca и проверяем WebView2.';
    WizardForm.ProgressGauge.Style := npbstMarquee;
    WizardForm.Update;

    ExecSucceeded := ExecAndCaptureOutput(
      PowerShellPath, Parameters, ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode, PowerShellOutput);

    AppendLogLines(SetupLogPath, 'STDOUT: ', PowerShellOutput.StdOut);
    AppendLogLines(SetupLogPath, 'STDERR: ', PowerShellOutput.StdErr);
    if PowerShellOutput.Error then
      AppendLogLine(SetupLogPath, 'WARNING: Ошибка при чтении вывода PowerShell.');

    if not ExecSucceeded then
    begin
      ErrorMessage := 'Не удалось запустить PowerShell. Код Windows: ' +
        IntToStr(ResultCode) + ' (' + SysErrorMessage(ResultCode) + ').';
      AppendLogLine(SetupLogPath, ErrorMessage);
      RaiseException(ErrorMessage + #13#10 + 'Журнал: ' + SetupLogPath);
    end;

    AppendLogLine(SetupLogPath, 'PowerShell exit code: ' + IntToStr(ResultCode));

    if ResultCode <> 0 then
    begin
      ErrorMessage :=
        'Не удалось завершить настройку Loca Loca. Код ошибки PowerShell: ' +
        IntToStr(ResultCode) + '.' + #13#10#13#10;

      if LoadStringsFromFile(SetupLogPath, SetupLogLines) then
      begin
        for I := 0 to GetArrayLength(SetupLogLines) - 1 do
          ErrorMessage := ErrorMessage + SetupLogLines[I] + #13#10;
      end
      else
        ErrorMessage := ErrorMessage + 'Не удалось прочитать журнал: ' + SetupLogPath;

      ErrorMessage := ErrorMessage + #13#10 + 'Журнал: ' + SetupLogPath;
      RaiseException(ErrorMessage);
    end;

    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfiguredApiUrl: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove only the environment value owned by this installer. Preserve a
    // custom LOCA_API_BASE_URL if the user changed it to another endpoint.
    if RegQueryStringValue(HKCU, 'Environment', 'LOCA_API_BASE_URL', ConfiguredApiUrl) then
    begin
      if Lowercase(Trim(ConfiguredApiUrl)) = 'https://localoca-api.onrender.com' then
      begin
        if RegDeleteValue(HKCU, 'Environment', 'LOCA_API_BASE_URL') then
          Log('Removed Loca Loca LOCA_API_BASE_URL user environment variable.');
      end;
    end;
  end;
end;
