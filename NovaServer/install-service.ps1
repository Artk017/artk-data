﻿# Artk Server —— 装成 Windows 服务（计划任务方式，不需要额外组件）
# 用法：
#   右键「以管理员身份运行 PowerShell」，然后
#   powershell -ExecutionPolicy Bypass -File install-service.ps1
#   卸载：powershell -ExecutionPolicy Bypass -File install-service.ps1 -Uninstall
param(
    [switch]$Uninstall,
    [int]$Port = 5080,
    [string]$TaskName = "ArtkServer"
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限：请右键 PowerShell → 以管理员身份运行，再执行本脚本。" -ForegroundColor Red
    exit 1
}

# 程序目录：直接解压的文件夹版 = 脚本所在目录；安装包装的 = <安装目录>\current
$appDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$home = $appDir
if (Test-Path (Join-Path (Split-Path -Parent $appDir) "Update.exe")) {
    $home = Split-Path -Parent $appDir
}

$exe = Join-Path $appDir "Artk Server.exe"
$configPath = Join-Path $home "server.json"
$dataDir = Join-Path $home "data"

# ── 卸载 ─────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Host "==> 停止并删除计划任务 $TaskName" -ForegroundColor Cyan
    schtasks /End /TN $TaskName 2>$null | Out-Null
    schtasks /Delete /TN $TaskName /F 2>$null | Out-Null

    Get-Process -Name "Artk Server" -ErrorAction SilentlyContinue | Stop-Process -Force

    Remove-NetFirewallRule -DisplayName "Artk Server 清单服务" -ErrorAction SilentlyContinue
    Write-Host "==> 已卸载服务（数据和配置文件保留在 $home）" -ForegroundColor Green
    exit 0
}

# ── 安装 ─────────────────────────────────────────────────────────────
if (-not (Test-Path $exe)) {
    Write-Host "找不到 $exe，先把发布的整个文件夹拷过来，或先运行安装包。" -ForegroundColor Red
    exit 1
}

# 首次运行生成 server.json（含默认密钥）
if (-not (Test-Path $configPath)) {
    Write-Host "==> 首次运行，生成 server.json" -ForegroundColor Cyan
    # 新版是 WinExe（GUI 子系统），直接 & 调用 PowerShell 不会等它跑完，必须 Start-Process -Wait
    Start-Process -FilePath $exe -ArgumentList "--init" -WorkingDirectory $appDir -NoNewWindow -Wait | Out-Null
}

$config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$config.Port = $Port
($config | ConvertTo-Json -Depth 5) | Set-Content $configPath -Encoding UTF8

Write-Host "==> 注册开机自启计划任务（SYSTEM 身份，--headless 无窗口）" -ForegroundColor Cyan
schtasks /Create /TN $TaskName /TR "`"$exe`" --headless" /SC ONSTART /RU SYSTEM /RL HIGHEST /F | Out-Null
if ($LASTEXITCODE -ne 0) { throw "计划任务创建失败" }

Write-Host "==> 放行防火墙端口 $Port" -ForegroundColor Cyan
Remove-NetFirewallRule -DisplayName "Artk Server 清单服务" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "Artk Server 清单服务" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null

Write-Host "==> 立刻启动" -ForegroundColor Cyan
# 如果现在开着设置界面，它会占着端口，先关掉（关掉设置界面不影响已保存的配置）
$running = Get-Process -Name "Artk Server" -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}
schtasks /Run /TN $TaskName | Out-Null
Start-Sleep -Seconds 3

try {
    # 读接口也要密钥了，健康检查同样要带 X-Api-Key
    $headers = @{ 'X-Api-Key' = $config.ApiKey }
    $health = Invoke-RestMethod "http://localhost:$Port/api/health" -Headers $headers -TimeoutSec 5
    Write-Host ""
    Write-Host "==> 服务已跑起来：http://localhost:$Port  （$($health.count) 款软件，版本 v$($health.version)）" -ForegroundColor Green
}
catch {
    Write-Host "服务好像没起来，看下 http://localhost:$Port/api/health （要带 X-Api-Key 头）；日志在计划任务的历史记录里。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "API Key：$($config.ApiKey)" -ForegroundColor Gray
Write-Host "设置界面：双击 Artk Server.exe（不用带参数）。服务在跑也能打开，界面会自动连本机服务。" -ForegroundColor Gray
Write-Host "  · 端口 / 数据源改完要重启计划任务：schtasks /End /TN $TaskName ; schtasks /Run /TN $TaskName" -ForegroundColor DarkGray
Write-Host "  · 改端口还要放行防火墙：重新运行 install-service.ps1 -Port <新端口>" -ForegroundColor DarkGray
Write-Host "局域网访问：http://$($env:COMPUTERNAME):$Port ，桌面端填这个地址和上面的 Key" -ForegroundColor Gray
Write-Host ""
Write-Host "数据目录：$dataDir（catalog.json / *.reg 首次启动自动从 GitHub 下载）" -ForegroundColor Gray
Write-Host ""
Write-Host "想让清单改动自动传回 GitHub？打开 Artk Server.exe 界面，在「GitHub 自动发布」里填仓库和 token 保存即可。" -ForegroundColor Gray
Write-Host "（等价于在 server.json 里写 autoPublish / publishToken / publishRepo / publishTag 再重启服务）" -ForegroundColor DarkGray
Write-Host "手动推一次：Artk Server.exe --publish-now" -ForegroundColor DarkGray
