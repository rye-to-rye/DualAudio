using DualAudio.Core.Startup;
using Microsoft.Win32;

namespace DualAudio.Platform.Windows.Startup;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "DualAudio". Per user: no admin rights needed.
/// Only this one value is ever read, written or deleted.
/// </summary>
public sealed class RunKeyAutoStartEntry : IAutoStartEntry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DualAudio";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
