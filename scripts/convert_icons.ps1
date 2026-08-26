Add-Type -AssemblyName PresentationCore, WindowsBase

function Convert-PngToIco($pngPath, $icoPath, $finalPngPath) {
    Write-Host "Converting $pngPath -> $icoPath"
    $src = New-Object System.Windows.Media.Imaging.BitmapImage
    $src.BeginInit()
    $src.UriSource = New-Object System.Uri($pngPath, [System.UriKind]::Absolute)
    $src.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $src.EndInit()
    $src.Freeze()
    
    if ($pngPath -ne $finalPngPath) {
        Copy-Item -Path $pngPath -Destination $finalPngPath -Force
    }

    $sizes = @(256, 128, 64, 48, 32, 16)
    $pngDataList = [System.Collections.Generic.List[byte[]]]::new()

    foreach ($size in $sizes) {
        $scaleX = [double]$size / $src.PixelWidth
        $scaleY = [double]$size / $src.PixelHeight
        $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap($src, (New-Object System.Windows.Media.ScaleTransform($scaleX, $scaleY)))
        $resized.Freeze()
        
        $ms = New-Object System.IO.MemoryStream
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($resized))
        $encoder.Save($ms)
        $pngDataList.Add($ms.ToArray())
        $ms.Dispose()
    }

    $fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
    $writer = New-Object System.IO.BinaryWriter($fs)
    $writer.Write([ushort]0)
    $writer.Write([ushort]1)
    $writer.Write([ushort]$sizes.Length)

    $offset = 6 + (16 * $sizes.Length)
    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $s = $sizes[$i]
        $bSize = if ($s -ge 256) { [byte]0 } else { [byte]$s }
        $data = $pngDataList[$i]
        $writer.Write($bSize)
        $writer.Write($bSize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([ushort]1)
        $writer.Write([ushort]32)
        $writer.Write([uint]$data.Length)
        $writer.Write([uint]$offset)
        $offset += $data.Length
    }

    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $writer.Write($pngDataList[$i])
    }
    $writer.Close()
    $fs.Close()
    Write-Host "Generated $icoPath ($((Get-Item $icoPath).Length) bytes)"
}

$RootDir = Split-Path -Parent $PSScriptRoot
$AssetsDir = Join-Path $RootDir "assets"

Convert-PngToIco "$AssetsDir\app_icon_raw.png" "$AssetsDir\app_icon.ico" "$AssetsDir\app_icon.png"
Convert-PngToIco "$AssetsDir\pdf_file_raw.png" "$AssetsDir\pdf_file.ico" "$AssetsDir\pdf_file.png"

# Copy to src/PdfViewer/assets and src/Installer/assets
Copy-Item "$AssetsDir\*" "$RootDir\src\PdfViewer\assets\" -Force
Copy-Item "$AssetsDir\*" "$RootDir\src\Installer\assets\" -Force

Write-Host "All icons converted and copied successfully!" -ForegroundColor Green
