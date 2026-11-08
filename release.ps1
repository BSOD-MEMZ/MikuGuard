#Requires -Version 7.0
# ============================================================
# MikuGuard 发版脚本
# 做的事：改版本号 → 编译 → 校验 cipx → 归集到 dist\ → 出校验和与发版说明
#         （加 -Publish 再提交版本号、打 tag、用 gh 建 Release）
#
# 用法：
#   .\release.ps1                          # 用当前版本号打包到 dist\
#   .\release.ps1 -Version 1.0.1.0         # 先把版本号改成 1.0.1.0 再打包
#   .\release.ps1 -Version 1.0.1.0 -Publish   # 顺手提交 + tag + gh release
#   .\release.ps1 -Notes "修了什么"        # 自定义发版说明首行
# ============================================================
param(
    [string]$Version,
    [string]$Notes,
    [switch]$Publish,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$manifest   = Join-Path $projectDir "manifest.yml"
$assembly   = Join-Path $projectDir "AssemblyInfo.cs"
$cipxPath   = Join-Path $projectDir "cipx\MikuGuard.cipx"
$distDir    = Join-Path $projectDir "dist"

function Get-ManifestVersion {
    (Select-String -Path $manifest -Pattern '^version:\s*(\S+)\s*$').Matches.Groups[1].Value
}

# ---------------- 1) 版本号 ----------------

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        Write-Host "版本号要写成四段：1.0.1.0（收到：$Version）" -ForegroundColor Red
        exit 1
    }

    Write-Host "==> 版本号 → $Version" -ForegroundColor Cyan

    $text = Get-Content $manifest -Raw
    $text = [regex]::Replace($text, '(?m)^version:[ \t]*\S+[ \t]*$', "version: $Version")
    Set-Content -Path $manifest -Value $text -NoNewline -Encoding utf8

    $text = Get-Content $assembly -Raw
    $text = [regex]::Replace($text, 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$Version`")")
    $text = [regex]::Replace($text, 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$Version`")")
    Set-Content -Path $assembly -Value $text -NoNewline -Encoding utf8
}

$version = Get-ManifestVersion
if (-not $version) {
    Write-Host "从 manifest.yml 里读不到 version。" -ForegroundColor Red
    exit 1
}

# 两处版本号必须一致，否则宿主显示的版本和包里的 DLL 对不上
$dllVersion = (Select-String -Path $assembly -Pattern 'AssemblyVersion\("([^"]+)"\)').Matches.Groups[1].Value
if ($dllVersion -ne $version) {
    Write-Host "manifest.yml（$version）和 AssemblyInfo.cs（$dllVersion）版本号不一致。" -ForegroundColor Red
    exit 1
}

# ---------------- 2) 构建 ----------------

if (-not $NoBuild) {
    & (Join-Path $projectDir "build.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "构建失败，发版中止。" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path $cipxPath)) {
    Write-Host "找不到 cipx：$cipxPath" -ForegroundColor Red
    exit 1
}

# cipx 必须比 dll 新，否则说明打的是旧包（踩过这种「发出去的包没改」）
$cipxTime = (Get-Item $cipxPath).LastWriteTime
$dllTime  = (Get-Item (Join-Path $projectDir "bin\Release\net8.0-windows10.0.19041.0\MikuGuard.dll")).LastWriteTime
if ($cipxTime -lt $dllTime) {
    Write-Host "cipx 比 dll 旧，可能没重新打包：cipx=$cipxTime dll=$dllTime" -ForegroundColor Red
    exit 1
}

# ---------------- 3) 归集产物 ----------------

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$artifact = Join-Path $distDir "MikuGuard-$version.cipx"
Copy-Item $cipxPath $artifact -Force

$hash = (Get-FileHash $artifact -Algorithm SHA256).Hash
$hashFile = "$artifact.sha256"
Set-Content -Path $hashFile -Value "$hash  MikuGuard-$version.cipx" -Encoding utf8

# ---------------- 4) 发版说明 ----------------

if (-not $Notes) {
    $Notes = "MikuGuard $version"
}

$recentLines = git -C $projectDir log --oneline -12 --pretty=format:"- %s"
if (-not $recentLines) { $recentLines = @("- （没有提交记录）") }
$recent = ($recentLines -split "`r?`n" | Where-Object { $_ }) -join "`n"
$releaseMd = Join-Path $distDir "RELEASE-$version.md"
$body = @"
## MikuGuard $version

$Notes

### 安装

1. 关闭 ClassIsland。
2. 把 ``MikuGuard-$version.cipx`` 拖到 ClassIsland 的插件目录 ``data\Plugins\``，或在应用的插件页里安装。
3. 启动 ClassIsland，在「设置 → MikuGuard」里调参数。

### 校验和

``````
$hash  MikuGuard-$version.cipx
``````

### 最近改动

$recent
"@
Set-Content -Path $releaseMd -Value $body -Encoding utf8

$size = [math]::Round((Get-Item $artifact).Length / 1KB, 1)
Write-Host ""
Write-Host "==> 发版产物（v$version，$size KB）" -ForegroundColor Green
Write-Host "    $artifact"
Write-Host "    $hashFile"
Write-Host "    $releaseMd"
Write-Host "    SHA256 $hash"

# ---------------- 5) 发布 ----------------

if (-not $Publish) {
    Write-Host ""
    Write-Host "只打包没发布。要提交 + tag + 建 Release，加 -Publish。" -ForegroundColor DarkGray
    exit 0
}

$tag = "v$version"
Write-Host ""
Write-Host "==> 提交版本号、打 tag $tag、建 Release ..." -ForegroundColor Cyan

Push-Location $projectDir
try {
    git add manifest.yml AssemblyInfo.cs
    git commit -m "chore: 发版 $version" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "没有需要提交的版本号改动（可能已经提交过）。" -ForegroundColor Yellow
    }

    $existing = git tag --list $tag
    if ($existing) {
        Write-Host "tag $tag 已存在，跳过。" -ForegroundColor Yellow
    }
    else {
        git tag $tag
    }

    git push origin HEAD
    git push origin $tag

    $assets = @($artifact, $hashFile)
    $already = gh release view $tag 2>$null
    if ($LASTEXITCODE -eq 0) {
        gh release upload $tag @assets --clobber
        gh release edit $tag --title "MikuGuard $version" --notes-file $releaseMd
    }
    else {
        gh release create $tag @assets --title "MikuGuard $version" --notes-file $releaseMd
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "gh release 失败了，产物已经在 dist\ 里，手动传也一样。" -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host "已发布 $tag。" -ForegroundColor Green
