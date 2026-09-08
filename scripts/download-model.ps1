[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\installer\staging\models\isnet-general-use-q8.onnx'),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ModelUri = 'https://huggingface.co/SacredNoir/isnet-general-use-onnx/resolve/main/isnet-general-use-q8.onnx?download=true'
$ExpectedBytes = 44436071L
$ExpectedSha256 = 'feed6f32a5e707ca7e939576b2d891b23fb9eb4114749657a5efc64e8651e43a'

$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($DryRun) {
    Write-Host "DRY RUN: would download $ModelUri"
    Write-Host "DRY RUN: would verify $ExpectedBytes bytes and SHA-256 $ExpectedSha256"
    exit 0
}

New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
$temp = "$destinationPath.download"
try {
    Remove-Item -Force -ErrorAction SilentlyContinue $temp
    $client = [Net.Http.HttpClient]::new()
    try {
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('BackgroundCut-release-builder/1.0')
        $response = $client.GetAsync($ModelUri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode()
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $file = [IO.File]::Create($temp)
            try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
        } finally { $response.Dispose() }
    } finally { $client.Dispose() }

    $actualBytes = (Get-Item $temp).Length
    if ($actualBytes -ne $ExpectedBytes) { throw "Model length mismatch: expected $ExpectedBytes bytes, got $actualBytes." }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temp).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) { throw "Model SHA-256 mismatch: expected $ExpectedSha256, got $actualHash." }
    Move-Item -Force $temp $destinationPath
    Write-Host "Verified model: $destinationPath"
} catch {
    Remove-Item -Force -ErrorAction SilentlyContinue $temp
    throw "Default model download/verification failed. $($_.Exception.Message)"
}
