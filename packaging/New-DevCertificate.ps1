#Requires -Version 5.1
<#
.SYNOPSIS
    创建开发用自签名代码签名证书，并导出 pfx / cer。

.DESCRIPTION
    证书 Subject 必须与 Package.appxmanifest 中的 Identity Publisher 一致（CN=MemoDesk Dev）。
    生成后需把 .cer 导入当前用户的“受信任人”存储，MSIX 才能安装：
        Import-Certificate -FilePath .\MemoDeskDev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=MemoDesk Dev',
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName 'MemoDesk Dev' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @(
        '2.5.29.37={text}1.3.6.1.5.5.7.3.3',  # 代码签名用途
        '2.5.29.19={text}'                    # 基本约束
    )

$password = ConvertTo-SecureString -String 'MemoDeskDev' -Force -AsPlainText
$pfxPath = Join-Path $OutDir 'MemoDeskDev.pfx'
$cerPath = Join-Path $OutDir 'MemoDeskDev.cer'

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Write-Host ''
Write-Host "证书指纹: $($cert.Thumbprint)"
Write-Host "PFX: $pfxPath（口令 MemoDeskDev，仅限本地开发）"
Write-Host "CER: $cerPath"
Write-Host ''
Write-Host '安装前把证书导入“受信任人”（当前用户）：'
Write-Host "  Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
