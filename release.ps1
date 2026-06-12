#Requires -Version 5.1
<#
.SYNOPSIS
    Build, package, and publish a new release of Jellyfin.Plugin.LetterboxdSocial.

.PARAMETER Version
    New version number in the form 1.0.0.X  (e.g. 1.0.0.39).

.PARAMETER Changelog
    One-line release note shown in Jellyfin's plugin catalogue and the GitHub release.

.EXAMPLE
    .\release.ps1 -Version 1.0.0.39 -Changelog "Fix Letterboxd pagination headers so /films/page/2+ is accessible."
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Changelog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot    = $PSScriptRoot
$Dotnet      = Join-Path $RepoRoot '.dotnet\dotnet.exe'
$PublishDir  = Join-Path $RepoRoot "dist\LetterboxdSocial"
$ZipName     = "LetterboxdSocial_$Version.zip"
$ZipPath     = Join-Path $RepoRoot "dist\$ZipName"

# Locate GitHub CLI - check PATH first, fall back to default install location.
$ghCmd = Get-Command gh -ErrorAction SilentlyContinue
$GhExe = if ($ghCmd) { $ghCmd.Source } else { 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not (Test-Path $GhExe)) { throw "GitHub CLI (gh) not found. Install from https://cli.github.com/" }

$env:DOTNET_CLI_HOME                   = Join-Path $RepoRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = '1'

$Today             = (Get-Date).ToUniversalTime().Date
$TimestampMeta     = $Today.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
$TimestampManifest = $Today.ToString("yyyy-MM-ddTHH:mm:ssZ")

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Done([string]$msg) { Write-Host "    $msg" -ForegroundColor Green }

Write-Host "`nReleasing Letterboxd Social v$Version" -ForegroundColor Yellow
Write-Host "Changelog: $Changelog`n"

# --- 1. .csproj ---
Write-Step "Bumping version in .csproj"
$CsprojPath = Join-Path $RepoRoot 'Jellyfin.Plugin.LetterboxdSocial.csproj'
$csproj = Get-Content $CsprojPath -Raw
$csproj = $csproj -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[System.IO.File]::WriteAllText($CsprojPath, $csproj, [System.Text.Encoding]::UTF8)
Write-Done "Done."

# --- 2. meta.json ---
Write-Step "Updating meta.json"
$MetaPath = Join-Path $RepoRoot 'meta.json'
$meta = Get-Content $MetaPath -Raw -Encoding UTF8 | ConvertFrom-Json
$meta.version   = $Version
$meta.changelog = $Changelog
$meta.timestamp = $TimestampMeta
[System.IO.File]::WriteAllText($MetaPath, ($meta | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)
Write-Done "Done."

# --- 3. build.yaml ---
Write-Step "Updating build.yaml"
$BuildPath = Join-Path $RepoRoot 'build.yaml'
$build = Get-Content $BuildPath -Raw -Encoding UTF8
$build = $build -replace '(?m)^version:\s*"[^"]+"', "version: `"$Version`""
# changelog is the last key; replace from "changelog:" to end of file
$build = $build -replace '(?s)^(changelog:.*)$', "changelog: >`n  $Changelog`n"
[System.IO.File]::WriteAllText($BuildPath, $build, [System.Text.Encoding]::UTF8)
Write-Done "Done."

# --- 4. Publish ---
Write-Step "Publishing"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
& $Dotnet publish $CsprojPath -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Done "Published to $PublishDir"

# --- 5. Zip ---
Write-Step "Zipping"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath
if (-not (Test-Path $ZipPath)) { throw "Zip was not created." }
Write-Done "Created $ZipPath"

# --- 6. MD5 ---
Write-Step "Computing MD5"
$Md5 = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToUpperInvariant()
Write-Done "MD5: $Md5"

# --- 7. manifest.json ---
Write-Step "Updating manifest.json"
$ManifestPath = Join-Path $RepoRoot 'manifest.json'
$manifestParsed = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
# PS 5.1 returns the element directly for a single-element JSON array, not a PS array.
$plugin = if ($manifestParsed -is [System.Array]) { $manifestParsed[0] } else { $manifestParsed }
$newEntry = [PSCustomObject]@{
    version   = $Version
    changelog = $Changelog
    targetAbi = "10.11.0.0"
    sourceUrl = "https://github.com/dcm2610/jellyfin-letterboxd-social/releases/download/v$Version/$ZipName"
    checksum  = $Md5
    timestamp = $TimestampManifest
}
$allVersions = @($newEntry) + @($plugin.versions)
$plugin | Add-Member -MemberType NoteProperty -Name 'versions' -Value $allVersions -Force
[System.IO.File]::WriteAllText($ManifestPath, (ConvertTo-Json -InputObject @($plugin) -Depth 10), [System.Text.Encoding]::UTF8)
Write-Done "Done."

# --- 8. README.md ---
Write-Step "Updating README.md"
$ReadmePath = Join-Path $RepoRoot 'README.md'
# -Encoding UTF8 is required: PS 5.1 defaults to ANSI and would mangle em-dashes/arrows on rewrite.
$readme = Get-Content $ReadmePath -Raw -Encoding UTF8
$readme = $readme -replace 'releases/download/v[\d.]+/LetterboxdSocial_[\d.]+\.zip', "releases/download/v$Version/LetterboxdSocial_$Version.zip"
[System.IO.File]::WriteAllText($ReadmePath, $readme, [System.Text.Encoding]::UTF8)
Write-Done "Done."

# --- 9. Validate JSON ---
Write-Step "Validating JSON"
Get-Content $ManifestPath -Raw | ConvertFrom-Json | Out-Null
Get-Content $MetaPath     -Raw | ConvertFrom-Json | Out-Null
Write-Done "manifest.json and meta.json are valid."

# --- 10. Commit ---
Write-Step "Committing"
git -C $RepoRoot add Jellyfin.Plugin.LetterboxdSocial.csproj meta.json build.yaml manifest.json README.md
git -C $RepoRoot commit -m @"
Release v$Version

$Changelog
"@
if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
Write-Done "Committed."

# --- 11. Push ---
Write-Step "Pushing to origin/main"
git -C $RepoRoot push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
Write-Done "Pushed."

# --- 12. GitHub release ---
Write-Step "Creating GitHub release"
& $GhExe release create "v$Version" $ZipPath `
    --repo dcm2610/jellyfin-letterboxd-social `
    --title "v$Version" `
    --notes $Changelog
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Host "`nDone. v$Version is live." -ForegroundColor Yellow
Write-Host "Manifest URL (use this in Jellyfin):"
Write-Host "  https://raw.githubusercontent.com/dcm2610/jellyfin-letterboxd-social/main/manifest.json"
