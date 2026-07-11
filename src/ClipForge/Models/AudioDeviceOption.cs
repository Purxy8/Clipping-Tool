namespace ClipForge.Models;

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault = false)
{
    public string DisplayName => IsDefault ? $"System default — {Name}" : Name;

    public override string ToString() => DisplayName;
}

