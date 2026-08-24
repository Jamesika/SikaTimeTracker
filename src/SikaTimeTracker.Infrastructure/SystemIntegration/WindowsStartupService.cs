using System.Runtime.Versioning;
using Microsoft.Win32;
using SikaTimeTracker.Core.Contracts;

namespace SikaTimeTracker.Infrastructure.SystemIntegration;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SikaTimeTracker";
    private readonly string _executablePath;

    public WindowsStartupService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommandLine(_executablePath, startMinimized), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string BuildCommandLine(string executablePath, bool startMinimized)
    {
        return startMinimized
            ? $"\"{executablePath}\" --minimized"
            : $"\"{executablePath}\"";
    }
}
