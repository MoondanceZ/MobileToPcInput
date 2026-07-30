using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public sealed class AudioReceiverServer : IDisposable
{
    private const int HeaderLength = 5;
    private const int ControlFrame = 1;
    private const int AudioFrame = 2;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly object _sessionLock = new();
    private int _connectedClientCount;
    private long _nextClientId;
    private long? _activeSpeakerClientId;

    public event Action<byte[]>? AudioFrameReceived;
    public event Func<string, Task>? ControlMessageReceived;
    public event Action<bool>? ClientStateChanged;
    public event Action<string>? StatusChanged;

    public int BoundPort =>
        (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    public Task StartAsync(int port)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        AppLogger.Info($"TCP listening on 0.0.0.0:{port}");
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        AppLogger.Info("TCP server stopped");
        ClientStateChanged?.Invoke(false);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                try
                {
                    client.NoDelay = true;
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    await SendSessionStatusAsync(client, "session-accepted", token);
                    AppLogger.Info($"Accepted TCP client {client.Client.RemoteEndPoint}");
                    var clientId = Interlocked.Increment(ref _nextClientId);
                    _ = Task.Run(() => HandleClientAsync(clientId, client, token));
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Accept TCP client failed", ex);
                StatusChanged?.Invoke($"接收连接失败: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(
        long clientId,
        TcpClient client,
        CancellationToken token)
    {
        using var _ = client;
        var audioFrames = 0;
        long audioBytes = 0;
        var controlFrames = 0;
        var isFirstConnectedClient = RegisterClient();
        try
        {
            using var stream = client.GetStream();
            if (isFirstConnectedClient)
            {
                ClientStateChanged?.Invoke(true);
                StatusChanged?.Invoke("手机已连接，等待音频");
            }

            while (!token.IsCancellationRequested && client.Connected)
            {
                var header = await ReadExactAsync(stream, HeaderLength, token);
                if (header.Length == 0)
                {
                    break;
                }

                var type = header[0];
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
                if (length < 0 || length > 1024 * 1024)
                {
                    throw new InvalidDataException($"TCP frame length is invalid: {length}");
                }

                var payload = await ReadExactAsync(stream, length, token);
                if (payload.Length != length)
                {
                    break;
                }

                switch (type)
                {
                    case AudioFrame:
                        audioFrames++;
                        audioBytes += payload.Length;
                        if (audioFrames == 1 || audioFrames % 20 == 0)
                        {
                            AppLogger.Info(
                                $"TCP audio frame received. frames={audioFrames}, totalBytes={audioBytes}, lastBytes={payload.Length}");
                        }

                        if (IsActiveSpeaker(clientId))
                        {
                            AudioFrameReceived?.Invoke(payload);
                        }
                        break;
                    case ControlFrame:
                        controlFrames++;
                        var message = Encoding.UTF8.GetString(payload);
                        AppLogger.Info($"TCP control frame received. controls={controlFrames}, message={message}");
                        await HandleControlMessageAsync(clientId, message);
                        break;
                    default:
                        AppLogger.Info($"Ignored unknown TCP frame type {type} length {length}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        {
            SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted
        })
        {
            AppLogger.Info($"TCP client disconnected: {ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TCP client connection ended with error", ex);
            StatusChanged?.Invoke($"手机连接结束: {ex.Message}");
        }
        finally
        {
            await ReleaseSpeakerOnDisconnectAsync(clientId);
            AppLogger.Info(
                $"TCP client disconnected. controls={controlFrames}, audioFrames={audioFrames}, audioBytes={audioBytes}");
            if (UnregisterClient())
            {
                ClientStateChanged?.Invoke(false);
            }
        }
    }

    private bool RegisterClient()
    {
        lock (_sessionLock)
        {
            _connectedClientCount++;
            return _connectedClientCount == 1;
        }
    }

    private bool UnregisterClient()
    {
        lock (_sessionLock)
        {
            _connectedClientCount = Math.Max(0, _connectedClientCount - 1);
            return _connectedClientCount == 0;
        }
    }

    private bool IsActiveSpeaker(long clientId)
    {
        lock (_sessionLock)
        {
            return _activeSpeakerClientId == clientId;
        }
    }

    private async Task HandleControlMessageAsync(long clientId, string message)
    {
        var controlType = GetControlType(message);
        if (IsStartControl(controlType))
        {
            var ownsSession = false;
            lock (_sessionLock)
            {
                if (_activeSpeakerClientId is null)
                {
                    _activeSpeakerClientId = clientId;
                }

                ownsSession = _activeSpeakerClientId == clientId;
            }

            if (ownsSession)
            {
                await InvokeControlMessageAsync(message);
            }

            return;
        }

        if (IsStopControl(controlType))
        {
            if (!IsActiveSpeaker(clientId))
            {
                return;
            }

            try
            {
                await InvokeControlMessageAsync(message);
            }
            finally
            {
                ReleaseSpeaker(clientId);
            }

            return;
        }

        await InvokeControlMessageAsync(message);
    }

    private async Task ReleaseSpeakerOnDisconnectAsync(long clientId)
    {
        if (!IsActiveSpeaker(clientId))
        {
            return;
        }

        try
        {
            await InvokeControlMessageAsync("""{"type":"asr-stop"}""");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Stopping disconnected speaker session failed", ex);
        }
        finally
        {
            ReleaseSpeaker(clientId);
        }
    }

    private void ReleaseSpeaker(long clientId)
    {
        lock (_sessionLock)
        {
            if (_activeSpeakerClientId == clientId)
            {
                _activeSpeakerClientId = null;
            }
        }
    }

    private async Task InvokeControlMessageAsync(string message)
    {
        var handler = ControlMessageReceived;
        if (handler is not null)
        {
            await handler(message);
        }
    }

    private static string? GetControlType(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.TryGetProperty("type", out var type)
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsStartControl(string? type)
    {
        return type is "asr-start" or "vocotype-start";
    }

    private static bool IsStopControl(string? type)
    {
        return type is "asr-stop" or "vocotype-stop";
    }

    private static async Task<byte[]> ReadExactAsync(
        NetworkStream stream,
        int length,
        CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), token);
            if (read == 0)
            {
                return offset == 0 ? [] : buffer[..offset];
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task SendSessionStatusAsync(
        TcpClient client,
        string type,
        CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type });
        var frame = new byte[HeaderLength + payload.Length];
        frame[0] = ControlFrame;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        var stream = client.GetStream();
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    public void Dispose()
    {
        var acceptTask = _acceptTask;
        Stop();
        if (acceptTask is not null && !acceptTask.IsCompleted)
        {
            try
            {
                acceptTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // Stop() cancels the accept loop; cancellation is expected.
            }
        }

        _cts?.Dispose();
        if (acceptTask?.IsCompleted == true)
        {
            acceptTask.Dispose();
        }
    }
}
