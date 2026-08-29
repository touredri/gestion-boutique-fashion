Add-Type -AssemblyName System.Drawing
$root = "C:\Users\coulb\Desktop\gestion-boutique-fashion"
$assets = Join-Path $root "src\BoutiqueFashion.App\Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$terracotta = [System.Drawing.Color]::FromArgb(255, 168, 79, 53)
$terracottaDark = [System.Drawing.Color]::FromArgb(255, 132, 57, 35)
$gold = [System.Drawing.Color]::FromArgb(255, 184, 138, 66)
$ivory = [System.Drawing.Color]::FromArgb(255, 255, 251, 246)

function Draw-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear($terracotta)
    $s = $size / 256.0
    $penGold = New-Object System.Drawing.Pen($gold, (6 * $s)); $penGold.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round; $penGold.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $penGold.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penRing = New-Object System.Drawing.Pen($gold, (3 * $s))
    # anneau
    $g.DrawEllipse($penRing, [int](14 * $s), [int](14 * $s), [int](228 * $s), [int](228 * $s))
    # crochet du cintre
    $g.DrawArc($penGold, [int](116 * $s), [int](48 * $s), [int](24 * $s), [int](24 * $s), -60, 240)
    # tige
    $g.DrawLine($penGold, (128 * $s), (72 * $s), (128 * $s), (92 * $s))
    # corps du cintre
    $g.DrawLine($penGold, (128 * $s), (92 * $s), (58 * $s), (138 * $s))
    $g.DrawLine($penGold, (128 * $s), (92 * $s), (198 * $s), (138 * $s))
    $g.DrawLine($penGold, (58 * $s), (138 * $s), (198 * $s), (138 * $s))
    # monogramme BF
    $font = New-Object System.Drawing.Font("Georgia", (72 * $s), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $brushIvory = New-Object System.Drawing.SolidBrush($ivory)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF((32 * $s), (146 * $s), (192 * $s), (92 * $s))
    $g.DrawString("BF", $font, $brushIvory, $rect, $sf)
    $g.Dispose()
    return $bmp
}

$master = Draw-Logo 256
$master.Save((Join-Path $assets "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# logo-nav.png (96)
$nav = Draw-Logo 96
$nav.Save((Join-Path $assets "logo-nav.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# logo.ico : conteneur ICO avec une entrée PNG 256
$pngStream = New-Object System.IO.MemoryStream
$master.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$icoPath = Join-Path $assets "logo.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)          # header
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)  # 256 => 0
$bw.Write([UInt16]1); $bw.Write([UInt16]32)                                 # planes, bpp
$bw.Write([UInt32]$pngBytes.Length); $bw.Write([UInt32]22)                  # size, offset
$bw.Write($pngBytes)
$bw.Flush(); $bw.Close(); $fs.Close()

# images installateur
$side = New-Object System.Drawing.Bitmap(164, 314)
$g = [System.Drawing.Graphics]::FromImage($side)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear($terracottaDark)
$g.DrawImage($master, 22, 60, 120, 120)
$fontT = New-Object System.Drawing.Font("Georgia", 17, [System.Drawing.FontStyle]::Bold)
$fontS = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$brushIv = New-Object System.Drawing.SolidBrush($ivory)
$brushGold = New-Object System.Drawing.SolidBrush($gold)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString("BOUTIQUE", $fontT, $brushIv, (New-Object System.Drawing.RectangleF(0, 196, 164, 30)), $sf)
$g.DrawString("FASHION", $fontT, $brushIv, (New-Object System.Drawing.RectangleF(0, 220, 164, 30)), $sf)
$g.DrawString("POINT DE VENTE", $fontS, $brushGold, (New-Object System.Drawing.RectangleF(0, 254, 164, 20)), $sf)
$g.Dispose()
$side.Save((Join-Path $assets "installer-side.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$small = New-Object System.Drawing.Bitmap(55, 58)
$g = [System.Drawing.Graphics]::FromImage($small)
$g.Clear($terracotta)
$g.DrawImage($master, 2, 3, 50, 50)
$g.Dispose()
$small.Save((Join-Path $assets "installer-small.png"), [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "assets générés:"
Get-ChildItem $assets | Select-Object Name, Length | Format-Table
