# Rebuild original app artwork with Windows GDI+; no external tools or assets.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
[IO.Directory]::CreateDirectory($assetPath) | Out-Null
$sizes = @(16,20,24,32,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-Object Drawing.Bitmap($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(($size / 256.0), ($size / 256.0))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8,8,64,64,180,90); $path.AddArc(184,8,64,64,270,90)
    $path.AddArc(184,184,64,64,0,90); $path.AddArc(8,184,64,64,90,90); $path.CloseFigure()
    $background = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(23,34,53))
    $g.FillPath($background,$path)
    $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(225,239,250),16)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(96,61),[Drawing.PointF]::new(61,61),[Drawing.PointF]::new(61,96)))
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(160,61),[Drawing.PointF]::new(195,61),[Drawing.PointF]::new(195,96)))
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(61,160),[Drawing.PointF]::new(61,195),[Drawing.PointF]::new(96,195)))
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(160,195),[Drawing.PointF]::new(195,195),[Drawing.PointF]::new(195,160)))
    $red = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(246,77,91))
    $g.FillEllipse($red,100,100,56,56)
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetPath 'FreeCamRecord.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $red.Dispose(); $pen.Dispose(); $background.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$output = [IO.File]::Create((Join-Path $assetPath 'FreeCamRecord.ico'))
$writer = New-Object IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output "Generated app icon ($($sizes -join ', ')) and 256px PNG in $assetPath"
