# 打包安装包（Artk Tools）
#
# 用法：在仓库根目录执行
#     powershell -ExecutionPolicy Bypass -File build-installer.ps1
#     powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.2.0
#     powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.2.0 -OutDir dist\release-1.2.0
#
# 产物（默认 dist\release\）：
#     ArtkTools-win-Setup.exe     安装器（Inno Setup 向导：目标位置 / 开始菜单 / 附加任务 / 安装 / 完成）
#     ArtkTools-win-Portable.zip  免安装版
#
# Artk Tools 用 Inno Setup 打包（installer.iss，和 tubatools-master 同款向导）；
# Artk Server 仍用 Velopack + 自定义向导。
#
# 依赖：.NET 10 SDK + Inno Setup 6（winget install JRSoftware.InnoSetup）+ Velopack CLI（dotnet tool install --global vpk）。
# 说明：应用是 unpackaged WinUI 3，这里做「自包含」发布（.NET 运行时 + Windows App SDK 一起打包），
#      目标机器不需要另外装运行时；代价是装完约 230 MB。
#
# 版本号有讲究：安装包版本 = 这里的 -Version，装过一次之后再运行更高版本的 Setup 就是「更新」，
# 相同版本才会显示「修复」。所以每次发新版都要往上加。

param(
    [string]$Version = "",
    # 对外文件名前缀（ArtkTools-win-Setup.exe / ArtkTools-win-Portable.zip）
    [string]$ArtifactName = "ArtkTools",
    [string]$OutDir = "dist\release",
    # Artk Server 的安装包（独立 packId，卸载项不会和 Artk Tools 冲突）
    [string]$ServerPackId = "ArtkServer",
    [string]$ServerTitle = "Artk Server",
    [string]$ServerOutDir = "dist\release-server"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

try {
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"

    # 没指定版本就自动递增：装过一次之后，只有更高的版本号才会被认成「更新」
    $versionFile = "dist\version.txt"
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $last = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "1.0.0" }
        $parts = $last.Split(".")
        if ($parts.Count -lt 3) { $parts = @("1", "0", "0") }
        $Version = "{0}.{1}.{2}" -f $parts[0], $parts[1], ([int]$parts[2] + 1)
        Write-Host "==> 自动版本号：$last -> $Version" -ForegroundColor Cyan
    }

    Write-Host "==> 发布自包含版本（$Version）" -ForegroundColor Cyan
    # 先清掉旧的 dist\app，避免改名前的 WinUIDemo.exe 之类旧文件混进安装包
    Remove-Item "dist\app" -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish ArtkTools.csproj -c Release -r win-x64 --self-contained true `
        -p:WindowsAppSDKSelfContained=true -p:Platform=x64 -p:PublishReadyToRun=false `
        -p:Version=$Version -o dist\app -v m
    if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

    # 旧版本的「直链工具」已经从界面移除，清掉发布目录里的残留，别再混进安装包
    Remove-Item "dist\app\tools" -Recurse -Force -ErrorAction SilentlyContinue

    # Artk Server：自包含文件夹（WinUI + Windows App SDK），独立放 dist\server，供服务器部署和打包
    # dist\server 是构建产物，先清掉；数据文件随后从 NovaServer\data 复制
    Remove-Item "dist\server" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "==> 发布 Artk Server（清单服务）" -ForegroundColor Cyan
    dotnet publish NovaServer\NovaServer.csproj -c Release -r win-x64 --self-contained true `
        -p:WindowsAppSDKSelfContained=true -p:Platform=x64 `
        -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
        -p:Version=$Version -o dist\server -v m
    if ($LASTEXITCODE -ne 0) { throw "Artk Server publish 失败" }

    # 老名字（NovaServer.*）残留会让目录里出现两个 exe，清掉
    Get-ChildItem "dist\server" -Filter "NovaServer.*" -ErrorAction SilentlyContinue | Remove-Item -Force

    # 装服务的脚本跟着一起放过去，拷走整个 dist\server 就能在服务器上跑
    Copy-Item "NovaServer\install-service.ps1" "dist\server" -Force

    # 服务器没有网页，IIS 用的 web.config / 静态资源清单留着只会误导人，删掉
    Get-ChildItem "dist\server" -Filter "web.config" -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem "dist\server" -Filter "*.staticwebassets.endpoints.json" -ErrorAction SilentlyContinue | Remove-Item -Force

    # 资源文件（.reg 等）放在 data 目录，随服务一起分发
    New-Item -ItemType Directory -Force -Path "dist\server\data" | Out-Null
    if (Test-Path "NovaServer\data") {
        Copy-Item "NovaServer\data\*" "dist\server\data" -Recurse -Force
    }

    # ── Artk Tools 安装包：Inno Setup（和 tubatools-master 同款向导） ────────
    # 直接用 Inno 把 dist\app 的发布文件装到 Program Files，不再套 Velopack 的 Setup.exe；
    # 卸载器、开始菜单 / 桌面快捷方式、完成页「运行 Artk Tools」都由 Inno 负责。
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc) {
        $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($cmd) { $iscc = $cmd.Source }
    }

    if (-not $iscc) {
        throw "没有找到 Inno Setup 的 ISCC.exe，请先安装：winget install JRSoftware.InnoSetup"
    }

    # 上一版安装包可能正被 Windows 扫描或用户双击着，删不掉就换个名字让路，别把整个流程卡死
    if (Test-Path $OutDir) {
        try {
            Remove-Item $OutDir -Recurse -Force
        }
        catch {
            $parked = "$OutDir-old-" + (Get-Date -Format "HHmmss")
            Move-Item $OutDir $parked -ErrorAction Stop
            Write-Host "    旧目录被占用，已改名为 $parked" -ForegroundColor Yellow
        }
    }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    Write-Host "==> 编译 Inno Setup 安装包（版本 $Version）" -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$Version" "installer.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }

    $finalSetup = Join-Path $OutDir "$ArtifactName-win-Setup.exe"
    if (-not (Test-Path $finalSetup)) { throw "ISCC 没有生成 $finalSetup" }

    # 便携版：直接压缩发布目录（绿色解压即用，不经过安装器）
    Write-Host "==> 生成便携版 zip" -ForegroundColor Gray
    $finalZip = Join-Path $OutDir "$ArtifactName-win-Portable.zip"
    Remove-Item $finalZip -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path "dist\app").Path, $finalZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # ── Artk Server 安装包（Velopack + 自定义向导，和客户端共用一套向导代码） ──────────
    Write-Host "==> 生成 Artk Server 安装包（版本 $Version）" -ForegroundColor Cyan

    if (Test-Path $ServerOutDir) {
        try {
            Remove-Item $ServerOutDir -Recurse -Force
        }
        catch {
            $parked = "$ServerOutDir-old-" + (Get-Date -Format "HHmmss")
            Move-Item $ServerOutDir $parked -ErrorAction Stop
            Write-Host "    旧目录被占用，已改名为 $parked" -ForegroundColor Yellow
        }
    }

    vpk pack -u $ServerPackId -v $Version -p dist\server -e "Artk Server.exe" -r win-x64 `
        -i Assets\app.ico --packTitle $ServerTitle --packAuthors "Artk" `
        --delta None -o $ServerOutDir
    if ($LASTEXITCODE -ne 0) { throw "Artk Server vpk pack 失败" }

    $serverNupkg = Join-Path $ServerOutDir "$ServerPackId-$Version-full.nupkg"
    if (-not (Test-Path $serverNupkg)) {
        $actual = Get-ChildItem $ServerOutDir -Filter "*-full.nupkg" | Select-Object -First 1
        throw "Artk Server 生成的包版本不对：期望 $Version，实际 $($actual.Name)"
    }

    # 把 vpk 的 Setup.exe 内嵌进 Artk Server 安装向导（和 Artk Tools 同一套向导，多一个「注册后台服务」选项）
    Write-Host "==> 构建 Artk Server 安装向导" -ForegroundColor Cyan
    $serverInnerSetup = Join-Path $ServerOutDir "$ServerPackId-win-Setup.exe"
    $serverPayloadDir = "ServerSetup\payload"
    New-Item -ItemType Directory -Force -Path $serverPayloadDir | Out-Null
    Get-ChildItem $serverPayloadDir -Filter "*-win-Setup.exe" -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item $serverInnerSetup (Join-Path $serverPayloadDir "$ServerPackId-win-Setup.exe") -Force

    Remove-Item "dist\serversetupgen" -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish ServerSetup\ServerSetup.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -p:Version=$Version -o dist\serversetupgen -v m
    if ($LASTEXITCODE -ne 0) { throw "Artk Server 安装向导 publish 失败" }

    # 注意：服务端的 packId 和对外文件名一样，这里的 Copy 会直接覆盖 vpk 的 Setup.exe，
    # 不能再像客户端那样先 Copy 再 Remove（那会把刚写进去的向导删掉）
    Copy-Item "dist\serversetupgen\ArtkServerSetup.exe" (Join-Path $ServerOutDir "$ServerPackId-win-Setup.exe") -Force
    Write-Host "    已用自定义安装向导替换 Artk Server Setup.exe" -ForegroundColor Gray

    # 便携版：vpk 按 packId 命名，和对外名字一致就不用改
    Write-Host "    Artk Server 产物：$ServerOutDir" -ForegroundColor Gray

    # 记下来，下次自动 +1
    New-Item -ItemType Directory -Force -Path (Split-Path $versionFile) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $versionFile), $Version)

    Write-Host "==> 完成（版本 $Version）" -ForegroundColor Green
    Write-Host "    Artk Tools：$OutDir" -ForegroundColor Gray
    Get-ChildItem $OutDir -File |
        Where-Object { $_.Extension -in @(".exe", ".zip") } |
        Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize

    Write-Host "    Artk Server：$ServerOutDir" -ForegroundColor Gray
    Get-ChildItem $ServerOutDir -File |
        Where-Object { $_.Extension -in @(".exe", ".zip") } |
        Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize

    # 中间产物（app / server / setupgen / serversetupgen）和改名腾位留下的旧目录都清掉，
    # 构建后 dist 里只保留 dist\release 和 dist\release-server（version.txt 留着给下次自动 +1）
    Remove-Item "dist\app", "dist\server", "dist\setupgen", "dist\serversetupgen" -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem "dist" -Directory | Where-Object { $_.Name -like "*-old-*" } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "    已清理中间产物，只保留 dist\release 与 dist\release-server" -ForegroundColor Gray
}
finally {
    Pop-Location
}
