Add-Type -AssemblyName System.Drawing

function Generate-Icon([int]$size, [string]$outPath) {
    $scale = [double]$size / 38.0
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $g.Clear([System.Drawing.Color]::Transparent)

    # 1. Outer subtle glow
    $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 0, 210, 255), [float](2.4 * $scale))
    $g.DrawEllipse($glowPen, [float](3.5 * $scale), [float](3.5 * $scale), [float](31 * $scale), [float](31 * $scale))
    $glowPen.Dispose()

    # 2. Main Badge Circle with Radial/Linear Gradient
    $rect = New-Object System.Drawing.RectangleF([float](4 * $scale), [float](4 * $scale), [float](30 * $scale), [float](30 * $scale))
    $cTop = [System.Drawing.Color]::FromArgb(255, 20, 56, 102)      # Kerbin navy
    $cBottom = [System.Drawing.Color]::FromArgb(255, 6, 18, 38)     # Deep space
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $cTop, $cBottom, 90.0)
    $g.FillEllipse($brush, $rect)
    $brush.Dispose()

    # 3. Inner grid/meridian arc (Kerbin globe feeling)
    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 80, 170, 240), [float](1.0 * $scale))
    $g.DrawEllipse($gridPen, [float](11 * $scale), [float](4 * $scale), [float](16 * $scale), [float](30 * $scale)) # Longitude
    $g.DrawLine($gridPen, [float](4 * $scale), [float](19 * $scale), [float](34 * $scale), [float](19 * $scale))    # Equator
    $gridPen.Dispose()

    # 4. Planetary atmosphere rim
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 56, 180, 245), [float](1.6 * $scale))
    $g.DrawEllipse($rimPen, [float](4 * $scale), [float](4 * $scale), [float](30 * $scale), [float](30 * $scale))
    $rimPen.Dispose()

    # 5. Glowing Orbital Ring
    $orbitPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(240, 0, 230, 255), [float](1.5 * $scale))
    $state = $g.Save()
    $g.TranslateTransform([float](19 * $scale), [float](19 * $scale))
    $g.RotateTransform([float]-28.0)
    $g.DrawEllipse($orbitPen, [float](-16 * $scale), [float](-7 * $scale), [float](32 * $scale), [float](14 * $scale))

    # Satellite on orbit
    $satBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 230, 80))
    $g.FillEllipse($satBrush, [float](13 * $scale), [float](-3 * $scale), [float](3.4 * $scale), [float](3.4 * $scale))
    $satBrush.Dispose()

    $g.Restore($state)
    $orbitPen.Dispose()

    # 6. Glyphs: "A" on left, "Wen" on right
    $fontFamily = [System.Drawing.FontFamily]::GenericSansSerif
    $fontA = New-Object System.Drawing.Font($fontFamily, [float](10.5 * $scale), [System.Drawing.FontStyle]::Bold)
    $brushA = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $formatA = New-Object System.Drawing.StringFormat
    $formatA.Alignment = [System.Drawing.StringAlignment]::Center
    $formatA.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rectA = New-Object System.Drawing.RectangleF([float](5 * $scale), [float](10 * $scale), [float](14 * $scale), [float](16 * $scale))
    $g.DrawString("A", $fontA, $brushA, $rectA, $formatA)

    # Right: Chinese character U+6587 (Wen)
    $cjkFamily = $null
    foreach ($fn in @("Microsoft YaHei", "SimHei", "PingFang SC", "Segoe UI", "Arial")) {
        try {
            $testFont = New-Object System.Drawing.Font($fn, 10)
            if ($testFont.Name -eq $fn) { $cjkFamily = $fn; break }
        } catch {}
    }
    if (-not $cjkFamily) { $cjkFamily = [System.Drawing.FontFamily]::GenericSansSerif.Name }

    $fontZh = New-Object System.Drawing.Font($cjkFamily, [float](9.5 * $scale), [System.Drawing.FontStyle]::Bold)
    $brushZh = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 215, 64))
    $rectZh = New-Object System.Drawing.RectangleF([float](18 * $scale), [float](10.5 * $scale), [float](16 * $scale), [float](16 * $scale))
    $charWen = [char]0x6587
    $g.DrawString($charWen.ToString(), $fontZh, $brushZh, $rectZh, $formatA)

    # 7. Translation arrow / arc in center
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 100, 220, 255), [float](1.2 * $scale))
    $g.DrawArc($arrowPen, [float](13 * $scale), [float](8 * $scale), [float](12 * $scale), [float](8 * $scale), 200, 140)
    $g.DrawArc($arrowPen, [float](13 * $scale), [float](22 * $scale), [float](12 * $scale), [float](8 * $scale), 20, 140)
    $arrowPen.Dispose()

    # Save
    $full = [System.IO.Path]::GetFullPath($outPath)
    $p = [System.IO.Path]::GetDirectoryName($full)
    if (-not [System.IO.Directory]::Exists($p)) {
        [System.IO.Directory]::CreateDirectory($p) | Out-Null
    }
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Output ("Saved: " + $full)
}

Generate-Icon 38 "GameData/UI18n/Textures/icon.png"
Generate-Icon 76 "GameData/UI18n/Textures/icon@2x.png"
Generate-Icon 38 "GameData/UI18n/icon.png"
