[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)][string]$Version = '0.1.0',
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$staging = Join-Path $repo 'installer\staging'
$artifacts = Join-Path $repo 'artifacts'
$iss = Join-Path $repo 'installer\BackgroundCut.iss'
if ($DryRun) {
    & (Join-Path $PSScriptRoot 'publish-win-x64.ps1') -DryRun
    & (Join-Path $PSScriptRoot 'download-model.ps1') -DryRun
    & (Join-Path $PSScriptRoot 'assemble-notices.ps1') -DryRun
    & (Join-Path $PSScriptRoot 'make-portable-zip.ps1') -DryRun
    & (Join-Path $PSScriptRoot 'generate-checksums.ps1') -DryRun
    Write-Host 'DRY RUN: would locate Inno Setup 6/7 and compile a per-user installer.'
    exit 0
}
if (-not (Test-Path $iss)) { throw "Inno script not found: $iss" }
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force -Path $staging, $artifacts | Out-Null

& (Join-Path $PSScriptRoot 'publish-win-x64.ps1') -PublishDir (Join-Path $staging 'app')
if ($LASTEXITCODE -ne 0) { throw 'Publish step failed.' }
& (Join-Path $PSScriptRoot 'download-model.ps1') -Destination (Join-Path $staging 'models\isnet-general-use-q8.onnx')
& (Join-Path $PSScriptRoot 'assemble-notices.ps1') -Output (Join-Path $staging 'THIRD-PARTY-NOTICES.txt')
& (Join-Path $PSScriptRoot 'make-portable-zip.ps1') -SourceDir $staging -Output (Join-Path $artifacts "BackgroundCut-portable-$Version.zip")

$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }
$iscc = $candidates | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 or 7 (ISCC.exe) was not found in per-user or system install paths.' }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$staging" "/DOutputDir=$artifacts" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
& (Join-Path $PSScriptRoot 'generate-checksums.ps1') -ArtifactsDir $artifacts
Write-Host "Release artifacts are in $artifacts"
