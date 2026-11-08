[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $env:LOCALAPPDATA 'LocaLoca\server-config.json'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\database\seed\02_seed.sql'),
    [int]$ExpectedCount = 44
)

$ErrorActionPreference = 'Stop'

function ConvertTo-SqlText {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return 'NULL' }
    return "'" + ([string]$Value).Replace("'", "''") + "'"
}

function ConvertTo-SqlNumber {
    param([Parameter(Mandatory = $true)][object]$Value)

    $number = [decimal]::Parse(
        [string]$Value,
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture
    )
    return $number.ToString([Globalization.CultureInfo]::InvariantCulture) + '::numeric'
}

$connectionString = [Environment]::GetEnvironmentVariable('LOCA_DB_CONNECTION', 'Process')
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Не найден файл конфигурации активной базы Loca Loca: $ConfigPath"
    }

    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    $connectionString = [string]$config.connectionString

    if ([string]::IsNullOrWhiteSpace($connectionString) -and
        -not [string]::IsNullOrWhiteSpace([string]$config.encryptedPassword)) {
        Add-Type -AssemblyName System.Security

        $encryptedBytes = $null
        $passwordBytes = $null
        $password = $null
        try {
            $encryptedBytes = [Convert]::FromBase64String([string]$config.encryptedPassword)
            $passwordBytes = [Security.Cryptography.ProtectedData]::Unprotect(
                $encryptedBytes,
                $null,
                [Security.Cryptography.DataProtectionScope]::CurrentUser
            )
            $password = [Text.Encoding]::UTF8.GetString($passwordBytes)

            $connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
            $connectionBuilder['Host'] = [string]$config.host
            $connectionBuilder['Port'] = if ($config.port) { [int]$config.port } else { 5432 }
            $connectionBuilder['Database'] = if ($config.database) { [string]$config.database } else { 'postgres' }
            $connectionBuilder['Username'] = [string]$config.username
            $connectionBuilder['Password'] = $password
            $connectionBuilder['SSL Mode'] = 'Require'
            $connectionBuilder['Pooling'] = $true
            $connectionBuilder['Timeout'] = 15
            $connectionBuilder['Command Timeout'] = 30
            $connectionBuilder['Maximum Pool Size'] = 5
            $connectionString = $connectionBuilder.ConnectionString
        }
        catch {
            throw "Не удалось расшифровать пароль из $ConfigPath. Запусти PowerShell под той же учётной записью Windows, под которой создавалась конфигурация. $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $encryptedBytes) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }
            if ($null -ne $passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
            $password = $null
            $connectionBuilder = $null
        }
    }
}
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "Не найдена строка подключения или DPAPI-конфигурация ни в LOCA_DB_CONNECTION, ни в файле $ConfigPath."
}

$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder.ConnectionString = $connectionString

foreach ($requiredKey in @('Host', 'Database', 'Username', 'Password')) {
    if (-not $builder.ContainsKey($requiredKey) -or [string]::IsNullOrWhiteSpace([string]$builder[$requiredKey])) {
        throw "В строке подключения отсутствует обязательный параметр $requiredKey."
    }
}

$hostName = [string]$builder['Host']
$databaseName = [string]$builder['Database']
$userName = [string]$builder['Username']
$password = [string]$builder['Password']
$port = if ($builder.ContainsKey('Port')) { [string]$builder['Port'] } else { '5432' }

$psqlCommand = Get-Command psql.exe -ErrorAction SilentlyContinue
$psqlPath = if ($psqlCommand) { $psqlCommand.Source } else { $null }
if (-not $psqlPath) {
    $postgresRoot = Join-Path $env:ProgramFiles 'PostgreSQL'
    if (Test-Path -LiteralPath $postgresRoot) {
        $candidate = Get-ChildItem -LiteralPath $postgresRoot -Filter psql.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'bin' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { $psqlPath = $candidate.FullName }
    }
}
if (-not $psqlPath) {
    throw 'Не найден psql.exe. Установи PostgreSQL Command Line Tools или добавь папку PostgreSQL\bin в PATH.'
}

$query = @'
SELECT COALESCE(json_agg(row_to_json(source_rows) ORDER BY source_rows.country, source_rows.city, source_rows.name), '[]'::json)
FROM (
    SELECT
        a.name,
        COALESCE(a.description, '') AS description,
        a.history,
        a.latitude::text AS latitude,
        a.longitude::text AS longitude,
        cat.name AS category,
        co.name AS country,
        ci.name AS city
    FROM attractions a
    JOIN categories cat ON cat.category_id = a.category_id
    JOIN cities ci ON ci.city_id = a.city_id
    JOIN countries co ON co.country_id = ci.country_id
) AS source_rows;
'@

$previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD', 'Process')
$previousSslMode = [Environment]::GetEnvironmentVariable('PGSSLMODE', 'Process')
$previousConnectTimeout = [Environment]::GetEnvironmentVariable('PGCONNECT_TIMEOUT', 'Process')
try {
    $env:PGPASSWORD = $password
    if ($builder.ContainsKey('SSL Mode')) {
        $sslMode = ([string]$builder['SSL Mode']).ToLowerInvariant().Replace('_', '-')
        if ($sslMode -in @('disable', 'allow', 'prefer', 'require', 'verify-ca', 'verify-full')) {
            $env:PGSSLMODE = $sslMode
        }
    }
    if ($builder.ContainsKey('Timeout')) {
        $timeoutSeconds = [int]$builder['Timeout']
        if ($timeoutSeconds -gt 0) { $env:PGCONNECT_TIMEOUT = [string]$timeoutSeconds }
    }

    $psqlArgs = @(
        '--no-psqlrc', '--quiet', '--tuples-only', '--no-align',
        '--host', $hostName,
        '--port', $port,
        '--username', $userName,
        '--dbname', $databaseName,
        '--command', $query
    )

    $output = & $psqlPath @psqlArgs 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Не удалось прочитать каталог из PostgreSQL (код $exitCode): $(($output | Out-String).Trim())"
    }
    $json = ($output | Out-String).Trim()
}
finally {
    if ($null -eq $previousPassword) {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    else {
        $env:PGPASSWORD = $previousPassword
    }
    if ($null -eq $previousSslMode) {
        Remove-Item Env:PGSSLMODE -ErrorAction SilentlyContinue
    }
    else {
        $env:PGSSLMODE = $previousSslMode
    }
    if ($null -eq $previousConnectTimeout) {
        Remove-Item Env:PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue
    }
    else {
        $env:PGCONNECT_TIMEOUT = $previousConnectTimeout
    }
    $password = $null
    $connectionString = $null
    $config = $null
    $builder = $null
}

if ([string]::IsNullOrWhiteSpace($json)) {
    throw 'PostgreSQL вернул пустой список достопримечательностей.'
}

$items = @($json | ConvertFrom-Json)
if ($items.Count -ne $ExpectedCount) {
    throw "В подключённой базе найдено $($items.Count) достопримечательностей, ожидалось $ExpectedCount. Файл $OutputPath не изменён. Проверь активную базу или укажи -ExpectedCount."
}

$categories = @($items | ForEach-Object { [string]$_.category } | Sort-Object -Unique)
$countries = @($items | ForEach-Object { [string]$_.country } | Sort-Object -Unique)
$cities = @($items | ForEach-Object {
    [pscustomobject]@{ Country = [string]$_.country; City = [string]$_.city }
} | Sort-Object Country, City -Unique)

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("-- Loca Loca: начальные справочные данные ($($items.Count) достопримечательностей).")
$lines.Add('-- Сформировано из активной базы Loca Loca. Повторный запуск не дублирует места.')
$lines.Add('')
$lines.Add('INSERT INTO categories (name) VALUES')
for ($i = 0; $i -lt $categories.Count; $i++) {
    $suffix = if ($i -lt $categories.Count - 1) { ',' } else { '' }
    $lines.Add("    ($(ConvertTo-SqlText $categories[$i]))$suffix")
}
$lines.Add('ON CONFLICT (name) DO NOTHING;')
$lines.Add('')
$lines.Add('INSERT INTO countries (name) VALUES')
for ($i = 0; $i -lt $countries.Count; $i++) {
    $suffix = if ($i -lt $countries.Count - 1) { ',' } else { '' }
    $lines.Add("    ($(ConvertTo-SqlText $countries[$i]))$suffix")
}
$lines.Add('ON CONFLICT (name) DO NOTHING;')
$lines.Add('')
$lines.Add('INSERT INTO cities (country_id, name)')
$lines.Add('SELECT co.country_id, v.city_name')
$lines.Add('FROM (VALUES')
for ($i = 0; $i -lt $cities.Count; $i++) {
    $suffix = if ($i -lt $cities.Count - 1) { ',' } else { '' }
    $tuple = "    ($(ConvertTo-SqlText $cities[$i].Country), $(ConvertTo-SqlText $cities[$i].City))$suffix"
    $lines.Add($tuple)
}
$lines.Add(') AS v(country_name, city_name)')
$lines.Add('JOIN countries co ON co.name = v.country_name')
$lines.Add('ON CONFLICT (country_id, name) DO NOTHING;')
$lines.Add('')
$lines.Add('INSERT INTO attractions')
$lines.Add('    (name, description, history, latitude, longitude, category_id, city_id)')
$lines.Add('SELECT v.name, v.description, v.history, v.latitude, v.longitude, cat.category_id, ci.city_id')
$lines.Add('FROM (VALUES')

for ($i = 0; $i -lt $items.Count; $i++) {
    $item = $items[$i]
    $suffix = if ($i -lt $items.Count - 1) { ',' } else { '' }
    $tuple = '    (' +
        (ConvertTo-SqlText $item.name) + ', ' +
        (ConvertTo-SqlText $item.description) + ', ' +
        (ConvertTo-SqlText $item.history) + ', ' +
        (ConvertTo-SqlNumber $item.latitude) + ', ' +
        (ConvertTo-SqlNumber $item.longitude) + ', ' +
        (ConvertTo-SqlText $item.category) + ', ' +
        (ConvertTo-SqlText $item.country) + ', ' +
        (ConvertTo-SqlText $item.city) + ')' + $suffix
    $lines.Add($tuple)
}

$lines.Add(') AS v(name, description, history, latitude, longitude, category_name, country_name, city_name)')
$lines.Add('JOIN categories cat ON cat.name = v.category_name')
$lines.Add('JOIN countries co ON co.name = v.country_name')
$lines.Add('JOIN cities ci ON ci.country_id = co.country_id AND ci.name = v.city_name')
$lines.Add('WHERE NOT EXISTS (')
$lines.Add('    SELECT 1 FROM attractions a')
$lines.Add('    WHERE a.name = v.name AND a.city_id = ci.city_id')
$lines.Add(');')
$lines.Add('')
$lines.Add('SELECT count(*) AS seeded_attractions FROM attractions;')

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$tempPath = $outputFullPath + '.tmp'
[IO.File]::WriteAllLines($tempPath, $lines, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempPath -Destination $outputFullPath -Force

Write-Host "Экспортировано достопримечательностей: $($items.Count)" -ForegroundColor Green
Write-Host "Категорий: $($categories.Count); стран: $($countries.Count); городов: $($cities.Count)"
Write-Host "Файл для установщика: $outputFullPath"
