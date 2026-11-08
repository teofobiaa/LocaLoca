[CmdletBinding()]
param(
    [string]$UserConfigPath = (Join-Path $env:LOCALAPPDATA 'LocaLoca\server-config.json'),
    [string]$LogPath = (Join-Path $env:TEMP 'LocaLoca-CloudSetup.log')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue }
$WebViewRuntimeGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'

trap {
    $record = $_
    $lineNumber = 0
    if ($record.InvocationInfo) { $lineNumber = $record.InvocationInfo.ScriptLineNumber }
    $detail = "Setup failed: $($record.Exception.Message)" + [Environment]::NewLine +
        "Script line: $lineNumber" + [Environment]::NewLine +
        "Command: $($record.InvocationInfo.Line)"
    try {
        [IO.File]::WriteAllText($LogPath, $detail, [Text.UTF8Encoding]::new($false))
    }
    catch { }
    Write-Host ''
    Write-Host "Ошибка настройки Loca Loca: $($record.Exception.Message)" -ForegroundColor Red
    Write-Host "Подробности записаны в: $LogPath" -ForegroundColor Yellow
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
    if (Test-WebView2Installed) {
        Write-Host 'WebView2 Runtime уже установлен.' -ForegroundColor Green
        return
    }

    Write-Host 'Установка Microsoft Edge WebView2 Runtime...' -ForegroundColor Cyan
    $installerPath = Join-Path $env:TEMP 'LocaLoca-WebView2-Setup.exe'
    if (Test-Path -LiteralPath $installerPath) { Remove-Item -LiteralPath $installerPath -Force }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $installerPath -UseBasicParsing

    $signature = Get-AuthenticodeSignature -FilePath $installerPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
        throw 'Не удалось подтвердить подпись установщика WebView2 от Microsoft.'
    }

    $process = Start-Process -FilePath $installerPath -ArgumentList @('/silent', '/install') -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Не удалось установить WebView2 Runtime. Код установщика: $($process.ExitCode)."
    }

    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    if (-not (Test-WebView2Installed)) { throw 'Установщик завершился, но WebView2 Runtime не обнаружен.' }
    Write-Host 'WebView2 Runtime установлен.' -ForegroundColor Green
}

function Get-ConfiguredCloudApiUrl {
    foreach ($scope in @('Process', 'User', 'Machine')) {
        $value = [Environment]::GetEnvironmentVariable('LOCA_API_BASE_URL', $scope)
        if ([string]::IsNullOrWhiteSpace($value)) { continue }

        $uri = $null
        if (-not [Uri]::TryCreate($value.Trim(), [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "LOCA_API_BASE_URL задан некорректно. Укажи полный HTTPS-адрес Render, например https://localoca-api.onrender.com."
        }

        return $uri
    }

    return $null
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
    Write-Host "Обнаружен облачный API: $($cloudApiUri.GetLeftPart([UriPartial]::Authority))" -ForegroundColor Green
    Write-Host 'Прямое подключение Windows-клиента к Supabase не требуется. Пропускаем настройку локальной БД.' -ForegroundColor Gray
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
    $builder['Trust Server Certificate'] = $true
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
