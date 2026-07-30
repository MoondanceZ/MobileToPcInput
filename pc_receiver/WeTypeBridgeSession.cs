using System;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public interface IWeTypeAudioOutput
{
    TimeSpan BufferedDuration { get; }
    void ClearBuffer();
    void AddSamples(byte[] bytes);
    void AddSilence(TimeSpan duration);
    Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IWeTypeHotkeyController
{
    void Press();
    void Release();
}

public sealed class WeTypeBridgeSession : IDisposable
{
    private static readonly TimeSpan StartupSilence = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MinimumDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrainSafetyMargin = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly IWeTypeAudioOutput _audioOutput;
    private readonly IWeTypeHotkeyController _hotkey;
    private int _state;

    public WeTypeBridgeSession(IWeTypeAudioOutput audioOutput, IWeTypeHotkeyController hotkey)
    {
        _audioOutput = audioOutput;
        _hotkey = hotkey;
    }

    public bool IsActive => Volatile.Read(ref _state) != 0;

    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_state != 0)
            {
                return Task.CompletedTask;
            }

            _audioOutput.ClearBuffer();
            try
            {
                _hotkey.Press();
                _audioOutput.AddSilence(StartupSilence);
                Volatile.Write(ref _state, 1);
            }
            catch
            {
                _hotkey.Release();
                _audioOutput.ClearBuffer();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public void AddAudio(byte[] bytes)
    {
        if (Volatile.Read(ref _state) != 1 || bytes.Length == 0)
        {
            return;
        }

        _audioOutput.AddSamples(bytes);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state != 1)
            {
                return;
            }

            Volatile.Write(ref _state, 2);
        }

        try
        {
            _audioOutput.AddSilence(TrailingSilence);
            var drainTimeout = _audioOutput.BufferedDuration + DrainSafetyMargin;
            if (drainTimeout < MinimumDrainTimeout)
            {
                drainTimeout = MinimumDrainTimeout;
            }

            await _audioOutput.DrainAsync(drainTimeout, cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                try
                {
                    _hotkey.Release();
                }
                finally
                {
                    _audioOutput.ClearBuffer();
                    Volatile.Write(ref _state, 0);
                }
            }
        }
    }

    public void Abort()
    {
        lock (_sync)
        {
            try
            {
                _hotkey.Release();
            }
            finally
            {
                _audioOutput.ClearBuffer();
                Volatile.Write(ref _state, 0);
            }
        }
    }

    public void Dispose()
    {
        Abort();
    }
}
