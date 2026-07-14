using ClipForge.Models;

namespace ClipForge.Capture;

/// <summary>
/// Resolves a capture preset to an even, aspect-preserving output size. Presets
/// are upper bounds: ClipForge never upscales a smaller/custom display surface
/// and never encodes a padded fixed-size canvas full of unused black pixels.
/// </summary>
internal static class CaptureGeometry
{
    public static CaptureOutputSize ResolveOutputSize(
        DisplayOption display,
        ResolutionOption resolution)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(resolution);
        if (display.Width < 2 || display.Height < 2)
        {
            throw new ArgumentException(
                "The selected display must be at least 2 by 2 pixels.",
                nameof(display));
        }

        var sourceWidth = MakeEven(display.Width);
        var sourceHeight = MakeEven(display.Height);
        if (resolution.Width is null || resolution.Height is null)
        {
            return new CaptureOutputSize(sourceWidth, sourceHeight, RequiresScaling: false);
        }

        var maximumWidth = resolution.Width.Value;
        var maximumHeight = resolution.Height.Value;
        if (maximumWidth < 2 || maximumHeight < 2)
        {
            throw new ArgumentException(
                "The selected resolution has invalid dimensions.",
                nameof(resolution));
        }

        var scale = Math.Min(
            1d,
            Math.Min(maximumWidth / (double)display.Width, maximumHeight / (double)display.Height));
        if (scale >= 1d)
        {
            return new CaptureOutputSize(sourceWidth, sourceHeight, RequiresScaling: false);
        }

        // Pick the nearest encodable (even) dimension rather than always
        // rounding both axes down.  On non-16:9 surfaces such as 3440x1440 or
        // 1080x1920, double floor rounding introduces more aspect distortion
        // than the encoder alignment itself requires (1920x802 instead of the
        // much closer 1920x804, for example).
        var width = RoundToEven(display.Width * scale);
        var height = RoundToEven(display.Height * scale);
        width = Math.Min(width, Math.Min(sourceWidth, MakeEven(maximumWidth)));
        height = Math.Min(height, Math.Min(sourceHeight, MakeEven(maximumHeight)));
        return new CaptureOutputSize(
            width,
            height,
            RequiresScaling: width != sourceWidth || height != sourceHeight);
    }

    public static bool RequiresRestartForDisplayChange(
        CaptureSessionPlan capturePlan,
        DisplayOption currentDisplay)
    {
        ArgumentNullException.ThrowIfNull(capturePlan);
        ArgumentNullException.ThrowIfNull(currentDisplay);

        var previousDisplay = capturePlan.Display;
        if (!string.Equals(
                previousDisplay.DeviceName,
                currentDisplay.DeviceName,
                StringComparison.OrdinalIgnoreCase) ||
            previousDisplay.MonitorIndex != currentDisplay.MonitorIndex)
        {
            return true;
        }

        if (capturePlan.Strategy.CaptureBackend == DesktopCaptureBackend.Gdi)
        {
            // gdigrab bakes both the desktop coordinates and input dimensions
            // into its input arguments, so any geometry change needs a restart.
            return previousDisplay.Left != currentDisplay.Left ||
                   previousDisplay.Top != currentDisplay.Top ||
                   previousDisplay.Width != currentDisplay.Width ||
                   previousDisplay.Height != currentDisplay.Height;
        }

        // WGC tracks the selected monitor across coordinate changes and can
        // resize a changing source into an unchanged fixed output texture. Only
        // restart when the encoded frame dimensions themselves must change.
        var previousOutput = ResolveOutputSize(previousDisplay, capturePlan.Resolution);
        var currentOutput = ResolveOutputSize(currentDisplay, capturePlan.Resolution);
        return previousOutput.Width != currentOutput.Width ||
               previousOutput.Height != currentOutput.Height;
    }

    private static int MakeEven(int value) => value - value % 2;

    private static int RoundToEven(double value)
    {
        var rounded = (int)Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2;
        return Math.Max(2, rounded);
    }
}

internal readonly record struct CaptureOutputSize(
    int Width,
    int Height,
    bool RequiresScaling);

internal sealed record CaptureSessionPlan(
    DisplayOption Display,
    ResolutionOption Resolution,
    VideoEncodingStrategy Strategy);
