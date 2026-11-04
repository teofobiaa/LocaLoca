# Настройка подключения к удалённому PostgreSQL для Loca Loca.
# Скрипт не создаёт БД/пользователя на хостинге и не запускает pgAdmin.
# Он сохраняет строку подключения локального API в %LOCALAPPDATA%\LocaLoca\server-config.json.

$ErrorActionPreference = 'Stop'

function Read-RequiredValue([string]$Prompt, [string]$Default = '') {
    while ($true) {
        $suffix = if ($Default) { " [$Default]" } else { '' }
        $value = Read-Host "$Prompt$suffix"
        if ([string]::IsNullOrWhiteSpace($value) -and $Default) { return $Default }
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        Write-Host 'Это значение обязательно.' -ForegroundColor Yellow
    }
}

Write-Host 'Настройка подключения Loca Loca к удалённому PostgreSQL' -ForegroundColor Cyan
Write-Host 'Значения базы, пользователя, пароля и SSL возьми из панели хостинга.' -ForegroundColor Gray
Write-Host 'Пароль будет сохранён локально для работы приложения; не отправляй этот файл в GitHub.' -ForegroundColor Yellow

$hostName = Read-RequiredValue 'Хост PostgreSQL' 'pg4.sweb.ru'
$portText = Read-RequiredValue 'Порт PostgreSQL' '5433'
$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
    throw 'Порт должен быть числом от 1 до 65535.'
}
$database = Read-RequiredValue 'Имя базы данных'
$username = Read-RequiredValue 'Имя пользователя базы'
$sslMode = Read-RequiredValue 'Режим SSL (уточни в панели/документации хостинга)' 'Prefer'
$validSslModes = @('Disable', 'Allow', 'Prefer', 'Require', 'VerifyCA', 'VerifyFull')
if ($validSslModes -notcontains $sslMode) {
    throw "Неизвестный SSL Mode '$sslMode'. Допустимые значения: $($validSslModes -join ', ')."
}

$securePassword = Read-Host 'Пароль пользователя базы' -AsSecureString
$pointer = [IntPtr]::Zero
$password = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)

    # DbConnectionStringBuilder корректно экранирует специальные символы в значениях.
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = $hostName
    $builder['Port'] = $port
    $builder['Database'] = $database
    $builder['Username'] = $username
    $builder['Password'] = $password
    $builder['SSL Mode'] = $sslMode
    $builder['Timeout'] = 15
    $builder['Command Timeout'] = 30
    $builder['Pooling'] = $true

    $configDir = Join-Path $env:LOCALAPPDATA 'LocaLoca'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    $configPath = Join-Path $configDir 'server-config.json'
    $config = @{ connectionString = $builder.ConnectionString } | ConvertTo-Json
    Set-Content -Path $configPath -Value $config -Encoding UTF8

    Write-Host ''
    Write-Host "Настройки сохранены: $configPath" -ForegroundColor Green
    Write-Host 'Теперь запусти Loca Loca и проверь http://127.0.0.1:5181/health.' -ForegroundColor Green
    Write-Host 'Если health сообщает об ошибке БД, проверь имя базы/пользователя, пароль, внешний доступ и SSL.' -ForegroundColor Yellow
} finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    $password = $null
}
