using System;

namespace GrabEngine.Rendering;

/// <summary>Small deterministic value-noise helper used for GRAB-style clouds.</summary>
public static class ProceduralNoise
{
    private static readonly int[] Perm = BuildPerm();

    private static int[] BuildPerm()
    {
        var p = new int[512];
        for (int i = 0; i < 256; i++) p[i] = i;
        int seed = 1337;
        for (int i = 255; i > 0; i--)
        {
            seed = unchecked(seed * 1103515245 + 12345);
            int j = (seed & 0x7fffffff) % (i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 256; i++) p[i + 256] = p[i];
        return p;
    }

    private static double Hash(int x, int y, int z) => Perm[(Perm[(Perm[x & 255] + y) & 255] + z) & 255] / 255.0;
    private static double Hash(int x, int y) => Perm[(Perm[x & 255] + y) & 255] / 255.0;
    private static double Fade(double t) => t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Value2(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = Fade(x - xi), yf = Fade(y - yi);
        double a = Lerp(Hash(xi, yi), Hash(xi + 1, yi), xf);
        double b = Lerp(Hash(xi, yi + 1), Hash(xi + 1, yi + 1), xf);
        return Lerp(a, b, yf);
    }

    public static double Value3(double x, double y, double z)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
        double xf = Fade(x - xi), yf = Fade(y - yi), zf = Fade(z - zi);
        double x00 = Lerp(Hash(xi, yi, zi), Hash(xi + 1, yi, zi), xf);
        double x10 = Lerp(Hash(xi, yi + 1, zi), Hash(xi + 1, yi + 1, zi), xf);
        double x01 = Lerp(Hash(xi, yi, zi + 1), Hash(xi + 1, yi, zi + 1), xf);
        double x11 = Lerp(Hash(xi, yi + 1, zi + 1), Hash(xi + 1, yi + 1, zi + 1), xf);
        return Lerp(Lerp(x00, x10, yf), Lerp(x01, x11, yf), zf);
    }

    public static double Fbm2(double x, double y, int octaves)
    {
        double sum = 0, amp = 0.5, freq = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value2(x * freq, y * freq) * amp;
            norm += amp;
            amp *= 0.5;
            freq *= 2.03;
        }
        return sum / Math.Max(norm, 1e-9);
    }

    public static double Fbm3(double x, double y, double z, int octaves)
    {
        double sum = 0, amp = 0.5, freq = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value3(x * freq, y * freq, z * freq) * amp;
            norm += amp;
            amp *= 0.5;
            freq *= 2.03;
        }
        return sum / Math.Max(norm, 1e-9);
    }
}
