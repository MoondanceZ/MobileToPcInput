using System;
using System.Diagnostics;
using System.IO;

namespace pc_receiver;

public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MobileToPcInput",
        "pc_receiver.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception ex)
    {
        Write("ERROR", $"{message}: {ex}");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] MobileToPcInput {message}";
        Debug.WriteLine(line);

        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{line}{Environment.NewLine}");
        }
    }
}
