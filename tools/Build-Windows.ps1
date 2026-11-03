$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) { throw 'Не найден .NET SDK. Установи стабильный .NET 10 SDK.' }
$dotnetVersion = (& $dotnetCommand.Source --version).Trim()
if ($dotnetVersion -notmatch '^10\.\d+\.\d+$') { throw "Требуется стабильный .NET 10 SDK (10.0.x), без Preview/RC. Обнаружено: $dotnetVersion" }
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ApiPublishScript = Join-Path $ProjectRoot 'tools/Publish-LocalApi.ps1'
$WinUiProject = Join-Path $ProjectRoot 'LocaLoca/LocaLoca.csproj'
$Output = Join-Path $ProjectRoot 'Builds/Windows'

& $ApiPublishScript
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать локальный API.' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
& dotnet publish $WinUiProject -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Сборка WinUI 3 завершилась ошибкой.' }
Write-Host "Сборка готова: $Output" -ForegroundColor Green
