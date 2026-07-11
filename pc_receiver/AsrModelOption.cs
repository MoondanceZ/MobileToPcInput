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
    public required string VadModel { get; init; }
    public string Revision { get; init; } = "v2.0.5";
    public AsrEngine Engine { get; init; } = AsrEngine.NativeParaformer;
    public ModelProvider Provider { get; init; } = ModelProvider.ModelScope;
    public bool RequiresPunctuationModel { get; init; } = true;
    public bool RequiresVadModel { get; init; } = true;
    public bool IsSupported { get; init; } = true;
    public bool IsDownloaded => IsSupported && AsrModelCatalog.IsModelDownloaded(AsrModel);
    public bool CanSelectInPicker => IsDownloaded;
    public bool IsPunctuationDownloaded => !RequiresPunctuationModel || (IsSupported && AsrModelCatalog.IsPunctuationModelDownloaded(PunctuationModel));
    public bool IsVadDownloaded => !RequiresVadModel || (IsSupported && AsrModelCatalog.IsVadModelDownloaded(VadModel));

    public override string ToString()
    {
        return DisplayName;
    }
}

public enum AsrEngine
{
    NativeParaformer,
    SherpaOnnxParaformer
}

public enum ModelProvider
{
    ModelScope,
    HuggingFace
}

public static class AsrModelCatalog
{
    private const string DefaultPunctuationModel = "iic/punc_ct-transformer_zh-cn-common-vocab272727-onnx";
    private const string DefaultVadModel = "iic/speech_fsmn_vad_zh-cn-16k-common-onnx";

    public static IReadOnlyList<AsrModelOption> Models { get; } =
    [
        new AsrModelOption
        {
            Id = "paraformer-large-zh-cn",
            DisplayName = "paraformer-large-zh-cn（专用中文模型）",
            Description = "中文普通话 16k 离线识别，当前默认模型。",
            AsrModel = "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
            PunctuationModel = DefaultPunctuationModel,
            VadModel = DefaultVadModel
        },
        new AsrModelOption
        {
            Id = "paraformer-large-en",
            DisplayName = "paraformer-large-en（专用英文模型）",
            Description = "英文 16k 离线识别模型。",
            AsrModel = "iic/speech_paraformer-large_asr_nat-en-16k-common-vocab10020-onnx",
            PunctuationModel = DefaultPunctuationModel,
            VadModel = DefaultVadModel
        },
        new AsrModelOption
        {
            Id = "paraformer-large-zh-cn-contextual",
            DisplayName = "paraformer-large-zh-cn-contextual（中文热词增强模型）",
            Description = "中文热词增强 16k 离线识别模型。",
            AsrModel = "iic/speech_paraformer-large-contextual_asr_nat-zh-cn-16k-common-vocab8404-onnx",
            PunctuationModel = DefaultPunctuationModel,
            VadModel = DefaultVadModel
        },
        new AsrModelOption
        {
            Id = "sherpa-onnx-paraformer-zh-cn",
            DisplayName = "sherpa-onnx-paraformer-zh-cn（Sherpa 中文模型）",
            Description = "Sherpa-ONNX 中文离线识别模型，便于对比新识别引擎。",
            AsrModel = "csukuangfj/sherpa-onnx-paraformer-zh-2023-03-28",
            PunctuationModel = DefaultPunctuationModel,
            VadModel = DefaultVadModel,
            Revision = "main",
            Engine = AsrEngine.SherpaOnnxParaformer,
            Provider = ModelProvider.HuggingFace,
            RequiresVadModel = false
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
        var model = Models.FirstOrDefault(item => string.Equals(item.AsrModel, modelName, StringComparison.Ordinal));
        if (model?.Engine == AsrEngine.SherpaOnnxParaformer)
        {
            return IsSherpaParaformerModelDownloaded(modelName);
        }

        var directory = GetModelCacheDirectory(modelName);
        var hasModel = File.Exists(Path.Combine(directory, "model_quant.onnx"))
            || File.Exists(Path.Combine(directory, "model.onnx"));
        var hasConfig = File.Exists(Path.Combine(directory, "asr.yaml"))
            || File.Exists(Path.Combine(directory, "config.yaml"));
        var hasTokens = File.Exists(Path.Combine(directory, "tokens.json"))
            || File.Exists(Path.Combine(directory, "tokens.txt"));
        return hasModel
            && hasConfig
            && hasTokens
            && File.Exists(Path.Combine(directory, "am.mvn"));
    }

    public static bool IsSherpaParaformerModelDownloaded(string modelName)
    {
        var directory = GetModelCacheDirectory(modelName);
        return File.Exists(Path.Combine(directory, "model.int8.onnx"))
            && File.Exists(Path.Combine(directory, "tokens.txt"));
    }

    public static bool IsPunctuationModelDownloaded(string modelName)
    {
        var directory = GetModelCacheDirectory(modelName);
        var hasModel = File.Exists(Path.Combine(directory, "model_quant.onnx"))
            || File.Exists(Path.Combine(directory, "model.onnx"));
        var hasTokens = File.Exists(Path.Combine(directory, "tokens.json"))
            || File.Exists(Path.Combine(directory, "tokens.txt"));
        return hasModel
            && hasTokens
            && File.Exists(Path.Combine(directory, "config.yaml"));
    }

    public static bool IsVadModelDownloaded(string modelName)
    {
        var directory = GetModelCacheDirectory(modelName);
        var hasModel = File.Exists(Path.Combine(directory, "model_quant.onnx"))
            || File.Exists(Path.Combine(directory, "model.onnx"));
        return hasModel
            && File.Exists(Path.Combine(directory, "config.yaml"))
            && File.Exists(Path.Combine(directory, "am.mvn"))
            && File.Exists(Path.Combine(directory, "configuration.json"));
    }

    public static bool AreSharedModelsDownloaded(AsrModelOption model)
    {
        return model.IsPunctuationDownloaded && model.IsVadDownloaded;
    }

    public static string GetModelCacheDirectory(string modelName)
    {
        var shortName = modelName.Split('/').Last();
        return Path.Combine(CacheRoot, shortName);
    }

    public static int DeleteModelFiles(AsrModelOption model)
    {
        var deleted = 0;
        var directories = new List<string>
        {
            GetModelCacheDirectory(model.AsrModel)
        };

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
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
