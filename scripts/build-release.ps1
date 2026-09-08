[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)][string]$Version = '1.0.0',
    [string]$DefaultModelPath,
    [string]$StrongModelPath,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$staging = Join-Path $repo 'installer\staging'
$artifacts = Join-Path $repo 'artifacts'
$iss = Join-Path $repo 'installer\BackgroundCut.iss'
$fastDestination = Join-Path $staging 'models\isnet-general-use-q8.onnx'
$strongDestination = Join-Path $staging 'models\birefnet-fp16.onnx'
$fastHash = 'feed6f32a5e707ca7e939576b2d891b23fb9eb4114749657a5efc64e8651e43a'
$strongHash = '3654c741eb80bd926ada8fed1713b506ccf8d30eb1f6487e87eb9f234f33df09'

function Copy-VerifiedArtifact([string]$Source, [string]$Destination, [string]$ExpectedHash, [string]$Label) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "$Label not found: $sourcePath" }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) { throw "$Label SHA-256 mismatch: expected $ExpectedHash, got $actualHash." }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $Destination -Force
    Write-Host "Verified ${Label}: $sourcePath"
}

$infrastructureTestProject = Join-Path $repo 'tests\BackgroundCut.Infrastructure.Tests\BackgroundCut.Infrastructure.Tests.csproj'

if ($DryRun) {
    Write-Host "DRY RUN: dotnet test $(Join-Path $repo 'BackgroundCut.sln') --configuration Release"
    & (Join-Path $PSScriptRoot 'publish-win-x64.ps1') -Version $Version -DryRun
    if ($DefaultModelPath) { Write-Host "DRY RUN: would verify and copy the supplied default model." }
    else { & (Join-Path $PSScriptRoot 'download-model.ps1') -DryRun }
    if ($StrongModelPath) { Write-Host "DRY RUN: would verify and include the supplied strong model." }
    Write-Host "DRY RUN: would set BACKGROUNDCUT_TEST_MODEL to the verified default model and re-run dotnet test $infrastructureTestProject --configuration Release to gate the release on a real inference pass."
    if ($StrongModelPath) { Write-Host "DRY RUN: would also set BACKGROUNDCUT_TEST_STRONG_MODEL to the verified strong model and include it in that gating test run." }
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

Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'LICENSE.txt') -Force
Copy-Item (Join-Path $repo 'README.md') (Join-Path $staging 'README.md') -Force
Copy-Item (Join-Path $repo 'docs\USER_GUIDE.txt') (Join-Path $staging 'USER_GUIDE.txt') -Force
Copy-Item (Join-Path $repo 'licenses') (Join-Path $staging 'licenses') -Recurse -Force

dotnet test (Join-Path $repo 'BackgroundCut.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

& (Join-Path $PSScriptRoot 'publish-win-x64.ps1') -PublishDir (Join-Path $staging 'app') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Publish step failed.' }

if ($DefaultModelPath) { Copy-VerifiedArtifact $DefaultModelPath $fastDestination $fastHash 'default model' }
else { & (Join-Path $PSScriptRoot 'download-model.ps1') -Destination $fastDestination }
if ($StrongModelPath) { Copy-VerifiedArtifact $StrongModelPath $strongDestination $strongHash 'strong model' }

# By this point a real, checksum-verified model file is on disk at $fastDestination (and, if
# supplied, $strongDestination). The smoke tests
# (RealModelSmokeSkipsUnlessBackgroundcutTestModelIsSet and its strong-model counterpart) report
# "passed" without touching a model unless these env vars are set, so a release must not be
# produced without re-running them against the real bundled model: that is what would have caught
# a hardcoded tensor name/dtype mismatch before it reached a user's install.
$env:BACKGROUNDCUT_TEST_MODEL = $fastDestination
if ($StrongModelPath) { $env:BACKGROUNDCUT_TEST_STRONG_MODEL = $strongDestination }
try {
    dotnet test $infrastructureTestProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Model-backed smoke test gate failed: the bundled model did not load or produce a plausible mask.' }
}
finally {
    Remove-Item Env:\BACKGROUNDCUT_TEST_MODEL -ErrorAction SilentlyContinue
    Remove-Item Env:\BACKGROUNDCUT_TEST_STRONG_MODEL -ErrorAction SilentlyContinue
}

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
