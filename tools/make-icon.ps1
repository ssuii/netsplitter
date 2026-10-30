Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$rect = New-Object System.Drawing.Rectangle 8, 8, ($size - 16), ($size - 16)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 52
$path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
$path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
$path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)), ([System.Drawing.Color]::FromArgb(255, 29, 78, 216)), 55.0
$g.FillPath($brush, $path)

$white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 26
$white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$white.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

# split fork: stem at bottom center -> center -> two branches
$g.DrawLine($white, 128, 206, 128, 146)
$g.DrawLine($white, 128, 146, 74, 92)
$g.DrawLine($white, 128, 146, 182, 92)

# arrowheads
$g.FillPolygon([System.Drawing.Brushes]::White, @(
    (New-Object System.Drawing.Point 54, 74),
    (New-Object System.Drawing.Point 96, 66),
    (New-Object System.Drawing.Point 80, 108)
))
$g.FillPolygon([System.Drawing.Brushes]::White, @(
    (New-Object System.Drawing.Point 202, 74),
    (New-Object System.Drawing.Point 160, 66),
    (New-Object System.Drawing.Point 176, 108)
))

$g.Dispose()

$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$out = Join-Path $PSScriptRoot "..\src\app.ico"
$fs = [System.IO.File]::Create($out)
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$bmp.Dispose()

Write-Output ("icon written: " + (Resolve-Path $out))
