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
# Compare against the directory path plus a trailing separator so a sibling like
# "app-extra" or "models-old" can never prefix-match "app"/"models" and get
# misclassified/flattened into the archive root.
$appDirPrefix = $appDir.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$modelDirPrefix = $modelDir.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if ($DryRun) { Write-Host "DRY RUN: would create deterministic, flat portable ZIP $zipPath"; exit 0 }
if (-not (Test-Path $appDir)) { throw "Portable source is missing app output: $appDir" }
if (-not (Test-Path (Join-Path $modelDir 'isnet-general-use-q8.onnx'))) { throw 'Portable source is missing the verified default model.' }
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($zipPath)) | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue $zipPath

$items = @()
foreach ($file in (Get-ChildItem -LiteralPath $source -File -Recurse)) {
    if ($file.FullName.StartsWith($appDirPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $file.FullName.Substring($appDirPrefix.Length)
    }
    elseif ($file.FullName.StartsWith($modelDirPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = 'models\' + $file.FullName.Substring($modelDirPrefix.Length)
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
        # Build the timestamp with an EXPLICIT zero offset via the DateTimeOffset(y,m,d,h,m,s,offset)
        # constructor rather than parsing a 'Z' string through [DateTime]::Parse. Parsing a 'Z' string
        # yields a Local-Kind DateTime converted to the machine's local time, and
        # [DateTimeOffset]::new(DateTime) then stamps the machine's local UTC offset onto it - west of
        # UTC that rolls the date back to 1979-12-31 local, which ZipArchiveEntry.LastWriteTime rejects
        # (and, even when it doesn't throw, makes the archive's bytes depend on the builder's timezone,
        # defeating determinism). Constructing the DateTimeOffset directly with offset zero means its
        # .DateTime component - the value the setter actually validates - is always exactly what we
        # write here, on every machine, in every timezone. Use :02 seconds past midnight rather than
        # :00 to stay clear of the 1980-01-01 00:00:00 DOS-time lower boundary entirely.
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 2, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead($item.File.FullName)
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
} finally { $archive.Dispose() }
Write-Host "Wrote portable ZIP: $zipPath"
