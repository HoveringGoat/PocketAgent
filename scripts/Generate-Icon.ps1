$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/PocketAgent.Desktop/Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$bitmap = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#111B17'))
$accent = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#C1DAA6')),12
$accent.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
$shape = New-Object Drawing.Drawing2D.GraphicsPath
try {
    $shape.AddArc(8,8,56,56,180,90); $shape.AddArc(192,8,56,56,270,90)
    $shape.AddArc(192,192,56,56,0,90); $shape.AddArc(8,192,56,56,90,90); $shape.CloseFigure()
    $graphics.FillPath($background,$shape)
    $pocket = [Drawing.Point[]]@([Drawing.Point]::new(58,58),[Drawing.Point]::new(198,58),[Drawing.Point]::new(198,158),[Drawing.Point]::new(128,202),[Drawing.Point]::new(58,158))
    $graphics.DrawPolygon($accent,$pocket)
    $graphics.DrawLines($accent,[Drawing.Point[]]@([Drawing.Point]::new(88,94),[Drawing.Point]::new(112,114),[Drawing.Point]::new(88,134)))
    $graphics.DrawLine($accent,132,137,164,137)
    $bitmap.Save((Join-Path $assets 'PocketAgent.png'),[Drawing.Imaging.ImageFormat]::Png)
    $png = New-Object IO.MemoryStream
    $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
    $file = [IO.File]::Create((Join-Path $assets 'PocketAgent.ico'))
    $writer = New-Object IO.BinaryWriter $file
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22); $writer.Write($png.ToArray())
    } finally { $writer.Dispose(); $png.Dispose() }
} finally { $shape.Dispose(); $accent.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
