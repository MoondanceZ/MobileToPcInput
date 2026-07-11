namespace pc_receiver;

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public string SelectedModelId { get; set; } = AsrModelCatalog.DefaultModel.Id;
    public bool StartupEnabled { get; set; }
    public bool ReplaceTrailingFullStopWithSpace { get; set; } = true;
}
