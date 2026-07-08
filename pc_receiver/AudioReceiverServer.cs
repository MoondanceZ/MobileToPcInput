using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    public event Action<byte[]>? AudioFrameReceived;
    public event Action<string>? ControlMessageReceived;
    public event Action<bool>? ClientStateChanged;
    public event Action<string>? StatusChanged;

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
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                AppLogger.Info($"Accepted TCP client {client.Client.RemoteEndPoint}");
                _ = Task.Run(() => HandleClientAsync(client, token), token);
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        var audioFrames = 0;
        long audioBytes = 0;
        var controlFrames = 0;
        try
        {
            using var stream = client.GetStream();
            ClientStateChanged?.Invoke(true);
            StatusChanged?.Invoke("手机已连接，等待音频");

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

                        AudioFrameReceived?.Invoke(payload);
                        break;
                    case ControlFrame:
                        controlFrames++;
                        var message = Encoding.UTF8.GetString(payload);
                        AppLogger.Info($"TCP control frame received. controls={controlFrames}, message={message}");
                        ControlMessageReceived?.Invoke(message);
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
            AppLogger.Info(
                $"TCP client disconnected. controls={controlFrames}, audioFrames={audioFrames}, audioBytes={audioBytes}");
            ClientStateChanged?.Invoke(false);
        }
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

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _acceptTask?.Dispose();
    }
}
