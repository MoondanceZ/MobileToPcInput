using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace pc_receiver;

public sealed class BridgeHotkeyDefinition
{
    private static readonly IReadOnlyDictionary<string, HotkeyKey> KnownKeys = BuildKnownKeys();

    private BridgeHotkeyDefinition(IReadOnlyList<HotkeyKey> keys)
    {
        Keys = keys;
        SerializedValue = string.Join("+", keys.Select(item => item.Token));
        DisplayName = keys.Count == 0
            ? "未绑定"
            : string.Join(" + ", keys.Select(item => item.DisplayName));
        VirtualKeys = keys.Select(item => item.VirtualKey).ToArray();
        ReleaseVirtualKeys = VirtualKeys.Reverse().ToArray();
    }

    public static BridgeHotkeyDefinition Unbound { get; } = new([]);
    public static BridgeHotkeyDefinition Default { get; } = new(
        [KnownKeys["Ctrl"], KnownKeys["Win"]]);

    public IReadOnlyList<HotkeyKey> Keys { get; }
    public bool IsBound => Keys.Count > 0;
    public string SerializedValue { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ushort> VirtualKeys { get; }
    public IReadOnlyList<ushort> ReleaseVirtualKeys { get; }

    public BridgeHotkeyDefinition ForSession(bool enabled)
    {
        return enabled ? this : Unbound;
    }

    public static BridgeHotkeyDefinition Parse(string? value)
    {
        if (value is null)
        {
            return Default;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return Unbound;
        }

        return TryCreate(
            value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            out var definition)
            ? definition
            : Default;
    }

    public static bool TryCreate(
        IEnumerable<string> tokens,
        out BridgeHotkeyDefinition definition)
    {
        var keys = new List<HotkeyKey>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (!KnownKeys.TryGetValue(token, out var key) || !seen.Add(key.Token))
            {
                definition = DefaultOrFallback();
                return false;
            }

            keys.Add(key);
        }

        if (keys.Count < 1)
        {
            definition = DefaultOrFallback();
            return false;
        }

        definition = new BridgeHotkeyDefinition(keys);
        return true;
    }

    public static bool TryGetToken(Key key, out string token)
    {
        token = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LWin or Key.RWin => "Win",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.Space => "Space",
            Key.Enter => "Enter",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F12 => key.ToString(),
            _ => string.Empty,
        };
        return token.Length > 0;
    }

    public static bool TryGetToken(ushort virtualKey, out string token)
    {
        token = virtualKey switch
        {
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x5B or 0x5C => "Win",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x20 => "Space",
            0x0D => "Enter",
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x70 and <= 0x7B => $"F{virtualKey - 0x70 + 1}",
            _ => string.Empty,
        };
        return token.Length > 0;
    }

    private static BridgeHotkeyDefinition DefaultOrFallback()
    {
        return Default;
    }

    private static IReadOnlyDictionary<string, HotkeyKey> BuildKnownKeys()
    {
        var keys = new Dictionary<string, HotkeyKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = new("Ctrl", "Ctrl", 0x11),
            ["Win"] = new("Win", "Win", 0x5B),
            ["Alt"] = new("Alt", "Alt", 0x12),
            ["Shift"] = new("Shift", "Shift", 0x10),
            ["Space"] = new("Space", "Space", 0x20),
            ["Enter"] = new("Enter", "Enter", 0x0D),
        };

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var token = letter.ToString();
            keys[token] = new HotkeyKey(token, token, letter);
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            var token = digit.ToString();
            keys[token] = new HotkeyKey(token, token, (ushort)(0x30 + digit));
        }

        for (var number = 1; number <= 12; number++)
        {
            var token = $"F{number}";
            keys[token] = new HotkeyKey(token, token, (ushort)(0x70 + number - 1));
        }

        return keys;
    }

    public sealed record HotkeyKey(string Token, string DisplayName, ushort VirtualKey);
}
