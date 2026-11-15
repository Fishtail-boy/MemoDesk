#Requires -Version 5.1
[CmdletBinding()]
param([switch]$RunUiChecks, [switch]$BuildDaozaiIsland)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Invalid release version.' }
$output = Join-Path $root 'artifacts\release'
$run = Join-Path $output ([Guid]::NewGuid().ToString('N'))
$stage = Join-Path $run "MemoDesk-$version-win-x64"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $root
try {
    dotnet restore MemoDesk.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build MemoDesk.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test MemoDesk.sln -c Release --no-build --no-restore --logger trx --results-directory (Join-Path $run 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($RunUiChecks) {
        dotnet run --project tests/MemoDesk.Vault.UiProbe -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Windows UI checks failed.' }
        dotnet run --project docs/implementation-results/2026-10-25-experience-upgrade/MemoFirstProbe -c Release -- --experience-checks
        if ($LASTEXITCODE -ne 0) { throw 'Reminder experience checks failed.' }
        $experience = @(Get-Content -LiteralPath 'docs/implementation-results/2026-10-25-experience-upgrade/experience-results.json' -Raw | ConvertFrom-Json)
        if ($experience.Count -ne 9 -or @($experience | Where-Object Status -ne 'PASS').Count -gt 0) {
            throw 'Reminder experience checks reported failures.'
        }
    }
    dotnet publish src/MemoDesk.App/MemoDesk.App.csproj -c Release -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($BuildDaozaiIsland) {
        & (Join-Path $root 'integrations/daozai/build.ps1') -Output (Join-Path $stage 'Island')
    }
    foreach ($required in @('MemoDesk.App.exe','coreclr.dll','e_sqlite3.dll','Microsoft.WindowsAppRuntime.Insights.Resource.dll','ThirdParty\THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Missing release dependency: $required" }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE-README.md') -Destination (Join-Path $stage '使用说明.md')
    $commit = git rev-parse HEAD
    $dirty = @(git status --porcelain).Count -gt 0
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $run 'tests') -Filter *.trx)
    if ($testFiles.Count -ne 3) { throw 'Expected results from all three test projects.' }
    $passed = 0
    foreach ($file in $testFiles) {
        [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ([int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) { throw 'Release tests are incomplete.' }
        $passed += [int]$counts.passed
    }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($stage.Length + 1); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ Version = $version; SourceCommit = [string]$commit; SourceHasUncommittedChanges = $dirty;
        BuiltAtUtc = [DateTime]::UtcNow.ToString('o'); Runtime = 'win-x64'; SelfContained = $true;
        TestsPassed = $passed; UiChecksRequested = [bool]$RunUiChecks; Files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'BUILD-INFO.json') -Encoding UTF8
    $zip = Join-Path $output "MemoDesk-$version-win-x64.zip"
    Compress-Archive -Path $stage -DestinationPath $zip -Force
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    "$($hash.Hash)  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
    Write-Host "Ready: $zip"
    Write-Host "Tests passed: $passed; SHA256: $($hash.Hash)"
}
finally { Pop-Location }
