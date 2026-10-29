using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

/// <summary>
/// Samples the animation clips stored in a GRAB .level. The game stores clips as
/// (time, position, rotation) keys with a restart / ping-pong direction and linear
/// or Catmull-Rom interpolation. This class is shared by the animation window, the
/// play-mode runtime and the C# animation API so all three show the same motion.
/// </summary>
public static class AnimationSampler
{
    public const string StopClip = "__stop";

    public static float Duration(GrabAnimation a)
    {
        if (a.Frames.Count == 0) return 0;
        return MathF.Max(0, a.Frames.Max(f => f.Time));
    }

    public static bool IsStopClip(GrabAnimation? a) =>
        a != null && string.Equals(a.Name, StopClip, StringComparison.OrdinalIgnoreCase);

    /// <summary>Total clip length in seconds, taking Direction and Speed into account.</summary>
    public static float PlayLength(GrabAnimation a)
    {
        float d = Duration(a);
        float speed = MathF.Max(0.01f, MathF.Abs(a.Speed));
        return a.Direction == 1 ? d * 2f / speed : d / speed;
    }

    /// <summary>
    /// Returns the clip pose at <paramref name="time"/> seconds. When
    /// <paramref name="loop"/> is false the clip clamps on its last frame.
    /// </summary>
    public static (Vec3 Position, Quat Rotation) Sample(GrabAnimation a, float time, bool loop = true, bool reverse = false)
    {
        var frames = a.Frames;
        if (frames.Count == 0) return (Vec3.Zero, Quat.Identity);
        if (frames.Count == 1) return (frames[0].Position, frames[0].Rotation);

        var ordered = frames.OrderBy(f => f.Time).ToList();
        float duration = MathF.Max(0f, ordered[^1].Time);
        float speed = MathF.Max(0.01f, MathF.Abs(a.Speed));
        float t = time * speed;
        if (reverse) t = -t;

        if (duration <= 1e-5f)
            return (ordered[^1].Position, ordered[^1].Rotation);

        if (a.Direction == 1)
        {
            // Ping-pong: forward through the clip, then backward.
            float span = duration * 2f;
            t %= span;
            if (t < 0) t += span;
            if (t > duration) t = span - t;
        }
        else if (loop)
        {
            t %= duration;
            if (t < 0) t += duration;
        }
        else
        {
            t = Math.Clamp(t, 0f, duration);
        }

        int i = 0;
        while (i < ordered.Count - 2 && ordered[i + 1].Time <= t) i++;
        var a0 = ordered[i];
        var a1 = ordered[Math.Min(i + 1, ordered.Count - 1)];
        float seg = a1.Time - a0.Time;
        float u = seg <= 1e-6f ? 0f : Math.Clamp((t - a0.Time) / seg, 0f, 1f);

        if (a.Interpolation == 1 && ordered.Count > 2)
        {
            var p0 = ordered[Math.Max(0, i - 1)];
            var p2 = ordered[Math.Min(ordered.Count - 1, i + 2)];
            var pos = CatmullRom(p0.Position, a0.Position, a1.Position, p2.Position, u);
            var rot = Quat.Lerp(a0.Rotation, a1.Rotation, Smooth(u));
            return (pos, rot);
        }

        return (Vec3.Lerp(a0.Position, a1.Position, u), Quat.Lerp(a0.Rotation, a1.Rotation, u));
    }

    private static float Smooth(float u) => u * u * (3f - 2f * u);

    private static Vec3 CatmullRom(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        float Axis(float a, float b, float c, float d) =>
            0.5f * (2f * b + (-a + c) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (-a + 3f * b - 3f * c + d) * t3);
        return new Vec3(
            Axis(p0.X, p1.X, p2.X, p3.X),
            Axis(p0.Y, p1.Y, p2.Y, p3.Y),
            Axis(p0.Z, p1.Z, p2.Z, p3.Z));
    }

    /// <summary>Mirrors a clip in time so it plays backwards, keeping its duration.</summary>
    public static GrabAnimation Reversed(GrabAnimation source, string? name = null)
    {
        float duration = Duration(source);
        return new GrabAnimation
        {
            Name = name ?? source.Name + " Reverse",
            Direction = source.Direction,
            Speed = source.Speed,
            Interpolation = source.Interpolation,
            Frames = source.Frames
                .Select(f => new GrabAnimationFrame
                {
                    Time = MathF.Max(0, duration - f.Time),
                    Position = f.Position,
                    Rotation = f.Rotation
                })
                .OrderBy(f => f.Time)
                .ToList()
        };
    }

    /// <summary>
    /// Sorts frames and removes exact duplicate timestamps without moving any key.
    /// Used by Release export because it cannot change clip timing or gameplay.
    /// Returns true when the key list actually changed.
    /// </summary>
    public static bool SortAndDeduplicate(GrabAnimation a)
    {
        if (a.Frames.Count == 0) return false;
        var original = a.Frames.ToList();
        var ordered = original.OrderBy(f => f.Time).ToList();
        var result = new List<GrabAnimationFrame>();
        foreach (var f in ordered)
        {
            if (result.Count > 0 && MathF.Abs(result[^1].Time - f.Time) < 1e-4f)
                result[^1] = f; // last key at this time wins, exactly like sampling did
            else
                result.Add(f);
        }
        bool changed = result.Count != original.Count;
        if (!changed)
            for (int i = 0; i < result.Count; i++)
                if (!ReferenceEquals(result[i], original[i])) { changed = true; break; }
        a.Frames = result;
        return changed;
    }

    /// <summary>
    /// Sorts frames, removes duplicate timestamps and moves the first key to time 0.
    /// This is an explicit authoring tool in the Animation window; export uses
    /// <see cref="SortAndDeduplicate"/> so it never shifts a clip.
    /// </summary>
    public static void Normalize(GrabAnimation a)
    {
        if (a.Frames.Count == 0) return;
        SortAndDeduplicate(a);
        float start = a.Frames[0].Time;
        if (start > 0)
            foreach (var f in a.Frames) f.Time -= start;
    }

    /// <summary>Convenience helper used by the inspector and animation window.</summary>
    public static void ApplyPose(LevelNode node, Vec3 position, Quat rotation)
    {
        if (node.Kind != NodeKind.Finish) node.Position = position;
        node.Rotation = rotation;
    }
}
