using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public sealed class ParaformerAsrService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrTask;
    private CancellationTokenSource? _workerCts;
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
            _model = model;
            DisposeProcess();
            AppLogger.Info($"ASR model configured. id={model.Id}, asr={model.AsrModel}, punc={model.PunctuationModel}");
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
            DisposeProcess();
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
            await EnsureWorkerAsync(cancellationToken);
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
            await EnsureWorkerAsync(cancellationToken);
            var id = Guid.NewGuid().ToString("N");
            var wavBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(wavPath));
            var request = $"{{\"id\":\"{id}\",\"wav_b64\":\"{wavBase64}\"}}";
            AppLogger.Info(
                $"ASR worker request sending. id={id}, requestLength={request.Length}, requestPrefix={request[..Math.Min(48, request.Length)]}, wav={wavPath}");
            await _stdin!.WriteLineAsync(request);
            await _stdin.FlushAsync(cancellationToken);

            while (true)
            {
                var line = await _stdout!.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw new InvalidOperationException("ASR worker stdout closed");
                }

                AppLogger.Info($"ASR worker stdout: {Tail(line, 1200)}");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is "ready")
                {
                    continue;
                }

                var responseIdText = root.TryGetProperty("id", out var responseId)
                    ? responseId.GetString()
                    : null;
                var isCurrentResponse = responseIdText is null || responseIdText == id;
                if (!isCurrentResponse)
                {
                    continue;
                }

                if (type is "result")
                {
                    var text = root.GetProperty("text").GetString()?.Trim() ?? string.Empty;
                    AppLogger.Info($"ASR worker result. id={id}, textLength={text.Length}");
                    return text;
                }

                if (type is "error" or "fatal")
                {
                    throw new InvalidOperationException(root.GetProperty("error").GetString());
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        DisposeProcess();
        _workerCts = new CancellationTokenSource();
        var model = _model;

        var scriptPath = FindExistingPath(
            Path.Combine(AppContext.BaseDirectory, "scripts", "asr_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "asr_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "asr_worker.py"));
        if (scriptPath is null)
        {
            throw new FileNotFoundException("找不到 ASR worker 脚本 scripts/asr_worker.py");
        }

        var pythonPath = FindExistingPath(
            Path.Combine(AppContext.BaseDirectory, "asr_runtime", ".venv", "Scripts", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "asr_runtime", ".venv", "Scripts", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "asr_runtime", ".venv", "Scripts", "python.exe"))
            ?? "python";

        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["FUNASR_MODEL_REVISION"] = model.Revision;
        psi.Environment["FUNASR_ASR_MODEL"] = model.AsrModel;
        psi.Environment["FUNASR_PUNC_MODEL"] = model.PunctuationModel;

        AppLogger.Info(
            $"ASR worker starting. python={pythonPath}, script={scriptPath}, model={model.Id}, revision={model.Revision}");
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Python ASR worker");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stderrTask = Task.Run(() => DrainStderrAsync(_process, _workerCts.Token));
        AppLogger.Info($"ASR worker process started. pid={_process.Id}");

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyCts.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            var line = await _stdout.ReadLineAsync(readyCts.Token);
            if (line is null)
            {
                throw new InvalidOperationException("ASR worker exited before ready");
            }

            AppLogger.Info($"ASR worker stdout: {Tail(line, 1200)}");
            using var document = JsonDocument.Parse(line);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type is "ready")
            {
                AppLogger.Info("ASR worker ready.");
                return;
            }

            if (type is "fatal")
            {
                throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());
            }
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    WorkerStatusChanged?.Invoke(line);
                    LogWorkerStderr(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("ASR worker stderr reader failed", ex);
        }
    }

    private static string? FindExistingPath(params string[] paths)
    {
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static string Tail(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" | ");
        return value.Length <= maxLength ? value : value[^maxLength..];
    }

    private static void LogWorkerStderr(string line)
    {
        if (line.Contains("Downloading:", StringComparison.Ordinal)
            || line.Contains("modelscope_hub.download", StringComparison.Ordinal)
            || line.Contains("WARNING:root:trust_remote_code", StringComparison.Ordinal)
            || line.Contains("DEBUG:jieba:", StringComparison.Ordinal))
        {
            return;
        }

        AppLogger.Info($"ASR worker stderr: {Tail(line, 1200)}");
    }

    private void DisposeProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _workerCts?.Cancel();
        _stdin?.Dispose();
        _stdout?.Dispose();
        _process?.Dispose();
        _workerCts?.Dispose();
        _stdin = null;
        _stdout = null;
        _process = null;
        _workerCts = null;
    }

    public void Dispose()
    {
        DisposeProcess();
        _gate.Dispose();
    }
}
