using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public static class TextInputService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;

    public static Task TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppLogger.Info("TextInput skipped because text is empty.");
            return Task.CompletedTask;
        }

        AppLogger.Info($"TextInput starting. length={text.Length}, preview={Preview(text)}");
        return Task.Run(() =>
        {
            var sentCount = 0;
            foreach (var ch in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sentCount += SendUnicodeChar(ch, up: false) ? 1 : 0;
                sentCount += SendUnicodeChar(ch, up: true) ? 1 : 0;
                Thread.Sleep(1);
            }

            AppLogger.Info($"TextInput finished. chars={text.Length}, sendInputEvents={sentCount}");
        }, cancellationToken);
    }

    private static bool SendUnicodeChar(char ch, bool up)
    {
        var input = new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KeyEventFUnicode | (up ? KeyEventFKeyUp : 0)
                }
            }
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            AppLogger.Info(
                $"TextInput SendInput failed. charCode={(int)ch}, keyUp={up}, cbSize={Marshal.SizeOf<Input>()}, error={Marshal.GetLastWin32Error()}");
        }

        return sent == 1;
    }

    private static string Preview(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 30 ? text : text[..30];
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
