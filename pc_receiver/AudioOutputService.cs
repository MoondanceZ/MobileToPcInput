using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace pc_receiver;

public sealed class AudioOutputService : IWeTypeAudioOutput, IDisposable
{
    private readonly WaveFormat _inputFormat = new(16000, 16, 1);
    private readonly object _sync = new();
    private BufferedWaveProvider? _buffer;
    private WasapiOut? _waveOut;

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                return _buffer?.BufferedDuration ?? TimeSpan.Zero;
            }
        }
    }

    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        var devices = new List<AudioOutputDevice>();
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render,
            DeviceState.Active);
        for (var index = 0; index < endpoints.Count; index++)
        {
            var endpoint = endpoints[index];
            devices.Add(new AudioOutputDevice(
                index + 1,
                endpoint.FriendlyName,
                endpoint.ID));
        }

        return devices;
    }

    public AudioOutputDevice? FindDevice(string? preferredName)
    {
        return SelectDevice(GetDevices(), preferredName);
    }

    public static AudioOutputDevice? SelectDevice(
        IReadOnlyList<AudioOutputDevice> devices,
        string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var exact = devices.FirstOrDefault(
                item => string.Equals(item.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var standardCable = devices.FirstOrDefault(
            item => string.Equals(
                        item.Name,
                        "CABLE Input",
                        StringComparison.OrdinalIgnoreCase)
                    || item.Name.StartsWith(
                        "CABLE Input (",
                        StringComparison.OrdinalIgnoreCase));
        if (standardCable is not null)
        {
            return standardCable;
        }

        return devices.FirstOrDefault(item => item.IsLikelyVirtualCable);
    }

    public string? GetDefaultCaptureDeviceName()
    {
        try
        {
            return WaveIn.DeviceCount > 0
                ? WaveIn.GetCapabilities(0).ProductName
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Default capture device detection failed", ex);
            return null;
        }
    }

    public void Start(string deviceName)
    {
        var device = FindDevice(deviceName)
            ?? throw new InvalidOperationException($"找不到虚拟音频输出设备：{deviceName}");
        Start(device);
    }

    public void Start(int deviceNumber)
    {
        var device = GetDevices().FirstOrDefault(
            item => item.DeviceNumber == deviceNumber)
            ?? throw new InvalidOperationException($"找不到音频输出设备：{deviceNumber}");
        Start(device);
    }

    public void Start(AudioOutputDevice device)
    {
        lock (_sync)
        {
            StopCore();

            _buffer = new BufferedWaveProvider(_inputFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(30),
                DiscardOnBufferOverflow = false,
            };
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = enumerator.GetDevice(device.EndpointId);
            _waveOut = new WasapiOut(
                endpoint,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 100);
            _waveOut.Init(_buffer);
            _waveOut.Play();
        }
    }

    public void AddSamples(byte[] bytes)
    {
        lock (_sync)
        {
            _buffer?.AddSamples(bytes, 0, bytes.Length);
        }
    }

    public void AddSilence(TimeSpan duration)
    {
        var byteCount = (int)Math.Round(
            _inputFormat.AverageBytesPerSecond * duration.TotalSeconds);
        byteCount -= byteCount % _inputFormat.BlockAlign;
        if (byteCount <= 0)
        {
            return;
        }

        AddSamples(new byte[byteCount]);
    }

    public void ClearBuffer()
    {
        lock (_sync)
        {
            _buffer?.ClearBuffer();
        }
    }

    public async Task DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan bufferedDuration;
            lock (_sync)
            {
                bufferedDuration = _buffer?.BufferedDuration ?? TimeSpan.Zero;
            }

            if (bufferedDuration <= TimeSpan.FromMilliseconds(20))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
        }

        AppLogger.Info("Audio output drain reached timeout; releasing WeType hotkey.");
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void StopCore()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _buffer = null;
    }
}
