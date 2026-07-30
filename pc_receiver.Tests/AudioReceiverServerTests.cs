using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using pc_receiver;

namespace pc_receiver.Tests;

public sealed class AudioReceiverServerTests
{
    [Test]
    public async Task Multiple_clients_are_accepted_and_remain_connected()
    {
        using var server = new AudioReceiverServer();
        await server.StartAsync(0);

        using var first = new TcpClient();
        await first.ConnectAsync("127.0.0.1", server.BoundPort);
        var firstType = await ReadControlTypeAsync(first);

        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", server.BoundPort);
        var secondType = await ReadControlTypeAsync(second);

        Assert.That(firstType, Is.EqualTo("session-accepted"));
        Assert.That(secondType, Is.EqualTo("session-accepted"));
        Assert.That(first.Connected, Is.True);
        Assert.That(second.Connected, Is.True);
    }

    [Test]
    public async Task Only_the_active_speaker_can_forward_audio_and_stop()
    {
        using var server = new AudioReceiverServer();
        var controls = new ConcurrentQueue<string>();
        var audio = new ConcurrentQueue<byte[]>();
        server.ControlMessageReceived += message =>
        {
            controls.Enqueue(GetControlType(message)!);
            return Task.CompletedTask;
        };
        server.AudioFrameReceived += bytes => audio.Enqueue(bytes);
        await server.StartAsync(0);

        using var first = new TcpClient();
        await first.ConnectAsync("127.0.0.1", server.BoundPort);
        await ReadControlTypeAsync(first);
        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", server.BoundPort);
        await ReadControlTypeAsync(second);

        await SendControlAsync(first, "asr-start");
        await SendControlAsync(second, "asr-start");
        await SendAudioAsync(second, [9, 9]);
        await SendAudioAsync(first, [1, 2, 3]);
        await SendControlAsync(second, "asr-stop");
        await SendControlAsync(first, "asr-stop");

        await WaitUntilAsync(() => controls.Count == 2 && audio.Count == 1);
        Assert.That(controls, Is.EqualTo(new[] { "asr-start", "asr-stop" }));
        Assert.That(audio.Single(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Disconnecting_the_active_speaker_stops_it_and_releases_ownership()
    {
        using var server = new AudioReceiverServer();
        var controls = new ConcurrentQueue<string>();
        server.ControlMessageReceived += message =>
        {
            controls.Enqueue(GetControlType(message)!);
            return Task.CompletedTask;
        };
        await server.StartAsync(0);

        var first = new TcpClient();
        await first.ConnectAsync("127.0.0.1", server.BoundPort);
        await ReadControlTypeAsync(first);
        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", server.BoundPort);
        await ReadControlTypeAsync(second);

        await SendControlAsync(first, "asr-start");
        await WaitUntilAsync(() => controls.Count == 1);
        first.Dispose();
        await WaitUntilAsync(() => controls.Count == 2);

        await SendControlAsync(second, "asr-start");
        await WaitUntilAsync(() => controls.Count == 3);

        Assert.That(
            controls,
            Is.EqualTo(new[] { "asr-start", "asr-stop", "asr-start" }));
    }

    private static async Task<string?> ReadControlTypeAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stream = client.GetStream();
        var header = await ReadExactAsync(stream, 5, timeout.Token);
        Assert.That(header[0], Is.EqualTo(1));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var payload = await ReadExactAsync(stream, length, timeout.Token);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("type").GetString();
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
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, length - offset),
                token);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static string? GetControlType(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("type").GetString();
    }

    private static Task SendControlAsync(TcpClient client, string type)
    {
        return SendFrameAsync(
            client,
            1,
            JsonSerializer.SerializeToUtf8Bytes(new { type }));
    }

    private static Task SendAudioAsync(TcpClient client, byte[] payload)
    {
        return SendFrameAsync(client, 2, payload);
    }

    private static async Task SendFrameAsync(
        TcpClient client,
        byte type,
        byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        await client.GetStream().WriteAsync(frame);
        await client.GetStream().FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
