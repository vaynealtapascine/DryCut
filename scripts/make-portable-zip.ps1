[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\installer\staging'),
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\BackgroundCut-portable.zip'),
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = [IO.Path]::GetFullPath($SourceDir)
$zipPath = [IO.Path]::GetFullPath($Output)
$appDir = Join-Path $source 'app'
$modelDir = Join-Path $source 'models'
if ($DryRun) { Write-Host "DRY RUN: would create deterministic, flat portable ZIP $zipPath"; exit 0 }
if (-not (Test-Path $appDir)) { throw "Portable source is missing app output: $appDir" }
if (-not (Test-Path (Join-Path $modelDir 'isnet-general-use-q8.onnx'))) { throw 'Portable source is missing the verified default model.' }
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($zipPath)) | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue $zipPath

$items = @()
foreach ($file in (Get-ChildItem -LiteralPath $source -File -Recurse)) {
    if ($file.FullName.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $file.FullName.Substring($appDir.Length).TrimStart('\','/')
    }
    elseif ($file.FullName.StartsWith($modelDir, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = 'models\' + $file.FullName.Substring($modelDir.Length).TrimStart('\','/')
    }
    else {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\','/')
    }
    $items += [PSCustomObject]@{ File = $file; Relative = ($relative -replace '\\','/') }
}

$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in ($items | Sort-Object Relative)) {
        $entry = $archive.CreateEntry($item.Relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new([DateTime]::Parse('1980-01-01T00:00:00Z'))
        $input = [IO.File]::OpenRead($item.File.FullName)
        $outputStream = $entry.Open()
        try { $input.CopyTo($outputStream) } finally { $outputStream.Dispose(); $input.Dispose() }
    }
} finally { $archive.Dispose() }
Write-Host "Wrote portable ZIP: $zipPath"
