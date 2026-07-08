using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace pc_receiver;

public sealed class AudioOutputService : IDisposable
{
    private readonly WaveFormat _inputFormat = new(16000, 16, 1);
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

    public void Start(int deviceNumber)
    {
        Stop();

        _buffer = new BufferedWaveProvider(_inputFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            DesiredLatency = 120,
            NumberOfBuffers = 3,
        };
        _waveOut.Init(_buffer);
        _waveOut.Play();
    }

    public void AddSamples(byte[] bytes)
    {
        _buffer?.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _buffer = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
