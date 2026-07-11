using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AliParaformerAsr;
using NAudio.Wave;
using SherpaOnnx;
using NativeOfflineRecognizer = AliParaformerAsr.OfflineRecognizer;
using SherpaOfflineRecognizer = SherpaOnnx.OfflineRecognizer;

namespace pc_receiver;

public sealed class ParaformerAsrService : IDisposable
{
    private const int RecognitionSampleRate = 16000;
    private const int MaxRecognitionChunkSeconds = 8;
    private const int MaxRecognitionChunkSamples = RecognitionSampleRate * MaxRecognitionChunkSeconds;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PunctuationRestorer _punctuationRestorer = new();
    private NativeOfflineRecognizer? _recognizer;
    private SherpaOfflineRecognizer? _sherpaRecognizer;
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
                if (_model.Engine == AsrEngine.SherpaOnnxParaformer)
                {
                    EnsureSherpaRecognizer();
                }
                else
                {
                    EnsureRecognizer();
                }

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
                WorkerStatusChanged?.Invoke("C# ONNX recognition starting");
                AppLogger.Info($"C# ASR request starting. model={_model.Id}, wav={wavPath}");
                var text = _model.Engine == AsrEngine.SherpaOnnxParaformer
                    ? RecognizeWithSherpa(wavPath, cancellationToken)
                    : RecognizeWithNative(wavPath, EnsureRecognizer(), cancellationToken);
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

    private NativeOfflineRecognizer EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        if (_model.Engine == AsrEngine.SherpaOnnxParaformer)
        {
            throw new InvalidOperationException("Sherpa-ONNX recognizer should be loaded via EnsureSherpaRecognizer.");
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
        _recognizer = new NativeOfflineRecognizer(
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

    private SherpaOfflineRecognizer EnsureSherpaRecognizer()
    {
        if (_sherpaRecognizer is not null)
        {
            return _sherpaRecognizer;
        }

        var modelDirectory = AsrModelCatalog.GetModelCacheDirectory(_model.AsrModel);
        var modelPath = FindRequiredFile(modelDirectory, "model.int8.onnx", "model.onnx");
        var tokensPath = FindRequiredFile(modelDirectory, "tokens.txt");
        WorkerStatusChanged?.Invoke("loading Sherpa-ONNX model");
        AppLogger.Info($"Sherpa-ONNX model loading. model={_model.Id}, modelFile={modelPath}, tokens={tokensPath}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.Paraformer.Model = modelPath;
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _sherpaRecognizer = new SherpaOfflineRecognizer(config);
        WorkerStatusChanged?.Invoke("Sherpa-ONNX model ready");
        AppLogger.Info($"Sherpa-ONNX model ready. model={_model.Id}");
        return _sherpaRecognizer;
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

    private string RecognizeWithNative(string wavPath, NativeOfflineRecognizer recognizer, CancellationToken cancellationToken)
    {
        var samples = LoadNativeWavSamples(wavPath);
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

        return string.Concat(textParts);
    }

    private string RecognizeWithSherpa(string wavPath, CancellationToken cancellationToken)
    {
        var recognizer = EnsureSherpaRecognizer();
        var (sampleRate, samples) = LoadFloatWavSamples(wavPath);
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        cancellationToken.ThrowIfCancellationRequested();
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
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

    private static float[] LoadNativeWavSamples(string wavPath)
    {
        var (_, samples) = LoadFloatWavSamples(wavPath);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= 32768f;
        }

        return samples;
    }

    private static (int SampleRate, float[] Samples) LoadFloatWavSamples(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        var samples = new List<float>((int)Math.Min(reader.Length / sizeof(float), int.MaxValue));
        var buffer = new float[reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i += reader.WaveFormat.Channels)
            {
                samples.Add(buffer[i]);
            }
        }

        return (reader.WaveFormat.SampleRate, samples.ToArray());
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
        _sherpaRecognizer?.Dispose();
        _sherpaRecognizer = null;
    }

    public void Dispose()
    {
        DisposeRecognizer();
        _punctuationRestorer.Dispose();
        _gate.Dispose();
    }
}
