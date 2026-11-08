#Requires -Version 7.0
# 全屏抓图（验证花火用）：每张之间歇 ms，抓 count 张。
param(
    [string]$OutDir = "D:\Dev\MikuGuard\_probe\shots",
    [int]$Count = 20,
    [int]$IntervalMs = 200
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen

for ($i = 0; $i -lt $Count; $i++) {
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir ("shot_{0:d3}.png" -f $i)), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Start-Sleep -Milliseconds $IntervalMs
}

"done" | Out-File -Encoding utf8 (Join-Path $OutDir "done.txt")
