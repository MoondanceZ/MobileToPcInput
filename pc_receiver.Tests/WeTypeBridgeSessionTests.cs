using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using pc_receiver;

namespace pc_receiver.Tests;

public sealed class WeTypeBridgeSessionTests
{
    [TestCase("local", RecognitionModes.Local)]
    [TestCase("ONLINE", RecognitionModes.Online)]
    [TestCase("wetype", RecognitionModes.WeType)]
    [TestCase("", RecognitionModes.Local)]
    [TestCase("legacy-value", RecognitionModes.Local)]
    public void Recognition_mode_normalizes_legacy_settings(string value, string expected)
    {
        Assert.That(RecognitionModes.Normalize(value), Is.EqualTo(expected));
    }

    [Test]
    public void WeType_hotkey_uses_the_native_x64_input_structure_size()
    {
        Assert.That(WeTypeHotkeyService.NativeInputSize, Is.EqualTo(40));
    }

    [Test]
    public void Bridge_hotkey_round_trips_a_custom_simultaneous_combination()
    {
        var hotkey = BridgeHotkeyDefinition.Parse("Ctrl+Shift+Space");

        Assert.That(hotkey.IsBound, Is.True);
        Assert.That(hotkey.SerializedValue, Is.EqualTo("Ctrl+Shift+Space"));
        Assert.That(hotkey.DisplayName, Is.EqualTo("Ctrl + Shift + Space"));
        Assert.That(hotkey.VirtualKeys, Is.EqualTo(new ushort[] { 0x11, 0x10, 0x20 }));
        Assert.That(hotkey.ReleaseVirtualKeys, Is.EqualTo(new ushort[] { 0x20, 0x10, 0x11 }));
    }

    [Test]
    public void Bridge_hotkey_accepts_a_single_supported_key()
    {
        var created = BridgeHotkeyDefinition.TryCreate(["F8"], out var hotkey);

        Assert.That(created, Is.True);
        Assert.That(hotkey.SerializedValue, Is.EqualTo("F8"));
        Assert.That(hotkey.VirtualKeys, Is.EqualTo(new ushort[] { 0x77 }));
    }

    [TestCase(true, "F8")]
    [TestCase(false, "")]
    public void Bridge_hotkey_enabled_setting_controls_the_session_hotkey(
        bool enabled,
        string expected)
    {
        var configured = BridgeHotkeyDefinition.Parse("F8");

        var sessionHotkey = configured.ForSession(enabled);

        Assert.That(sessionHotkey.SerializedValue, Is.EqualTo(expected));
    }

    [Test]
    public void Bridge_hotkey_can_be_explicitly_unbound()
    {
        var hotkey = BridgeHotkeyDefinition.Parse(string.Empty);

        Assert.That(hotkey.IsBound, Is.False);
        Assert.That(hotkey.SerializedValue, Is.Empty);
        Assert.That(hotkey.DisplayName, Is.EqualTo("未绑定"));
        Assert.That(hotkey.VirtualKeys, Is.Empty);
        Assert.That(BridgeHotkeyDefinition.Parse(null).SerializedValue, Is.EqualTo("Ctrl+Win"));
    }

    [Test]
    public void Unbound_hotkey_press_and_release_are_safe_no_ops()
    {
        using var service = new WeTypeHotkeyService();
        service.SetHotkey(BridgeHotkeyDefinition.Unbound);

        Assert.DoesNotThrow(service.Press);
        Assert.DoesNotThrow(service.Release);
    }

    [Test]
    public void Native_pressed_keys_override_a_misreported_window_key()
    {
        var tokens = BridgeHotkeyCapture.ResolveKeyDownTokens(
            ["Ctrl", "Win"],
            "Shift");

        Assert.That(tokens, Is.EqualTo(new[] { "Ctrl", "Win" }));
    }

    [Test]
    public async Task Start_is_idempotent_and_routes_audio_only_while_active()
    {
        var events = new List<string>();
        var output = new FakeAudioOutput(events);
        var hotkey = new FakeHotkeyController(events);
        using var session = new WeTypeBridgeSession(output, hotkey);

        session.AddAudio([0x01, 0x02]);
        await session.StartAsync();
        await session.StartAsync();
        session.AddAudio([0x03, 0x04]);

        Assert.That(events, Is.EqualTo(new[]
        {
            "clear",
            "press",
            "audio:2",
        }));
        Assert.That(session.IsActive, Is.True);
    }

    [Test]
    public async Task Normal_stop_drains_audio_before_releasing_hotkey()
    {
        var events = new List<string>();
        var output = new FakeAudioOutput(events);
        var hotkey = new FakeHotkeyController(events);
        using var session = new WeTypeBridgeSession(output, hotkey);

        await session.StartAsync();
        await session.StopAsync();
        await session.StopAsync();
        session.AddAudio([0x01]);

        Assert.That(events, Is.EqualTo(new[]
        {
            "clear",
            "press",
            "drain",
            "release",
            "clear",
        }));
        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public async Task Abort_releases_immediately_without_draining()
    {
        var events = new List<string>();
        var output = new FakeAudioOutput(events);
        var hotkey = new FakeHotkeyController(events);
        using var session = new WeTypeBridgeSession(output, hotkey);

        await session.StartAsync();
        session.Abort();

        Assert.That(events, Is.EqualTo(new[]
        {
            "clear",
            "press",
            "release",
            "clear",
        }));
        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void Start_failure_releases_hotkey_and_clears_audio()
    {
        var events = new List<string>();
        var output = new FakeAudioOutput(events);
        var hotkey = new FakeHotkeyController(events) { ThrowOnPress = true };
        using var session = new WeTypeBridgeSession(output, hotkey);

        Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.That(events, Is.EqualTo(new[]
        {
            "clear",
            "press",
            "release",
            "clear",
        }));
        Assert.That(session.IsActive, Is.False);
    }

    private sealed class FakeAudioOutput(List<string> events) : IWeTypeAudioOutput
    {
        public void ClearBuffer()
        {
            events.Add("clear");
        }

        public void AddSamples(byte[] bytes)
        {
            events.Add($"audio:{bytes.Length}");
        }

        public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            events.Add("drain");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHotkeyController(List<string> events) : IWeTypeHotkeyController
    {
        public bool ThrowOnPress { get; init; }

        public void Press()
        {
            events.Add("press");
            if (ThrowOnPress)
            {
                throw new InvalidOperationException("press failed");
            }
        }

        public void Release()
        {
            events.Add("release");
        }
    }
}
