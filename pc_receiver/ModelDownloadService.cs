using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public sealed class ModelDownloadService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly string[] AsrFiles = ["config.yaml", "tokens.json", "am.mvn", "model_quant.onnx"];
    private static readonly string[] SherpaParaformerFiles = ["tokens.txt", "model.int8.onnx"];
    private static readonly string[] PunctuationFiles = ["config.yaml", "tokens.json", "model_quant.onnx"];
    private static readonly string[] VadFiles = ["config.yaml", "am.mvn", "configuration.json", "model_quant.onnx"];

    public async Task DownloadRequiredModelsAsync(
        AsrModelOption model,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plans = BuildPlans(model).ToArray();
        var missingFiles = plans.SelectMany(plan => plan.Files.Where(file => !File.Exists(file.TargetPath))).ToArray();
        if (missingFiles.Length == 0)
        {
            progress?.Report(new ModelDownloadProgress("模型文件已完整", 100, false));
            return;
        }

        progress?.Report(new ModelDownloadProgress("正在获取模型文件大小...", 0, true));
        var fileSizes = await TryGetRemoteFileSizesAsync(missingFiles, cancellationToken);
        var hasFullSizeInfo = fileSizes.Count == missingFiles.Length;
        var totalBytes = hasFullSizeInfo ? fileSizes.Values.Sum() : 0;
        long completedBytes = 0;
        var completed = 0;
        foreach (var plan in plans)
        {
            Directory.CreateDirectory(plan.Directory);
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(file.TargetPath))
                {
                    continue;
                }

                var baseProgress = completed * 100d / missingFiles.Length;
                progress?.Report(new ModelDownloadProgress($"正在下载 {plan.DisplayName}: {file.FileName}", baseProgress, true));
                var fileSize = fileSizes.GetValueOrDefault(file.TargetPath);
                await DownloadFileAsync(
                    file.Url,
                    file.TargetPath,
                    plan.DisplayName,
                    file.FileName,
                    baseProgress,
                    100d / missingFiles.Length,
                    hasFullSizeInfo,
                    completedBytes,
                    totalBytes,
                    progress,
                    cancellationToken);
                completedBytes += fileSize;
                completed++;
            }
        }

        progress?.Report(new ModelDownloadProgress("模型下载完成", 100, false));
    }

    private static IEnumerable<ModelDownloadPlan> BuildPlans(AsrModelOption model)
    {
        if (!model.IsDownloaded)
        {
            yield return BuildPlan(
                "语音识别模型",
                model.AsrModel,
                model.Revision,
                model.Provider,
                model.Engine == AsrEngine.SherpaOnnxParaformer ? SherpaParaformerFiles : AsrFiles);
        }

        if (model.RequiresPunctuationModel && !model.IsPunctuationDownloaded)
        {
            yield return BuildPlan("标点恢复模型", model.PunctuationModel, model.Revision, ModelProvider.ModelScope, PunctuationFiles);
        }

        if (model.RequiresVadModel && !model.IsVadDownloaded)
        {
            yield return BuildPlan("语音活动检测模型", model.VadModel, model.Revision, ModelProvider.ModelScope, VadFiles);
        }
    }

    private static ModelDownloadPlan BuildPlan(
        string displayName,
        string modelName,
        string revision,
        ModelProvider provider,
        string[] fileNames)
    {
        var directory = AsrModelCatalog.GetModelCacheDirectory(modelName);
        return new ModelDownloadPlan(
            displayName,
            directory,
            fileNames.Select(fileName => new ModelDownloadFile(
                fileName,
                Path.Combine(directory, fileName),
                BuildDownloadUrl(modelName, revision, provider, fileName))).ToArray());
    }

    private static string BuildDownloadUrl(string modelName, string revision, ModelProvider provider, string fileName)
    {
        return provider switch
        {
            ModelProvider.HuggingFace => $"https://huggingface.co/{modelName}/resolve/{Uri.EscapeDataString(revision)}/{Uri.EscapeDataString(fileName)}?download=true",
            _ => $"https://www.modelscope.cn/api/v1/models/{modelName}/repo?Revision={Uri.EscapeDataString(revision)}&FilePath={Uri.EscapeDataString(fileName)}"
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(
            new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true
            })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MobileToPcInput/1.0 (+https://www.modelscope.cn)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.modelscope.cn/");
        return client;
    }

    private static async Task<Dictionary<string, long>> TryGetRemoteFileSizesAsync(
        IReadOnlyCollection<ModelDownloadFile> files,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetRemoteFileSizesAsync(files, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Model file size query failed; falling back to per-file progress", ex);
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task<Dictionary<string, long>> GetRemoteFileSizesAsync(
        IReadOnlyCollection<ModelDownloadFile> files,
        CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await HttpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 0 and var length)
            {
                sizes[file.TargetPath] = length;
            }
        }

        return sizes;
    }

    private static async Task DownloadFileAsync(
        string url,
        string targetPath,
        string modelDisplayName,
        string fileName,
        double baseProgress,
        double progressWeight,
        bool useByteWeightedProgress,
        long completedBytesBeforeFile,
        long totalDownloadBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{targetPath}.download";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"下载 {modelDisplayName}: {fileName} 失败，ModelScope 返回 {(int)response.StatusCode} {response.ReasonPhrase}。",
                null,
                response.StatusCode);
        }

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

        var buffer = new byte[1024 * 128];
        long downloadedBytes = 0;
        var lastProgressReport = Stopwatch.GetTimestamp();
        double lastReportedOverall = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;
            if (totalBytes is > 0)
            {
                var fileProgress = Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0, 100);
                var overall = useByteWeightedProgress && totalDownloadBytes > 0
                    ? Math.Clamp((completedBytesBeforeFile + downloadedBytes) * 100d / totalDownloadBytes, 0, 99)
                    : Math.Clamp(baseProgress + fileProgress * progressWeight / 100d, 0, 99);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(lastProgressReport, now);
                if (overall - lastReportedOverall >= 0.5 || elapsed >= TimeSpan.FromMilliseconds(150))
                {
                    progress?.Report(new ModelDownloadProgress(
                        BuildProgressMessage(modelDisplayName, fileName, downloadedBytes, totalBytes.Value, completedBytesBeforeFile, totalDownloadBytes, useByteWeightedProgress),
                        overall,
                        false));
                    lastProgressReport = now;
                    lastReportedOverall = overall;
                }
            }
        }

        if (totalBytes is > 0)
        {
            var fileProgress = Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0, 100);
            var overall = useByteWeightedProgress && totalDownloadBytes > 0
                ? Math.Clamp((completedBytesBeforeFile + downloadedBytes) * 100d / totalDownloadBytes, 0, 99)
                : Math.Clamp(baseProgress + fileProgress * progressWeight / 100d, 0, 99);
            progress?.Report(new ModelDownloadProgress(
                BuildProgressMessage(modelDisplayName, fileName, downloadedBytes, totalBytes.Value, completedBytesBeforeFile, totalDownloadBytes, useByteWeightedProgress),
                overall,
                false));
        }

        output.Close();
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static string BuildProgressMessage(
        string modelDisplayName,
        string fileName,
        long downloadedBytes,
        long fileTotalBytes,
        long completedBytesBeforeFile,
        long totalDownloadBytes,
        bool useByteWeightedProgress)
    {
        var filePart = $"{FormatBytes(downloadedBytes)}/{FormatBytes(fileTotalBytes)}";
        if (!useByteWeightedProgress || totalDownloadBytes <= 0)
        {
            return $"正在下载 {modelDisplayName}: {fileName} ({filePart})";
        }

        var overallBytes = completedBytesBeforeFile + downloadedBytes;
        return $"正在下载 {modelDisplayName}: {fileName} ({filePart}，总计 {FormatBytes(overallBytes)}/{FormatBytes(totalDownloadBytes)})";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##}{units[unit]}";
    }

    private sealed record ModelDownloadPlan(string DisplayName, string Directory, IReadOnlyList<ModelDownloadFile> Files);
    private sealed record ModelDownloadFile(string FileName, string TargetPath, string Url);
}

public sealed record ModelDownloadProgress(string Message, double Progress, bool IsIndeterminate);
