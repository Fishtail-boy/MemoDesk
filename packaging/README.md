# MSIX 打包说明

M0 阶段目标：验证 WPF 应用能以稳定应用身份（Package Identity / AUMID）打包、安装和启动，为 M5 的 Windows 通知打基础。

产物：`artifacts/msix/MemoDesk_<版本>_win-x64.msix`（默认 0.10.2.0）。

## 前置条件（一次性）

1. **.NET 10 SDK**（Windows 本机）。
2. **Windows SDK**（提供 `makeappx.exe` / `signtool.exe`）。如果没有：
   ```powershell
   winget install Microsoft.WindowsSDK.10.0.26100.1742
   ```
3. **开发证书**（Subject 必须与 `Package.appxmanifest` 的 Publisher 一致）：
   ```powershell
   cd packaging
   .\New-DevCertificate.ps1
   Import-Certificate -FilePath .\MemoDeskDev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```
   记下脚本输出的证书指纹。

## 打包与签名

```powershell
cd packaging
.\build-msix.ps1 -CertificateThumbprint <上一步的指纹>
```

## 安装与启动

```powershell
Add-AppxPackage -Path ..\artifacts\msix\MemoDesk_0.10.2.0_win-x64.msix
```

开始菜单中会出现“备忘助手”。也可以用 PowerShell 以 AUMID 启动（M5 通知激活会用到）：

```powershell
Get-StartApps | Where-Object Name -eq '备忘助手'
Start-Process "shell:AppsFolder\<PackageFamilyName>!App"
```

数据库与日志位于 `%LOCALAPPDATA%\MemoDesk\`（打包运行时可能被系统重定向到包容器的 LocalCache 下，属正常现象）。

## 卸载

```powershell
Get-AppxPackage MemoDesk | Remove-AppxPackage
```

## 注意

- `Identity Publisher` 与证书 Subject 必须完全一致，否则安装报 `0x800B0109`。
- 图标为占位素材，可用 `python packaging/tools/generate_assets.py` 重新生成，正式素材到位后替换 `packaging/Assets/`。
- 打包验证通过后，M1 起日常开发仍以 `dotnet run --project src/MemoDesk.App` 为主，无需每次打 MSIX。
- 应用使用 **Windows App SDK 自包含部署**（构建时还原约 170MB Runtime 包），MSIX 无需单独安装 Windows App Runtime；清单已包含 toast 通知激活与 COM 服务器扩展，用于系统通知按钮/冷启动。
- 即时系统通知使用 Windows App SDK 注册，支持打包与未打包应用；2026-10-25 已在 Windows 11 未打包测试宿主验证注册和请求交接。系统计划通知仍依赖可用身份；通知按钮、下拉、冷启动和 Windows 10 需要分别实测。注册或明确发送失败才允许按设置回落卡片，未知交接不自动重发。
- `Directory.Build.targets` 从当前架构的官方 Runtime MSIX 提取 `Microsoft.WindowsAppRuntime.Insights.Resource.dll`，补齐 SDK 2.5.1 组件自包含产物遗漏的文件。正常 build、publish、MSIX 载荷与独立探针输出均需要保留它；缺少此文件会使通知初始化失败。
- 计划通知使用 UWP `ScheduledToastNotification`；登记、撤销和启动对账已接入。明确的 5 分钟以内相对提醒由应用单调计时，到点调用系统即时通知，避免两种计时通道重复投递。
