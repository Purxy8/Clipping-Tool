using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ClipForge.Models;

/// <summary>
/// Modifier flags accepted by the Win32 global-hotkey API.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyModifiers>))]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

/// <summary>
/// A serializable, immutable keyboard gesture suitable for a system-wide hotkey.
/// </summary>
public sealed record HotkeyGesture
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Alt |
        HotkeyModifiers.Control |
        HotkeyModifiers.Shift |
        HotkeyModifiers.Windows;

    [JsonConstructor]
    public HotkeyGesture(HotkeyModifiers modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public HotkeyModifiers Modifiers { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<Key>))]
    public Key Key { get; init; }

    [JsonIgnore]
    public bool IsValid => TryValidate(out _);

    [JsonIgnore]
    public string DisplayText => FormatDisplayText();

    public static HotkeyGesture DefaultSaveClip { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.F10);

    public static HotkeyGesture DefaultToggleOverlay { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.F9);

    /// <summary>
    /// Checks constraints before a gesture is passed to RegisterHotKey.
    /// Bare keys, modifier-only gestures, unknown values, and debugger-reserved F12 are rejected.
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if ((Modifiers & ~AllowedModifiers) != 0)
        {
            error = "The hotkey contains an unsupported modifier.";
            return false;
        }

        if (Modifiers == HotkeyModifiers.None)
        {
            error = "A global hotkey must include at least one modifier key.";
            return false;
        }

        if (!Enum.IsDefined(Key))
        {
            error = "The hotkey contains an unknown key.";
            return false;
        }

        if (IsModifierKey(Key))
        {
            error = "Choose a non-modifier key for the hotkey.";
            return false;
        }

        if (Key == Key.F12)
        {
            error = "F12 is reserved by the Windows debugger and cannot be used as a global hotkey.";
            return false;
        }

        if (KeyInterop.VirtualKeyFromKey(Key) <= 0)
        {
            error = "The selected key does not map to a Windows virtual key.";
            return false;
        }

        error = null;
        return true;
    }

    public void Validate()
    {
        if (!TryValidate(out var error))
        {
            throw new ArgumentException(error, nameof(HotkeyGesture));
        }
    }

    public override string ToString() => DisplayText;

    internal uint GetVirtualKey()
    {
        Validate();
        return checked((uint)KeyInterop.VirtualKeyFromKey(Key));
    }

    private string FormatDisplayText()
    {
        var parts = new List<string>(5);

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(Key));
        return string.Join(" + ", parts);
    }

    private static bool IsModifierKey(Key key) => key is
        Key.None or
        Key.LeftAlt or
        Key.RightAlt or
        Key.LeftCtrl or
        Key.RightCtrl or
        Key.LeftShift or
        Key.RightShift or
        Key.LWin or
        Key.RWin or
        Key.System or
        Key.ImeProcessed or
        Key.DeadCharProcessed;

    private static string FormatKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            var digit = (int)key - (int)Key.NumPad0;
            return $"Num {digit}";
        }

        return key switch
        {
            Key.Back => "Backspace",
            Key.Capital => "Caps Lock",
            Key.Escape => "Esc",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            Key.Return => "Enter",
            Key.Snapshot => "Print Screen",
            Key.Space => "Space",
            Key.OemComma => ",",
            Key.OemMinus => "-",
            Key.OemPeriod => ".",
            Key.OemPlus => "+",
            _ => key.ToString()
        };
    }
}
