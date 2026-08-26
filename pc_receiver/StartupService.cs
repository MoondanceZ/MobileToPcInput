using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace pc_receiver;

public sealed class StartupService
{
    public const string StartupArgument = "--startup";
    private const string AppName = "MobileToPcInput";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsStartupLaunch(IEnumerable<string>? args)
    {
        return args?.Any(argument =>
            string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(AppName) as string;
        var enabled = string.Equals(value, BuildStartupCommand(), StringComparison.OrdinalIgnoreCase);
        if (enabled)
        {
            return true;
        }

        // Keep existing installations enabled, then migrate their old command
        // to the explicit startup launch argument.
        if (string.Equals(value, BuildLegacyStartupCommand(), StringComparison.OrdinalIgnoreCase))
        {
            SetEnabled(true);
            return true;
        }

        return false;
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

        return $"{BuildLegacyStartupCommand(exePath)} {StartupArgument}";
    }

    private static string BuildLegacyStartupCommand(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
        }

        return $"\"{exePath}\"";
    }
}
