using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace pc_receiver;

public sealed class AsrModelOption
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string AsrModel { get; init; }
    public required string PunctuationModel { get; init; }
    public string Revision { get; init; } = "v2.0.5";
    public bool IsSupported { get; init; } = true;
    public bool IsDownloaded => IsSupported
        && AsrModelCatalog.IsModelDownloaded(AsrModel)
        && AsrModelCatalog.IsModelDownloaded(PunctuationModel);

    public override string ToString()
    {
        var status = IsSupported
            ? IsDownloaded ? "已下载" : "未下载"
            : "待支持";
        return $"{DisplayName} · {status}";
    }
}

public static class AsrModelCatalog
{
    private const string DefaultPunctuationModel = "iic/punc_ct-transformer_zh-cn-common-vocab272727-onnx";

    public static IReadOnlyList<AsrModelOption> Models { get; } =
    [
        new AsrModelOption
        {
            Id = "paraformer-large-zh-cn",
            DisplayName = "paraformer-large-zh-cn（专用中文模型）",
            Description = "中文普通话 16k 离线识别，当前默认模型。",
            AsrModel = "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
            PunctuationModel = DefaultPunctuationModel
        },
        new AsrModelOption
        {
            Id = "paraformer-large-en",
            DisplayName = "paraformer-large-en（专用英文模型）",
            Description = "英文模型入口已预留，当前版本暂不加载。",
            AsrModel = "iic/speech_paraformer-large_asr_nat-en-16k-common-vocab10020-onnx",
            PunctuationModel = DefaultPunctuationModel,
            IsSupported = false
        },
        new AsrModelOption
        {
            Id = "paraformer-large-zh-cn-contextual",
            DisplayName = "paraformer-large-zh-cn-contextual（中文热词增强模型）",
            Description = "热词增强模型入口已预留，当前版本暂不加载。",
            AsrModel = "iic/speech_paraformer-large-contextual_asr_nat-zh-cn-16k-common-vocab8404-onnx",
            PunctuationModel = DefaultPunctuationModel,
            IsSupported = false
        }
    ];

    public static AsrModelOption DefaultModel => Models[0];

    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "modelscope",
        "hub",
        "models",
        "iic");

    public static bool IsModelDownloaded(string modelName)
    {
        var directory = GetModelCacheDirectory(modelName);
        return File.Exists(Path.Combine(directory, "model_quant.onnx"))
            || File.Exists(Path.Combine(directory, "model.onnx"));
    }

    public static string GetModelCacheDirectory(string modelName)
    {
        var shortName = modelName.Split('/').Last();
        return Path.Combine(CacheRoot, shortName);
    }

    public static int DeleteModelFiles(AsrModelOption model)
    {
        var deleted = 0;
        foreach (var directory in new[]
                 {
                     GetModelCacheDirectory(model.AsrModel),
                     GetModelCacheDirectory(model.PunctuationModel)
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
            deleted++;
        }

        return deleted;
    }
}
