using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pc_receiver;

public sealed class WindowsHotkeyCaptureService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const ushort VkEscape = 0x1B;

    private readonly LowLevelKeyboardProc _hookCallback;
    private readonly HashSet<ushort> _pressedKeys = [];
    private Action<HotkeyCaptureEvent>? _eventHandler;
    private nint _hookHandle;

    public WindowsHotkeyCaptureService()
    {
        _hookCallback = HandleKeyboardMessage;
    }

    public bool IsActive => _hookHandle != 0;

    public void Start(Action<HotkeyCaptureEvent> eventHandler)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        Stop();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("快捷键录入仅支持 Windows");
        }

        _eventHandler = eventHandler;
        _pressedKeys.Clear();
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookCallback, moduleHandle, 0);
        if (_hookHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _eventHandler = null;
            throw new Win32Exception(error, "无法启动快捷键录入");
        }
    }

    public void Stop()
    {
        var hookHandle = _hookHandle;
        _hookHandle = 0;
        _eventHandler = null;
        _pressedKeys.Clear();
        if (hookHandle != 0)
        {
            UnhookWindowsHookEx(hookHandle);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private nint HandleKeyboardMessage(int code, nint message, nint dataPointer)
    {
        if (code < 0 || _hookHandle == 0)
        {
            return CallNextHookEx(_hookHandle, code, message, dataPointer);
        }

        var messageValue = unchecked((int)message);
        var isKeyDown = messageValue is WmKeyDown or WmSysKeyDown;
        var isKeyUp = messageValue is WmKeyUp or WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(_hookHandle, code, message, dataPointer);
        }

        var keyboardData = Marshal.PtrToStructure<LowLevelKeyboardInput>(dataPointer);
        var virtualKey = (ushort)keyboardData.VirtualKey;
        if (isKeyDown)
        {
            _pressedKeys.Add(virtualKey);
        }
        else
        {
            _pressedKeys.Remove(virtualKey);
        }

        var cancelRequested = virtualKey == VkEscape && isKeyDown;
        var token = BridgeHotkeyDefinition.TryGetToken(virtualKey, out var resolvedToken)
            ? resolvedToken
            : null;
        _eventHandler?.Invoke(new HotkeyCaptureEvent(
            token,
            isKeyDown,
            _pressedKeys.Count == 0,
            cancelRequested));

        // Recording a shortcut must not activate an application that already
        // owns it (for example WeType's Alt+Q hold-to-talk shortcut).
        return 1;
    }

    private delegate nint LowLevelKeyboardProc(int code, nint message, nint dataPointer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardInput
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hookHandle,
        int code,
        nint message,
        nint dataPointer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}

public sealed record HotkeyCaptureEvent(
    string? Token,
    bool IsKeyDown,
    bool AllKeysReleased,
    bool CancelRequested);
