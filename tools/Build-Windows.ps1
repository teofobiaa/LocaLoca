$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ApiPublishScript = Join-Path $ProjectRoot 'tools/Publish-LocalApi.ps1'
$WinUiProject = Join-Path $ProjectRoot 'LocaLoca/LocaLoca.csproj'
$ApiOutput = Join-Path $ProjectRoot 'LocaLoca/Server'
$Output = Join-Path $ProjectRoot 'Builds/Windows'

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) { throw 'Не найден .NET SDK. Установи стабильный .NET 10 SDK.' }
$dotnetVersion = (& $dotnetCommand.Source --version).Trim()
if ($dotnetVersion -notmatch '^10\.\d+\.\d+$') {
    throw "Требуется стабильный .NET 10 SDK (10.0.x), без Preview/RC. Обнаружено: $dotnetVersion"
}

& $ApiPublishScript
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать локальный API.' }

# Remove generated output only after prerequisites and API publication succeeded.
if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
& dotnet publish $WinUiProject -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Сборка WinUI 3 завершилась ошибкой.' }

$apiDestination = Join-Path $Output 'Server'
New-Item -ItemType Directory -Path $apiDestination -Force | Out-Null
Copy-Item -Path (Join-Path $ApiOutput '*') -Destination $apiDestination -Recurse -Force

$assetsOutput = Join-Path $Output 'Assets'
New-Item -ItemType Directory -Path $assetsOutput -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $assetsOutput 'Mascot') -Force | Out-Null
foreach ($asset in @('Assets\loca-loca.ico', 'Assets\loca-loca-icon.png', 'Assets\Mascot\mascot-map.png', 'Assets\Mascot\mascot-celebrate.png')) {
    $assetSource = Join-Path $ProjectRoot "LocaLoca\$asset"
    $assetDestination = Join-Path $Output $asset
    Copy-Item -LiteralPath $assetSource -Destination $assetDestination -Force
}

if (-not (Test-Path (Join-Path $Output 'Server\LocaLoca.Api.exe'))) {
    throw 'В публикации не найден локальный API: Builds\Windows\Server\LocaLoca.Api.exe.'
}
if (-not (Test-Path (Join-Path $Output 'Assets\loca-loca.ico'))) {
    throw 'В публикации не найдена иконка приложения: Builds\Windows\Assets\loca-loca.ico.'
}
Write-Host "Сборка готова: $Output" -ForegroundColor Green
