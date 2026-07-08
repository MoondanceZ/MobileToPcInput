using System;

namespace pc_receiver;

public static class AudioLevelMeter
{
    public static double CalculatePercent(byte[] bytes)
    {
        if (bytes.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(bytes, i) / 32768.0;
            sum += sample * sample;
            samples++;
        }

        if (samples == 0)
        {
            return 0;
        }

        return Math.Min(100, Math.Sqrt(sum / samples) * 400);
    }
}
