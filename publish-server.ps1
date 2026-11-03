# 发布 Artk Server（原 NovaServer）：自包含文件夹（目标机器不用装 .NET / Windows App SDK）
# 新版 Artk Server.exe 自带 WinUI 设置界面，Windows App SDK 没法压成单文件，所以这里发布整个文件夹。
# 需要安装包时用 build-installer.ps1（会额外生成 ArtkServer-win-Setup.exe）。
# 用法：powershell -ExecutionPolicy Bypass -File publish-server.ps1
param(
    [string]$OutDir = "dist\server"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

try {
    Write-Host "==> 发布 Artk Server（自包含文件夹 + WinUI 设置界面）" -ForegroundColor Cyan
    dotnet publish NovaServer\NovaServer.csproj -c Release -r win-x64 --self-contained true `
        -p:WindowsAppSDKSelfContained=true -p:Platform=x64 `
        -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
        -o $OutDir -v m

    if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

    # 旧版是单文件 + 内嵌网页，这里顺手清掉 IIS / 静态资源的残留
    Get-ChildItem $OutDir -Filter "web.config" -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem $OutDir -Filter "*.staticwebassets.endpoints.json" -ErrorAction SilentlyContinue | Remove-Item -Force

    # 老名字留下的文件会让目录里出现两个 exe
    Get-ChildItem $OutDir -Filter "NovaServer.*" -ErrorAction SilentlyContinue | Remove-Item -Force

    # 装服务的脚本跟着一起放过去，拷走整个文件夹就能在服务器上跑
    Copy-Item "NovaServer\install-service.ps1" $OutDir -Force

    $exe = Join-Path $OutDir "Artk Server.exe"
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $total = [math]::Round((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 0)

    Write-Host ""
    Write-Host "==> 完成：$exe（主程序 $size MB，整个文件夹约 $total MB）" -ForegroundColor Green
    Write-Host "    把整个 $OutDir 文件夹拷到 Windows 服务器上：" -ForegroundColor Gray
    Write-Host "      - 后台服务：管理员运行 install-service.ps1（以 --headless 方式开机自启）" -ForegroundColor Gray
    Write-Host "      - 设置界面：双击 Artk Server.exe（服务在跑也能打开，会自动连本机服务）" -ForegroundColor Gray
}
finally {
    Pop-Location
}
