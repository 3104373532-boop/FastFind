param(
    [string]$IconPath = (Join-Path $PSScriptRoot '..\build\fastfind.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Draw a 256x256 app icon: rounded accent square + magnifier glyph.
$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$rect = New-Object System.Drawing.Rectangle(8, 8, 240, 240)
$gp = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = 56
$gp.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$gp.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$gp.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$gp.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$gp.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 12, 112, 222), [System.Drawing.Color]::FromArgb(255, 6, 74, 156), 45.0)
$g.FillPath($brush, $gp)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 20)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawEllipse($pen, 62, 56, 96, 96)
$g.DrawLine($pen, 148, 146, 196, 194)

$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()

# ICONDIR + single PNG-compressed 256x256 entry
$outMs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($outMs)
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type: icon
$bw.Write([UInt16]1)      # image count
$bw.Write([Byte]0)        # width 256 -> 0
$bw.Write([Byte]0)        # height 256 -> 0
$bw.Write([Byte]0)        # palette
$bw.Write([Byte]0)        # reserved
$bw.Write([UInt16]1)      # color planes
$bw.Write([UInt16]32)     # bpp
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)     # offset (6 + 16)
$bw.Write($png)
$bw.Flush()

$dir = Split-Path -Parent $IconPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes($IconPath, $outMs.ToArray())
$bw.Dispose()
$outMs.Dispose()
Write-Host ("icon: {0} ({1:N0} bytes)" -f $IconPath, (Get-Item $IconPath).Length)