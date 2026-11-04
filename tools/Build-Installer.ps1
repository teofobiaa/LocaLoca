$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$BuildScript = Join-Path $PSScriptRoot 'Build-Windows.ps1'
$InstallerScript = Join-Path $ProjectRoot 'installer/LocaLoca.iss'
$InstallerOutput = Join-Path $ProjectRoot 'Builds/Installer'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'Не найден .NET SDK. Установите стабильный .NET 10 SDK.' }
$dotnetVersion = (& $dotnet.Source --version).Trim()
if ($dotnetVersion -notmatch '^10\.\d+\.\d+$') {
    throw "Требуется стабильный .NET 10 SDK (10.0.x). Обнаружено: $dotnetVersion"
}

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($candidates) { $isccPath = $candidates[0] }
} else {
    $isccPath = $iscc.Source
}
if (-not $isccPath) {
    throw 'Не найден Inno Setup Compiler (ISCC.exe). Установите Inno Setup 6 или 7 и повторите запуск.'
}

& $BuildScript
if ($LASTEXITCODE -ne 0) { throw 'Сборка приложения завершилась ошибкой.' }
if (-not (Test-Path (Join-Path $ProjectRoot 'Builds/Windows/LocaLoca.exe'))) {
    throw 'Не найден Builds/Windows/LocaLoca.exe после сборки.'
}
New-Item -ItemType Directory -Path $InstallerOutput -Force | Out-Null
& $isccPath $InstallerScript
if ($LASTEXITCODE -ne 0) { throw 'Компиляция установщика завершилась ошибкой.' }

$installer = Join-Path $InstallerOutput 'LocaLoca-Setup.exe'
if (-not (Test-Path $installer)) { throw "Установщик не создан: $installer" }
Write-Host "Установщик готов: $installer" -ForegroundColor Green
