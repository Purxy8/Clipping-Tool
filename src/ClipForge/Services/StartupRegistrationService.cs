using Velopack.Locators;
using Velopack.Windows;

namespace ClipForge.Services;

internal interface IStartupShortcutBackend
{
    bool IsSupported { get; }

    bool IsRegistered(string relativeExecutablePath);

    void Create(string relativeExecutablePath, string arguments);

    void Delete(string relativeExecutablePath);
}

/// <summary>
/// Owns ClipForge's per-user Windows Startup shortcut. The shortcut contains
/// only the packaged executable name and a fixed private launch argument.
/// </summary>
internal sealed class StartupRegistrationService
{
    public const string ApplicationExecutableName = "ClipForge.exe";

    private readonly IStartupShortcutBackend _backend;

    public StartupRegistrationService()
        : this(new VelopackStartupShortcutBackend())
    {
    }

    internal StartupRegistrationService(IStartupShortcutBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public bool IsSupported => _backend.IsSupported;

    public bool IsEnabled =>
        IsSupported && _backend.IsRegistered(ApplicationExecutableName);

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            if (enabled)
            {
                throw new InvalidOperationException(
                    "Windows startup is available only in the installed ClipForge app.");
            }

            return;
        }

        if (enabled)
        {
            _backend.Create(ApplicationExecutableName, AppLaunchOptions.AutoStartArgument);
        }
        else
        {
            _backend.Delete(ApplicationExecutableName);
        }
    }

    public static void TryRemoveForUninstall()
    {
        try
        {
            new StartupRegistrationService().SetEnabled(false);
        }
        catch (Exception)
        {
            // A fast uninstall callback must remain best-effort and non-interactive.
        }
    }
}

internal sealed class VelopackStartupShortcutBackend : IStartupShortcutBackend
{
    public bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !VelopackLocator.IsCurrentSet)
            {
                return false;
            }

            try
            {
                var locator = VelopackLocator.Current;
                return !locator.IsPortable &&
                       locator.CurrentlyInstalledVersion is not null &&
                       string.Equals(
                           locator.ThisExeRelativePath,
                           StartupRegistrationService.ApplicationExecutableName,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or
                System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
    }

    public bool IsRegistered(string relativeExecutablePath)
    {
        ValidateExecutableName(relativeExecutablePath);
        EnsureSupported();

#pragma warning disable CS0618 // Extra Startup shortcuts with fixed arguments require this Velopack helper.
        var shortcuts = new Shortcuts(VelopackLocator.Current);
#pragma warning restore CS0618
        return shortcuts
            .FindShortcuts(relativeExecutablePath, ShortcutLocation.Startup)
            .ContainsKey(ShortcutLocation.Startup);
    }

    public void Create(string relativeExecutablePath, string arguments)
    {
        ValidateExecutableName(relativeExecutablePath);
        if (!string.Equals(arguments, AppLaunchOptions.AutoStartArgument, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Windows startup argument is invalid.", nameof(arguments));
        }

        EnsureSupported();
#pragma warning disable CS0618 // Extra Startup shortcuts with fixed arguments require this Velopack helper.
        var shortcuts = new Shortcuts(VelopackLocator.Current);
#pragma warning restore CS0618
        shortcuts.CreateShortcut(
            relativeExecutablePath,
            ShortcutLocation.Startup,
            updateOnly: false,
            programArguments: arguments,
            icon: null);
    }

    public void Delete(string relativeExecutablePath)
    {
        ValidateExecutableName(relativeExecutablePath);
        EnsureSupported();

#pragma warning disable CS0618 // Extra Startup shortcuts with fixed arguments require this Velopack helper.
        var shortcuts = new Shortcuts(VelopackLocator.Current);
#pragma warning restore CS0618
        shortcuts.DeleteShortcuts(relativeExecutablePath, ShortcutLocation.Startup);
    }

    private static void ValidateExecutableName(string relativeExecutablePath)
    {
        if (!string.Equals(
                relativeExecutablePath,
                StartupRegistrationService.ApplicationExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Only the packaged ClipForge executable can be registered for startup.",
                nameof(relativeExecutablePath));
        }
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "Windows startup is available only in the installed ClipForge app.");
        }
    }
}
