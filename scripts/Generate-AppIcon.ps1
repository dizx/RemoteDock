[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'src\RemoteDock.App\Assets\Brand.xaml'
$output = Join-Path $root 'src\RemoteDock.App\Assets\RemoteDock.ico'
$resources = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText($source))
$image = $resources['RemoteDockMark']
$sizes = @(16, 20, 24, 32, 48, 64, 256)
$frames = @()

foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.DrawImage($image, [System.Windows.Rect]::new(0, 0, $size, $size))
    $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $frames += ,$stream.ToArray()
    }
    finally { $stream.Dispose() }
}

$file = [System.IO.File]::Create($output)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }

Write-Output "Generated RemoteDock.ico with $($sizes.Count) sizes from Brand.xaml."
