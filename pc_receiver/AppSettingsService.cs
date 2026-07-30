using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pc_receiver;

public sealed class AppSettingsService
{
    private static readonly AppSettingsJsonContext JsonContext = new(new JsonSerializerOptions
    {
        WriteIndented = true
    });

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MobileToPcInput",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize(json, JsonContext.AppSettings) ?? new AppSettings();
            settings.RecognitionMode = RecognitionModes.Normalize(settings.RecognitionMode);
            if (string.IsNullOrWhiteSpace(settings.BridgeHotkey))
            {
                settings.BridgeHotkey = BridgeHotkeyDefinition.Default.SerializedValue;
                settings.BridgeHotkeyEnabled = false;
            }
            else
            {
                settings.BridgeHotkey =
                    BridgeHotkeyDefinition.Parse(settings.BridgeHotkey).SerializedValue;
            }

            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Error("App settings load failed", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, JsonContext.AppSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("App settings save failed", ex);
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
