<#
.SYNOPSIS
  Builds the DualAudio installer, portable zip and Velopack update feed locally (CI uses the same script).

.EXAMPLE
  ./build/pack.ps1                      # version from Directory.Build.props
  ./build/pack.ps1 -Version 0.1.1       # what the release workflow does for tag v0.1.1

.NOTES
  Output (in -OutputDir, default ./releases):
    DualAudioApp-win-Setup.exe       installer (per user, no admin)
    DualAudioApp-win-Portable.zip    portable copy
    DualAudioApp-<ver>-full.nupkg    update package (+ -delta.nupkg when the previous release is in -OutputDir)
    releases.win.json, assets.win.json, RELEASES   update feed files
  The pack id "DualAudioApp" is also the install folder (%LOCALAPPDATA%\DualAudioApp). It must never be
  "DualAudio": %LOCALAPPDATA%\DualAudio holds settings and logs, and Velopack deletes its install folder on uninstall.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDir = 'releases',
    [string] $PublishDir = 'artifacts/publish',
    # Passed to vpk --signParams (signtool), e.g. '/f cert.pfx /p **** /fd sha256 /tr http://timestamp.digicert.com /td sha256'.
    [string] $SignParams
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PackId = 'DualAudioApp'
$Root = Split-Path -Parent $PSScriptRoot
Push-Location $Root
try {
    if (-not $Version) {
        [xml] $props = Get-Content (Join-Path $Root 'Directory.Build.props')
        $Version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "Version '$Version' is not SemVer (expected e.g. 0.1.0)."
    }
    Write-Host "DualAudio $Version -> $OutputDir"

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    dotnet publish src/DualAudio.App/DualAudio.App.csproj -c Release -r win-x64 --self-contained `
        -p:Version=$Version -o $PublishDir
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    $pack = @(
        'vpk', 'pack',
        '--packId', $PackId,
        '--packVersion', $Version,
        '--runtime', 'win-x64',
        '--packDir', $PublishDir,
        '--mainExe', 'DualAudio.exe',
        '--packTitle', 'DualAudio',
        '--packAuthors', 'DualAudio',
        '--icon', 'branding/DualAudio.ico',
        '--outputDir', $OutputDir
    )
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    dotnet @pack
    if ($LASTEXITCODE) { throw 'vpk pack failed' }

    Write-Host ''
    Write-Host 'Created:'
    Get-ChildItem $OutputDir -File | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-40} {1,10:N0} bytes" -f $_.Name, $_.Length) }
}
finally {
    Pop-Location
}
