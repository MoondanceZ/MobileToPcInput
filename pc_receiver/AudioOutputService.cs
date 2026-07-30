using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace pc_receiver;

public sealed class AudioOutputService : IWeTypeAudioOutput, IDisposable
{
    private readonly WaveFormat _inputFormat = new(16000, 16, 1);
    private readonly object _sync = new();
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;

    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        var devices = new List<AudioOutputDevice>();
        var count = WaveInterop.waveOutGetNumDevs();
        for (var i = 0; i < count; i++)
        {
            WaveInterop.waveOutGetDevCaps(
                new IntPtr(i),
                out var caps,
                Marshal.SizeOf<WaveOutCapabilities>());
            devices.Add(new AudioOutputDevice(i, caps.ProductName));
        }

        return devices;
    }

    public AudioOutputDevice? FindDevice(string? preferredName)
    {
        var devices = GetDevices();
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var exact = devices.FirstOrDefault(
                item => string.Equals(item.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
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
        Start(device.DeviceNumber);
    }

    public void Start(int deviceNumber)
    {
        lock (_sync)
        {
            StopCore();

            _buffer = new BufferedWaveProvider(_inputFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _waveOut = new WaveOutEvent
            {
                DeviceNumber = deviceNumber,
                DesiredLatency = 60,
                NumberOfBuffers = 2,
            };
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
