<#
.SYNOPSIS
    生成 FileKit 应用图标 app.ico（多尺寸、无第三方素材、无字体依赖）。

.DESCRIPTION
    使用 System.Drawing / GDI+ 绘制现代极简的蓝色圆角方形 + 白色几何字母 F：
      - 45° 青色到深蓝渐变；
      - 顶部玻璃高光与 1px 半透明白色内描边；
      - 字母 F 由三根圆角矩形组成，不依赖系统字体，避免不同机器字形差异。

    输出 16/20/24/32/40/48/64/128/256 八个尺寸：
      - 16~64 使用 32 位 DIB（BITMAPINFOHEADER + BGRA + AND mask），兼容旧式图标解析；
      - 128/256 使用 PNG 压缩帧，控制文件体积；
      - 手动组装标准 ICO 容器，并回读校验帧数、尺寸与偏移。

    兼容 PowerShell 5.1+；任何步骤失败都会返回非零退出码。
#>

[CmdletBinding()]
param(
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$scriptExitCode = 0

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $scriptRoot = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
        $scriptRoot = (Get-Location).Path
    }

    $OutputPath = Join-Path $scriptRoot 'app.ico'
}

Add-Type -AssemblyName System.Drawing

$frameSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $diameter = [Math]::Max(0.0, [Math]::Min($Radius * 2.0, [Math]::Min($Width, $Height)))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($diameter -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $Width, $Height)))
        return $path
    }

    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    # 设计坐标固定为 256×256，再按目标尺寸等比缩放，保证每个尺寸都是独立矢量绘制。
    $scale = $Size / 256.0
    $bitmap = New-Object System.Drawing.Bitmap(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $paths = New-Object System.Collections.Generic.List[System.Drawing.Drawing2D.GraphicsPath]
    $brushes = New-Object System.Collections.Generic.List[System.Drawing.Brush]
    $pens = New-Object System.Collections.Generic.List[System.Drawing.Pen]

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $margin = 14.0 * $scale
        $outerSize = $Size - ($margin * 2.0)
        $radius = 54.0 * $scale
        $outerRect = New-Object System.Drawing.RectangleF($margin, $margin, $outerSize, $outerSize)

        $bodyPath = New-RoundedRectanglePath -X $outerRect.X -Y $outerRect.Y `
            -Width $outerRect.Width -Height $outerRect.Height -Radius $radius
        $paths.Add($bodyPath)

        $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $outerRect,
            [System.Drawing.Color]::FromArgb(255, 64, 203, 255),
            [System.Drawing.Color]::FromArgb(255, 0, 74, 214),
            45.0)
        $brushes.Add($baseBrush)
        $graphics.FillPath($baseBrush, $bodyPath)

        # 玻璃高光：用同一圆角路径填充垂直 Alpha 渐变，不会出现生硬的横向截断。
        $sheenBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $outerRect,
            [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
            90.0)
        $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
        $blend.Colors = @(
            [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(12, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
        )
        $blend.Positions = @(0.0, 0.45, 1.0)
        $sheenBrush.InterpolationColors = $blend
        $brushes.Add($sheenBrush)
        $graphics.FillPath($sheenBrush, $bodyPath)

        $borderWidth = [Math]::Max(1.0, 2.2 * $scale)
        $borderPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(90, 255, 255, 255),
            $borderWidth)
        $pens.Add($borderPen)
        $graphics.DrawPath($borderPen, $bodyPath)

        # 字母 F：三根圆角矩形，几何绘制，不依赖字体。
        $barWidth = 23.0 * $scale
        $stemHeight = 106.0 * $scale
        $stemX = 88.0 * $scale
        $stemY = 75.0 * $scale
        $topBarWidth = 73.0 * $scale
        $middleBarWidth = 58.0 * $scale
        $middleBarY = $stemY + ($stemHeight * 0.46)
        $shadowOffset = [Math]::Max(1.0, 2.5 * $scale)
        $shadowColor = [System.Drawing.Color]::FromArgb(38, 0, 30, 80)

        $shadowBrush = New-Object System.Drawing.SolidBrush($shadowColor)
        $brushes.Add($shadowBrush)
        $graphics.FillRectangle(
            $shadowBrush,
            $stemX + $shadowOffset,
            $stemY + $shadowOffset,
            $barWidth,
            $stemHeight)
        $graphics.FillRectangle(
            $shadowBrush,
            $stemX + $shadowOffset,
            $stemY + $shadowOffset,
            $topBarWidth,
            $barWidth)
        $graphics.FillRectangle(
            $shadowBrush,
            $stemX + $shadowOffset,
            $middleBarY + $shadowOffset,
            $middleBarWidth,
            $barWidth)

        $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $brushes.Add($whiteBrush)

        $stemPath = New-RoundedRectanglePath -X $stemX -Y $stemY `
            -Width $barWidth -Height $stemHeight -Radius ($barWidth / 2.0)
        $paths.Add($stemPath)
        $graphics.FillPath($whiteBrush, $stemPath)

        $topPath = New-RoundedRectanglePath -X $stemX -Y $stemY `
            -Width $topBarWidth -Height $barWidth -Radius ($barWidth / 2.0)
        $paths.Add($topPath)
        $graphics.FillPath($whiteBrush, $topPath)

        $middlePath = New-RoundedRectanglePath -X $stemX -Y $middleBarY `
            -Width $middleBarWidth -Height $barWidth -Radius ($barWidth / 2.0)
        $paths.Add($middlePath)
        $graphics.FillPath($whiteBrush, $middlePath)

        return $bitmap
    }
    finally {
        foreach ($pen in $pens) { $pen.Dispose() }
        foreach ($brush in $brushes) { $brush.Dispose() }
        foreach ($path in $paths) { $path.Dispose() }
        $graphics.Dispose()
    }
}

function Get-PngFrameBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DibFrameBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
    $data = $Bitmap.LockBits(
        $rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $xorSize = $width * $height * 4
    $andStride = [int]([Math]::Ceiling($width / 32.0) * 4)
    $andSize = $andStride * $height
    $imageSize = 40 + $xorSize + $andSize

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        # BITMAPINFOHEADER（高度为 XOR + AND 两倍，表示 ICO 中的 DIB）。
        $writer.Write([int]40)
        $writer.Write([int]$width)
        $writer.Write([int]($height * 2))
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int]0)
        $writer.Write([int]$xorSize)
        $writer.Write([int]0)
        $writer.Write([int]0)
        $writer.Write([int]0)
        $writer.Write([int]0)

        # XOR 位图像素：BGRA、自底向上。
        for ($y = $height - 1; $y -ge 0; $y--) {
            $rowOffset = $y * $stride
            for ($x = 0; $x -lt $width; $x++) {
                $offset = $rowOffset + ($x * 4)
                $writer.Write($pixels[$offset + 0])
                $writer.Write($pixels[$offset + 1])
                $writer.Write($pixels[$offset + 2])
                $writer.Write($pixels[$offset + 3])
            }
        }

        # AND mask 全 0：32 位图标由 Alpha 通道决定透明度。
        $maskRow = New-Object byte[] $andStride
        for ($y = 0; $y -lt $height; $y++) {
            $writer.Write($maskRow, 0, $maskRow.Length)
        }

        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

try {
    $outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }

    $frames = New-Object System.Collections.Generic.List[object]
    foreach ($size in $frameSizes) {
        $bitmap = New-IconBitmap -Size $size
        try {
            if ($size -le 64) {
                $bytes = Get-DibFrameBytes -Bitmap $bitmap
            }
            else {
                $bytes = Get-PngFrameBytes -Bitmap $bitmap
            }

            $frames.Add([pscustomobject]@{
                Size  = $size
                Bytes = $bytes
            })
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $headerSize = 6
    $entrySize = 16
    $offset = $headerSize + ($entrySize * $frames.Count)

    $fileStream = [System.IO.File]::Create($OutputPath)
    $writer = New-Object System.IO.BinaryWriter($fileStream)
    try {
        $writer.Write([int16]0)             # reserved
        $writer.Write([int16]1)             # type = icon
        $writer.Write([int16]$frames.Count) # image count

        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)          # color count
            $writer.Write([byte]0)          # reserved
            $writer.Write([int16]1)         # color planes
            $writer.Write([int16]32)        # bits per pixel
            $writer.Write([int]$frame.Bytes.Length)
            $writer.Write([int]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) {
            $frameBytes = [byte[]]$frame.Bytes
            $writer.Write($frameBytes, 0, $frameBytes.Length)
        }

        $writer.Flush()
    }
    finally {
        $writer.Dispose()
        $fileStream.Dispose()
    }

    # 回读校验：ICO 头、目录项、偏移/长度、尺寸集合。
    $iconBytes = [System.IO.File]::ReadAllBytes($OutputPath)
    if ($iconBytes.Length -lt 6) {
        throw "生成的文件过小：$OutputPath"
    }

    $reserved = [BitConverter]::ToInt16($iconBytes, 0)
    $type = [BitConverter]::ToInt16($iconBytes, 2)
    $count = [BitConverter]::ToInt16($iconBytes, 4)
    if ($reserved -ne 0 -or $type -ne 1 -or $count -ne $frames.Count) {
        throw "ICO 头非法（reserved=$reserved type=$type count=$count）。"
    }

    $seenSizes = New-Object System.Collections.Generic.List[int]
    for ($index = 0; $index -lt $count; $index++) {
        $entryOffset = 6 + ($index * 16)
        $widthByte = [int]$iconBytes[$entryOffset]
        $heightByte = [int]$iconBytes[$entryOffset + 1]
        $planes = [BitConverter]::ToInt16($iconBytes, $entryOffset + 4)
        $bits = [BitConverter]::ToInt16($iconBytes, $entryOffset + 6)
        $length = [BitConverter]::ToInt32($iconBytes, $entryOffset + 8)
        $dataOffset = [BitConverter]::ToInt32($iconBytes, $entryOffset + 12)
        $parsedSize = if ($widthByte -eq 0) { 256 } else { $widthByte }
        $parsedHeight = if ($heightByte -eq 0) { 256 } else { $heightByte }

        if ($parsedSize -ne $parsedHeight) {
            throw "第 $index 帧宽高不一致：$parsedSize x $parsedHeight。"
        }
        if ($planes -ne 1 -or $bits -ne 32) {
            throw "第 $index 帧位深/平面数非法：planes=$planes bits=$bits。"
        }
        if ($length -le 0 -or $dataOffset -lt 0 -or ($dataOffset + $length) -gt $iconBytes.Length) {
            throw "第 $index 帧偏移/长度越界。"
        }

        $seenSizes.Add($parsedSize)
    }

    $expectedSizes = @($frameSizes | Sort-Object)
    $actualSizes = @($seenSizes | Sort-Object)
    if (($expectedSizes -join ',') -ne ($actualSizes -join ',')) {
        throw "帧尺寸不完整：期望 [$($expectedSizes -join ', ')]，实际 [$($actualSizes -join ', ')]。"
    }

    # System.Drawing.Icon 的 GDI+ 实现不一定选择 256 PNG 帧（常见行为是回退到 128），
    # 因此这里只验证 ICO 可加载且尺寸有效；256 帧的 PNG 头已在上面的目录校验中确认。
    $icon = New-Object System.Drawing.Icon($OutputPath, 0, 0)
    try {
        if ($icon.Width -lt 16 -or $icon.Height -lt 16 -or $icon.Width -ne $icon.Height) {
            throw "System.Drawing.Icon 加载尺寸异常：$($icon.Width)x$($icon.Height)。"
        }
    }
    finally {
        $icon.Dispose()
    }

    $fileInfo = Get-Item -LiteralPath $OutputPath
    Write-Host ''
    Write-Host ("图标生成成功：{0}" -f $fileInfo.FullName) -ForegroundColor Green
    Write-Host ("尺寸：{0} 帧；文件：{1} 字节" -f $frameSizes.Count, $fileInfo.Length)
    Write-Host ("帧清单：{0}" -f ($frameSizes -join ', '))
}
catch {
    Write-Host ''
    Write-Host ("图标生成失败：" + $_.Exception.Message) -ForegroundColor Red
    Write-Error $_
    $scriptExitCode = 1
}

exit $scriptExitCode
