param([string]$Out)
Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

# Renders a gamepad glyph on a rounded dark tile with an accent gradient, at several sizes,
# and writes them into a single .ico (PNG-compressed entries, supported since Windows Vista).
function Render-Png([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $r = $size * 0.22
    $bg = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.Color]::FromRgb(0x1E,0x2A,0x3A)), ([System.Windows.Media.Color]::FromRgb(0x10,0x14,0x1C)), 90
    $penBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(90,0x60,0xCD,0xFF))
    $pen = New-Object System.Windows.Media.Pen $penBrush, ([math]::Max(1, $size/48))
    $dc.DrawRoundedRectangle($bg, $pen, (New-Object System.Windows.Rect 0,0,$size,$size), $r, $r)
    $accent = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.Color]::FromRgb(0x86,0xDB,0xFF)), ([System.Windows.Media.Color]::FromRgb(0x3A,0x9B,0xF0)), 90
    $tf = New-Object System.Windows.Media.Typeface 'Segoe Fluent Icons'
    $ft = New-Object System.Windows.Media.FormattedText ([string][char]0xE7FC), ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $tf, ($size*0.62), $accent, 1.0
    $dc.DrawText($ft, (New-Object System.Windows.Point (($size-$ft.Width)/2), (($size-$ft.Height)/2)))
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) { $pngs.Add((Render-Png $s)) }
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) {0} else {$s}))); $bw.Write([byte]($(if ($s -ge 256) {0} else {$s})))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush(); [IO.File]::WriteAllBytes($Out, $ms.ToArray())
# preview
[IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($Out, '.preview.png'), $pngs[-1])
