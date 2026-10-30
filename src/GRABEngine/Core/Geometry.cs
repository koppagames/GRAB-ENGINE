using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace GrabEngine.Core;

public struct Vec3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vec3() { }
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One = new(1, 1, 1);
    public static readonly Vec3 Up = new(0, 1, 0);
    public static readonly Vec3 Down = new(0, -1, 0);
    public static readonly Vec3 Left = new(-1, 0, 0);
    public static readonly Vec3 Right = new(1, 0, 0);
    public static readonly Vec3 Forward = new(0, 0, 1);
    public static readonly Vec3 Back = new(0, 0, -1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(float s, Vec3 a) => a * s;
    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vec3 operator /(Vec3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3 Normalized => Length < 1e-8f ? Zero : this / Length;

    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t);

    public float this[int i]
    {
        get => i switch { 0 => X, 1 => Y, 2 => Z, _ => 0 };
        set { if (i == 0) X = value; else if (i == 1) Y = value; else if (i == 2) Z = value; }
    }

    public override string ToString() => FormattableString.Invariant($"{X:0.###}, {Y:0.###}, {Z:0.###}");
}

public struct Quat
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }

    public Quat() { W = 1; }
    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static readonly Quat Identity = new(0, 0, 0, 1);

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public Vec3 Rotate(Vec3 v)
    {
        // q * v * q^-1
        float x2 = X + X, y2 = Y + Y, z2 = Z + Z;
        float xx = X * x2, xy = X * y2, xz = X * z2;
        float yy = Y * y2, yz = Y * z2, zz = Z * z2;
        float wx = W * x2, wy = W * y2, wz = W * z2;
        return new Vec3(
            (1 - (yy + zz)) * v.X + (xy - wz) * v.Y + (xz + wy) * v.Z,
            (xy + wz) * v.X + (1 - (xx + zz)) * v.Y + (yz - wx) * v.Z,
            (xz - wy) * v.X + (yz + wx) * v.Y + (1 - (xx + yy)) * v.Z);
    }

    public static Quat FromAxisAngle(Vec3 axis, float degrees)
    {
        var a = axis.Normalized;
        float r = degrees * MathF.PI / 360f;
        float s = MathF.Sin(r);
        return new Quat(a.X * s, a.Y * s, a.Z * s, MathF.Cos(r));
    }

    /// <summary>GRAB stores quaternions; the editor exposes Euler degrees (YXZ order).</summary>
    public static Quat FromEulerDegrees(Vec3 e)
    {
        float rx = e.X * MathF.PI / 180f;
        float ry = e.Y * MathF.PI / 180f;
        float rz = e.Z * MathF.PI / 180f;
        var qx = new Quat(MathF.Sin(rx / 2), 0, 0, MathF.Cos(rx / 2));
        var qy = new Quat(0, MathF.Sin(ry / 2), 0, MathF.Cos(ry / 2));
        var qz = new Quat(0, 0, MathF.Sin(rz / 2), MathF.Cos(rz / 2));
        return qy * qx * qz;
    }

    public Vec3 ToEulerDegrees()
    {
        // YXZ euler extraction
        float sinX = 2f * (W * X + Y * Z);
        float cosX = 1f - 2f * (X * X + Y * Y);
        float ex = MathF.Atan2(sinX, cosX);
        float sinY = 2f * (W * Y - Z * X);
        sinY = Math.Clamp(sinY, -1f, 1f);
        float ey = MathF.Asin(sinY);
        float sinZ = 2f * (W * Z + X * Y);
        float cosZ = 1f - 2f * (Y * Y + Z * Z);
        float ez = MathF.Atan2(sinZ, cosZ);
        return new Vec3(ex * 180f / MathF.PI, ey * 180f / MathF.PI, ez * 180f / MathF.PI);
    }

    public Quat Inverse() => new(-X, -Y, -Z, W);

    public static Quat Lerp(Quat a, Quat b, float t)
    {
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
        if (dot > 0.9995f) return new Quat(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        float theta = MathF.Acos(dot) * t;
        float sinTheta = MathF.Sin(theta);
        float wa = MathF.Cos(theta) - dot * sinTheta / MathF.Sin(MathF.Acos(dot));
        float wb = sinTheta / MathF.Sin(MathF.Acos(dot));
        return new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
    }

    public override string ToString() => FormattableString.Invariant($"{X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###}");
}

public struct Col
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }

    public Col() { R = G = B = A = 1; }
    public Col(float r, float g, float b, float a = 1) { R = r; G = g; B = b; A = a; }

    public static readonly Col White = new(1, 1, 1, 1);
    public static readonly Col Black = new(0, 0, 0, 1);
    public static readonly Col Gray = new(0.72f, 0.72f, 0.72f, 1);
    public static readonly Col Clear = new(0, 0, 0, 0);

    public static Col FromBytes(int r, int g, int b, int a = 255) =>
        new(Math.Clamp(r, 0, 255) / 255f, Math.Clamp(g, 0, 255) / 255f, Math.Clamp(b, 0, 255) / 255f, Math.Clamp(a, 0, 255) / 255f);

    public int Ri => (int)MathF.Round(Math.Clamp(R, 0, 1) * 255);
    public int Gi => (int)MathF.Round(Math.Clamp(G, 0, 1) * 255);
    public int Bi => (int)MathF.Round(Math.Clamp(B, 0, 1) * 255);
    public int Ai => (int)MathF.Round(Math.Clamp(A, 0, 1) * 255);

    public string Hex => $"#{Ri:X2}{Gi:X2}{Bi:X2}";

    /// <summary>#RRGGBB for opaque colours, #RRGGBBAA when the alpha is not 255.</summary>
    public string HexAlpha => Ai == 255 ? Hex : Hex + Ai.ToString("X2");

    public static Col FromHex(string hex, Col fallback = default)
    {
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 || hex.Length == 8)
            {
                int r = int.Parse(hex[..2], NumberStyles.HexNumber);
                int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                int a = hex.Length == 8 ? int.Parse(hex.Substring(6, 2), NumberStyles.HexNumber) : 255;
                return FromBytes(r, g, b, a);
            }
        }
        catch { }
        if (fallback.R == 0 && fallback.G == 0 && fallback.B == 0 && fallback.A == 0) return White;
        return fallback;
    }

    public Col WithAlpha(float a) => new(R, G, B, a);
    public override string ToString() => Hex;
}
