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

[Code]
var
  DbPage: TInputQueryWizardPage;

function EscapeConnectionValue(const Value: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '"';
  for I := 1 to Length(Value) do
  begin
    C := Value[I];
    if C = '\' then
      Result := Result + '\\'
    else if C = '"' then
      Result := Result + '\"'
    else
      Result := Result + C;
  end;
  Result := Result + '"';
end;

function EscapeJson(const Value: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(Value) do
  begin
    C := Value[I];
    if C = '\' then
      Result := Result + '\\'
    else if C = '"' then
      Result := Result + '\"'
    else if C = #13 then
      Result := Result + '\r'
    else if C = #10 then
      Result := Result + '\n'
    else if C = #9 then
      Result := Result + '\t'
    else
      Result := Result + C;
  end;
end;

function BuildConnectionString: String;
begin
  Result := 'Host=' + EscapeConnectionValue(DbPage.Values[0]) +
    ';Port=' + DbPage.Values[1] +
    ';Database=' + EscapeConnectionValue(DbPage.Values[2]) +
    ';Username=' + EscapeConnectionValue(DbPage.Values[3]) +
    ';Password=' + EscapeConnectionValue(DbPage.Values[4]) +
    ';SSL Mode=' + EscapeConnectionValue(DbPage.Values[5]) +
    ';Timeout=15;Command Timeout=30;Pooling=true';
end;

procedure InitializeWizard;
begin
  DbPage := CreateInputQueryPage(wpSelectDir,
    'Подключение к PostgreSQL',
    'Укажите адрес сервера базы данных',
    'Введите данные подключения, предоставленные администратором PostgreSQL или хостингом. Пароль хранится локально в профиле Windows.');
  DbPage.Add('IP-адрес или имя хоста PostgreSQL:', False);
  DbPage.Add('Порт:', False);
  DbPage.Add('Имя базы данных:', False);
  DbPage.Add('Пользователь базы данных:', False);
  DbPage.Add('Пароль:', True);
  DbPage.Add('Режим SSL (Disable, Allow, Prefer, Require, VerifyCA, VerifyFull):', False);
  DbPage.Values[0] := 'pg4.sweb.ru';
  DbPage.Values[1] := '5433';
  DbPage.Values[2] := '';
  DbPage.Values[3] := '';
  DbPage.Values[4] := '';
  DbPage.Values[5] := 'Prefer';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
  SslMode: String;
begin
  Result := True;
  if CurPageID = DbPage.ID then
  begin
    if Trim(DbPage.Values[0]) = '' then
    begin
      MsgBox('Укажите IP-адрес или имя хоста PostgreSQL.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    if (not TryStrToInt(DbPage.Values[1], Port)) or (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Порт должен быть числом от 1 до 65535.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    if (Trim(DbPage.Values[2]) = '') or (Trim(DbPage.Values[3]) = '') or (DbPage.Values[4] = '') then
    begin
      MsgBox('Заполните имя базы данных, пользователя и пароль.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    SslMode := Lowercase(Trim(DbPage.Values[5]));
    if (SslMode <> 'disable') and (SslMode <> 'allow') and (SslMode <> 'prefer') and
       (SslMode <> 'require') and (SslMode <> 'verifyca') and (SslMode <> 'verifyfull') then
    begin
      MsgBox('Укажите один из режимов SSL: Disable, Allow, Prefer, Require, VerifyCA или VerifyFull.', mbError, MB_OK);
      Result := False;
      exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDir: String;
  ConfigPath: String;
  ConfigJson: String;
  ConnectionString: String;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigDir := ExpandConstant('{localappdata}\LocaLoca');
    ForceDirectories(ConfigDir);
    ConfigPath := ConfigDir + '\server-config.json';
    ConnectionString := BuildConnectionString;
    ConfigJson := '{"connectionString":"' + EscapeJson(ConnectionString) + '"}';
    if not SaveStringToFile(ConfigPath, ConfigJson, False) then
      RaiseException('Не удалось сохранить настройки PostgreSQL: ' + ConfigPath);
  end;
end;
  