# Render our SVG geometry with Windows WPF. No external graphics package is needed.
param([string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\FocusLedger.App\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
[xml]$svg = Get-Content -LiteralPath (Join-Path $AssetDirectory 'focusledger.svg') -Raw
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    foreach ($shape in $svg.svg.ChildNodes) {
        if ($shape.LocalName -eq 'title') { continue }
        $brush = $null
        if ($shape.fill -and $shape.fill -ne 'none') { $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($shape.fill) }
        $pen = $null
        if ($shape.stroke) {
            $pen = [Windows.Media.Pen]::new([Windows.Media.BrushConverter]::new().ConvertFromInvariantString($shape.stroke), [double]$shape.'stroke-width')
            $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
        }
        switch ($shape.LocalName) {
            'rect' { $drawing.DrawRoundedRectangle($brush, $pen, [Windows.Rect]::new([double]$shape.x, [double]$shape.y, [double]$shape.width, [double]$shape.height), [double]$shape.rx, [double]$shape.rx) }
            'path' { $drawing.DrawGeometry($brush, $pen, [Windows.Media.Geometry]::Parse($shape.d)) }
            'circle' { $drawing.DrawEllipse($brush, $pen, [Windows.Point]::new([double]$shape.cx, [double]$shape.cy), [double]$shape.r, [double]$shape.r) }
        }
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames.Add($stream.ToArray()); $stream.Dispose()
}
[IO.File]::WriteAllBytes((Join-Path $AssetDirectory 'focusledger.png'), $frames[$frames.Count - 1])
$file = [IO.File]::Create((Join-Path $AssetDirectory 'focusledger.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated PNG and ICO: $($sizes -join ', ') px from focusledger.svg"
