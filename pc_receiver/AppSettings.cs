namespace pc_receiver;

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public string SelectedModelId { get; set; } = AsrModelCatalog.DefaultModel.Id;
    public string RecognitionMode { get; set; } = "local";
    public string SelectedOnlineServiceId { get; set; } = OnlineAsrCatalog.DefaultService.Id;
    public string XiaomiMimoApiKey { get; set; } = string.Empty;
    public string XiaomiMimoLanguage { get; set; } = "auto";
    public bool StartupEnabled { get; set; }
    public bool ReplaceTrailingFullStopWithSpace { get; set; } = true;
}
