[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (& git rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot))
{
    throw "Run this script from inside the AIUsageMonitor Git repository."
}

Push-Location -LiteralPath $repositoryRoot
try
{
    & git config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not configure the repository Git hooks path."
    }

    & (Join-Path $repositoryRoot "scripts/Invoke-PrivacyCheck.ps1") -Scope Repository
    if ($LASTEXITCODE -ne 0)
    {
        throw "The initial privacy and credential check failed."
    }

    Write-Host "AIUsageMonitor pre-commit privacy hook installed." -ForegroundColor Green
}
finally
{
    Pop-Location
}
