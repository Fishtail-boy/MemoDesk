#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageDirectory,
    [string]$Compiler = 'ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Invalid version.' }
foreach ($required in @('MemoDesk.exe','coreclr.dll','e_sqlite3.dll','Island\daozai.exe','Island\LICENSE','ThirdParty\THIRD-PARTY-NOTICES.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Missing installer dependency: $required" }
}
& $Compiler "/DStageDir=$stage" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'MemoDesk.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
