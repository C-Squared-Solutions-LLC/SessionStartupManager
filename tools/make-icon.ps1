# Draws a provider-neutral blue terminal icon with three session tabs into app.ico.
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $w = $s - 1
    $d = [Math]::Max(2, $s * 0.36)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 99, 155))
    $g.FillPath($background, $path)
    $background.Dispose()
    $path.Dispose()

    $tabs = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(142, 216, 237)), ([Math]::Max(1.1, $s * 0.055))
    $tabs.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tabs.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($x in @(0.24, 0.44, 0.64)) {
        $g.DrawLine($tabs, [single]($s * $x), [single]($s * 0.20), [single]($s * ($x + 0.10)), [single]($s * 0.20))
    }
    $tabs.Dispose()

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, $s * 0.11))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $chevron = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.24), ($s * 0.39)),
        (New-Object System.Drawing.PointF ($s * 0.44), ($s * 0.55)),
        (New-Object System.Drawing.PointF ($s * 0.24), ($s * 0.71)))
    $g.DrawLines($pen, $chevron)
    $g.DrawLine($pen, [single]($s * 0.55), [single]($s * 0.71), [single]($s * 0.78), [single]($s * 0.71))
    $pen.Dispose()
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
    $ms.Dispose()
}

$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $px = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$px); $bw.Write([byte]$px); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Wrote $Out"
