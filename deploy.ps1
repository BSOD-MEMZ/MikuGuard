#Requires -Version 7.0
# ============================================================
# MikuGuard 部署脚本（编译 → 关宿主 → 覆盖插件目录 → 启动）
# 用法：
#   .\deploy.ps1             # 构建并部署，然后启动 ClassIsland
#   .\deploy.ps1 -NoBuild    # 跳过构建（只部署当前产物）
#   .\deploy.ps1 -NoStart    # 构建并部署，但不启动 ClassIsland
#   .\deploy.ps1 -NoCipx     # 构建时不打 cipx（更快）
# ============================================================
param(
    [switch]$NoBuild,
    [switch]$NoStart,
    [switch]$NoCipx,
    [string]$PluginDir = "D:\Dev\ClassIsland\data\Plugins\mikuguard.camera-guard",
    [string]$HostExe   = "D:\Dev\ClassIsland\ClassIsland.exe"
)
$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$outputDir  = Join-Path $projectDir "bin\Release\net8.0-windows10.0.19041.0"

if (-not $NoBuild) {
    # 构建走同一套脚本，参数原样透传
    $buildArgs = @{}
    if ($NoCipx) { $buildArgs["NoCipx"] = $true }
    & (Join-Path $projectDir "build.ps1") @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "构建失败，已中止部署。" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path $outputDir)) {
    Write-Host "未找到产物目录：$outputDir" -ForegroundColor Red
    exit 1
}

# 关宿主必须用 Stop-Process：DLL 被锁住的话新版本根本拷不进去，
# 而残留的旧进程会让你以为「改了没效果」。
Write-Host "==> 关闭 ClassIsland（避免 DLL 被占用）..." -ForegroundColor Cyan
Get-Process -Name "ClassIsland*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "==> 部署到 $PluginDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
# 先清空再整体复制：避免旧布局残留（例如资源曾平铺在顶层）造成同名目录/文件冲突
Remove-Item (Join-Path $PluginDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $outputDir "*") $PluginDir -Recurse -Force

if (-not $NoStart) {
    if (Test-Path $HostExe) {
        Write-Host "==> 启动 ClassIsland ..." -ForegroundColor Cyan
        Start-Process $HostExe
    }
    else {
        Write-Host "找不到宿主：$HostExe（用 -HostExe 指一个）" -ForegroundColor Yellow
    }
}

Write-Host "部署完成。" -ForegroundColor Green
