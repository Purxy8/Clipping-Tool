using System.Globalization;

namespace ClipForge.Models;

/// <summary>
/// The complete set of colors derived from the three user-facing appearance
/// choices. Keeping derivation in one pure model prevents individual controls
/// from inventing subtly different hover, pressed, or border colors.
/// </summary>
internal sealed record AppearanceThemePalette(
    string BackgroundColor,
    string SurfaceColor,
    string SurfaceRaisedColor,
    string SurfaceHoverColor,
    string SurfaceTranslucentColor,
    string SurfaceOverlayColor,
    string BorderColor,
    string BorderStrongColor,
    string AccentColor,
    string AccentHoverColor,
    string AccentPressedColor,
    string AccentSoftColor,
    string AccentBorderColor,
    string AccentGradientStartColor,
    string AccentGradientEndColor,
    string PrimaryButtonTextColor,
    string HeroGradientStartColor,
    string HeroGradientMiddleColor,
    string HeroGradientEndColor)
{
    public static AppearanceThemePalette Create(
        string? requestedBackgroundColor,
        string? requestedAccentColor,
        string? requestedSurfaceColor)
    {
        var background = AppearanceColorPolicy.ParseCanonical(
            AppSettings.NormalizeBackgroundColor(requestedBackgroundColor));
        var accent = AppearanceColorPolicy.ParseCanonical(
            AppSettings.NormalizeAccentColor(requestedAccentColor));
        var surface = AppearanceColorPolicy.ParseCanonical(
            AppSettings.NormalizeSurfaceColor(requestedSurfaceColor));

        var surfaceRaised = AppearanceColorPolicy.Mix(surface, AppearanceColorPolicy.White, 0.035);
        var surfaceHover = AppearanceColorPolicy.Mix(surface, AppearanceColorPolicy.White, 0.075);
        var border = AppearanceColorPolicy.Mix(surface, AppearanceColorPolicy.White, 0.105);
        var borderStrong = AppearanceColorPolicy.Mix(surface, AppearanceColorPolicy.White, 0.19);
        var accentHover = AppearanceColorPolicy.Mix(accent, AppearanceColorPolicy.White, 0.12);
        var accentPressed = AppearanceColorPolicy.Mix(accent, AppearanceColorPolicy.Black, 0.14);
        var accentGradientStart = AppearanceColorPolicy.Mix(accent, AppearanceColorPolicy.White, 0.07);
        var accentGradientEnd = AppearanceColorPolicy.Mix(accent, AppearanceColorPolicy.Black, 0.13);
        var heroStart = AppearanceColorPolicy.Mix(surfaceRaised, accent, 0.085);
        var heroMiddle = AppearanceColorPolicy.Mix(background, surface, 0.62);
        var heroEnd = AppearanceColorPolicy.Mix(surface, accent, 0.17);

        return new AppearanceThemePalette(
            background.ToHex(),
            surface.ToHex(),
            surfaceRaised.ToHex(),
            surfaceHover.ToHex(),
            surface.ToHex(alpha: 0xA1),
            surface.ToHex(alpha: 0xF2),
            border.ToHex(),
            borderStrong.ToHex(),
            accent.ToHex(),
            accentHover.ToHex(),
            accentPressed.ToHex(),
            accent.ToHex(alpha: 0x24),
            accent.ToHex(alpha: 0x59),
            accentGradientStart.ToHex(),
            accentGradientEnd.ToHex(),
            AppearanceColorPolicy.ChooseContrastingText(accentGradientStart, accentGradientEnd),
            heroStart.ToHex(),
            heroMiddle.ToHex(),
            heroEnd.ToHex());
    }
}

/// <summary>
/// Validation and color math for persisted appearance values. This policy does
/// not depend on WPF, which keeps it deterministic and straightforward to test.
/// </summary>
internal static class AppearanceColorPolicy
{
    private const byte MaximumDarkChannel = 48;
    private const double MinimumAccentLuminance = 0.18;

    internal static RgbColor Black { get; } = new(0, 0, 0);
    internal static RgbColor White { get; } = new(255, 255, 255);

    public static string NormalizeDarkColor(string? requestedColor, string fallbackColor)
    {
        if (!TryParse(requestedColor, out var color))
        {
            color = ParseCanonical(fallbackColor);
        }

        var brightestChannel = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        if (brightestChannel <= MaximumDarkChannel)
        {
            return color.ToHex();
        }

        var scale = MaximumDarkChannel / (double)brightestChannel;
        return new RgbColor(
            ScaleChannel(color.Red, scale),
            ScaleChannel(color.Green, scale),
            ScaleChannel(color.Blue, scale)).ToHex();
    }

    public static string NormalizeAccentColor(string? requestedColor, string fallbackColor)
    {
        if (!TryParse(requestedColor, out var color))
        {
            color = ParseCanonical(fallbackColor);
        }

        if (RelativeLuminance(color) >= MinimumAccentLuminance)
        {
            return color.ToHex();
        }

        // Very dark saturated choices (especially blue) disappear against the
        // app shell. Mix only as much white as needed to reach a visible accent.
        var lower = 0d;
        var upper = 1d;
        for (var iteration = 0; iteration < 18; iteration++)
        {
            var amount = (lower + upper) / 2;
            if (RelativeLuminance(Mix(color, White, amount)) < MinimumAccentLuminance)
            {
                lower = amount;
            }
            else
            {
                upper = amount;
            }
        }

        return Mix(color, White, upper).ToHex();
    }

    internal static RgbColor ParseCanonical(string color) =>
        TryParse(color, out var parsed)
            ? parsed
            : throw new ArgumentException("A canonical #RRGGBB color is required.", nameof(color));

    internal static RgbColor Mix(RgbColor from, RgbColor to, double amount)
    {
        var clampedAmount = Math.Clamp(amount, 0, 1);
        return new RgbColor(
            MixChannel(from.Red, to.Red, clampedAmount),
            MixChannel(from.Green, to.Green, clampedAmount),
            MixChannel(from.Blue, to.Blue, clampedAmount));
    }

    internal static string ChooseContrastingText(params RgbColor[] backgrounds)
    {
        ArgumentNullException.ThrowIfNull(backgrounds);
        if (backgrounds.Length == 0)
        {
            return "#FFFFFF";
        }

        var minimumBlackContrast = backgrounds.Min(color => ContrastRatio(color, Black));
        var minimumWhiteContrast = backgrounds.Min(color => ContrastRatio(color, White));
        return minimumBlackContrast >= minimumWhiteContrast ? "#000000" : "#FFFFFF";
    }

    private static bool TryParse(string? requestedColor, out RgbColor color)
    {
        color = default;
        if (requestedColor is null || requestedColor.Length != 7 || requestedColor[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(requestedColor.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(requestedColor.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(requestedColor.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue);
        return true;
    }

    private static byte ScaleChannel(byte channel, double scale) =>
        (byte)Math.Round(channel * scale, MidpointRounding.AwayFromZero);

    private static byte MixChannel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * amount), MidpointRounding.AwayFromZero);

    private static double ContrastRatio(RgbColor first, RgbColor second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(RgbColor color) =>
        (0.2126 * Linearize(color.Red)) +
        (0.7152 * Linearize(color.Green)) +
        (0.0722 * Linearize(color.Blue));

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    internal readonly record struct RgbColor(byte Red, byte Green, byte Blue)
    {
        public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";

        public string ToHex(byte alpha) => $"#{alpha:X2}{Red:X2}{Green:X2}{Blue:X2}";
    }
}
