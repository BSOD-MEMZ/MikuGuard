Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$out = 'D:\Dev\MikuGuard\_shot.png'
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$rect = New-Object System.Drawing.Rectangle($b.X, $b.Y, $b.Width, 120)
$bmp = New-Object System.Drawing.Bitmap($rect.Width, $rect.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Location, [System.Drawing.Point]::Empty, $rect.Size)
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved" | Out-File -Encoding utf8 'D:\Dev\MikuGuard\_shot.txt'
