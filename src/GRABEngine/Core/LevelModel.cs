using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using GrabEngine.AI;

namespace GrabEngine.Core;

public enum NodeKind
{
    Start = 1, Finish = 2, Static = 3, Sign = 4, Crumbling = 5, Group = 7,
    Gravity = 8, LobbyTerminal = 9, Trigger = 10, ParticleEmitter = 11,
    Sound = 12, GASM = 13, Light = 14
}

public static class NodeKindInfo
{
    public static string Display(NodeKind k) => k switch
    {
        NodeKind.Start => "Start",
        NodeKind.Finish => "Finish",
        NodeKind.Static => "Block",
        NodeKind.Sign => "Sign",
        NodeKind.Crumbling => "Crumbling Block",
        NodeKind.Group => "Group",
        NodeKind.Gravity => "Gravity Block",
        NodeKind.LobbyTerminal => "Lobby Terminal",
        NodeKind.Trigger => "Trigger",
        NodeKind.ParticleEmitter => "Particle Emitter",
        NodeKind.Sound => "Sound",
        NodeKind.GASM => "Code Block",
        NodeKind.Light => "Light",
        _ => k.ToString()
    };
}

public static class ShapeInfo
{
    public const int Start = 0, Finish = 1, Sign = 2, Gravity = 3, LobbyTerminal = 4,
        ParticleEmitter = 5, Sound = 6, Gasm = 7, Light = 8, Cube = 1000,
        Sphere = 1001, Cylinder = 1002, Pyramid = 1003, Prism = 1004, Cone = 1005, PyramidSquare = 1006;

    public static readonly (int Id, string Name)[] All =
    {
        (Cube, "Cube"), (Sphere, "Sphere"), (Cylinder, "Cylinder"),
        (Pyramid, "Pyramid"), (Prism, "Prism"), (Cone, "Cone"),
        (PyramidSquare, "Pyramid (Square Base)")
    };

    public static string Display(int id) => All.FirstOrDefault(x => x.Id == id).Name ?? "Cube";
    public static string ModelName(int id) => id switch
    {
        Sphere => "sphere", Cylinder => "cylinder", Pyramid => "pyramid",
        Prism => "prism", Cone => "cone", PyramidSquare => "pyramid4", _ => "cube"
    };
}

public static class MaterialInfo
{
    public static readonly (int Id, string Name, string Texture)[] All =
    {
        (0, "Default", "default"),
        (1, "Grabbable", "grabbable"),
        (2, "Ice", "ice"),
        (3, "Lava", "lava"),
        (4, "Wood", "wood"),
        (5, "Grapplable", "grapplable"),
        (6, "Grapplable Lava", "grapplable_lava"),
        (7, "Grabbable Crumbling", "grabbable_crumbling"),
        (8, "Default Colored", "default_colored"),
        (9, "Bouncing", "bouncing"),
        (10, "Snow", "snow"),
        (11, "Trigger", "trigger")
    };

    public static string Display(int id) => All.FirstOrDefault(x => x.Id == id).Name ?? "Default";
    public static string Texture(int id) => All.FirstOrDefault(x => x.Id == id).Texture ?? "default";

    /// <summary>
    /// GRAB interprets this Color 1 value as "no texture": the block is drawn as a
    /// flat, untextured surface instead of sampling the material texture. It is the
    /// same trick the retail editor uses when you tick "No Texture" on a block.
    /// </summary>
    public const float NoTextureColor = 99999999f;

    /// <summary>
    /// Only GRAB's "Default Colored" material mixes Color1 into the texture. Every
    /// other material (ice, lava, wood, snow, ...) samples its own fixed texture and
    /// ignores the paint colour, so the editor must not offer a tint that the game
    /// would silently throw away.
    /// </summary>
    public static bool IsColorable(int id) => id == 8;
}

public class AmbienceSettings
{
    // The values the retail editor starts a new level with (sky #4779BA → #E9F4F4).
    public Col SkyZenithColor { get; set; } = new(0.28f, 0.476f, 0.73f, 1);
    public Col SkyHorizonColor { get; set; } = new(0.916f, 0.957f, 0.957f, 1);
    public float SunAltitude { get; set; } = 45f;
    public float SunAzimuth { get; set; } = 315f;
    public float SunSize { get; set; } = 1f;
    public float FogDensity { get; set; } = 0.08f;
    public bool UseAdvancedSunSettings { get; set; }
    public Col SunColor { get; set; } = new(1f, 0.95f, 0.86f, 1);
    public float SunBrightness { get; set; } = 1f;
    public Col AmbientColor { get; set; } = new(1f, 1f, 1f, 1);
    public float AmbientBrightness { get; set; } = 1f;
    public float StarDensity { get; set; } = 0f;
    public float StarClustering { get; set; } = 0f;
    public Vec3 StarRotationEuler { get; set; } = Vec3.Zero;
    public float StarSize { get; set; } = 1f;
    public float StarVisibility { get; set; } = 0f;
    public Col CloudColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float CloudShading { get; set; } = 1f;
    public float CloudDensity { get; set; } = 0.75f;
    public float CloudHeight { get; set; } = 120f;
    public float CloudSpeedX { get; set; } = 2f;
    public float CloudSpeedZ { get; set; } = 1f;

    public AmbienceSettings Clone() => (AmbienceSettings)MemberwiseClone();
}

public class GrabAnimationFrame
{
    public float Time { get; set; }
    public Vec3 Position { get; set; }
    public Quat Rotation { get; set; } = Quat.Identity;
}

public class GrabAnimation
{
    public string Name { get; set; } = "idle";
    public List<GrabAnimationFrame> Frames { get; set; } = new();
    public int Direction { get; set; }          // 0 restart, 1 pingpong
    public float Speed { get; set; } = 1f;
    public int Interpolation { get; set; }      // 0 linear, 1 catmull-rom
}

public class TriggerSource
{
    public int BasicType { get; set; } = 0;     // HAND HEAD GRAPPLE FEET BLOCK SPAWN
    public int Hand { get; set; }                 // BOTH LEFT RIGHT
    public List<int> Buttons { get; set; } = new();
    public List<string> BlockNames { get; set; } = new();
    public bool UsesHand { get; set; }
    public bool UsesBlockNames { get; set; }

    public string Display() => UsesBlockNames
        ? "Named groups: " + string.Join(", ", BlockNames)
        : UsesHand
            ? $"{(Hand == 1 ? "Left" : Hand == 2 ? "Right" : "Both")} hand" + (Buttons.Count > 0 ? " [" + string.Join(",", Buttons) + "]" : "")
            : new[] { "Hand", "Head", "Grapple", "Feet", "Block", "Spawn" }[Math.Clamp(BasicType, 0, 5)];
}

/// <summary>COD.Types.TriggerTargetAnimation.Mode values from GRAB's types.proto.</summary>
public static class AnimationTargetMode
{
    public const int Stop = 0;
    public const int Start = 1;
    public const int Toggle = 2;
    public const int ToggleReverse = 3;
    public const int Restart = 4;
    public const int Reset = 5;
}

public class TriggerTarget
{
    public int Mode { get; set; } // 0 enter, 1 leave, 2 both, 3 none
    public int Kind { get; set; } // 1 anim, 2 sublevel, 3 sound, 6 ambience, 7 gasm, 8 light
    public ulong ObjectId { get; set; }
    public string ObjectName { get; set; } = "";
    public string AnimationName { get; set; } = "idle";
    public bool Loop { get; set; }
    public bool Reverse { get; set; }
    public int TargetMode { get; set; }
    public bool Repeat { get; set; }
    public string LevelIdentifier { get; set; } = "";
    public string SpawnPoint { get; set; } = "";
    public Col Color { get; set; } = Col.White;
    public float Range { get; set; } = 10f;
    public float Brightness { get; set; } = 2f;
    public float FadeDuration { get; set; } = 0.5f;
    public AmbienceSettings Ambient { get; set; } = new();
    public float ChangeDuration { get; set; } = 1f;
    public int InterpolationType { get; set; }

    public string Display() => Kind switch
    {
        1 => $"Animation '{AnimationName}' on {ObjectName}",
        2 => $"Sublevel {LevelIdentifier}",
        3 => $"Sound on {ObjectName}",
        6 => "Ambience change",
        7 => $"Program on {ObjectName}",
        8 => $"Light on {ObjectName}",
        _ => "Target"
    };
}

public class EditorLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public string Color { get; set; } = "#4A90D9";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }

    public bool IsDefault => Id == GrabLevel.DefaultLayerId;
}

public class LevelNode
{
    public string EditorId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public NodeKind Kind { get; set; }
    public bool IsLocked { get; set; }

    /// <summary>
    /// Set on a Code Block that is attached to another object as a Unity-style script
    /// component. The block stays a normal top-level Code Block in the .level (GRAB only
    /// runs programs from objects), but the editor shows it under its host and the
    /// Inspector treats it as a component.
    /// </summary>
    public string ComponentHostId { get; set; } = "";
    /// <summary>Display name of the component (the C# class name).</summary>
    public string ComponentName { get; set; } = "";

    /// <summary>Per-instance values for [SerializeField] / public fields, keyed by field name.</summary>
    public Dictionary<string, string> FieldOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Prefab asset this node was instantiated from (empty for authored objects).</summary>
    public string PrefabSource { get; set; } = "";
    /// <summary>Stable key of this node inside its prefab; used for overrides and revert.</summary>
    public string PrefabNodeKey { get; set; } = "";
    /// <summary>EditorId of the prefab instance root (only set on that root).</summary>
    public string PrefabInstanceId { get; set; } = "";

    /// <summary>Editor-only biome index for terrain blocks (0 = automatic).</summary>
    public int Biome { get; set; }
    /// <summary>Editor-only layer. Never written into a GRAB .level.</summary>
    public string LayerId { get; set; } = GrabLevel.DefaultLayerId;

    /// <summary>True for helper blocks made by the compiler (continuation pages and buses).</summary>
    public bool Generated { get; set; }
    /// <summary>EditorId of the Code Block that owns this generated node.</summary>
    public string GeneratedFor { get; set; } = "";
    /// <summary>1-based page number for continuation Code Blocks.</summary>
    public int PageIndex { get; set; }

    /// <summary>True for a real scene object created by a C# `new` expression.</summary>
    public bool ScriptCreated { get; set; }
    /// <summary>EditorId of the Code Block whose script created this object.</summary>
    public string ScriptSourceBlock { get; set; } = "";
    /// <summary>Stable compile-time key of the new expression / voxel cell.</summary>
    public string ScriptSourceKey { get; set; } = "";

    public Vec3 Position { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;
    public Vec3 Scale { get; set; } = Vec3.One;

    [JsonIgnore] public LevelNode? Parent { get; set; }
    public List<LevelNode> Children { get; set; } = new();
    public bool PhysicsObject { get; set; }
    public bool LocalPhysicsObject { get; set; }
    public float Mass { get; set; } = 1f;

    // Static / crumbling
    public int Shape { get; set; } = ShapeInfo.Cube;
    public int Material { get; set; } = 8;
    public Col Color1 { get; set; } = new(0.72f, 0.72f, 0.72f, 1f);
    /// <summary>Exports Color 1 as the 99999999 sentinel so GRAB draws no texture.</summary>
    public bool NoTexture { get; set; }
    public Col Color2 { get; set; } = new(0.15f, 0.15f, 0.15f, 1f);
    public Vec3 GradientDirection { get; set; } = new(0, 1, 0);
    // GRAB's block inspector has a Rendering dropdown: Default, Specularity, Neon, ...
    // A block is only "Specularity" when this is above zero, so new blocks start at 0
    // (Default) exactly like a block placed in the real editor.
    public float SpecularBrightness { get; set; } = 0f;
    public bool IsNeon { get; set; }
    public bool IsTransparent { get; set; }
    public bool IsGradient { get; set; }
    public bool IsAdditive { get; set; }
    public bool IsGrabbable { get; set; }
    public bool IsGrapplable { get; set; }
    public bool IsPassable { get; set; }
    public float StableTime { get; set; } = 1f;
    public float RespawnTime { get; set; } = 3f;
    public bool IsLocal { get; set; }

    // Start / finish
    public float Radius { get; set; } = 0.5f;
    public bool IsHidden { get; set; }

    // Sign
    public string Text { get; set; } = "Hello!";
    /// <summary>Text written to a non-sign object from C#; shown in the editor (GRAB only draws Sign text).</summary>
    public string EditorLabel { get; set; } = "";
    public float SignScale { get; set; } = 1f;
    public Col SignColor { get; set; } = Col.White;
    public int SignWeight { get; set; }
    public bool HideModel { get; set; }
    public bool SignNeon { get; set; }

    // Gravity
    public int GravityMode { get; set; }
    public Vec3 GravityDirection { get; set; } = new(0, -1, 0);
    public bool HideParticles { get; set; }

    // Trigger
    public bool IsSharedTrigger { get; set; }
    public List<TriggerSource> TriggerSources { get; set; } = new();
    public List<TriggerTarget> TriggerTargets { get; set; } = new();

    // Particle emitter
    public uint ParticlesPerSecond { get; set; } = 20;
    public float LifeSpanMin { get; set; } = 1f;
    public float LifeSpanMax { get; set; } = 2f;
    public Col StartColor { get; set; } = Col.White;
    public Col EndColor { get; set; } = Col.Clear;
    public float StartSizeMin { get; set; } = 0.2f;
    public float StartSizeMax { get; set; } = 0.3f;
    public float EndSizeMin { get; set; }
    public float EndSizeMax { get; set; }
    public Vec3 Velocity { get; set; } = new(0, 1, 0);
    public Vec3 VelocityMin { get; set; }
    public Vec3 VelocityMax { get; set; }
    public Vec3 AccelerationMin { get; set; }
    public Vec3 AccelerationMax { get; set; }

    // Sound
    public string SoundName { get; set; } = "sound";
    public bool SoundRepeat { get; set; }
    public float SoundVolume { get; set; } = 1f;
    public bool SoundStartActive { get; set; } = true;
    public float SoundMaxRange { get; set; } = 1000f;
    public float SoundWaveType { get; set; }
    public float SoundFrequency { get; set; } = 440f;
    public float SoundAttack { get; set; } = 0.01f;
    public float SoundSustain { get; set; } = 0.3f;
    public float SoundRelease { get; set; } = 0.3f;
    // The rest of GRAB's advanced synth. These defaults are the ones the retail
    // editor writes for a fresh sound block: in particular the low-pass is 25000
    // (fully open) and the high-pass is 0 (off). Leaving the low-pass at 0 is
    // what made imported sounds silent in the real game.
    public float SoundPunch { get; set; }
    public float SoundFrequencyMin { get; set; } = 35f;
    public float SoundFrequencySlide { get; set; }
    public float SoundFrequencyDeltaSlide { get; set; }
    public float SoundVibratoStrength { get; set; }
    public float SoundVibratoSpeed { get; set; }
    public float SoundPitchJumpMod { get; set; } = 1f;
    public float SoundPitchJumpSpeed { get; set; }
    public float SoundDutyCycle { get; set; } = 0.5f;
    public float SoundDutyCycleSweep { get; set; }
    public float SoundRepeatSpeed { get; set; }
    public float SoundFlangerFrequency { get; set; }
    public float SoundFlangerDepth { get; set; }
    public float SoundLowPass { get; set; } = 25000f;
    public float SoundHighPass { get; set; }
    public float SoundReverbDelay { get; set; }
    public float SoundReverbDecay { get; set; }
    [JsonIgnore] public byte[]? RawSoundParameters { get; set; }

    // GASM
    public string Script { get; set; } = "";
    public GasmProgram? ImportedProgram { get; set; }
    public List<GasmConnection> ImportedConnections { get; set; } = new();
    [JsonIgnore] public GasmCompileResult? Compiled { get; set; }
    public bool StartActive { get; set; } = true;
    public bool IsSharedGasm { get; set; }
    public bool LateUpdate { get; set; }

    // Light
    public int LightType { get; set; }
    public Col LightColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float LightIntensity { get; set; } = 2.3f;
    public float LightRange { get; set; } = 4.3f;
    public float LightAngle { get; set; } = 45f;
    public float DistanceFalloffShape { get; set; } = 3f;
    public float ConeFalloffShape { get; set; }
    public uint CreationOrder { get; set; }

    // Animation
    public List<GrabAnimation> Animations { get; set; } = new();
    /// <summary>
    /// GRAB's Animate-menu "Start Active" setting. A value of -1 means the object
    /// does not start an animation when the level loads; otherwise this is the index
    /// of the clip that plays on spawn. Written to the .level as activeAnimation.
    /// </summary>
    public int ActiveAnimation { get; set; }
    public bool WantsCreationHistory { get; set; }

    [JsonIgnore] public bool IsGroup => Kind == NodeKind.Group;
    [JsonIgnore] public bool HasPosition => Kind is not NodeKind.Finish;
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? NodeKindInfo.Display(Kind) : Name;

    public IEnumerable<LevelNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<LevelNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var d in Descendants()) yield return d;
    }

    public void RebuildParents(LevelNode? parent = null)
    {
        Parent = parent;
        foreach (var c in Children) c.RebuildParents(this);
    }

    public Vec3 WorldPosition
    {
        get
        {
            if (Parent == null) return Position;
            return Parent.WorldPosition + Parent.WorldRotation.Rotate(Position * Parent.WorldScale);
        }
    }

    public Quat WorldRotation
    {
        get
        {
            if (Parent == null) return Rotation;
            return Parent.WorldRotation * Rotation;
        }
    }

    public Vec3 WorldScale
    {
        get
        {
            if (Parent == null) return Scale;
            var p = Parent.WorldScale;
            return new Vec3(p.X * Scale.X, p.Y * Scale.Y, p.Z * Scale.Z);
        }
    }

    public LevelNode Clone(bool newIds = true)
    {
        var n = (LevelNode)MemberwiseClone();
        n.EditorId = newIds ? Guid.NewGuid().ToString("N") : EditorId;
        n.Children = Children.Select(c => c.Clone(newIds)).ToList();
        n.TriggerSources = TriggerSources.Select(s => new TriggerSource
        {
            BasicType = s.BasicType, Hand = s.Hand, UsesHand = s.UsesHand,
            UsesBlockNames = s.UsesBlockNames, Buttons = new List<int>(s.Buttons),
            BlockNames = new List<string>(s.BlockNames)
        }).ToList();
        n.TriggerTargets = TriggerTargets.Select(t => new TriggerTarget
        {
            Mode = t.Mode, Kind = t.Kind, ObjectId = t.ObjectId, ObjectName = t.ObjectName,
            AnimationName = t.AnimationName, Loop = t.Loop, Reverse = t.Reverse,
            TargetMode = t.TargetMode, Repeat = t.Repeat, LevelIdentifier = t.LevelIdentifier,
            SpawnPoint = t.SpawnPoint, Color = t.Color, Range = t.Range, Brightness = t.Brightness,
            FadeDuration = t.FadeDuration, Ambient = t.Ambient.Clone(),
            ChangeDuration = t.ChangeDuration, InterpolationType = t.InterpolationType
        }).ToList();
        n.Animations = Animations.Select(a => new GrabAnimation
        {
            Name = a.Name, Direction = a.Direction, Speed = a.Speed, Interpolation = a.Interpolation,
            Frames = a.Frames.Select(f => new GrabAnimationFrame { Time = f.Time, Position = f.Position, Rotation = f.Rotation }).ToList()
        }).ToList();
        n.ImportedProgram = ImportedProgram;
        n.ImportedConnections = ImportedConnections;
        n.RawSoundParameters = RawSoundParameters;
        n.FieldOverrides = new Dictionary<string, string>(FieldOverrides, StringComparer.Ordinal);
        n.RebuildParents();
        return n;
    }
}

public class GrabLevel
{
    public const string DefaultLayerId = "default";

    public int FormatVersion { get; set; } = 25;
    public string Title { get; set; } = "My Level";
    public string Creators { get; set; } = "GRAB ENGINE";
    public string Description { get; set; } = "";
    public uint Complexity { get; set; }
    public uint MaxCheckpointCount { get; set; } = 100;
    public ulong DefaultSpawnPointId { get; set; }
    public bool Unlisted { get; set; }
    public bool ShowReplays { get; set; } = true;
    public bool DisablePracticeMode { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>Prefab asset path when this project is a prefab variant (editor-only).</summary>
    public string PrefabVariantOf { get; set; } = "";
    public AmbienceSettings Ambience { get; set; } = new();
    public List<LevelNode> Nodes { get; set; } = new();

    /// <summary>Persistent Neural Lab graph, bindings and trained weights (editor-only).</summary>
    public NeuralLabConfig? NeuralLab { get; set; }

    /// <summary>Pathfinding Studio selection, agent tuning and follow settings (editor-only).</summary>
    public PathfindingSettings Pathfinding { get; set; } = new();

    /// <summary>Editor-only layers (visibility, locking and organisation).</summary>
    public List<EditorLayer> EditorLayers { get; set; } = new();
    public string ActiveLayerId { get; set; } = DefaultLayerId;

    [JsonIgnore] public string? FilePath { get; set; }
    [JsonIgnore] public bool IsDirty { get; set; }
    /// <summary>Editor-only grid step. Not part of a GRAB level.</summary>
    [JsonIgnore] public double GridSnap { get; set; } = 0.5;

    public IEnumerable<LevelNode> AllNodes()
    {
        foreach (var n in Nodes)
            foreach (var d in n.SelfAndDescendants())
                yield return d;
    }

    /// <summary>Creates the default layer and repairs blank/removed layer ids. Safe to call
    /// on old projects and imported .level files.</summary>
    public void EnsureLayers()
    {
        if (EditorLayers == null) EditorLayers = new List<EditorLayer>();
        if (EditorLayers.Count == 0)
        {
            EditorLayers.Add(new EditorLayer { Id = DefaultLayerId, Name = "Default", Color = "#4A90D9" });
        }
        if (EditorLayers.All(l => l.Id != DefaultLayerId))
            EditorLayers.Insert(0, new EditorLayer { Id = DefaultLayerId, Name = "Default", Color = "#4A90D9" });
        var ids = EditorLayers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var n in AllNodes())
            if (string.IsNullOrWhiteSpace(n.LayerId) || !ids.Contains(n.LayerId))
                n.LayerId = DefaultLayerId;
        if (string.IsNullOrWhiteSpace(ActiveLayerId) || !ids.Contains(ActiveLayerId))
            ActiveLayerId = DefaultLayerId;
    }

    public EditorLayer LayerOf(LevelNode node)
    {
        EnsureLayers();
        return EditorLayers.FirstOrDefault(l => l.Id == node.LayerId) ?? EditorLayers[0];
    }

    public EditorLayer? LayerById(string? id)
    {
        EnsureLayers();
        return EditorLayers.FirstOrDefault(l => l.Id == id);
    }

    public bool IsLayerVisible(LevelNode node)
    {
        for (var n = node; n != null; n = n.Parent)
        {
            if (LayerById(n.LayerId) is { Visible: false }) return false;
        }
        return true;
    }

    public bool IsLayerLocked(LevelNode node)
    {
        for (var n = node; n != null; n = n.Parent)
        {
            if (LayerById(n.LayerId) is { Locked: true }) return true;
            if (n.IsLocked) return true;
        }
        return false;
    }

    public void RebuildParents()
    {
        foreach (var n in Nodes) n.RebuildParents();
    }

    public LevelNode? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return AllNodes().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? AllNodes().FirstOrDefault(n => string.Equals(n.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    public LevelNode? FindByEditorId(string id) => AllNodes().FirstOrDefault(n => n.EditorId == id);

    public Dictionary<LevelNode, ulong> BuildObjectIds()
    {
        // GRAB gives every serialized node a 1-based hierarchy id. Trigger targets
        // and GASM connections both reference those ids, so 0 stays reserved for
        // "no object" (the player/lobby connections).
        var map = new Dictionary<LevelNode, ulong>();
        ulong id = 1;
        foreach (var n in AllNodes()) map[n] = id++;
        return map;
    }

    public ulong ObjectIdOf(LevelNode node) => BuildObjectIds().TryGetValue(node, out var id) ? id : 0;

    public void EnsureNames()
    {
        int block = 1, group = 1, start = 1, finish = 1, sign = 1, code = 1, light = 1, trigger = 1, sound = 1, grav = 1, part = 1, crumb = 1;
        foreach (var n in AllNodes())
        {
            if (!string.IsNullOrWhiteSpace(n.Name)) continue;
            n.Name = n.Kind switch
            {
                NodeKind.Static => "Block " + block++,
                NodeKind.Group => "Group " + group++,
                NodeKind.Start => "Start " + start++,
                NodeKind.Finish => "Finish " + finish++,
                NodeKind.Sign => "Sign " + sign++,
                NodeKind.GASM => "Code " + code++,
                NodeKind.Light => "Light " + light++,
                NodeKind.Trigger => "Trigger " + trigger++,
                NodeKind.Sound => "Sound " + sound++,
                NodeKind.Gravity => "Gravity " + grav++,
                NodeKind.ParticleEmitter => "Particles " + part++,
                NodeKind.Crumbling => "Crumbling " + crumb++,
                _ => NodeKindInfo.Display(n.Kind)
            };
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in AllNodes())
        {
            string baseName = n.Name;
            int i = 2;
            while (!seen.Add(n.Name)) n.Name = baseName + " " + i++;
        }
    }

    public void AddNode(LevelNode node, LevelNode? parent = null)
    {
        if (parent != null) parent.Children.Add(node);
        else Nodes.Add(node);
        node.RebuildParents(parent);
        EnsureNames();
        IsDirty = true;
    }

    public void RemoveNode(LevelNode node)
    {
        if (node.Parent != null) node.Parent.Children.Remove(node);
        else Nodes.Remove(node);
        IsDirty = true;
    }

    public static GrabLevel CreateDefault()
    {
        var level = new GrabLevel { Title = "My Level", Creators = "GRAB ENGINE", Description = "Made with GRAB ENGINE" };
        level.EditorLayers.Add(new EditorLayer { Id = DefaultLayerId, Name = "Default", Color = "#4A90D9" });
        var floor = new LevelNode
        {
            Name = "Floor", Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
            Position = new Vec3(0, -0.5f, 0), Scale = new Vec3(10, 1, 10),
            Color1 = new Col(0.72f, 0.72f, 0.72f, 1)
        };
        // Face the showcase and finish down the -Z axis. This is also the direction
        // Play mode aims the third-person camera, so the player starts looking into
        // the level instead of at the empty edge of the floor.
        var start = new LevelNode
        {
            Name = "Start", Kind = NodeKind.Start, Position = new Vec3(0, 0, 4), Radius = 0.5f,
            Rotation = Quat.FromEulerDegrees(new Vec3(0, 180, 0))
        };
        var finish = new LevelNode { Name = "Finish", Kind = NodeKind.Finish, Position = new Vec3(0, 0, -4), Radius = 1f };
        level.Nodes.Add(floor);
        // A small showcase so the real GRAB textures and shapes are visible immediately.
        level.Nodes.Add(new LevelNode
        {
            Name = "Wood Cube", Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 4,
            Position = new Vec3(-3.2f, 0.75f, 1.2f), Scale = new Vec3(1.5f, 1.5f, 1.5f), Color1 = Col.White
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Ice Sphere", Kind = NodeKind.Static, Shape = ShapeInfo.Sphere, Material = 2,
            Position = new Vec3(-1.1f, 0.75f, 1.2f), Scale = new Vec3(1.5f, 1.5f, 1.5f), Color1 = Col.White
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Lava Cylinder", Kind = NodeKind.Static, Shape = ShapeInfo.Cylinder, Material = 3,
            Position = new Vec3(1.1f, 0.75f, 1.2f), Scale = new Vec3(1.5f, 1.5f, 1.5f), Color1 = Col.White
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Bouncing Pyramid", Kind = NodeKind.Static, Shape = ShapeInfo.Pyramid, Material = 9,
            Position = new Vec3(3.2f, 0.75f, 1.2f), Scale = new Vec3(1.5f, 1.5f, 1.5f), Color1 = Col.White
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Sun Light", Kind = NodeKind.Light, Position = new Vec3(0, 4.5f, 1.5f),
            LightColor = new Col(1f, 0.94f, 0.85f), LightIntensity = 2.3f, LightRange = 8f
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Particle Emitter", Kind = NodeKind.ParticleEmitter, Position = new Vec3(0, 2.6f, 1.2f),
            Scale = new Vec3(1, 1, 1), StartColor = new Col(0.35f, 0.9f, 1f), ParticlesPerSecond = 40
        });
        level.Nodes.Add(start);
        level.Nodes.Add(finish);
        level.RebuildParents();
        level.EnsureNames();
        return level;
    }

    /// <summary>
    /// A brand-new level: a Start point and a Finish point, nothing else. This is what
    /// Ctrl+N and File > New create, so an empty level really is empty — no showcase
    /// blocks, floor, light or particle emitter to delete first.
    /// </summary>
    public static GrabLevel CreateBlank()
    {
        var level = new GrabLevel { Title = "My Level", Creators = "GRAB ENGINE", Description = "Made with GRAB ENGINE" };
        level.EditorLayers.Add(new EditorLayer { Id = DefaultLayerId, Name = "Default", Color = "#4A90D9" });
        // Start faces -Z, the direction Play mode aims the camera, so a new level already
        // looks "forwards" before anything is placed.
        level.Nodes.Add(new LevelNode
        {
            Name = "Start", Kind = NodeKind.Start, Position = new Vec3(0, 0, 4), Radius = 0.5f,
            Rotation = Quat.FromEulerDegrees(new Vec3(0, 180, 0))
        });
        level.Nodes.Add(new LevelNode
        {
            Name = "Finish", Kind = NodeKind.Finish, Position = new Vec3(0, 0, -4), Radius = 1f
        });
        level.RebuildParents();
        level.EnsureNames();
        return level;
    }
}
