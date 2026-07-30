using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace pc_receiver;

public sealed class WeTypeHotkeyService : IWeTypeHotkeyController, IDisposable
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private readonly object _sync = new();
    private BridgeHotkeyDefinition _hotkey = BridgeHotkeyDefinition.Default;
    private BridgeHotkeyDefinition? _pressedHotkey;
    private bool _isPressed;

    internal static int NativeInputSize => Marshal.SizeOf<Input>();

    public BridgeHotkeyDefinition Hotkey
    {
        get
        {
            lock (_sync)
            {
                return _hotkey;
            }
        }
    }

    public void SetHotkey(BridgeHotkeyDefinition hotkey)
    {
        lock (_sync)
        {
            if (_isPressed)
            {
                throw new InvalidOperationException("按住说话期间不能修改快捷键");
            }

            _hotkey = hotkey;
        }
    }

    public void Press()
    {
        lock (_sync)
        {
            if (_isPressed)
            {
                return;
            }

            if (!_hotkey.IsBound)
            {
                AppLogger.Info("Bridge hold-to-talk hotkey is unbound; audio-only session started.");
                return;
            }

            _isPressed = true;
            _pressedHotkey = _hotkey;
            try
            {
                Send(_pressedHotkey.VirtualKeys.Select(
                    virtualKey => CreateKeyInput(virtualKey, keyUp: false)).ToArray());
                AppLogger.Info(
                    $"Bridge hold-to-talk hotkey pressed ({_pressedHotkey.DisplayName}).");
            }
            catch
            {
                try
                {
                    Send(_pressedHotkey.ReleaseVirtualKeys.Select(
                        virtualKey => CreateKeyInput(virtualKey, keyUp: true)).ToArray());
                }
                finally
                {
                    _isPressed = false;
                    _pressedHotkey = null;
                }

                throw;
            }
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            if (!_isPressed)
            {
                return;
            }

            try
            {
                var hotkey = _pressedHotkey ?? _hotkey;
                Send(hotkey.ReleaseVirtualKeys.Select(
                    virtualKey => CreateKeyInput(virtualKey, keyUp: true)).ToArray());
                AppLogger.Info($"Bridge hold-to-talk hotkey released ({hotkey.DisplayName}).");
            }
            finally
            {
                _isPressed = false;
                _pressedHotkey = null;
            }
        }
    }

    public void Dispose()
    {
        Release();
    }

    private static Input CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? KeyEventFKeyUp : 0,
                },
            },
        };
    }

    private static void Send(params Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, NativeInputSize);
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            AppLogger.Info(
                $"WeType hotkey SendInput failed. requested={inputs.Length}, sent={sent}, error={error}");
            throw new Win32Exception(error, "无法触发微信输入法快捷键");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyboardInput ki;
        [FieldOffset(0)] public HardwareInput hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
