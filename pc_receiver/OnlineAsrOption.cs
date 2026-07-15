using System.Collections.Generic;

namespace pc_receiver;

public sealed class OnlineAsrOption
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ModelName { get; init; }
    public required string ProviderName { get; init; }
    public required string Description { get; init; }

    public override string ToString()
    {
        return DisplayName;
    }
}

public static class OnlineAsrCatalog
{
    public const string XiaomiMimoServiceId = "xiaomi-mimo-asr";
    public const string XiaomiMimoModelName = "mimo-v2.5-asr";

    public static IReadOnlyList<OnlineAsrOption> Services { get; } =
    [
        new OnlineAsrOption
        {
            Id = XiaomiMimoServiceId,
            DisplayName = "小米 MiMo ASR",
            ModelName = XiaomiMimoModelName,
            ProviderName = "小米 MiMo",
            Description = "中英双语与方言识别，自动标点，适合需要联网高准确率的场景。"
        }
    ];

    public static OnlineAsrOption DefaultService => Services[0];
}
