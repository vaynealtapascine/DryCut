[CmdletBinding()]
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\src\BackgroundCut.Desktop\BackgroundCut.Desktop.csproj'),
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\installer\staging\app'),
    [string]$Version = '1.0.0',
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectPath = [IO.Path]::GetFullPath($Project)
$publishPath = [IO.Path]::GetFullPath($PublishDir)
if (-not (Test-Path -LiteralPath $projectPath)) { throw "Desktop project not found: $projectPath" }
if ($DryRun) {
    Write-Host "DRY RUN: dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:Version=$Version"
    exit 0
}

if (Test-Path $publishPath) { Remove-Item -Recurse -Force $publishPath }
New-Item -ItemType Directory -Force -Path $publishPath | Out-Null

dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $publishPath --property:PublishSingleFile=false --property:PublishReadyToRun=false --property:DebugType=None --property:DebugSymbols=false --property:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path (Join-Path $publishPath 'BackgroundCut.Desktop.exe'))) { throw 'Publish completed without BackgroundCut.Desktop.exe.' }
