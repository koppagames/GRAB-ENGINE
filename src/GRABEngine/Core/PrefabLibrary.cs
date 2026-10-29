using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GrabEngine.Formats;

namespace GrabEngine.Core;

public sealed class PrefabInfo
{
    public string Name { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int NodeCount { get; init; }
    public DateTime Modified { get; init; }
    public string VariantOf { get; init; } = "";
    public bool IsVariant => !string.IsNullOrWhiteSpace(VariantOf);
}

/// <summary>One field that differs from the prefab asset.</summary>
public sealed class PrefabOverrideInfo
{
    public LevelNode Node { get; init; } = null!;
    public string NodeKey { get; init; } = "";
    public string Property { get; init; } = "";
    public string Current { get; init; } = "";
    public string BaseValue { get; init; } = "";
}

/// <summary>
/// User prefabs are stored as small .grabproj files in the GRAB ENGINE data folder.
/// Keeping the normal project serializer means prefabs preserve every property the
/// editor understands (materials, scripts, animation, triggers) without a second
/// format to keep in sync.
/// </summary>
public static class PrefabLibrary
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GRAB ENGINE", "Prefabs");

    public static void EnsureRoot()
    {
        try { Directory.CreateDirectory(Root); } catch { }
    }

    public static string Save(IReadOnlyList<LevelNode> roots, string name, string variantOf = "")
    {
        EnsureRoot();
        name = string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
        var nodes = roots.Where(n => n.Parent == null || !roots.Contains(n.Parent)).ToList();
        if (nodes.Count == 0) throw new InvalidOperationException("Select at least one object first.");

        // Prefab pivot is the centre of the selection. Objects store positions relative
        // to it so spawning a prefab at a different point preserves the arrangement.
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (var n in nodes)
        {
            var (c, s) = Rendering.SceneBuilder.BoundsOf(n);
            var h = new Vec3(Math.Abs(s.X) * 0.5f, Math.Abs(s.Y) * 0.5f, Math.Abs(s.Z) * 0.5f);
            min = new Vec3(MathF.Min(min.X, c.X - h.X), MathF.Min(min.Y, c.Y - h.Y), MathF.Min(min.Z, c.Z - h.Z));
            max = new Vec3(MathF.Max(max.X, c.X + h.X), MathF.Max(max.Y, c.Y + h.Y), MathF.Max(max.Z, c.Z + h.Z));
        }
        var pivot = new Vec3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);

        var document = new GrabLevel
        {
            Title = name,
            Creators = "GRAB ENGINE Prefab",
            Description = "Prefab created by GRAB ENGINE",
            PrefabVariantOf = variantOf ?? ""
        };
        foreach (var root in nodes)
        {
            var copy = root.Clone();
            copy.Position = root.WorldPosition - pivot;
            copy.PrefabSource = "";
            copy.PrefabInstanceId = "";
            copy.PrefabNodeKey = "";
            document.Nodes.Add(copy);
        }
        document.RebuildParents();
        document.EnsureNames();

        string path = Path.Combine(Root, SafeFileName(name) + ProjectIO.Extension);
        int suffix = 2;
        while (File.Exists(path) && !SamePrefabName(path, name))
            path = Path.Combine(Root, SafeFileName(name) + " " + suffix++ + ProjectIO.Extension);
        ProjectIO.Save(document, path);
        return path;
    }

    private static bool SamePrefabName(string path, string name)
        => string.Equals(Path.GetFileNameWithoutExtension(path), SafeFileName(name), StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Prefab" : result;
    }

    public static IReadOnlyList<PrefabInfo> List()
    {
        EnsureRoot();
        var result = new List<PrefabInfo>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Root, "*" + ProjectIO.Extension); }
        catch { return result; }
        foreach (var file in files)
        {
            try
            {
                var level = ProjectIO.Load(file);
                result.Add(new PrefabInfo
                {
                    Name = level.Title,
                    FilePath = file,
                    NodeCount = level.AllNodes().Count(),
                    Modified = File.GetLastWriteTime(file),
                    VariantOf = level.PrefabVariantOf
                });
            }
            catch
            {
                // A prefab from a newer/older format should not break the whole menu.
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static GrabLevel LoadTemplate(string path)
        => path.EndsWith(".level", StringComparison.OrdinalIgnoreCase)
            ? LevelIO.Load(path)
            : ProjectIO.Load(path);

    /// <summary>
    /// Instantiates a prefab with new editor ids. Trigger references internal to the
    /// prefab are rebound by name so copied code blocks keep pointing at their own
    /// signs, lights and blocks instead of the objects they were copied from.
    /// </summary>
    public static List<LevelNode> Instantiate(string path, GrabLevel level, Vec3 worldPosition, LevelNode? parent = null)
        => Instantiate(path, level, worldPosition, Quat.Identity, parent);

    public static List<LevelNode> Instantiate(string path, GrabLevel level, Vec3 worldPosition, Quat rotation, LevelNode? parent = null)
    {
        var template = LoadTemplate(path);
        var oldNodes = template.AllNodes().ToList();
        var keys = BuildKeys(template);
        var roots = template.Nodes.Select(n => n.Clone()).ToList();
        string instanceId = Guid.NewGuid().ToString("N");
        foreach (var root in roots)
        {
            root.Position = worldPosition + rotation.Rotate(root.Position);
            root.Rotation = rotation * root.Rotation;
            level.AddNode(root, parent);
        }
        level.EnsureNames();

        var newNodes = roots.SelectMany(r => r.SelfAndDescendants()).ToList();
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int count = Math.Min(oldNodes.Count, newNodes.Count);
        for (int i = 0; i < count; i++) rename[oldNodes[i].Name] = newNodes[i].Name;

        for (int i = 0; i < newNodes.Count; i++)
        {
            var node = newNodes[i];
            node.PrefabSource = path;
            node.PrefabInstanceId = instanceId;
            node.PrefabNodeKey = i < oldNodes.Count && keys.TryGetValue(oldNodes[i], out var key)
                ? key
                : i.ToString("D3");
            foreach (var target in node.TriggerTargets)
            {
                if (target.ObjectName != null && rename.TryGetValue(target.ObjectName, out var mapped))
                {
                    target.ObjectName = mapped;
                    target.ObjectId = 0;
                }
            }
        }
        level.IsDirty = true;
        return roots;
    }

    /// <summary>Stable pre-order key for every node inside a prefab document.</summary>
    private static Dictionary<LevelNode, string> BuildKeys(GrabLevel template)
    {
        var keys = new Dictionary<LevelNode, string>();
        void Walk(LevelNode node, string key)
        {
            keys[node] = key;
            for (int i = 0; i < node.Children.Count; i++)
                Walk(node.Children[i], key + "/" + i.ToString("D2"));
        }
        for (int i = 0; i < template.Nodes.Count; i++)
            Walk(template.Nodes[i], i.ToString("D3"));
        return keys;
    }

    /// <summary>All nodes that belong to one prefab instance, keyed by their node key.</summary>
    public static Dictionary<string, LevelNode> InstanceNodes(GrabLevel level, LevelNode anyInstanceNode)
    {
        if (string.IsNullOrWhiteSpace(anyInstanceNode.PrefabInstanceId)) return new();
        return level.AllNodes()
            .Where(n => n.PrefabInstanceId == anyInstanceNode.PrefabInstanceId && !string.IsNullOrWhiteSpace(n.PrefabNodeKey))
            .GroupBy(n => n.PrefabNodeKey)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>The template node corresponding to a prefab instance node.</summary>
    public static LevelNode? TemplateNode(string prefabPath, string key)
    {
        try
        {
            var template = LoadTemplate(prefabPath);
            var keys = BuildKeys(template);
            return keys.FirstOrDefault(kv => kv.Value == key).Key;
        }
        catch { return null; }
    }

    private static readonly string[] ComparedProperties =
    {
        "Position", "Rotation", "Scale", "Material", "Color1", "Color2", "Name", "Text",
        "SignScale", "LightColor", "LightIntensity", "LightRange", "SoundVolume",
        "ParticlesPerSecond", "Shape", "StartActive"
    };

    public static List<PrefabOverrideInfo> GetOverrides(GrabLevel level, LevelNode instanceRoot)
    {
        var result = new List<PrefabOverrideInfo>();
        if (string.IsNullOrWhiteSpace(instanceRoot.PrefabSource)) return result;
        var instance = InstanceNodes(level, instanceRoot);
        foreach (var kv in instance)
        {
            var template = TemplateNode(instanceRoot.PrefabSource, kv.Key);
            if (template == null) continue;
            var node = kv.Value;
            foreach (var property in ComparedProperties)
            {
                var (current, baseValue) = ReadProperty(node, template, property, instanceRoot);
                if (current != baseValue)
                    result.Add(new PrefabOverrideInfo { Node = node, NodeKey = kv.Key, Property = property, Current = current, BaseValue = baseValue });
            }
        }
        return result;
    }

    private static (string Current, string BaseValue) ReadProperty(LevelNode node, LevelNode template, string property, LevelNode root)
    {
        // Instance transforms are relative to the instance root so moving the whole
        // prefab does not count as an override on every child.
        Vec3 local(LevelNode n)
        {
            var offset = n.WorldPosition - root.WorldPosition;
            return new Vec3((float)Math.Round(offset.X, 3), (float)Math.Round(offset.Y, 3), (float)Math.Round(offset.Z, 3));
        }
        return property switch
        {
            "Position" => (local(node).ToString(), template.Position.ToString()),
            "Rotation" => (RoundEuler(node.WorldRotation.ToEulerDegrees()).ToString(), template.WorldRotation.ToEulerDegrees().ToString()),
            "Scale" => (Round(node.WorldScale).ToString(), template.WorldScale.ToString()),
            "Material" => (node.Material.ToString(), template.Material.ToString()),
            "Color1" => (node.Color1.Hex, template.Color1.Hex),
            "Color2" => (node.Color2.Hex, template.Color2.Hex),
            "Name" => (node.Name, template.Name),
            "Text" => (node.Text ?? "", template.Text ?? ""),
            "SignScale" => (node.SignScale.ToString("0.###"), template.SignScale.ToString("0.###")),
            "LightColor" => (node.LightColor.Hex, template.LightColor.Hex),
            "LightIntensity" => (node.LightIntensity.ToString("0.###"), template.LightIntensity.ToString("0.###")),
            "LightRange" => (node.LightRange.ToString("0.###"), template.LightRange.ToString("0.###")),
            "SoundVolume" => (node.SoundVolume.ToString("0.###"), template.SoundVolume.ToString("0.###")),
            "ParticlesPerSecond" => (node.ParticlesPerSecond.ToString(), template.ParticlesPerSecond.ToString()),
            "Shape" => (node.Shape.ToString(), template.Shape.ToString()),
            "StartActive" => (node.StartActive.ToString(), template.StartActive.ToString()),
            _ => ("", "")
        };
    }

    private static Vec3 Round(Vec3 v) => new((float)Math.Round(v.X, 3), (float)Math.Round(v.Y, 3), (float)Math.Round(v.Z, 3));
    private static Vec3 RoundEuler(Vec3 v) => new((float)Math.Round(v.X, 1), (float)Math.Round(v.Y, 1), (float)Math.Round(v.Z, 1));

    /// <summary>Copies a property from the prefab asset back onto the instance node.</summary>
    public static bool Revert(LevelNode instanceRoot, LevelNode node, string property)
    {
        var template = TemplateNode(instanceRoot.PrefabSource, node.PrefabNodeKey);
        if (template == null) return false;
        switch (property)
        {
            case "Position":
                node.Position = instanceRoot.WorldPosition + template.Position - instanceRoot.Position;
                break;
            case "Rotation": node.Rotation = template.Rotation; break;
            case "Scale": node.Scale = template.Scale; break;
            case "Material": node.Material = template.Material; break;
            case "Color1": node.Color1 = template.Color1; break;
            case "Color2": node.Color2 = template.Color2; break;
            case "Name": node.Name = template.Name; break;
            case "Text": node.Text = template.Text; break;
            case "SignScale": node.SignScale = template.SignScale; break;
            case "LightColor": node.LightColor = template.LightColor; break;
            case "LightIntensity": node.LightIntensity = template.LightIntensity; break;
            case "LightRange": node.LightRange = template.LightRange; break;
            case "SoundVolume": node.SoundVolume = template.SoundVolume; break;
            case "ParticlesPerSecond": node.ParticlesPerSecond = template.ParticlesPerSecond; break;
            case "Shape": node.Shape = template.Shape; break;
            case "StartActive": node.StartActive = template.StartActive; break;
            default: return false;
        }
        return true;
    }

    /// <summary>Applies the current instance values back into the prefab asset.</summary>
    public static void ApplyToPrefab(GrabLevel level, LevelNode instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot.PrefabSource) || !File.Exists(instanceRoot.PrefabSource)) return;
        var document = LoadTemplate(instanceRoot.PrefabSource);
        var keys = BuildKeys(document);
        var instance = InstanceNodes(level, instanceRoot);
        foreach (var kv in instance)
        {
            var target = keys.FirstOrDefault(x => x.Value == kv.Key).Key;
            if (target == null) continue;
            CopyInstanceToTemplate(kv.Value, target, instanceRoot, copyTransform: kv.Value.Parent != null);
        }
        ProjectIO.Save(document, instanceRoot.PrefabSource);
    }

    /// <summary>Pushes this instance's values onto every other instance of the same prefab.</summary>
    public static int ApplyToAllInstances(GrabLevel level, LevelNode instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot.PrefabSource) || string.IsNullOrWhiteSpace(instanceRoot.PrefabInstanceId)) return 0;
        var source = InstanceNodes(level, instanceRoot);
        var ids = level.AllNodes()
            .Where(n => n.PrefabSource == instanceRoot.PrefabSource && !string.IsNullOrWhiteSpace(n.PrefabInstanceId) && n.PrefabInstanceId != instanceRoot.PrefabInstanceId)
            .Select(n => n.PrefabInstanceId).Distinct().ToList();
        int updated = 0;
        foreach (var id in ids)
        {
            var nodes = level.AllNodes().Where(n => n.PrefabInstanceId == id)
                .GroupBy(n => n.PrefabNodeKey).ToDictionary(g => g.Key, g => g.First());
            foreach (var kv in source)
            {
                if (!nodes.TryGetValue(kv.Key, out var target) || ReferenceEquals(kv.Value, target)) continue;
                // Transforms are instance-specific: only copy authored properties so the
                // other copies keep their own placement.
                CopyInstanceToTemplate(kv.Value, target, instanceRoot, copyTransform: false);
                updated++;
            }
        }
        if (updated > 0) level.IsDirty = true;
        return updated;
    }

    private static void CopyInstanceToTemplate(LevelNode from, LevelNode to, LevelNode sourceRoot, bool copyTransform)
    {
        to.Material = from.Material;
        to.Color1 = from.Color1;
        to.Color2 = from.Color2;
        to.Shape = from.Shape;
        to.Text = from.Text;
        to.SignScale = from.SignScale;
        to.LightColor = from.LightColor;
        to.LightIntensity = from.LightIntensity;
        to.LightRange = from.LightRange;
        to.SoundVolume = from.SoundVolume;
        to.ParticlesPerSecond = from.ParticlesPerSecond;
        to.StartActive = from.StartActive;
        if (!copyTransform) return;
        var local = from.WorldPosition - sourceRoot.WorldPosition;
        to.Position = local;
        to.Rotation = from.Rotation;
        to.Scale = from.Scale;
    }
}
