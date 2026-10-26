$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetFolder = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force -Path $assetFolder | Out-Null
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(22, 32, 47))
$accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(48, 211, 166))
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
foreach ($arc in @(@(8,8,56,56,180,90),@(192,8,56,56,270,90),@(192,192,56,56,0,90),@(8,192,56,56,90,90))) {
    $path.AddArc($arc[0], $arc[1], $arc[2], $arc[3], $arc[4], $arc[5])
}
$path.CloseFigure()
$graphics.FillPath($background, $path)
$shield = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(128,40),[System.Drawing.PointF]::new(207,70),[System.Drawing.PointF]::new(195,151),[System.Drawing.PointF]::new(172,187),[System.Drawing.PointF]::new(128,216),[System.Drawing.PointF]::new(84,187),[System.Drawing.PointF]::new(61,151),[System.Drawing.PointF]::new(49,70))
$graphics.FillPolygon($accent, $shield)
$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(22,32,47), 17)
$pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$graphics.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(89,126),[System.Drawing.PointF]::new(116,153),[System.Drawing.PointF]::new(169,99)))
$bitmap.Save((Join-Path $assetFolder 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sizes = @(16,24,32,48,64,128,256)
$streams = @()
foreach ($size in $sizes) {
    $scaled = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = [System.IO.MemoryStream]::new()
    $scaled.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $streams += ,$stream.ToArray()
    $g.Dispose(); $scaled.Dispose(); $stream.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assetFolder 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$streams[$i].Length); $writer.Write([uint32]$offset)
    $offset += $streams[$i].Length
}
foreach ($bytes in $streams) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $file.Dispose(); $pen.Dispose(); $path.Dispose()
$accent.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
