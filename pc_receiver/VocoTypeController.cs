using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace pc_receiver;

public static class VocoTypeController
{
    private const ushort VkF2 = 0x71;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    private static bool _isF2Down;

    public static void PressF2()
    {
        var process = FindVocoTypeProcess();
        if (process is null)
        {
            AppLogger.Info("VocoType process was not found for F2 down.");
            return;
        }

        if (_isF2Down)
        {
            AppLogger.Info("VocoType F2 is already down.");
            return;
        }

        var posted = PostMessage(process.MainWindowHandle, WmKeyDown, new IntPtr(VkF2), IntPtr.Zero);
        var sent = SendF2(keyUp: false);
        if (sent || posted)
        {
            _isF2Down = true;
        }

        AppLogger.Info($"VocoType F2 down sent to process {process.Id}. post={posted}, send={sent}");
    }

    public static void ReleaseF2()
    {
        var process = FindVocoTypeProcess();
        if (process is null)
        {
            AppLogger.Info("VocoType process was not found for F2 up.");
            _isF2Down = false;
            return;
        }

        var posted = PostMessage(process.MainWindowHandle, WmKeyUp, new IntPtr(VkF2), IntPtr.Zero);
        var sent = SendF2(keyUp: true);
        _isF2Down = false;
        AppLogger.Info($"VocoType F2 up sent to process {process.Id}. post={posted}, send={sent}");
    }

    public static void ReleaseIfHeld()
    {
        if (_isF2Down)
        {
            ReleaseF2();
        }
    }

    private static Process? FindVocoTypeProcess()
    {
        return Process.GetProcessesByName("VocoType")
            .FirstOrDefault(item => item.MainWindowHandle != IntPtr.Zero);
    }

    private static bool SendF2(bool keyUp)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VkF2,
                    dwFlags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            AppLogger.Info($"SendInput F2 {(keyUp ? "up" : "down")} sent {sent}, error {Marshal.GetLastWin32Error()}.");
        }

        keybd_event((byte)VkF2, 0, keyUp ? KeyEventKeyUp : 0, UIntPtr.Zero);
        return sent == 1;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
