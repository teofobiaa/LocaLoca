[CmdletBinding()]
param(
    [string]$UserConfigPath = (Join-Path $env:LOCALAPPDATA 'LocaLoca\server-config.json')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WebViewRuntimeGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'

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

function Test-LoopbackHost {
    param([string]$HostName)
    $normalized = $HostName.Trim().Trim('[', ']')
    return $normalized -in @('localhost', '127.0.0.1', '::1')
}

function Read-ExistingConnectionString {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }

    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$json.connectionString)) { return $null }
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = [string]$json.connectionString
        if (-not $builder.ContainsKey('Host') -or -not $builder.ContainsKey('Password')) { return $null }
        return ,$builder
    }
    catch {
        throw "Не удалось прочитать существующий файл конфигурации. Он не будет перезаписан автоматически: $Path. $($_.Exception.Message)"
    }
}

Write-Host 'Настройка Loca Loca: Supabase PostgreSQL' -ForegroundColor Cyan
Write-Host 'Локальная PostgreSQL больше не устанавливается. Все устройства подключаются к общей облачной базе.' -ForegroundColor Gray

Ensure-WebView2

$configDirectory = Split-Path -Parent $UserConfigPath
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$existing = Read-ExistingConnectionString -Path $UserConfigPath
$existingHost = if ($existing -and $existing.ContainsKey('Host')) { [string]$existing['Host'] } else { '' }
$existingIsSupabase = $existingHost -like '*.supabase.com' -or $existingHost -like '*.supabase.co'

if ($existing -and $existingIsSupabase -and -not (Test-LoopbackHost $existingHost)) {
    Write-Host "Обнаружено готовое подключение к удалённой PostgreSQL ($existingHost). Текущие настройки сохранены." -ForegroundColor Green
    Write-Host "Файл конфигурации: $UserConfigPath"
    exit 0
}

if ($existing -and (Test-LoopbackHost $existingHost)) {
    Write-Host 'Обнаружена старая локальная PostgreSQL. Её настройки будут заменены подключением Supabase.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Открой Supabase Dashboard -> Connect -> Session Pooler и возьми параметры подключения своего проекта.' -ForegroundColor Gray

$hostName = Read-RequiredValue 'Supabase Pooler host' 'aws-0-eu-west-1.pooler.supabase.com'
$portText = Read-RequiredValue 'Порт' '5432'
$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) { throw 'Порт должен быть числом от 1 до 65535.' }

$database = Read-RequiredValue 'Имя базы данных' 'postgres'
$username = Read-RequiredValue 'Пользователь БД' 'postgres.kkpfmslpweyotusmthvo'
if (Test-LoopbackHost $hostName) { throw 'Нужно указать удалённый адрес Supabase, а не localhost или 127.0.0.1.' }
if ($hostName -notlike '*.supabase.com' -and $hostName -notlike '*.supabase.co') { throw 'Указанный адрес не похож на домен Supabase. Проверь значение в Supabase Dashboard -> Connect.' }

$securePassword = Read-Host 'Пароль PostgreSQL (ввод скрыт)' -AsSecureString
if ($securePassword.Length -eq 0) { throw 'Пароль PostgreSQL не может быть пустым.' }

$pointer = [IntPtr]::Zero
$password = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)

    # DbConnectionStringBuilder quotes special characters in the password.
    # The secret is written only to the per-user config file, never to Git.
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

    $config = @{ connectionString = $builder.ConnectionString } | ConvertTo-Json -Compress
    $temporaryPath = "$UserConfigPath.tmp"
    [IO.File]::WriteAllText($temporaryPath, $config, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $UserConfigPath -Force

    Write-Host ''
    Write-Host 'Настройки Supabase сохранены.' -ForegroundColor Green
    Write-Host "Файл конфигурации: $UserConfigPath"
    Write-Host 'Пароль не выводился на экран и не записывался в репозиторий.'
    Write-Host 'Запусти Loca Loca и проверь http://127.0.0.1:5181/health.'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
    if ($securePassword) { $securePassword.Dispose() }
}
