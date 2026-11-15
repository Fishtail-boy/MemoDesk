param(
    [Parameter(Mandatory)][string]$Output,
    [string]$Cargo = 'cargo',
    [switch]$Offline,
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'
$revision = '6eb33732f9b2150e02eef1b1f824b0c087a2c5f2'
$expectedHash = '16033EB397625E19B86F8A3F922B718DB715437AC7A4505A1620AB44506EDC5F'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cache = Join-Path $repository 'artifacts/daozai-build'
$archive = Join-Path $cache "$revision.zip"
$source = Join-Path $cache "daozai-$revision"
$Output = [IO.Path]::GetFullPath($Output, $PWD.Path)
New-Item -ItemType Directory -Path $cache,$Output -Force | Out-Null

if (!(Test-Path -LiteralPath $archive)) {
    if ($Offline) { throw "Missing cached Daozai archive: $archive" }
    Invoke-WebRequest -Uri "https://codeload.github.com/Fishtail-boy/daozai/zip/$revision" -OutFile "$archive.download"
    if ((Get-FileHash -LiteralPath "$archive.download").Hash -ne $expectedHash) { throw 'Daozai archive hash mismatch.' }
    Move-Item -LiteralPath "$archive.download" -Destination $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expectedHash) { throw 'Daozai archive hash mismatch.' }

Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
& (Join-Path $PSScriptRoot 'prepare.ps1') -Source $source
$target = Join-Path $cache 'target'
$profile = if ($DebugBuild) { 'debug' } else { 'release' }
$flags = @('build', '--locked', '--bin', 'daozai', '--target-dir', $target)
if (!$DebugBuild) { $flags += '--release' }
if ($Offline) { $flags += '--offline' }
Push-Location $source
try {
    & $Cargo @flags
    if ($LASTEXITCODE -ne 0) { throw "Daozai build failed ($LASTEXITCODE)." }
} finally { Pop-Location }

Copy-Item -LiteralPath (Join-Path $target "$profile/daozai.exe") -Destination $Output -Force
Copy-Item -LiteralPath (Join-Path $source 'LICENSE'),(Join-Path $source 'THIRD_PARTY_NOTICES.txt') -Destination $Output -Force
