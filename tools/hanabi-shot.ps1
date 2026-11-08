#Requires -Version 7.0
# 触发一次「假摄像头占用」并连续抓屏，用来验证花火。
# 用完记得把假记录删掉（本脚本开头结尾都会删）。
param(
    [string]$OutDir = "D:\Dev\MikuGuard\_probe\shots",
    [int]$Count = 26,
    [int]$IntervalMs = 130
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$reg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged\C:#Windows#explorer.exe"

function Clear-Fake {
    if (Test-Path $reg) { Remove-Item $reg -Recurse -Force -ErrorAction SilentlyContinue }
}

# 先清掉：让监控回到「空闲」，这样写入记录才算一次新的「开始使用」
Clear-Fake
Start-Sleep -Milliseconds 1800

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter "shot_*.png" -ErrorAction SilentlyContinue | Remove-Item -Force

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$capture = Start-Job -ArgumentList $bounds, $OutDir, $Count, $IntervalMs -ScriptBlock {
    param($bounds, $OutDir, $Count, $IntervalMs)
    Add-Type -AssemblyName System.Drawing
    for ($i = 0; $i -lt $Count; $i++) {
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $g.Dispose()
        $bmp.Save((Join-Path $OutDir ("shot_{0:d3}.png" -f $i)), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Start-Sleep -Milliseconds $IntervalMs
    }
}

# 抓屏先跑起来，再写假记录（监控 1.5s 一轮，花火会在 0~1.5s 内出现）
Start-Sleep -Milliseconds 900
New-Item -Path $reg -Force | Out-Null
Set-ItemProperty -Path $reg -Name "LastUsedTimeStart" -Value ([DateTime]::UtcNow.ToFileTime()) -Type QWord
Set-ItemProperty -Path $reg -Name "LastUsedTimeStop" -Value 0 -Type QWord
Set-ItemProperty -Path $reg -Name "Value" -Value "Allow" -Type String
"fake-record written at $(Get-Date -Format 'HH:mm:ss.fff')" | Out-File (Join-Path $OutDir "trigger.txt") -Encoding utf8

Wait-Job $capture | Out-Null
Remove-Job $capture -Force
Clear-Fake

"done" | Out-File (Join-Path $OutDir "done.txt") -Encoding utf8
