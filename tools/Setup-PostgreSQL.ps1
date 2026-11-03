$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Schema = Join-Path $ProjectRoot 'database/01_schema.sql'
$Seed = Join-Path $ProjectRoot 'database/02_seed.sql'
$WishlistMigration = Join-Path $ProjectRoot 'database/03_migrate_wishlist.sql'
$MapPlacesMigration = Join-Path $ProjectRoot 'database/04_migrate_user_map_places.sql'

function Find-Psql {
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $base = Join-Path $env:ProgramFiles 'PostgreSQL'
    if (Test-Path $base) {
        $candidate = Get-ChildItem $base -Filter psql.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'bin' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw 'Не найден psql.exe. Установи PostgreSQL для Windows и повтори запуск.'
}

function New-RandomPassword {
    $bytes = New-Object byte[] 36
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','A').Replace('/','B')
}

$Psql = Find-Psql
$previousClientEncoding = $env:PGCLIENTENCODING
$env:PGCLIENTENCODING = 'UTF8'
Write-Host 'Настройка локальной базы PostgreSQL для Loca Loca' -ForegroundColor Cyan
$hostName = '127.0.0.1'
Write-Host 'PostgreSQL будет подключён только локально: 127.0.0.1' -ForegroundColor Gray
$portText = Read-Host 'Порт PostgreSQL (Enter = 5432)'
if ([string]::IsNullOrWhiteSpace($portText)) { $portText = '5432' }
$adminUser = Read-Host 'Администратор PostgreSQL (Enter = postgres)'
if ([string]::IsNullOrWhiteSpace($adminUser)) { $adminUser = 'postgres' }
$adminSecure = Read-Host 'Пароль администратора PostgreSQL' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($adminSecure)
try { $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

$appPassword = New-RandomPassword
$env:PGPASSWORD = $adminPassword
try {
    $roleSql = @"
DO `$`$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'loca_app') THEN
    EXECUTE format('ALTER ROLE loca_app WITH LOGIN PASSWORD %L', '$appPassword');
  ELSE
    EXECUTE format('CREATE ROLE loca_app LOGIN PASSWORD %L', '$appPassword');
  END IF;
END;
`$`$;
"@
    & $Psql -h $hostName -p $portText -U $adminUser -d postgres -v ON_ERROR_STOP=1 -c $roleSql
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать/обновить роль loca_app. Проверь пароль администратора и службу PostgreSQL.' }

    $exists = & $Psql -h $hostName -p $portText -U $adminUser -d postgres -tA -c "SELECT 1 FROM pg_database WHERE datname='loca_loca'"
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить наличие базы loca_loca.' }
    if (($exists | Out-String).Trim() -ne '1') {
        & $Psql -h $hostName -p $portText -U $adminUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE loca_loca OWNER loca_app ENCODING 'UTF8'"
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать базу loca_loca.' }
    } else {
        Write-Host 'База loca_loca уже существует. Схема повторно не создаётся.' -ForegroundColor Yellow
    }

    # Выполняем DDL и seed под административной ролью: это безопасно работает
    # и для новой базы, и для уже существующей базы, владельцем которой является postgres.
    $env:PGPASSWORD = $adminPassword
    $hasSchema = & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -tA -c "SELECT (to_regclass('public.users') IS NOT NULL)::text"
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить структуру базы loca_loca.' }
    if (($hasSchema | Out-String).Trim() -ne 'true') {
        & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -v ON_ERROR_STOP=1 -f $Schema
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка при создании таблиц. Проверь вывод psql.' }
    } else {
        Write-Host 'Найдена существующая схема loca_loca. Таблицы и пользовательские данные не перезаписываются.' -ForegroundColor Yellow
    }
    # Seed идемпотентный: добавляет только отсутствующие справочники и стартовые места.
    & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -v ON_ERROR_STOP=1 -f $Seed
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка при добавлении демонстрационных достопримечательностей.' }
    & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -v ON_ERROR_STOP=1 -f $WishlistMigration
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка при применении миграции списка желаний.' }
    & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -v ON_ERROR_STOP=1 -f $MapPlacesMigration
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка при применении миграции пользовательских точек карты.' }

    # Разрешаем локальной роли подключаться и работать с уже существующей базой курсового проекта.
    $env:PGPASSWORD = $adminPassword
    & $Psql -h $hostName -p $portText -U $adminUser -d postgres -v ON_ERROR_STOP=1 -c "GRANT CONNECT ON DATABASE loca_loca TO loca_app"
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось выдать роли loca_app право подключения к базе.' }
    & $Psql -h $hostName -p $portText -U $adminUser -d loca_loca -v ON_ERROR_STOP=1 -c "GRANT USAGE ON SCHEMA public TO loca_app; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO loca_app; GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO loca_app; ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO loca_app; ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO loca_app"
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось выдать роли loca_app права на таблицы и последовательности.' }

    $configDir = Join-Path $env:LOCALAPPDATA 'LocaLoca'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    $connectionString = "Host=$hostName;Port=$portText;Database=loca_loca;Username=loca_app;Password=$appPassword;Pooling=true;Timeout=5;Command Timeout=15"
    $config = @{ connectionString = $connectionString } | ConvertTo-Json
    $configPath = Join-Path $configDir 'server-config.json'
    Set-Content -Path $configPath -Value $config -Encoding UTF8

    $env:PGPASSWORD = $appPassword
    & $Psql -h $hostName -p $portText -U loca_app -d loca_loca -v ON_ERROR_STOP=1 -c "SELECT count(*) AS attraction_count FROM attractions"
    if ($LASTEXITCODE -ne 0) { throw 'Проверочное подключение к базе не удалось.' }
    Write-Host ''
    Write-Host 'PostgreSQL настроен для Loca Loca.' -ForegroundColor Green
    Write-Host "База: loca_loca | Пользователь БД: loca_app | Адрес: $hostName`:$portText" -ForegroundColor Green
    Write-Host "Настройки подключения: $configPath" -ForegroundColor Green
    Write-Host 'Пароль роли loca_app сгенерирован автоматически и записан в локальный файл конфигурации.' -ForegroundColor Yellow
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($null -eq $previousClientEncoding) { Remove-Item Env:PGCLIENTENCODING -ErrorAction SilentlyContinue } else { $env:PGCLIENTENCODING = $previousClientEncoding }
    $adminPassword = $null
    $appPassword = $null
}
