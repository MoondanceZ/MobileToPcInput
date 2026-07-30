using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace pc_receiver;

public static class BridgeHotkeyCapture
{
    private const int PressedMask = 0x8000;

    public static IReadOnlyList<string> GetPressedTokens()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var tokens = new List<string>();
        AddIfPressed(tokens, "Ctrl", 0x11);
        if (IsPressed(0x5B) || IsPressed(0x5C))
        {
            tokens.Add("Win");
        }

        AddIfPressed(tokens, "Alt", 0x12);
        AddIfPressed(tokens, "Shift", 0x10);
        AddIfPressed(tokens, "Space", 0x20);
        AddIfPressed(tokens, "Enter", 0x0D);

        for (var virtualKey = 0x41; virtualKey <= 0x5A; virtualKey++)
        {
            AddIfPressed(tokens, ((char)virtualKey).ToString(), virtualKey);
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            AddIfPressed(tokens, digit.ToString(), 0x30 + digit);
        }

        for (var number = 1; number <= 12; number++)
        {
            AddIfPressed(tokens, $"F{number}", 0x70 + number - 1);
        }

        return tokens;
    }

    public static IReadOnlyList<string> ResolveKeyDownTokens(
        IEnumerable<string> nativePressedTokens,
        string? windowEventToken)
    {
        var nativeTokens = nativePressedTokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (nativeTokens.Length > 0)
        {
            return nativeTokens;
        }

        return string.IsNullOrWhiteSpace(windowEventToken)
            ? []
            : [windowEventToken];
    }

    private static void AddIfPressed(List<string> tokens, string token, int virtualKey)
    {
        if (IsPressed(virtualKey))
        {
            tokens.Add(token);
        }
    }

    private static bool IsPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & PressedMask) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
