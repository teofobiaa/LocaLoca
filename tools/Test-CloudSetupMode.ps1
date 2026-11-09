$ErrorActionPreference = 'Stop'

$bootstrapPath = Join-Path $PSScriptRoot 'Install-CloudDependencies.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($bootstrapPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object {
        "line $($_.Extent.StartLineNumber), column $($_.Extent.StartColumnNumber): $($_.Message)"
    }) -join [Environment]::NewLine
    throw ("Bootstrap script has parser errors:" + [Environment]::NewLine + $details)
}

$functionAst = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-ConfiguredCloudApiUrl'
}, $true)
if (-not $functionAst) {
    throw 'Get-ConfiguredCloudApiUrl was not found.'
}

function Write-SetupLog {
    param([string]$Message)
}

Invoke-Expression $functionAst.Extent.Text

# An omitted or blank URL must still select the hosted API, never the
# interactive Supabase credential prompt.
$CloudApiUrl = ''
$UseLegacySupabaseSetup = $false
$env:LOCA_API_BASE_URL = ''
$result = Get-ConfiguredCloudApiUrl
$expected = 'https://localoca-api.onrender.com'
if (-not $result -or $result.GetLeftPart([UriPartial]::Authority) -ne $expected) {
    throw "Blank CloudApiUrl did not resolve to the hosted API. Actual: '$result'."
}

# A caller-supplied HTTPS endpoint must be retained.
$CloudApiUrl = 'https://example-api.onrender.com'
$result = Get-ConfiguredCloudApiUrl
if (-not $result -or $result.GetLeftPart([UriPartial]::Authority) -ne 'https://example-api.onrender.com') {
    throw "A custom HTTPS CloudApiUrl was not retained. Actual: '$result'."
}

# Direct Supabase setup is allowed only by an explicit opt-in switch.
$UseLegacySupabaseSetup = $true
$result = Get-ConfiguredCloudApiUrl
if ($null -ne $result) {
    throw 'Legacy Supabase mode should return null only when explicitly enabled.'
}

Write-Host 'Cloud setup mode tests passed.' -ForegroundColor Green
