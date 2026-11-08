# GitHub 推送工具（Artk Tools → artk-data）
# 直接在当前项目目录使用 Git，不再维护第二份克隆。
# 只提交/推送有变化的文件：未变化的不暂存；本地删除的文件不会同步删除远端。
# 首次初始化只拉取提交和目录树（blob:none），不重复下载已经存在的文件内容。
[CmdletBinding()]
param(
    [string]$Source    = "",
    [string]$Remote    = "https://github.com/Artk017/artk-data.git",
    [string]$Branch    = "main",
    [string]$UserName  = "Artk017",
    [string]$UserEmail = "artk017@users.noreply.github.com"
)

$ErrorActionPreference = 'Stop'
$env:GIT_TERMINAL_PROMPT = '0'
$env:GIT_AUTHOR_NAME = $UserName
$env:GIT_AUTHOR_EMAIL = $UserEmail
$env:GIT_COMMITTER_NAME = $UserName
$env:GIT_COMMITTER_EMAIL = $UserEmail

# PowerShell 5.1 在参数默认值里取不到 $PSScriptRoot，进脚本后再补上
if ([string]::IsNullOrWhiteSpace($Source)) {
    if ($PSScriptRoot) {
        $Source = $PSScriptRoot
    }
    else {
        $Source = (Get-Location).Path
    }
}

function Say([string]$Text, [string]$Color = 'Gray') {
    Write-Host $Text -ForegroundColor $Color
}

function FindGit {
    $candidates = @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $first = @($candidates) | Select-Object -First 1
    if ($first) { return [string]$first }

    $command = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw "没有找到 git.exe，请先安装 Git for Windows（winget install Git.Git）。"
}

try {
    $script:Git = FindGit

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "项目目录不存在：$Source"
    }

    Set-Location -LiteralPath $Source

    # ---------------- 首次初始化 ----------------
    if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) {
        Say "==> 初始化本地 Git 仓库" Cyan
        & $script:Git -C $Source init -b $Branch | Out-Null

        & $script:Git -C $Source remote add origin $Remote
        if ($LASTEXITCODE -ne 0) { throw "添加 origin 失败。" }

        Say "==> 获取远端提交与目录树（不下载文件内容）" Cyan
        & $script:Git -C $Source fetch --filter=blob:none origin $Branch
        if ($LASTEXITCODE -ne 0) { throw "无法访问远端仓库，请检查网络或 GitHub 凭据。" }

        # 让 HEAD / 索引指向远端最新提交，但不动本地工作区文件
        & $script:Git -C $Source update-ref "refs/heads/$Branch" FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw "初始化分支失败。" }

        & $script:Git -C $Source read-tree FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw "初始化索引失败。" }
    }
    else {
        $origin = & $script:Git -C $Source remote get-url origin 2>$null
        if ($LASTEXITCODE -ne 0) {
            & $script:Git -C $Source remote add origin $Remote
            if ($LASTEXITCODE -ne 0) { throw "添加 origin 失败。" }
        }
        elseif ($origin -ne $Remote) {
            & $script:Git -C $Source remote set-url origin $Remote
            if ($LASTEXITCODE -ne 0) { throw "更新 origin 地址失败。" }
        }
    }

    Say "==> 获取远端最新状态" Cyan
    & $script:Git -C $Source fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { throw "无法访问远端仓库，请检查网络或 GitHub 凭据。" }

    # 没有本地改动且落后远端时，直接把 HEAD/索引对齐到远端，不检出远端文件
    $dirty = (& $script:Git -c core.quotePath=false -C $Source status --porcelain)
    $behind = [int](& $script:Git -C $Source rev-list --count "HEAD..origin/$Branch")
    if (-not $dirty -and $behind -gt 0) {
        Say "==> 本地无改动，对齐远端提交（不检出额外文件）" Yellow
        & $script:Git -C $Source update-ref "refs/heads/$Branch" "origin/$Branch"
        if ($LASTEXITCODE -ne 0) { throw "对齐远端分支失败。" }

        & $script:Git -C $Source read-tree "origin/$Branch"
        if ($LASTEXITCODE -ne 0) { throw "对齐远端索引失败。" }
    }

    # ---------------- 找出变化的文件 ----------------
    Say "==> 扫描有变化的文件" Cyan
    $status = & $script:Git -c core.quotePath=false -C $Source status --porcelain
    $changes = @()

    foreach ($line in $status) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $code = $line.Substring(0, 2)
        $path = $line.Substring(3)

        # 跳过“远端有、本地没有”的删除项：本工具只推送新增和修改过的内容
        if ($code.Contains('D')) { continue }

        $changes += $path
    }

    if ($changes.Count -eq 0) {
        Say "没有检测到内容变化，未提交、未推送。" Green
        exit 0
    }

    Say "检测到 $($changes.Count) 个有变化的文件：" Cyan
    foreach ($path in $changes) { Write-Host "  [变更] $path" }

    # ---------------- 只暂存这些变化的文件 ----------------
    Say "==> 暂存变化的文件" Cyan
    $batchSize = 40
    for ($i = 0; $i -lt $changes.Count; $i += $batchSize) {
        $last = [Math]::Min($i + $batchSize - 1, $changes.Count - 1)
        $batch = @($changes[$i..$last])

        & $script:Git -C $Source add -- $batch
        if ($LASTEXITCODE -ne 0) { throw "暂存文件失败。" }
    }

    # 暂存后确认真的有待提交内容（忽略未暂存的删除项）
    $staged = & $script:Git -C $Source diff --cached --name-only
    if (-not $staged) {
        Say "没有可提交的差异（内容与远端一致）。" Green
        exit 0
    }

    Say "==> 创建提交" Cyan
    & $script:Git -C $Source commit -m "Sync local changes ($($changes.Count) files)"
    if ($LASTEXITCODE -ne 0) { throw "提交失败。" }

    # 提交期间远端可能又有新提交
    & $script:Git -C $Source fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { throw "获取远端最新状态失败。" }

    $behind = [int](& $script:Git -C $Source rev-list --count "HEAD..origin/$Branch")
    if ($behind -gt 0) {
        Say "==> 远端有新提交，rebase 后重试" Yellow
        & $script:Git -C $Source pull --rebase origin $Branch
        if ($LASTEXITCODE -ne 0) { throw "rebase 失败，请手动解决冲突后重试。" }
    }

    # 防重复推送：远端树和本地完全一致时丢弃本次提交
    $localTree = (& $script:Git -C $Source rev-parse "HEAD^{tree}").Trim()
    $remoteTree = (& $script:Git -C $Source rev-parse "origin/$Branch^{tree}").Trim()
    if ($localTree -eq $remoteTree) {
        Say "远端已包含相同内容，跳过重复推送。" Green
        & $script:Git -C $Source reset --soft "origin/$Branch" | Out-Null
        exit 0
    }

    $ahead = [int](& $script:Git -C $Source rev-list --count "origin/$Branch..HEAD")
    if ($ahead -eq 0) {
        Say "没有需要推送的提交。" Green
        exit 0
    }

    Say "==> 推送到 origin/$Branch" Cyan
    & $script:Git -C $Source push origin $Branch
    if ($LASTEXITCODE -ne 0) { throw "推送失败，请检查 GitHub 凭据或网络。" }

    $head = (& $script:Git -C $Source rev-parse --short HEAD).Trim()
    Say "完成：已推送 $($changes.Count) 个文件的变更（$head）。" Green
    Say "仓库：$Remote" Gray
}
catch {
    Say "失败：$($_.Exception.Message)" Red
    exit 1
}
