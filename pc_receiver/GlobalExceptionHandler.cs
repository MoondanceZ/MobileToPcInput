using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace pc_receiver;

public static class GlobalExceptionHandler
{
    private const uint MbIconError = 0x00000010;
    private const uint MbTopmost = 0x00040000;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常");
            Handle("程序发生未处理异常", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle("后台任务发生异常", e.Exception);
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Handle("界面发生异常", e.Exception);
            e.Handled = true;
        };
    }

    private static void Handle(string title, Exception exception)
    {
        AppLogger.Error(title, exception);
        ShowError(title, exception);
    }

    private static void ShowError(string title, Exception exception)
    {
        var message = $"{title}{Environment.NewLine}{Environment.NewLine}{exception.Message}";
        try
        {
            MessageBoxW(IntPtr.Zero, message, "MobileToPcInput", MbIconError | MbTopmost);
        }
        catch
        {
            // Last-resort exception handling must never throw again.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
