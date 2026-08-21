[CmdletBinding()]
param(
    [ValidateSet("Repository", "Staged")]
    [string]$Scope = "Repository"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$locationPushed = $false

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = @(& git @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure)
    {
        throw "Git command failed: git $($Arguments -join ' ')"
    }

    return $output
}

function Add-Finding {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Findings,
        [Parameter(Mandatory)]
        [string]$Category,
        [Parameter(Mandatory)]
        [string]$Path,
        [int]$Line = 0
    )

    $Findings.Add([pscustomobject]@{
        Category = $Category
        File = $Path
        Line = if ($Line -gt 0) { $Line } else { "-" }
    })
}

try
{
    $repositoryRootOutput = @(Invoke-Git -Arguments @("rev-parse", "--show-toplevel"))
    $repositoryRoot = $repositoryRootOutput[0]
    Push-Location -LiteralPath $repositoryRoot
    $locationPushed = $true

    $files = if ($Scope -eq "Staged")
    {
        @(Invoke-Git -Arguments @("diff", "--cached", "--name-only", "--diff-filter=ACMR"))
    }
    else
    {
        @(Invoke-Git -Arguments @("ls-files", "--cached", "--others", "--exclude-standard"))
    }

    $files = @($files |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            ($Scope -eq "Staged" -or (Test-Path -LiteralPath $_ -PathType Leaf))
        } |
        Sort-Object -Unique)
    $findings = [System.Collections.Generic.List[object]]::new()

    $allowlistPath = Join-Path $repositoryRoot "scripts/privacy-allowlist.json"
    if (-not (Test-Path -LiteralPath $allowlistPath -PathType Leaf))
    {
        throw "Privacy allowlist is missing: scripts/privacy-allowlist.json"
    }

    $allowlist = Get-Content -LiteralPath $allowlistPath -Raw | ConvertFrom-Json
    $approvedBinaryFiles = @{}
    foreach ($property in $allowlist.approvedBinaryFiles.PSObject.Properties)
    {
        $approvedBinaryFiles[$property.Name.Replace("\", "/")] = [string]$property.Value
    }

    $binaryExtensions = @(
        ".7z", ".appx", ".appxbundle", ".bmp", ".db", ".dll", ".dmp", ".docx",
        ".exe", ".gif", ".ico", ".jpeg", ".jpg", ".msi", ".msix", ".nupkg",
        ".otf", ".p12", ".pdf", ".pdb", ".pem", ".pfx", ".png", ".pptx", ".sqlite",
        ".sqlite3", ".snupkg", ".ttf", ".webp", ".xlsx", ".zip"
    )

    $sensitivePathPatterns = @(
        '(?i)(^|/)(?:\.credentials\.json|\.env(?:\..+)?|secrets\.json|usage-cache\.json|window-placement\.json|hooks\.json)$',
        '(?i)(^|/)appsettings\..+\.local\.json$',
        '(?i)(^|/)(?:\.vs|artifacts|bin|debug|dist|obj|publish|release|testresults)(/|$)',
        '(?i)\.(?:bak|cache|crash|key|log|pid|temp|tmp|trace)$'
    )

    $contentPatterns = [ordered]@{
        PrivateKey = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        AnthropicToken = ('s' + 'k-ant-[A-Za-z0-9_-]{12,}')
        OpenAIToken = ('s' + 'k-(?:proj-)?[A-Za-z0-9_-]{20,}')
        GitHubToken = ('gh' + '[pousr]_[A-Za-z0-9]{20,}')
        CloudAccessKey = '(?:AKIA|ASIA)[A-Z0-9]{16}'
        BearerToken = ('(?i)\bBear' + 'er\s+[A-Za-z0-9._~+/-]{12,}')
        JsonWebToken = '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b'
        SecretLiteral = '(?i)\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|connection[_-]?string|cookie|session[_-]?id)\b\s*[:=]\s*["''][^"'']{8,}["'']'
        AuthorizationLiteral = '(?i)\bauthorization\b\s*[:=]\s*["''][^"'']{8,}["'']'
        AbsoluteWindowsPath = '(?i)(?<![A-Za-z0-9])[A-Z]:\\(?:[^\\/:*?"<>|\r\n]+\\)*[^\\/:*?"<>|\r\n]*'
        EmailAddress = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
        PrivateIpAddress = '\b(?:10\.(?:\d{1,3}\.){2}\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b'
    }

    foreach ($relativePathValue in $files)
    {
        $relativePath = $relativePathValue.Replace("\", "/")

        foreach ($pathPattern in $sensitivePathPatterns)
        {
            if ($relativePath -match $pathPattern)
            {
                Add-Finding -Findings $findings -Category "Sensitive or generated filename" -Path $relativePath
                break
            }
        }

        $extension = [IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        if ($binaryExtensions -contains $extension)
        {
            $blobHashOutput = @(if ($Scope -eq "Staged")
            {
                Invoke-Git -Arguments @("rev-parse", ":$relativePath")
            }
            else
            {
                Invoke-Git -Arguments @("hash-object", "--no-filters", "--", $relativePath)
            })
            $blobHash = $blobHashOutput[0]

            if (-not $approvedBinaryFiles.ContainsKey($relativePath) -or
                $approvedBinaryFiles[$relativePath] -ne $blobHash)
            {
                Add-Finding -Findings $findings -Category "Unapproved binary or image" -Path $relativePath
            }

            continue
        }

        $content = if ($Scope -eq "Staged")
        {
            (Invoke-Git -Arguments @("show", ":$relativePath")) -join "`n"
        }
        else
        {
            [IO.File]::ReadAllText((Join-Path $repositoryRoot $relativePath))
        }

        if ($content.Contains([char]0))
        {
            Add-Finding -Findings $findings -Category "Unapproved binary content" -Path $relativePath
            continue
        }

        $lines = $content -split "`r?`n"
        for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++)
        {
            foreach ($entry in $contentPatterns.GetEnumerator())
            {
                if ($lines[$lineIndex] -match $entry.Value)
                {
                    Add-Finding -Findings $findings -Category $entry.Key -Path $relativePath -Line ($lineIndex + 1)
                }
            }
        }
    }

    if ($findings.Count -gt 0)
    {
        Write-Host "Privacy and credential check FAILED." -ForegroundColor Red
        Write-Host "Only the issue type and location are shown; matching values are intentionally hidden."
        $findings | Sort-Object Category, File, Line -Unique | Format-Table -AutoSize
        Write-Host "Review the staged content. Do not bypass this hook for public commits." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "Privacy and credential check passed ($Scope scope, $($files.Count) files)." -ForegroundColor Green
}
catch
{
    Write-Error "Privacy and credential check could not complete: $($_.Exception.Message)"
    exit 2
}
finally
{
    if ($locationPushed)
    {
        Pop-Location
    }
}
