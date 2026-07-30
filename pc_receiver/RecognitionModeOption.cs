using System;

namespace pc_receiver;

public static class RecognitionModes
{
    public const string Local = "local";
    public const string Online = "online";
    public const string WeType = "wetype";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Online, StringComparison.OrdinalIgnoreCase))
        {
            return Online;
        }

        if (string.Equals(value, WeType, StringComparison.OrdinalIgnoreCase))
        {
            return WeType;
        }

        return Local;
    }
}

public sealed record RecognitionModeOption(string Id, string DisplayName)
{
    public bool CanSelectInPicker => true;

    public override string ToString()
    {
        return DisplayName;
    }
}
