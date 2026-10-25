#Requires -Version 5.1
<#
.SYNOPSIS
    发布 MemoDesk.App 并打包成 MSIX（可选签名）。

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -CertificateThumbprint 'ABCDEF...'
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.9.0.0',
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\MemoDesk.App\MemoDesk.App.csproj'
$stageDir = Join-Path $root 'artifacts\msix\stage'
$outputDir = Join-Path $root 'artifacts\msix'
$msixPath = Join-Path $outputDir ("MemoDesk_{0}_{1}.msix" -f $Version, $Runtime)

function Find-SdkTool([string]$Name) {
    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $kitsRoot)) {
        throw "未找到 Windows SDK：$kitsRoot。请安装 Windows SDK（例如 winget install Microsoft.WindowsSDK.10.0.26100.1742）。"
    }

    $tool = Get-ChildItem $kitsRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName

    if (-not $tool) {
        throw "未在 Windows SDK 中找到 $Name。"
    }

    return $tool
}

if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Host '>> dotnet publish（self-contained，安装包无需预装 .NET 运行时）'
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true -o $stageDir
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish 失败。'
}

Write-Host '>> 复制清单与图标资源'
Copy-Item (Join-Path $PSScriptRoot 'Package.appxmanifest') (Join-Path $stageDir 'AppxManifest.xml') -Force
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $stageDir 'Assets') -Recurse -Force

$manifestPath = Join-Path $stageDir 'AppxManifest.xml'
# PowerShell 5.1 的 Get-Content 默认按系统代码页读取；UTF-8 清单必须显式解码/编码。
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
$manifest = [regex]::Replace($manifest, '(?<=<Identity[^>]*Version=")[^"]+', $Version)
$utf8WithBom = [System.Text.UTF8Encoding]::new($true)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8WithBom)

try {
    $null = [xml]([System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8))
}
catch {
    throw "AppxManifest.xml 不是合法 XML，已停止打包：$($_.Exception.Message)"
}

Write-Host '>> makeappx pack'
$makeappx = Find-SdkTool 'makeappx.exe'
& $makeappx pack /d $stageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw 'makeappx 打包失败。'
}

if ($CertificateThumbprint) {
    Write-Host '>> signtool sign'
    $signtool = Find-SdkTool 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $CertificateThumbprint $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw 'signtool 签名失败。'
    }
}
else {
    Write-Warning '未提供 -CertificateThumbprint，生成的 MSIX 未签名，无法直接安装。'
}

Write-Host ''
Write-Host "生成：$msixPath"
