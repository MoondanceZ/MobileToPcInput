using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace pc_receiver;

public sealed class PunctuationRestorer : IDisposable
{
    private static readonly string[] DefaultPunctuationLabels = ["<unk>", "_", "，", "。", "？", "、"];

    private InferenceSession? _session;
    private Dictionary<string, int> _tokens = new(StringComparer.Ordinal);
    private string[] _punctuationLabels = DefaultPunctuationLabels;
    private string _modelDirectory = string.Empty;

    public bool IsLoaded => _session is not null;

    public void Load(string modelDirectory, int threads, CancellationToken cancellationToken = default)
    {
        if (_session is not null && string.Equals(_modelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeSession();
        cancellationToken.ThrowIfCancellationRequested();

        var modelPath = FindRequiredFile(modelDirectory, "model_quant.onnx", "model.onnx");
        var tokensPath = FindRequiredFile(modelDirectory, "tokens.json", "tokens.txt");
        var configPath = Path.Combine(modelDirectory, "config.yaml");

        _tokens = LoadTokens(tokensPath);
        _punctuationLabels = File.Exists(configPath)
            ? LoadPunctuationLabels(configPath)
            : DefaultPunctuationLabels;

        var options = new SessionOptions
        {
            InterOpNumThreads = threads,
            IntraOpNumThreads = threads
        };
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);
        _modelDirectory = modelDirectory;
    }

    public string Restore(string text)
    {
        if (_session is null || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var chars = text.Where(c => !char.IsWhiteSpace(c)).Select(c => c.ToString()).ToArray();
        if (chars.Length == 0)
        {
            return text;
        }

        var tokenIds = chars.Select(ToTokenId).ToArray();
        var inputsTensor = new DenseTensor<int>(new[] { 1, tokenIds.Length });
        for (var i = 0; i < tokenIds.Length; i++)
        {
            inputsTensor[0, i] = tokenIds[i];
        }

        var lengthTensor = new DenseTensor<int>(new[] { 1 });
        lengthTensor[0] = tokenIds.Length;

        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("inputs", inputsTensor),
            NamedOnnxValue.CreateFromTensor("text_lengths", lengthTensor)
        ]);
        var logits = results.First(item => item.Name == "logits").AsTensor<float>();
        return ApplyPunctuation(chars, logits);
    }

    private int ToTokenId(string token)
    {
        if (_tokens.TryGetValue(token, out var id))
        {
            return id;
        }

        var lower = token.ToLowerInvariant();
        if (_tokens.TryGetValue(lower, out id))
        {
            return id;
        }

        return _tokens.TryGetValue("<unk>", out id) ? id : 0;
    }

    private string ApplyPunctuation(string[] chars, Tensor<float> logits)
    {
        var result = new List<string>(chars.Length * 2);
        var labelCount = Math.Min(logits.Dimensions[2], _punctuationLabels.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            result.Add(chars[i]);
            var labelIndex = 1;
            var maxScore = logits[0, i, labelIndex];
            for (var label = 0; label < labelCount; label++)
            {
                if (logits[0, i, label] > maxScore)
                {
                    maxScore = logits[0, i, label];
                    labelIndex = label;
                }
            }

            if (labelIndex > 1)
            {
                result.Add(_punctuationLabels[labelIndex]);
            }
        }

        return string.Concat(result);
    }

    private static Dictionary<string, int> LoadTokens(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Invalid punctuation tokens file format.");
            }

            return document.RootElement
                .EnumerateArray()
                .Select((token, index) => new { Token = token.GetString() ?? string.Empty, Index = index })
                .Where(item => !string.IsNullOrEmpty(item.Token))
                .GroupBy(item => item.Token, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        }

        return File.ReadLines(path)
            .Select((line, index) => new { Token = line.Split('\t')[0].Trim(), Index = index })
            .Where(item => !string.IsNullOrEmpty(item.Token))
            .GroupBy(item => item.Token, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
    }

    private static string[] LoadPunctuationLabels(string configPath)
    {
        var labels = new List<string>();
        var inPunctuationList = false;
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "punc_list:")
            {
                inPunctuationList = true;
                continue;
            }

            if (inPunctuationList && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                labels.Add(Unquote(trimmed[2..].Trim()));
                continue;
            }

            if (inPunctuationList && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }
        }

        return labels.Count > 0 ? labels.ToArray() : DefaultPunctuationLabels;
    }

    private static string FindRequiredFile(string directory, params string[] fileNames)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"标点模型目录不存在: {directory}");
        }

        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"标点模型目录缺少文件: {string.Join(" 或 ", fileNames)}。目录: {directory}");
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf('#');
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
        _modelDirectory = string.Empty;
    }

    public void Dispose()
    {
        DisposeSession();
    }
}
