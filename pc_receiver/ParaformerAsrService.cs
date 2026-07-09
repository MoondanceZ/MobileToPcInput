using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AliParaformerAsr;
using NAudio.Wave;

namespace pc_receiver;

public sealed class ParaformerAsrService : IDisposable
{
    private const int RecognitionSampleRate = 16000;
    private const int MaxRecognitionChunkSeconds = 8;
    private const int MaxRecognitionChunkSamples = RecognitionSampleRate * MaxRecognitionChunkSeconds;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PunctuationRestorer _punctuationRestorer = new();
    private OfflineRecognizer? _recognizer;
    private AsrModelOption _model = AsrModelCatalog.DefaultModel;

    public event Action<string>? WorkerStatusChanged;

    public AsrModelOption CurrentModel => _model;

    public async Task ConfigureModelAsync(AsrModelOption model, CancellationToken cancellationToken = default)
    {
        if (!model.IsSupported)
        {
            throw new NotSupportedException($"当前版本暂不支持模型: {model.DisplayName}");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_model.Id != model.Id)
            {
                DisposeRecognizer();
                _punctuationRestorer.Dispose();
            }

            _model = model;
            AppLogger.Info($"ASR model configured. id={model.Id}, asr={model.AsrModel}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopWorkerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DisposeRecognizer();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRecognizer();
                EnsurePunctuationRestorer(cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RecognizeAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recognizer = EnsureRecognizer();
                WorkerStatusChanged?.Invoke("C# ONNX recognition starting");
                AppLogger.Info($"C# ASR request starting. model={_model.Id}, wav={wavPath}");
                var samples = LoadWavSamples(wavPath);
                var chunks = SplitSamples(samples).ToArray();
                AppLogger.Info($"C# ASR samples loaded. samples={samples.Length}, chunks={chunks.Length}");

                var textParts = new List<string>(chunks.Length);
                for (var i = 0; i < chunks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppLogger.Info($"C# ASR chunk starting. index={i + 1}, total={chunks.Length}, samples={chunks[i].Length}");
                    var results = recognizer.GetResults([chunks[i]]);
                    var chunkText = results.FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        textParts.Add(chunkText);
                    }
                }

                var text = string.Concat(textParts);
                text = RestorePunctuation(text);
                text = ReplaceTrailingFullStopWithSpace(text);
                AppLogger.Info($"C# ASR result. textLength={text.Length}");
                return text;
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OfflineRecognizer EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        var modelDirectory = AsrModelCatalog.GetModelCacheDirectory(_model.AsrModel);
        var modelFilePath = FindRequiredFile(modelDirectory, "model_quant.onnx", "model.onnx");
        var configFilePath = FindRequiredFile(modelDirectory, "asr.yaml", "config.yaml");
        var mvnFilePath = FindRequiredFile(modelDirectory, "am.mvn");
        var tokensFilePath = FindRequiredFile(modelDirectory, "tokens.json", "tokens.txt");
        var threads = 1;

        WorkerStatusChanged?.Invoke("loading C# ONNX model");
        AppLogger.Info(
            $"C# ASR model loading. model={_model.Id}, modelFile={modelFilePath}, config={configFilePath}, threads={threads}");
        _recognizer = new OfflineRecognizer(
            modelFilePath,
            configFilePath,
            mvnFilePath,
            tokensFilePath,
            threads,
            OnnxRumtimeTypes.CPU);
        WorkerStatusChanged?.Invoke("C# ONNX model ready");
        AppLogger.Info($"C# ASR model ready. model={_model.Id}");
        return _recognizer;
    }

    private void EnsurePunctuationRestorer(CancellationToken cancellationToken)
    {
        if (!_model.IsPunctuationDownloaded)
        {
            AppLogger.Info($"Punctuation model skipped because it is not downloaded. model={_model.PunctuationModel}");
            return;
        }

        if (_punctuationRestorer.IsLoaded)
        {
            return;
        }

        var modelDirectory = AsrModelCatalog.GetModelCacheDirectory(_model.PunctuationModel);
        var threads = 1;
        WorkerStatusChanged?.Invoke("loading punctuation model");
        AppLogger.Info($"Punctuation model loading. model={_model.PunctuationModel}, directory={modelDirectory}, threads={threads}");
        _punctuationRestorer.Load(modelDirectory, threads, cancellationToken);
        WorkerStatusChanged?.Invoke("punctuation model ready");
        AppLogger.Info($"Punctuation model ready. model={_model.PunctuationModel}");
    }

    private string RestorePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !_punctuationRestorer.IsLoaded)
        {
            return text;
        }

        try
        {
            return _punctuationRestorer.Restore(text);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Punctuation restoration failed", ex);
            return text;
        }
    }

    private static string ReplaceTrailingFullStopWithSpace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmedEnd = text.TrimEnd();
        if (trimmedEnd.Length == 0)
        {
            return text;
        }

        var last = trimmedEnd[^1];
        return last is '。' or '.' or '．'
            ? trimmedEnd[..^1] + " "
            : text;
    }

    private static string FindRequiredFile(string directory, params string[] fileNames)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"模型目录不存在: {directory}");
        }

        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"模型目录缺少文件: {string.Join(" 或 ", fileNames)}。目录: {directory}");
    }

    private static float[] LoadWavSamples(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        var samples = new List<float>((int)Math.Min(reader.Length / sizeof(float), int.MaxValue));
        var buffer = new float[reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i += reader.WaveFormat.Channels)
            {
                samples.Add(buffer[i] * 32768f);
            }
        }

        return samples.ToArray();
    }

    private static IEnumerable<float[]> SplitSamples(float[] samples)
    {
        if (samples.Length == 0)
        {
            yield return samples;
            yield break;
        }

        for (var offset = 0; offset < samples.Length; offset += MaxRecognitionChunkSamples)
        {
            var length = Math.Min(MaxRecognitionChunkSamples, samples.Length - offset);
            var chunk = new float[length];
            Array.Copy(samples, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    private void DisposeRecognizer()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    public void Dispose()
    {
        DisposeRecognizer();
        _punctuationRestorer.Dispose();
        _gate.Dispose();
    }
}
