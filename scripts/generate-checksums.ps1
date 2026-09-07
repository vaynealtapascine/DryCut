[CmdletBinding()]
param(
    [string]$ArtifactsDir = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$dir = [IO.Path]::GetFullPath($ArtifactsDir)
if ($DryRun) { Write-Host "DRY RUN: would hash distributable files in $dir"; exit 0 }
if (-not (Test-Path $dir)) { throw "Artifacts directory not found: $dir" }
$files = Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -ne 'checksums.txt' } | Sort-Object Name
if (-not $files) { throw "No artifacts found to checksum." }
$lines = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant(), $file.Name }
[IO.File]::WriteAllLines((Join-Path $dir 'checksums.txt'), $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote checksums: $(Join-Path $dir 'checksums.txt')"
