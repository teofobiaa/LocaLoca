$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $ProjectRoot 'Server/LocaLoca.Api/LocaLoca.Api.csproj'
$Output = Join-Path $ProjectRoot 'LocaLoca/Server'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '.NET 10 SDK не найден. Установи актуальный стабильный .NET 10 SDK.' }
$version = (& $dotnet.Source --version).Trim()
if ($version -notmatch '^10\.\d+\.\d+$') {
    throw "Требуется стабильный .NET 10 SDK (10.0.x), без Preview/RC. Обнаружено: $version"
}
if (-not (Test-Path $ApiProject)) { throw "Не найден проект API: $ApiProject" }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Write-Host 'Публикация локального API для Windows x64…' -ForegroundColor Cyan
& $dotnet.Source publish $ApiProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Публикация локального API завершилась ошибкой.' }
if (-not (Test-Path (Join-Path $Output 'LocaLoca.Api.exe'))) { throw 'После публикации не найден LocaLoca.Api.exe.' }
Write-Host "Готово: $Output" -ForegroundColor Green
Write-Host 'Теперь открой LocaLoca.sln в Visual Studio и запусти профиль Unpackaged.' -ForegroundColor Green
