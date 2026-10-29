using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrabEngine.Core;

namespace GrabEngine.Formats;

/// <summary>
/// GRAB ENGINE project format (.grabproj). A project keeps the whole level plus every
/// C# script, the level metadata and the ambience setup so the editor can be closed and
/// reopened without losing work. Exporting writes the real GRAB .level protobuf.
/// </summary>
public static class ProjectIO
{
    public const string Extension = ".grabproj";
    public const string Filter = "GRAB ENGINE Project (*.grabproj)|*.grabproj|All files (*.*)|*.*";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(GrabLevel level, string path)
    {
        level.RebuildParents();
        File.WriteAllText(path, Serialize(level));
        level.FilePath = path;
        level.IsDirty = false;
    }

    public static GrabLevel Load(string path)
    {
        var level = Deserialize(File.ReadAllText(path));
        level.FilePath = path;
        level.IsDirty = false;
        return level;
    }

    public static string Serialize(GrabLevel level)
    {
        level.RebuildParents();
        return JsonSerializer.Serialize(level, Options);
    }

    public static GrabLevel Deserialize(string json)
    {
        var level = JsonSerializer.Deserialize<GrabLevel>(json, Options) ?? GrabLevel.CreateDefault();
        if (level.Nodes.Count == 0) level.Nodes.AddRange(GrabLevel.CreateDefault().Nodes);
        level.RebuildParents();
        level.EnsureLayers();
        level.EnsureNames();
        return level;
    }

    public static GrabLevel Clone(GrabLevel level) => Deserialize(Serialize(level));
}
