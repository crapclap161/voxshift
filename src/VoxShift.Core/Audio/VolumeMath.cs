namespace VoxShift.Core.Audio;

/// <summary>Helpers for converting between UI volume values, linear gain and decibels.</summary>
public static class VolumeMath
{
    public const int MinPercent = 0;
    public const int MaxPercent = 200;

    /// <summary>Clamps a slider percentage to the supported 0–200 % range.</summary>
    public static int ClampPercent(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>
    /// Converts a slider percentage to linear gain. The mapping is linear (100 % = unity,
    /// 200 % = +6 dB) so that the label shown to the user is exactly the amplitude factor applied.
    /// </summary>
    public static float PercentToGain(double percent) => (float)(Math.Clamp(percent, MinPercent, MaxPercent) / 100.0);

    /// <summary>Converts a linear amplitude to dBFS (floored at <paramref name="floorDb"/>).</summary>
    public static double ToDecibels(double amplitude, double floorDb = -96)
    {
        if (amplitude <= 0) return floorDb;
        return Math.Max(floorDb, 20.0 * Math.Log10(amplitude));
    }

    /// <summary>Maps an amplitude to a 0..1 meter position on a dB scale between <paramref name="rangeDb"/> and 0 dBFS.</summary>
    public static double ToMeterPosition(double amplitude, double rangeDb = 60)
    {
        double db = ToDecibels(amplitude, -rangeDb);
        return Math.Clamp((db + rangeDb) / rangeDb, 0, 1);
    }
}
