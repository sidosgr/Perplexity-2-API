$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$images=@()
foreach($size in @(16,24,32,48,64,128,256)){
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $g=[Drawing.Graphics]::FromImage($bitmap);$g.SmoothingMode='AntiAlias'
    $g.ScaleTransform($size/256.0,$size/256.0)
    $g.Clear([Drawing.Color]::FromArgb(20,26,36))
    $pen=[Drawing.Pen]::new([Drawing.Color]::FromArgb(76,211,193),20);$pen.StartCap='Round';$pen.EndCap='Round';$pen.LineJoin='Round'
    # A distinct linked-node mark for the Perplexity bridge.
    $pen.Width=16
    $g.DrawLine($pen,70,128,186,128)
    $g.DrawLine($pen,128,70,128,186)
    $g.DrawEllipse($pen,37,37,182,182)
    $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(147,221,245))
    foreach($point in @(@(52,110),@(168,110),@(110,52),@(110,168))){$g.FillEllipse($brush,$point[0],$point[1],36,36)}
    $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $images+=,@{Size=$size;Bytes=$stream.ToArray()}
    if($size -eq 256){$bitmap.Save((Join-Path $folder 'Bridge.png'),[Drawing.Imaging.ImageFormat]::Png)}
    $stream.Dispose();$brush.Dispose();$pen.Dispose();$g.Dispose();$bitmap.Dispose()
}
$file=[IO.File]::Create((Join-Path $folder 'Bridge.ico'));$writer=[IO.BinaryWriter]::new($file)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
$offset=6+16*$images.Count
foreach($item in $images){$dimension=if($item.Size -eq 256){0}else{$item.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$item.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$item.Bytes.Length}
foreach($item in $images){$writer.Write([byte[]]$item.Bytes)}
$writer.Dispose();$file.Dispose()
