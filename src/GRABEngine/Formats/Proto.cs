using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GrabEngine.Formats;

/// <summary>Minimal protobuf writer matching the on-disk GRAB level format.</summary>
public sealed class PbWriter
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    private void Raw(byte b) => _ms.WriteByte(b);

    public void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            Raw((byte)(value | 0x80));
            value >>= 7;
        }
        Raw((byte)value);
    }

    private void Tag(int field, int wire) => WriteVarint(((ulong)(uint)field << 3) | (uint)wire);

    public void Varint(int field, ulong value)
    {
        Tag(field, 0);
        WriteVarint(value);
    }

    public void Bool(int field, bool value)
    {
        if (!value) return;
        Varint(field, 1);
    }

    public void Int32(int field, int value)
    {
        Tag(field, 0);
        // Negative int32 values are sign-extended to 64 bits (10 bytes), exactly like protobuf.
        WriteVarint(unchecked((ulong)(long)value));
    }

    public void Float(int field, float value)
    {
        Tag(field, 5);
        _ms.Write(BitConverter.GetBytes(value), 0, 4);
    }

    public void Double(int field, double value)
    {
        Tag(field, 1);
        _ms.Write(BitConverter.GetBytes(value), 0, 8);
    }

    public void String(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Bytes(field, Encoding.UTF8.GetBytes(value));
    }

    public void Bytes(int field, byte[] bytes)
    {
        Tag(field, 2);
        WriteVarint((ulong)bytes.Length);
        _ms.Write(bytes, 0, bytes.Length);
    }

    public void Message(int field, Action<PbWriter> write)
    {
        var child = new PbWriter();
        write(child);
        Bytes(field, child.ToArray());
    }

    public void MessageIf(int field, bool condition, Action<PbWriter> write)
    {
        if (condition) Message(field, write);
    }

    public void Vec3(int field, Core.Vec3 v, bool writeZero = false)
    {
        if (!writeZero && v.X == 0 && v.Y == 0 && v.Z == 0) return;
        Message(field, m =>
        {
            m.Float(1, v.X);
            m.Float(2, v.Y);
            m.Float(3, v.Z);
        });
    }

    public void Vec2(int field, float x, float y)
    {
        Message(field, m => { m.Float(1, x); m.Float(2, y); });
    }

    public void Quat(int field, Core.Quat q)
    {
        Message(field, m =>
        {
            m.Float(1, q.X);
            m.Float(2, q.Y);
            m.Float(3, q.Z);
            m.Float(4, q.W);
        });
    }

    public void Color(int field, Core.Col c)
    {
        Message(field, m =>
        {
            m.Float(1, c.R);
            m.Float(2, c.G);
            m.Float(3, c.B);
            m.Float(4, c.A);
        });
    }

    public void Enum(int field, int value)
    {
        if (value == 0) return;
        Varint(field, (ulong)value);
    }
}

public sealed class PbField
{
    public int Number { get; init; }
    public int WireType { get; init; }
    public ulong Varint { get; init; }
    public float Float { get; init; }
    public double Double { get; init; }
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    public string AsString() => Encoding.UTF8.GetString(Bytes);
    public bool AsBool() => Varint != 0;
    public int AsInt() => (int)Varint;
}

/// <summary>Minimal protobuf reader (enough for all GRAB level messages).</summary>
public static class PbReader
{
    public static List<PbField> Read(byte[] data)
    {
        var list = new List<PbField>();
        int i = 0;
        while (i < data.Length)
        {
            if (!TryVarint(data, ref i, out var key)) break;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field == 0) break;
            switch (wire)
            {
                case 0:
                    if (!TryVarint(data, ref i, out var v)) return list;
                    list.Add(new PbField { Number = field, WireType = 0, Varint = v });
                    break;
                case 1:
                    if (i + 8 > data.Length) return list;
                    list.Add(new PbField { Number = field, WireType = 1, Double = BitConverter.ToDouble(data, i) });
                    i += 8;
                    break;
                case 2:
                    if (!TryVarint(data, ref i, out var len)) return list;
                    if (i + (int)len > data.Length) return list;
                    var bytes = new byte[len];
                    Array.Copy(data, i, bytes, 0, (int)len);
                    i += (int)len;
                    list.Add(new PbField { Number = field, WireType = 2, Bytes = bytes });
                    break;
                case 5:
                    if (i + 4 > data.Length) return list;
                    list.Add(new PbField { Number = field, WireType = 5, Float = BitConverter.ToSingle(data, i) });
                    i += 4;
                    break;
                default:
                    return list;
            }
        }
        return list;
    }

    private static bool TryVarint(byte[] data, ref int i, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (i < data.Length && shift < 70)
        {
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    public static List<PbField> Message(PbField f) => Read(f.Bytes);

    public static IEnumerable<PbField> All(IEnumerable<PbField> fields, int number)
    {
        foreach (var f in fields) if (f.Number == number) yield return f;
    }

    public static PbField? First(IEnumerable<PbField> fields, int number)
    {
        foreach (var f in fields) if (f.Number == number) return f;
        return null;
    }

    public static ulong Varint(IEnumerable<PbField> fields, int number, ulong fallback = 0)
        => First(fields, number)?.Varint ?? fallback;

    public static int Int(IEnumerable<PbField> fields, int number, int fallback = 0)
        => (int)(First(fields, number)?.Varint ?? (ulong)fallback);

    public static bool Bool(IEnumerable<PbField> fields, int number, bool fallback = false)
        => First(fields, number) is { } f ? f.AsBool() : fallback;

    public static float Float(IEnumerable<PbField> fields, int number, float fallback = 0)
        => First(fields, number)?.Float ?? fallback;

    public static string String(IEnumerable<PbField> fields, int number, string fallback = "")
        => First(fields, number)?.AsString() ?? fallback;

    public static Core.Vec3 Vec3(IEnumerable<PbField> fields, int number, Core.Vec3? fallback = null)
    {
        var f = First(fields, number);
        if (f == null) return fallback ?? Core.Vec3.Zero;
        var m = Message(f);
        return new Core.Vec3(Float(m, 1), Float(m, 2), Float(m, 3));
    }

    public static Core.Quat Quat(IEnumerable<PbField> fields, int number)
    {
        var f = First(fields, number);
        if (f == null) return Core.Quat.Identity;
        var m = Message(f);
        return new Core.Quat(Float(m, 1), Float(m, 2), Float(m, 3), Float(m, 4, 1));
    }

    public static Core.Col Col(IEnumerable<PbField> fields, int number, Core.Col? fallback = null)
    {
        var f = First(fields, number);
        if (f == null) return fallback ?? Core.Col.White;
        var m = Message(f);
        return new Core.Col(Float(m, 1), Float(m, 2), Float(m, 3), Float(m, 4, 1));
    }

    public static float[] Floats(byte[] data)
    {
        var result = new float[data.Length / 4];
        for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToSingle(data, i * 4);
        return result;
    }
}
