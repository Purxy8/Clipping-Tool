param(
    [ValidateRange(5, 600)]
    [int]$DurationSeconds = 180
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$window = [System.Windows.Window]::new()
$window.Title = 'ClipForge deterministic motion surface'
$window.Width = 1600
$window.Height = 900
$window.Left = 440
$window.Top = 220
$window.Topmost = $true
$window.ResizeMode = [System.Windows.ResizeMode]::NoResize
$window.WindowStyle = [System.Windows.WindowStyle]::None
$window.Background = [System.Windows.Media.Brushes]::Black

$canvas = [System.Windows.Controls.Canvas]::new()
$canvas.ClipToBounds = $true
$window.Content = $canvas

$background = [System.Windows.Shapes.Rectangle]::new()
$background.Width = 1600
$background.Height = 900
$canvas.Children.Add($background) | Out-Null

$bar = [System.Windows.Shapes.Rectangle]::new()
$bar.Width = 420
$bar.Height = 900
$bar.Fill = [System.Windows.Media.Brushes]::White
$canvas.Children.Add($bar) | Out-Null

$marker = [System.Windows.Shapes.Ellipse]::new()
$marker.Width = 240
$marker.Height = 240
$marker.Fill = [System.Windows.Media.Brushes]::Lime
$canvas.Children.Add($marker) | Out-Null

$label = [System.Windows.Controls.TextBlock]::new()
$label.FontFamily = [System.Windows.Media.FontFamily]::new('Consolas')
$label.FontSize = 72
$label.FontWeight = [System.Windows.FontWeights]::Bold
$label.Foreground = [System.Windows.Media.Brushes]::Yellow
[System.Windows.Controls.Canvas]::SetLeft($label, 40)
[System.Windows.Controls.Canvas]::SetTop($label, 36)
$canvas.Children.Add($label) | Out-Null

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$frame = 0
$timer = [System.Windows.Threading.DispatcherTimer]::new(
    [System.Windows.Threading.DispatcherPriority]::Render)
$timer.Interval = [TimeSpan]::FromMilliseconds(1000.0 / 60.0)
$timer.Add_Tick({
    $script:frame++
    $phase = $script:frame % 360
    $red = [byte](32 + (($phase * 5) % 192))
    $green = [byte](32 + (($phase * 3) % 192))
    $blue = [byte](32 + (($phase * 7) % 192))
    $script:background.Fill =
        [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromRgb($red, $green, $blue))

    $x = (($script:frame * 23) % 2020) - 420
    [System.Windows.Controls.Canvas]::SetLeft($script:bar, $x)
    [System.Windows.Controls.Canvas]::SetTop($script:bar, 0)

    $markerX = (($script:frame * 37) % 1840) - 240
    $markerY = 330 + [Math]::Sin($script:frame / 10.0) * 260
    [System.Windows.Controls.Canvas]::SetLeft($script:marker, $markerX)
    [System.Windows.Controls.Canvas]::SetTop($script:marker, $markerY)
    $script:label.Text =
        ('CLIPFORGE MOTION {0:D8}  {1,8:0.000}s' -f
            $script:frame,
            $script:stopwatch.Elapsed.TotalSeconds)

    if ($script:stopwatch.Elapsed.TotalSeconds -ge $DurationSeconds) {
        $script:timer.Stop()
        $script:window.Close()
    }
})

$window.Add_KeyDown({
    if ($_.Key -eq [System.Windows.Input.Key]::Escape) {
        $script:timer.Stop()
        $script:window.Close()
    }
})
$window.Add_Closed({
    $script:timer.Stop()
})
$timer.Start()
$window.ShowDialog() | Out-Null
