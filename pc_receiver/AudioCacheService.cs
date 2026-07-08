using System;
using System.Collections.Generic;
using System.IO;

namespace pc_receiver;

public static class AudioCacheService
{
    public static string CacheDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "MobileToPcInput",
        "audio-cache");

    public static string CreateWavPath()
    {
        Directory.CreateDirectory(CacheDirectory);
        return Path.Combine(CacheDirectory, $"asr-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
    }

    public static int Clear()
    {
        if (!Directory.Exists(CacheDirectory))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in EnumerateCacheFiles())
        {
            if (TryDelete(path))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> EnumerateCacheFiles()
    {
        if (Directory.Exists(CacheDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(CacheDirectory, "*.wav"))
            {
                yield return path;
            }
        }

        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "MobileToPcInput-*.wav"))
        {
            yield return path;
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to delete audio cache file: {path}", ex);
        }

        return false;
    }
}
