#Requires -Version 7.0
# ============================================================
# MikuGuard 构建脚本
# 用法：
#   .\build.ps1                # Release 构建 + 打 cipx
#   .\build.ps1 -Clean         # 先清 bin/obj 再构建
#   .\build.ps1 -NoCipx        # 只出 bin 产物，不打 cipx（本地调试用）
# ============================================================
param(
    [switch]$Clean,
    [switch]$NoCipx
)
$ErrorActionPreference = "Stop"

# dotnet 的中文输出是控制台代码页（GBK）编码的，pwsh 默认按 UTF-8 解，不调一下会全是乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$projectDir = $PSScriptRoot
$project    = Join-Path $projectDir "MikuGuard.csproj"
$outputDir  = Join-Path $projectDir "bin\Release\net8.0-windows10.0.19041.0"
$cipxPath   = Join-Path $projectDir "cipx\MikuGuard.cipx"

# 定位可用的 dotnet：本机已知可用安装（带 SDK 8.0.423）在 %LOCALAPPDATA%\Microsoft\dotnet，
# 而 PATH 中靠前的 C:\Program Files\dotnet 可能是残缺安装（--list-sdks 能列但 build 找不到 SDK）。
# 因此优先取 LOCALAPPDATA，其次 PATH，再回退 Program Files。
$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$dotnet = $candidates | Select-Object -First 1
if (-not $dotnet) {
    Write-Host "未找到 dotnet，请先安装 .NET SDK（https://dotnet.microsoft.com/download）。" -ForegroundColor Red
    exit 1
}

if ($Clean) {
    Write-Host "==> 清理 bin/obj ..." -ForegroundColor Cyan
    foreach ($dir in @("bin", "obj")) {
        $path = Join-Path $projectDir $dir
        if (Test-Path $path) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host "==> 构建 Release$(if ($NoCipx) { '（不生成 cipx）' }) ..." -ForegroundColor Cyan
Push-Location $projectDir
try {
    $buildArgs = @("build", $project, "-c", "Release")
    if ($NoCipx) { $buildArgs += "-p:CreateCipx=false" }
    & $dotnet @buildArgs
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -ne 0) {
    Write-Host "构建失败，已中止。" -ForegroundColor Red
    exit $code
}

$dll = Join-Path $outputDir "MikuGuard.dll"
if (-not (Test-Path $dll)) {
    Write-Host "构建报告成功但找不到产物：$dll" -ForegroundColor Red
    exit 1
}

$version = (Select-String -Path (Join-Path $projectDir "manifest.yml") -Pattern '^version:\s*(.+)$').Matches.Groups[1].Value
Write-Host "==> 产物：$dll（v$version）" -ForegroundColor Green

if (-not $NoCipx) {
    if (Test-Path $cipxPath) {
        $size = [math]::Round((Get-Item $cipxPath).Length / 1KB, 1)
        Write-Host "==> 安装包：$cipxPath（$size KB）" -ForegroundColor Green
    }
    else {
        Write-Host "构建成功但没生成 cipx，检查 CreateCipx/CreateCipx 目标。" -ForegroundColor Yellow
    }
}
