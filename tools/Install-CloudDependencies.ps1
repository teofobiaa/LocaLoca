[CmdletBinding()]
param(
    [string]$UserConfigPath = (Join-Path $env:LOCALAPPDATA 'LocaLoca\server-config.json'),
    [string]$LogPath = (Join-Path $env:TEMP 'LocaLoca-CloudSetup.log'),
    # Ordinary installations always use the hosted API. The legacy direct
    # Supabase setup requires an explicit switch and is never selected implicitly.
    [string]$CloudApiUrl = 'https://localoca-api.onrender.com',
    [switch]$UseLegacySupabaseSetup
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WebViewRuntimeGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$script:SetupLogReady = $false

function Write-SetupLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    $entry = "[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}" -f (Get-Date), $Message, [Environment]::NewLine
    try {
        $parent = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            [IO.Directory]::CreateDirectory($parent) | Out-Null
        }
        [IO.File]::AppendAllText($LogPath, $entry, [Text.UTF8Encoding]::new($false))
        $script:SetupLogReady = $true
    }
    catch {
        Write-Warning "Не удалось записать журнал '$LogPath': $($_.Exception.Message)"
    }
}

try {
    $parent = Split-Path -Parent $LogPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    if (Test-Path -LiteralPath $LogPath) {
        Remove-Item -LiteralPath $LogPath -Force -ErrorAction Stop
    }
    Write-SetupLog 'Запуск настройки Loca Loca.'
}
catch {
    Write-Warning "Не удалось подготовить журнал '$LogPath': $($_.Exception.Message)"
}

trap {
    $record = $_
    $lineNumber = 0
    $command = ''
    if ($record.InvocationInfo) {
        $lineNumber = $record.InvocationInfo.ScriptLineNumber
        $command = $record.InvocationInfo.Line
    }
    $detail = @(
        'Setup failed.'
        "Message: $($record.Exception.Message)"
        "Exception: $($record.Exception.GetType().FullName)"
        "Script: $($record.InvocationInfo.ScriptName)"
        "Script line: $lineNumber"
        "Command: $command"
        "Position: $($record.InvocationInfo.PositionMessage)"
        "Category: $($record.CategoryInfo)"
        "FullyQualifiedErrorId: $($record.FullyQualifiedErrorId)"
    ) -join [Environment]::NewLine
    Write-SetupLog $detail
    try {
        $parent = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            [IO.Directory]::CreateDirectory($parent) | Out-Null
        }
        [IO.File]::WriteAllText($LogPath, $detail + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
    catch {
        Write-Host "Не удалось записать журнал '$LogPath': $($_.Exception.Message)" -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host "Ошибка настройки Loca Loca: $($record.Exception.Message)" -ForegroundColor Red
    Write-Host "Путь журнала: $LogPath" -ForegroundColor Yellow
    exit 1
}

function Read-RequiredValue {
    param([Parameter(Mandatory = $true)][string]$Prompt, [string]$Default = '')
    while ($true) {
        $suffix = if ($Default) { " [$Default]" } else { '' }
        $value = Read-Host "$Prompt$suffix"
        if ([string]::IsNullOrWhiteSpace($value) -and $Default) { return $Default }
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        Write-Host 'Это значение обязательно.' -ForegroundColor Yellow
    }
}

function Test-WebView2Installed {
    $registryPaths = @(
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebViewRuntimeGuid",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$WebViewRuntimeGuid",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebViewRuntimeGuid"
    )
    foreach ($registryPath in $registryPaths) {
        $entry = Get-ItemProperty -Path $registryPath -Name 'pv' -ErrorAction SilentlyContinue
        if ($entry -and $entry.pv -and $entry.pv -ne '0.0.0.0') { return $true }
    }
    return $false
}

function Ensure-WebView2 {
    Write-SetupLog 'Проверка Microsoft Edge WebView2 Runtime.'
    if (Test-WebView2Installed) {
        Write-Host 'WebView2 Runtime уже установлен.' -ForegroundColor Green
        return
    }

    Write-Host 'Установка Microsoft Edge WebView2 Runtime...' -ForegroundColor Cyan
    $installerPath = Join-Path $env:TEMP 'LocaLoca-WebView2-Setup.exe'
    if (Test-Path -LiteralPath $installerPath) { Remove-Item -LiteralPath $installerPath -Force }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-SetupLog "Загрузка WebView2 Runtime в '$installerPath'."
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $installerPath -UseBasicParsing

    $signature = Get-AuthenticodeSignature -FilePath $installerPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
        throw 'Не удалось подтвердить подпись установщика WebView2 от Microsoft.'
    }

    Write-SetupLog 'Запуск установщика WebView2 Runtime.'
    $process = Start-Process -FilePath $installerPath -ArgumentList @('/silent', '/install') -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Не удалось установить WebView2 Runtime. Код установщика: $($process.ExitCode)."
    }

    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    if (-not (Test-WebView2Installed)) { throw 'Установщик завершился, но WebView2 Runtime не обнаружен.' }
    Write-Host 'WebView2 Runtime установлен.' -ForegroundColor Green
}

function Get-ConfiguredCloudApiUrl {
    if ($UseLegacySupabaseSetup) {
        Write-SetupLog 'Явно включён режим прямой настройки Supabase.'
        return $null
    }

    $value = $CloudApiUrl
    if ([string]::IsNullOrWhiteSpace($value)) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            $configuredValue = [Environment]::GetEnvironmentVariable('LOCA_API_BASE_URL', $scope)
            if (-not [string]::IsNullOrWhiteSpace($configuredValue)) {
                $value = $configuredValue
                break
            }
        }
    }

    # A missing/empty optional argument must not send an unattended installer
    # into Read-Host prompts for Supabase credentials.
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = 'https://localoca-api.onrender.com'
    }

    $value = $value.Trim()
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps) {
        throw 'Параметр CloudApiUrl должен содержать корректный HTTPS-адрес Render.'
    }

    Write-SetupLog "Режим облачного API: $($uri.GetLeftPart([UriPartial]::Authority))."
    return $uri
}

function Test-LoopbackHost {
    param([string]$HostName)
    $normalized = $HostName.Trim().Trim('[', ']')
    return $normalized -in @('localhost', '127.0.0.1', '::1')
}

function Read-ExistingConfig {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Не удалось прочитать существующий файл конфигурации. Он не будет перезаписан автоматически: $Path. $($_.Exception.Message)"
    }
}

function Protect-Password {
    param([Parameter(Mandatory = $true)][string]$Password)
    Add-Type -AssemblyName System.Security
    $bytes = [Text.Encoding]::UTF8.GetBytes($Password)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Convert]::ToBase64String($protected)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Write-EncryptedConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][System.Data.Common.DbConnectionStringBuilder]$Connection
    )
    $encryptedPassword = Protect-Password -Password ([string]$Connection['Password'])
    $config = [ordered]@{
        version = 2
        host = [string]$Connection['Host']
        port = [int]$Connection['Port']
        database = [string]$Connection['Database']
        username = [string]$Connection['Username']
        encryptedPassword = $encryptedPassword
    } | ConvertTo-Json -Compress

    $temporaryPath = "$Path.tmp"
    [IO.File]::WriteAllText($temporaryPath, $config, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

Write-Host 'Настройка Loca Loca: Supabase PostgreSQL' -ForegroundColor Cyan
Write-Host 'Локальная PostgreSQL больше не устанавливается. Все устройства подключаются к общей облачной базе.' -ForegroundColor Gray

$cloudApiUri = Get-ConfiguredCloudApiUrl
Ensure-WebView2

if ($cloudApiUri) {
    $apiBaseUrl = $cloudApiUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    $clientConfigDirectory = Split-Path -Parent $UserConfigPath
    New-Item -ItemType Directory -Path $clientConfigDirectory -Force | Out-Null

    $clientConfigPath = Join-Path $clientConfigDirectory 'client-config.json'
    $clientConfigTemporaryPath = "$clientConfigPath.tmp"
    $clientConfig = [ordered]@{
        version = 1
        apiBaseUrl = $apiBaseUrl
    } | ConvertTo-Json -Compress

    Write-SetupLog "Создание каталога конфигурации '$clientConfigDirectory'."
    [IO.File]::WriteAllText(
        $clientConfigTemporaryPath,
        $clientConfig,
        [Text.UTF8Encoding]::new($false)
    )
    Move-Item -LiteralPath $clientConfigTemporaryPath -Destination $clientConfigPath -Force

    # Keep the environment variable as an explicit override for developers.
    [Environment]::SetEnvironmentVariable('LOCA_API_BASE_URL', $apiBaseUrl, 'User')
    Write-SetupLog "Cloud API configuration saved to '$clientConfigPath'."
    $env:LOCA_API_BASE_URL = $apiBaseUrl

    Write-SetupLog 'Облачная настройка завершена успешно.'
    Write-Host "Облачный API настроен: $apiBaseUrl" -ForegroundColor Green
    Write-Host "Конфигурация сохранена: $clientConfigPath"
    Write-Host 'Прямое подключение Windows-клиента к Supabase не требуется.' -ForegroundColor Gray
    exit 0
}

$configDirectory = Split-Path -Parent $UserConfigPath
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$existing = Read-ExistingConfig -Path $UserConfigPath
$legacyConnection = $null
$existingHost = ''
$existingUsername = ''
if ($existing -and -not [string]::IsNullOrWhiteSpace([string]$existing.connectionString)) {
    $legacyConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $legacyConnection.ConnectionString = [string]$existing.connectionString
    if ($legacyConnection.ContainsKey('Host')) { $existingHost = [string]$legacyConnection['Host'] }
    if ($legacyConnection.ContainsKey('Username')) { $existingUsername = [string]$legacyConnection['Username'] }
}
elseif ($existing -and -not [string]::IsNullOrWhiteSpace([string]$existing.host)) {
    $existingHost = [string]$existing.host
    $existingUsername = [string]$existing.username
}

$existingIsSupabase = $existingHost -like '*.supabase.com' -or $existingHost -like '*.supabase.co'
if ($existing -and $existingIsSupabase -and -not (Test-LoopbackHost $existingHost) -and $existingUsername -like 'localoca_app.*') {
    if ($legacyConnection -and $legacyConnection.ContainsKey('Password')) {
        Write-EncryptedConfig -Path $UserConfigPath -Connection $legacyConnection
        Write-Host 'Существующий пароль Supabase зашифрован средствами Windows DPAPI.' -ForegroundColor Green
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$existing.encryptedPassword)) {
        Write-Host 'Обнаружена готовая конфигурация Supabase с зашифрованным паролем.' -ForegroundColor Green
    }
    else {
        throw 'Конфигурация Supabase найдена, но пароль отсутствует. Запусти установщик повторно и введи параметры подключения.'
    }
    Write-Host "Файл конфигурации: $UserConfigPath"
    exit 0
}

if ($existingHost -and (Test-LoopbackHost $existingHost)) {
    Write-Host 'Обнаружена старая локальная PostgreSQL. Её настройки будут заменены подключением Supabase.' -ForegroundColor Yellow
}
elseif ($existingIsSupabase -and $existingUsername -and $existingUsername -notlike 'localoca_app.*') {
    Write-Host 'Обнаружено подключение Supabase под старой/административной ролью. Введи параметры роли localoca_app с минимальными правами.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Открой Supabase Dashboard -> Connect -> Session Pooler и возьми параметры подключения своего проекта.' -ForegroundColor Gray

$hostName = Read-RequiredValue 'Supabase Pooler host' 'aws-0-eu-west-1.pooler.supabase.com'
$portText = Read-RequiredValue 'Порт' '5432'
$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) { throw 'Порт должен быть числом от 1 до 65535.' }

$database = Read-RequiredValue 'Имя базы данных' 'postgres'
$username = Read-RequiredValue 'Пользователь БД (для Session Pooler: localoca_app.<project-ref>)'
if (Test-LoopbackHost $hostName) { throw 'Нужно указать удалённый адрес Supabase, а не localhost или 127.0.0.1.' }
if ($hostName -notlike '*.supabase.com' -and $hostName -notlike '*.supabase.co') { throw 'Указанный адрес не похож на домен Supabase. Проверь значение в Supabase Dashboard -> Connect.' }

$securePassword = Read-Host 'Пароль PostgreSQL (ввод скрыт)' -AsSecureString
if ($securePassword.Length -eq 0) { throw 'Пароль PostgreSQL не может быть пустым.' }

$pointer = [IntPtr]::Zero
$password = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)

    # Build a temporary connection builder so special characters in the
    # password are handled consistently, then persist only a DPAPI-protected secret.
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = $hostName
    $builder['Port'] = $port
    $builder['Database'] = $database
    $builder['Username'] = $username
    $builder['Password'] = $password
    $builder['SSL Mode'] = 'Require'
    $builder['Pooling'] = $true
    $builder['Timeout'] = 15
    $builder['Command Timeout'] = 30
    $builder['Maximum Pool Size'] = 5

    Write-EncryptedConfig -Path $UserConfigPath -Connection $builder

    Write-Host ''
    Write-Host 'Настройки Supabase сохранены.' -ForegroundColor Green
    Write-Host "Файл конфигурации: $UserConfigPath"
    Write-Host 'Пароль зашифрован Windows DPAPI для текущей учётной записи и не записывался в репозиторий.'
    Write-Host 'Запусти Loca Loca и проверь подключение к БД: http://127.0.0.1:5181/ready.'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
    if ($securePassword) { $securePassword.Dispose() }
}
