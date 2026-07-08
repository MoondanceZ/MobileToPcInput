using System;
using System.IO;
using NAudio.Wave;

namespace pc_receiver;

public sealed class AsrSessionBuffer
{
    private readonly object _gate = new();
    private readonly MemoryStream _buffer = new();
    private bool _isRecording;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _isRecording;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _buffer.SetLength(0);
            _isRecording = true;
        }
    }

    public void AddSamples(byte[] bytes)
    {
        lock (_gate)
        {
            if (!_isRecording)
            {
                return;
            }

            _buffer.Write(bytes, 0, bytes.Length);
        }
    }

    public byte[] Stop()
    {
        lock (_gate)
        {
            _isRecording = false;
            return _buffer.ToArray();
        }
    }

    public string WriteWavFile(byte[] pcmBytes)
    {
        var path = AudioCacheService.CreateWavPath();
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        writer.Write(pcmBytes, 0, pcmBytes.Length);
        return path;
    }
}
