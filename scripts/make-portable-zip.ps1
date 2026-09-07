[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\installer\staging'),
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\BackgroundCut-portable.zip'),
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$source = [IO.Path]::GetFullPath($SourceDir); $zipPath = [IO.Path]::GetFullPath($Output)
if ($DryRun) { Write-Host "DRY RUN: would create deterministic ZIP $zipPath"; exit 0 }
if (-not (Test-Path (Join-Path $source 'app'))) { throw "Portable source is missing app output: $source" }
if (-not (Test-Path (Join-Path $source 'models\isnet-general-use-q8.onnx'))) { throw "Portable source is missing the verified default model." }
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($zipPath)) | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue $zipPath
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object { $_.FullName -notlike "$zipPath*" } | Sort-Object FullName
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\','/') -replace '\\','/'
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new([DateTime]::Parse('1980-01-01T00:00:00Z'))
        $input = [IO.File]::OpenRead($file.FullName); $outputStream = $entry.Open()
        try { $input.CopyTo($outputStream) } finally { $outputStream.Dispose(); $input.Dispose() }
    }
} finally { $archive.Dispose() }
Write-Host "Wrote portable ZIP: $zipPath"
