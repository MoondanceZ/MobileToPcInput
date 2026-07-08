using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace pc_receiver;

public sealed class StartupService
{
    private const string AppName = "MobileToPcInput";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(AppName) as string;
        return string.Equals(value, BuildStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(AppName, BuildStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static string BuildStartupCommand()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
        }

        return $"\"{exePath}\"";
    }
}
