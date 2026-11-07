[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DatabaseScriptsPath,

    [string]$UserConfigPath = (Join-Path $env:LOCALAPPDATA 'LocaLoca\server-config.json')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$PostgresVersion = '17.11'
$PostgresInstallerUrl = 'https://get.enterprisedb.com/postgresql/postgresql-17.11-5-windows-x64.exe'
$PostgresInstallerSha256 = '612C7F5400A003AAFFA0372CB273EED52278F93C680AB911EB9CB2229E329072'
$PostgresServiceName = 'LocaLocaPostgreSQL17'
$PostgresServiceAccount = 'LocaLocaDb'
$PostgresInstallRoot = Join-Path $env:ProgramFiles 'LocaLoca\PostgreSQL\17'
$script:PostgresHost = '127.0.0.1'
$PostgresDataRoot = Join-Path $env:ProgramData 'LocaLoca\PostgreSQL\data'
$BootstrapRoot = Join-Path $env:ProgramData 'LocaLoca\Bootstrap'
$BootstrapFile = Join-Path $BootstrapRoot 'postgres-bootstrap.json'
$ConfigDirectory = Split-Path -Parent $UserConfigPath
$ConfigFile = $UserConfigPath
$WebViewRuntimeGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host $Message -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-RandomPassword {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return 'Aa1!' + ([BitConverter]::ToString($bytes).Replace('-', ''))
}

function Protect-BootstrapDirectory {
    New-Item -ItemType Directory -Path $BootstrapRoot -Force | Out-Null
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $BootstrapRoot '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось ограничить доступ к временному файлу учётных данных PostgreSQL.'
    }
}

function Save-BootstrapState {
    param([object]$State)

    Protect-BootstrapDirectory
    $json = $State | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($BootstrapFile, $json, [Text.UTF8Encoding]::new($false))
}

function Test-TcpPortAvailable {
    param([int]$Port)

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $listener.Stop()
    }
}

function Find-AvailablePostgresPort {
    foreach ($port in 5432..5450) {
        if (Test-TcpPortAvailable -Port $port) {
            return $port
        }
    }

    throw 'Не найден свободный локальный порт PostgreSQL в диапазоне 5432–5450.'
}

function Test-AuthenticodePublisher {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Publisher
    )

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Не удалось проверить цифровую подпись файла: $Path. Статус: $($signature.Status)."
    }

    if ($signature.SignerCertificate.Subject -notmatch [regex]::Escape($Publisher)) {
        throw "Неожиданный издатель файла $($Path): $($signature.SignerCertificate.Subject)"
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
        if ($entry -and $entry.pv -and $entry.pv -ne '0.0.0.0') {
            return $true
        }
    }

    return $false
}

function Ensure-WebView2 {
    if (Test-WebView2Installed) {
        Write-Host 'WebView2 Runtime уже установлен.' -ForegroundColor Green
        return
    }

    Write-Step 'Установка Microsoft Edge WebView2 Runtime...'
    $installerPath = Join-Path $env:TEMP 'LocaLoca-WebView2-Setup.exe'
    if (Test-Path -LiteralPath $installerPath) {
        Remove-Item -LiteralPath $installerPath -Force
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $installerPath -UseBasicParsing
    Test-AuthenticodePublisher -Path $installerPath -Publisher 'Microsoft Corporation'

    $process = Start-Process -FilePath $installerPath -ArgumentList @('/silent', '/install') -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Не удалось установить WebView2 Runtime. Код установщика: $($process.ExitCode)."
    }

    if (-not (Test-WebView2Installed)) {
        throw 'Установщик WebView2 завершился, но Runtime не обнаружен в системе.'
    }

    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    Write-Host 'WebView2 Runtime установлен.' -ForegroundColor Green
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory = $true)][string]$DatabaseUser,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string[]]$PsqlArguments
    )

    $previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD', 'Process')
    $env:PGPASSWORD = $Password
    try {
        $output = & $script:PsqlPath -X -w -v 'ON_ERROR_STOP=1' -h $script:PostgresHost -p ([string]$script:PostgresPort) -U $DatabaseUser -d $DatabaseName @PsqlArguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $details = ($output | Out-String).Trim()
            throw "Команда PostgreSQL завершилась с кодом $exitCode. $details"
        }

        return ($output | Out-String).Trim()
    }
    finally {
        if ($null -eq $previousPassword) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $previousPassword
        }
    }
}

function Find-PsqlExecutable {
    # Если установлен собственный PostgreSQL Loca Loca, используем его клиент,
    # чтобы однозначно определить, какой службой можно управлять далее.
    $managedCandidate = Join-Path $PostgresInstallRoot 'bin\psql.exe'
    if (Test-Path -LiteralPath $managedCandidate -PathType Leaf) {
        return $managedCandidate
    }

    # Для ручной установки ищем psql в PATH и стандартных каталогах PostgreSQL.
    $command = Get-Command psql.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        $command = Get-Command psql -ErrorAction SilentlyContinue
    }
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $roots = @(
        (Join-Path $env:ProgramFiles 'PostgreSQL'),
        (Join-Path $programFilesX86 'PostgreSQL')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        if ((Get-Item -LiteralPath $root).Name -eq '17' -and
            (Test-Path -LiteralPath (Join-Path $root 'bin\psql.exe') -PathType Leaf)) {
            return (Join-Path $root 'bin\psql.exe')
        }

        $versions = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($versionDirectory in $versions) {
            $candidate = Join-Path $versionDirectory.FullName 'bin\psql.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    throw 'Не найден psql.exe. Проверь установку PostgreSQL или добавь каталог bin PostgreSQL в PATH. Существующая база и конфигурация не изменены.'
}

function Invoke-DatabaseScripts {
    param(
        [Parameter(Mandatory = $true)][string]$DatabaseUser,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)][string]$Password
    )

    Write-Step 'Проверка существующей схемы и применение миграций...'

    # Не запускаем 01_schema.sql поверх частично созданной базы: этот файл
    # предназначен для пустой базы и содержит обычные CREATE TABLE.
    $coreTableCount = Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @(
        '-A', '-t', '-c',
        "SELECT count(*) FROM (VALUES ('users'), ('categories'), ('countries'), ('cities'), ('attractions'), ('visits'), ('photos')) AS required(name) WHERE to_regclass('public.' || quote_ident(required.name)) IS NOT NULL"
    )

    if ($coreTableCount -eq '0') {
        $canCreateSchema = Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @(
            '-A', '-t', '-c',
            "SELECT CASE WHEN has_schema_privilege(current_user, 'public', 'CREATE') THEN '1' ELSE '0' END"
        )
        if ($canCreateSchema -ne '1') {
            throw "У пользователя PostgreSQL '$DatabaseUser' нет права CREATE на схему public. База пуста, но создать начальную схему невозможно. Администратор PostgreSQL должен выполнить: GRANT CREATE ON SCHEMA public TO $DatabaseUser;"
        }

        $schemaFile = Join-Path $DatabaseScriptsPath '01_schema.sql'
        if (-not (Test-Path -LiteralPath $schemaFile)) {
            throw "Не найден файл схемы базы данных: $schemaFile"
        }

        # -1 выполняет весь SQL-файл в одной транзакции: при ошибке не остаётся
        # частично созданной базовой схемы.
        Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @('-1', '-f', $schemaFile) | Out-Null
    }
    elseif ($coreTableCount -ne '7') {
        throw "Обнаружена неполная базовая схема PostgreSQL: найдено $coreTableCount из 7 обязательных таблиц. Автоматическое восстановление остановлено, чтобы не затронуть существующие данные. Требуется проверить базу loca_loca вручную."
    }

    # Миграции создают таблицы и индексы. Проверяем эффективное право CREATE
    # заранее, чтобы выдать понятную инструкцию вместо неясной ошибки psql.
    $canCreateSchema = Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @(
        '-A', '-t', '-c',
        "SELECT CASE WHEN has_schema_privilege(current_user, 'public', 'CREATE') THEN '1' ELSE '0' END"
    )
    if ($canCreateSchema -ne '1') {
        throw "У пользователя PostgreSQL '$DatabaseUser' нет права CREATE на схему public, поэтому миграции не применены. Администратор PostgreSQL должен выполнить: GRANT CREATE ON SCHEMA public TO $DatabaseUser; После этого повтори установку. Существующие данные не изменены."
    }

    foreach ($migrationName in @('02_seed.sql', '03_migrate_wishlist.sql', '04_migrate_user_map_places.sql')) {
        $migrationPath = Join-Path $DatabaseScriptsPath $migrationName
        if (-not (Test-Path -LiteralPath $migrationPath)) {
            throw "Не найден SQL-файл $migrationName в $DatabaseScriptsPath"
        }

        # Каждый SQL-файл атомарен: ошибка не оставляет половину миграции.
        Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @('-1', '-f', $migrationPath) | Out-Null
    }

    Write-Step 'Проверка подключения приложения к базе...'
    Invoke-Psql -DatabaseUser $DatabaseUser -DatabaseName $DatabaseName -Password $Password -PsqlArguments @('-c', 'SELECT count(*) AS attraction_count FROM attractions') |
        ForEach-Object { Write-Host $_ }
}


# Повышаем права только для системных компонентов. Путь конфигурации передаётся
# из исходного пользовательского сеанса, даже если UAC запросит другую учётную запись.
if (-not (Test-IsAdministrator)) {
    $powershellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-DatabaseScriptsPath', ('"{0}"' -f $DatabaseScriptsPath),
        '-UserConfigPath', ('"{0}"' -f $UserConfigPath)
    )

    try {
        $elevatedProcess = Start-Process -FilePath $powershellPath -ArgumentList ($arguments -join ' ') -Verb RunAs -Wait -PassThru
    }
    catch {
        throw "Не удалось получить права администратора Windows. Первичная установка PostgreSQL отменена. $($_.Exception.Message)"
    }

    if ($elevatedProcess.ExitCode -ne 0) {
        throw "Настройка PostgreSQL завершилась с кодом $($elevatedProcess.ExitCode). Запусти установщик повторно."
    }

    exit 0
}

if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
    throw 'Loca Loca и PostgreSQL требуют 64-разрядную Windows.'
}

if (-not (Test-Path -LiteralPath $DatabaseScriptsPath -PathType Container)) {
    throw "Не найдена папка SQL-скриптов приложения: $DatabaseScriptsPath"
}

Write-Step 'Подготовка локальной базы данных Loca Loca...'
Ensure-WebView2

# Существующую конфигурацию не перезаписываем. Подключаемся именно к тому
# локальному серверу, который указан в ней; не предполагаем имя службы или путь установки.
if (Test-Path -LiteralPath $ConfigFile) {
    try {
        $existingConfig = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($existingConfig.connectionString)) {
            throw 'В файле отсутствует connectionString.'
        }

        $connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connectionBuilder.ConnectionString = [string]$existingConfig.connectionString
    }
    catch {
        throw "Файл конфигурации Loca Loca существует, но не читается. Он не будет перезаписан автоматически: $ConfigFile. $($_.Exception.Message)"
    }

    $hasStandardConnectionKeys = $true
    foreach ($requiredKey in @('Host', 'Port', 'Database', 'Username', 'Password')) {
        if (-not $connectionBuilder.ContainsKey($requiredKey)) {
            $hasStandardConnectionKeys = $false
            break
        }
    }

    if (-not $hasStandardConnectionKeys) {
        Write-Host 'Найдена пользовательская строка подключения нестандартного формата. Она сохранена; автоматическое изменение базы пропущено.' -ForegroundColor Yellow
        Write-Host "Файл: $ConfigFile"
        exit 0
    }

    $configuredHost = [string]$connectionBuilder['Host']
    $configuredDatabase = [string]$connectionBuilder['Database']
    $configuredUser = [string]$connectionBuilder['Username']

    # Скрипт обновляет только локальную базу Loca Loca. Внешние серверы,
    # другие базы и другие роли не изменяются.
    if ($configuredHost -notin @('127.0.0.1', 'localhost', '::1') -or
        $configuredDatabase -ne 'loca_loca' -or $configuredUser -ne 'loca_app') {
        Write-Host 'Найдена пользовательская конфигурация PostgreSQL. Она сохранена; автоматическое изменение этой базы пропущено.' -ForegroundColor Yellow
        Write-Host "Файл: $ConfigFile"
        exit 0
    }

    $configuredPort = 0
    if (-not [int]::TryParse([string]$connectionBuilder['Port'], [ref]$configuredPort) -or
        $configuredPort -lt 1 -or $configuredPort -gt 65535) {
        throw "В существующей конфигурации указан некорректный порт PostgreSQL. Конфигурация не изменена: $ConfigFile"
    }

    $configuredPassword = [string]$connectionBuilder['Password']
    if ([string]::IsNullOrWhiteSpace($configuredPassword)) {
        throw "В существующей конфигурации отсутствует пароль пользователя loca_app. Конфигурация не изменена: $ConfigFile"
    }

    $script:PostgresHost = $configuredHost
    $script:PostgresPort = $configuredPort
    $script:PsqlPath = Find-PsqlExecutable

    # Управляем службой только если её системная запись указывает на каталог
    # данных, созданный установщиком Loca Loca. Одного имени службы недостаточно.
    $managedServiceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$PostgresServiceName"
    $managedServiceImage = (Get-ItemProperty -LiteralPath $managedServiceRegistryPath -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    $managedDataRoot = [IO.Path]::GetFullPath($PostgresDataRoot)
    $isLocaLocaManagedService = -not [string]::IsNullOrWhiteSpace($managedServiceImage) -and
        $managedServiceImage.IndexOf($managedDataRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0

    if ($isLocaLocaManagedService) {
        $managedService = Get-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
        if (-not $managedService) {
            throw "Найден встроенный psql.exe, но служба $PostgresServiceName отсутствует. Конфигурация и данные не изменены."
        }

        if ($managedService.Status -ne 'Running') {
            Write-Step 'Запуск службы PostgreSQL Loca Loca...'
            Start-Service -Name $PostgresServiceName
            $serviceDeadline = (Get-Date).AddMinutes(3)
            do {
                Start-Sleep -Seconds 2
                $managedService = Get-Service -Name $PostgresServiceName
                if ($managedService.Status -eq 'Running') {
                    break
                }
            } while ((Get-Date) -lt $serviceDeadline)
        }

        if ((Get-Service -Name $PostgresServiceName).Status -ne 'Running') {
            throw "Не удалось запустить службу PostgreSQL $PostgresServiceName. Конфигурация и данные не изменены."
        }

        Set-Service -Name $PostgresServiceName -StartupType Automatic
    }

    Write-Step "Проверка существующего PostgreSQL ($($configuredHost):$configuredPort)..."
    # Для сторонней установки службу не трогаем: проверяем только реальное подключение.
    Invoke-Psql -DatabaseUser $configuredUser -DatabaseName $configuredDatabase -Password $configuredPassword -PsqlArguments @('-c', 'SELECT 1') | Out-Null

    Write-Step 'Обновление схемы существующей базы данных...'
    Invoke-DatabaseScripts -DatabaseUser $configuredUser -DatabaseName $configuredDatabase -Password $configuredPassword

    Write-Host 'Существующая конфигурация сохранена. Проверка подключения и применение миграций завершены.' -ForegroundColor Green
    Write-Host "Файл: $ConfigFile"
    exit 0
}

$service = Get-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
$hasBootstrapState = Test-Path -LiteralPath $BootstrapFile

if (-not $service -and -not $hasBootstrapState -and
    ((Test-Path -LiteralPath $PostgresInstallRoot) -or (Test-Path -LiteralPath $PostgresDataRoot))) {
    throw 'Обнаружены каталоги PostgreSQL Loca Loca без служебной записи восстановления. Они не будут удалены автоматически.'
}

if ($service -and -not $hasBootstrapState) {
    throw "Служба $PostgresServiceName уже существует, но файл восстановления настройки отсутствует. Чтобы не повредить существующую базу, автоматическая переинициализация остановлена."
}

if ($hasBootstrapState) {
    $bootstrap = Get-Content -LiteralPath $BootstrapFile -Raw | ConvertFrom-Json
    if (-not $bootstrap.PostgresPassword -or -not $bootstrap.ServicePassword -or -not $bootstrap.AppPassword -or -not $bootstrap.Port) {
        throw "Файл восстановления PostgreSQL повреждён: $BootstrapFile"
    }
    if ($null -eq $bootstrap.PSObject.Properties['InstallStarted']) {
        $bootstrap | Add-Member -NotePropertyName InstallStarted -NotePropertyValue $false
    }
}
else {
    $bootstrap = [pscustomobject]@{
        PostgresPassword = New-RandomPassword
        ServicePassword  = New-RandomPassword
        AppPassword      = New-RandomPassword
        Port             = Find-AvailablePostgresPort
        InstallStarted   = $false
        CreatedUtc       = [DateTime]::UtcNow.ToString('o')
    }
    Save-BootstrapState -State $bootstrap
}

$script:PostgresHost = '127.0.0.1'
$script:PostgresPort = [int]$bootstrap.Port
$script:PsqlPath = Join-Path $PostgresInstallRoot 'bin\psql.exe'

if (-not $service) {
    Write-Step "Загрузка PostgreSQL $PostgresVersion с официального сервера EDB..."
    $installerPath = Join-Path $env:TEMP "LocaLoca-PostgreSQL-$PostgresVersion-Setup.exe"

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if (-not (Test-Path -LiteralPath $installerPath)) {
        Invoke-WebRequest -Uri $PostgresInstallerUrl -OutFile $installerPath -UseBasicParsing
    }

    $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $PostgresInstallerSha256) {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
        throw 'Контрольная сумма установщика PostgreSQL не совпала. Файл удалён, установка остановлена.'
    }

    Test-AuthenticodePublisher -Path $installerPath -Publisher 'EnterpriseDB'

    # Удаляем каталоги только после доказанного старта нашей предыдущей попытки установки.
    if ($bootstrap.InstallStarted -eq $true) {
        if (Test-Path -LiteralPath $PostgresInstallRoot) {
            Remove-Item -LiteralPath $PostgresInstallRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $PostgresDataRoot) {
            Remove-Item -LiteralPath $PostgresDataRoot -Recurse -Force
        }
    }
    elseif ((Test-Path -LiteralPath $PostgresInstallRoot) -or (Test-Path -LiteralPath $PostgresDataRoot)) {
        throw 'Каталоги PostgreSQL появились до начала установки и не будут удалены автоматически.'
    }

    Write-Step 'Установка PostgreSQL (может занять несколько минут)...'
    $installerArguments = @(
        '--mode', 'unattended',
        '--unattendedmodeui', 'minimal',
        '--prefix', $PostgresInstallRoot,
        '--datadir', $PostgresDataRoot,
        '--serverport', [string]$script:PostgresPort,
        '--servicename', $PostgresServiceName,
        '--serviceaccount', $PostgresServiceAccount,
        '--servicepassword', $bootstrap.ServicePassword,
        '--superaccount', 'postgres',
        '--superpassword', $bootstrap.PostgresPassword,
        '--enable-components', 'server,commandlinetools',
        '--enable_acledit', '1',
        '--install_runtimes', '1',
        '--create_shortcuts', '0'
    )

    $bootstrap.InstallStarted = $true
    Save-BootstrapState -State $bootstrap

    & $installerPath @installerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Установщик PostgreSQL завершился с кодом $LASTEXITCODE. Служебный файл сохранён: $BootstrapFile"
    }

    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
}

Write-Step 'Запуск и проверка службы PostgreSQL...'
$deadline = (Get-Date).AddMinutes(3)
do {
    $service = Get-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        break
    }
    if ($service -and $service.Status -ne 'Running') {
        Start-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)

$service = Get-Service -Name $PostgresServiceName -ErrorAction SilentlyContinue
if (-not $service -or $service.Status -ne 'Running') {
    throw "Служба PostgreSQL $PostgresServiceName не запустилась. Служебный файл сохранён для повторной попытки: $BootstrapFile"
}

Set-Service -Name $PostgresServiceName -StartupType Automatic

if (-not (Test-Path -LiteralPath $script:PsqlPath)) {
    throw "После установки не найден psql.exe: $script:PsqlPath"
}

Write-Step 'Создание пользователя и базы данных...'
$roleExists = Invoke-Psql -DatabaseUser 'postgres' -DatabaseName 'postgres' -Password $bootstrap.PostgresPassword -PsqlArguments @('-A', '-t', '-c', "SELECT 1 FROM pg_roles WHERE rolname = 'loca_app'")

if ($roleExists -ne '1') {
    $roleSql = "CREATE ROLE loca_app LOGIN PASSWORD '$($bootstrap.AppPassword)'"
}
else {
    $roleSql = "ALTER ROLE loca_app WITH LOGIN PASSWORD '$($bootstrap.AppPassword)'"
}
Invoke-Psql -DatabaseUser 'postgres' -DatabaseName 'postgres' -Password $bootstrap.PostgresPassword -PsqlArguments @('-c', $roleSql) | Out-Null

$databaseExists = Invoke-Psql -DatabaseUser 'postgres' -DatabaseName 'postgres' -Password $bootstrap.PostgresPassword -PsqlArguments @('-A', '-t', '-c', "SELECT 1 FROM pg_database WHERE datname = 'loca_loca'")

if ($databaseExists -ne '1') {
    Invoke-Psql -DatabaseUser 'postgres' -DatabaseName 'postgres' -Password $bootstrap.PostgresPassword -PsqlArguments @('-c', "CREATE DATABASE loca_loca OWNER loca_app ENCODING 'UTF8' TEMPLATE template0") | Out-Null
}
else {
    Invoke-Psql -DatabaseUser 'postgres' -DatabaseName 'postgres' -Password $bootstrap.PostgresPassword -PsqlArguments @('-c', 'ALTER DATABASE loca_loca OWNER TO loca_app') | Out-Null
}

Invoke-DatabaseScripts -DatabaseUser 'loca_app' -DatabaseName 'loca_loca' -Password $bootstrap.AppPassword

New-Item -ItemType Directory -Path $ConfigDirectory -Force | Out-Null
$connectionString = "Host=127.0.0.1;Port=$($script:PostgresPort);Database=loca_loca;Username=loca_app;Password=$($bootstrap.AppPassword);Pooling=true;Timeout=5;Command Timeout=15"
$configJson = @{ connectionString = $connectionString } | ConvertTo-Json -Compress
[IO.File]::WriteAllText($ConfigFile, $configJson, [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $BootstrapFile -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Loca Loca готова к первому запуску!' -ForegroundColor Green
Write-Host "PostgreSQL: 127.0.0.1:$($script:PostgresPort)"
Write-Host "Конфигурация: $ConfigFile"
Write-Host 'Пароль администратора PostgreSQL нигде в настройках приложения не сохраняется.'
