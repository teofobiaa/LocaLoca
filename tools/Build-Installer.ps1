$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$BuildScript = Join-Path $PSScriptRoot 'Build-Windows.ps1'
$InstallerScript = Join-Path $ProjectRoot 'installer/LocaLoca.iss'
$InstallerIcon = Join-Path $ProjectRoot 'installer/Assets/LocaLoca-Installer.ico'
$BootstrapScript = Join-Path $ProjectRoot 'tools/Install-CloudDependencies.ps1'
$WindowsOutput = Join-Path $ProjectRoot 'Builds/Windows'
$InstallerOutput = Join-Path $ProjectRoot 'Builds/Installer'

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($iscc) {
	$isccPath = $iscc.Source
} else {
	$programRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
		Select-Object -Unique
	$isccPath = $null

	foreach ($version in @('7', '6')) {
		foreach ($root in $programRoots) {
			$candidate = Join-Path $root "Inno Setup $version\ISCC.exe"
			if (Test-Path -LiteralPath $candidate) {
				$isccPath = $candidate
				break
			}
		}
		if ($isccPath) { break }
	}
}

if (-not $isccPath) {
	throw 'Не найден Inno Setup Compiler (ISCC.exe). Установи Inno Setup 6.3 или новее и повтори запуск.'
}
if (-not (Test-Path -LiteralPath $InstallerScript)) { throw "Не найден сценарий установщика: $InstallerScript" }
if (-not (Test-Path -LiteralPath $InstallerIcon)) { throw "Не найдена иконка установщика: $InstallerIcon" }
if (-not (Test-Path -LiteralPath $BootstrapScript)) { throw "Не найден сценарий настройки облачной БД: $BootstrapScript" }

& $BuildScript
if ($LASTEXITCODE -ne 0) { throw 'Сборка приложения завершилась ошибкой.' }
if (-not (Test-Path -LiteralPath (Join-Path $WindowsOutput 'LocaLoca.exe'))) {
	throw 'Не найден Builds/Windows/LocaLoca.exe после сборки.'
}
if (-not (Test-Path -LiteralPath (Join-Path $WindowsOutput 'Server/LocaLoca.Api.exe'))) {
	throw 'Не найден Builds/Windows/Server/LocaLoca.Api.exe после сборки.'
}
if (-not (Test-Path -LiteralPath (Join-Path $WindowsOutput 'Assets/loca-loca.ico'))) {
	throw 'Не найдена иконка приложения в Builds/Windows/Assets.'
}

New-Item -ItemType Directory -Path $InstallerOutput -Force | Out-Null
& $isccPath $InstallerScript
if ($LASTEXITCODE -ne 0) { throw 'Компиляция установщика завершилась ошибкой.' }

$installer = Join-Path $InstallerOutput 'LocaLoca-Setup.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw "Установщик не создан: $installer" }
Write-Host "Готовый установщик: $installer" -ForegroundColor Green
