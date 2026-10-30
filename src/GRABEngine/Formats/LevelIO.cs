using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GrabEngine.Core;

namespace GrabEngine.Formats;

/// <summary>
/// Reads and writes the real GRAB (Rayne engine) .level protobuf format.
/// Schema recovered from GRAB's embedded FileDescriptorProto (COD.Level.Level).
/// </summary>
public static class LevelIO
{
    public static GrabLevel Load(string path)
    {
        var level = Read(File.ReadAllBytes(path));
        level.FilePath = path;
        level.IsDirty = false;
        return level;
    }

    public static void Save(GrabLevel level, string path)
    {
        var bytes = Write(level);
        File.WriteAllBytes(path, bytes);
        level.FilePath = path;
        level.IsDirty = false;
    }

    public static GrabLevel Read(byte[] data)
    {
        var fields = PbReader.Read(data);
        var level = new GrabLevel
        {
            FormatVersion = (int)PbReader.Varint(fields, 1, 25),
            Title = PbReader.String(fields, 2, "Untitled"),
            Creators = PbReader.String(fields, 3, ""),
            Description = PbReader.String(fields, 4, ""),
            Complexity = (uint)PbReader.Varint(fields, 5),
            MaxCheckpointCount = (uint)PbReader.Varint(fields, 7, 100),
            DefaultSpawnPointId = PbReader.Varint(fields, 10),
            Unlisted = PbReader.Bool(fields, 11),
            ShowReplays = PbReader.Bool(fields, 12),
            DisablePracticeMode = PbReader.Bool(fields, 13)
        };
        foreach (var f in fields.Where(x => x.Number == 9)) level.Tags.Add(f.AsString());
        var amb = PbReader.First(fields, 8);
        if (amb != null) level.Ambience = ReadAmbience(PbReader.Message(amb));
        foreach (var f in fields.Where(x => x.Number == 6))
            level.Nodes.Add(ReadNode(f.Bytes));
        level.RebuildParents();
        ResolveNodeReferences(level);
        MarkParkedHelpers(level);
        level.EnsureNames();
        return level;
    }

    /// <summary>
    /// GRAB's static-node message has no "hide model" field, so the tiny bus blocks a
    /// split script parks far below the level come back as ordinary cubes when a
    /// .level is reopened. Recognise that signature so the editor hides them again.
    /// </summary>
    private static void MarkParkedHelpers(GrabLevel level)
    {
        foreach (var n in level.AllNodes())
        {
            if (n.Kind != NodeKind.Static) continue;
            if (n.Position.Y > -500f) continue;
            if (n.Scale.X > 0.1f || n.Scale.Y > 0.1f || n.Scale.Z > 0.1f) continue;
            n.HideModel = true;
            n.Generated = true;
        }
    }

    private static void ResolveNodeReferences(GrabLevel level)
    {
        var map = new Dictionary<ulong, LevelNode>();
        ulong id = 1;
        foreach (var n in level.AllNodes()) map[id++] = n;
        foreach (var n in level.AllNodes())
        {
            foreach (var t in n.TriggerTargets)
            {
                if (t.ObjectId != 0 && map.TryGetValue(t.ObjectId, out var target))
                    t.ObjectName = target.DisplayName;
            }
        }
        if (level.DefaultSpawnPointId != 0 && map.TryGetValue(level.DefaultSpawnPointId, out var spawn))
            spawn.Name = string.IsNullOrWhiteSpace(spawn.Name) || spawn.Name.StartsWith("Start", StringComparison.OrdinalIgnoreCase)
                ? "Default Spawn" : spawn.Name;
    }

    public static byte[] Write(GrabLevel level)
    {
        RemapTriggerReferences(level);
        var w = new PbWriter();
        w.Varint(1, (ulong)level.FormatVersion);
        w.String(2, level.Title);
        w.String(3, level.Creators);
        w.String(4, level.Description);
        w.Varint(5, level.Complexity);
        foreach (var n in level.Nodes) w.Message(6, m => WriteNode(m, n));
        w.Varint(7, level.MaxCheckpointCount);
        w.Message(8, m => WriteAmbience(m, level.Ambience));
        foreach (var tag in level.Tags) w.String(9, tag);
        ulong spawnId = level.DefaultSpawnPointId;
        ulong nodeCount = (ulong)level.AllNodes().Count();
        if (spawnId == 0 || spawnId > nodeCount)
        {
            ulong id = 1;
            foreach (var n in level.AllNodes())
            {
                if (n.Kind == NodeKind.Start) { spawnId = id; break; }
                id++;
            }
        }
        if (spawnId != 0) w.Varint(10, spawnId);
        w.Bool(11, level.Unlisted);
        w.Bool(12, level.ShowReplays);
        w.Bool(13, level.DisablePracticeMode);
        return w.ToArray();
    }

    /// <summary>
    /// Trigger targets store numeric object ids. Ids are assigned by hierarchy order,
    /// so adding or removing a block can shift them. Resolve every target by its
    /// stored object name before writing, which keeps hand-built triggers and the
    /// compiler's generated animation triggers connected to the right object.
    /// </summary>
    private static void RemapTriggerReferences(GrabLevel level)
    {
        var ids = new Dictionary<LevelNode, ulong>();
        var byId = new Dictionary<ulong, LevelNode>();
        ulong id = 1;
        foreach (var n in level.AllNodes())
        {
            ids[n] = id;
            byId[id] = n;
            id++;
        }
        foreach (var n in level.AllNodes())
        {
            foreach (var t in n.TriggerTargets)
            {
                if (t.Kind is not (1 or 3 or 8)) continue;
                LevelNode? target = null;
                if (!string.IsNullOrWhiteSpace(t.ObjectName)) target = level.FindByName(t.ObjectName);
                if (target == null && t.ObjectId != 0 && byId.TryGetValue(t.ObjectId, out var mapped)) target = mapped;
                if (target == null) continue;
                t.ObjectId = ids[target];
                t.ObjectName = target.DisplayName;
            }
        }
        if (level.DefaultSpawnPointId != 0 && byId.TryGetValue(level.DefaultSpawnPointId, out var spawn) && ids.TryGetValue(spawn, out var newId))
            level.DefaultSpawnPointId = newId;
        else
        {
            // Old GRAB ENGINE projects stored 1000-based ids. Recover the spawn point
            // by picking the first Start node instead of exporting an invalid id.
            var firstStart = level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Start);
            if (firstStart != null) level.DefaultSpawnPointId = ids[firstStart];
        }
    }

    private static LevelNode ReadNode(byte[] data)
    {
        var f = PbReader.Read(data);
        var n = new LevelNode { IsLocked = PbReader.Bool(f, 6) };
        PbField? content = null;
        foreach (var field in f)
        {
            if (field.Number is 1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14)
            {
                content = field;
                break;
            }
        }
        if (content == null) return n;
        var c = PbReader.Message(content);
        n.Kind = (NodeKind)content.Number;
        switch (n.Kind)
        {
            case NodeKind.Start:
                n.Position = PbReader.Vec3(c, 1);
                n.Rotation = PbReader.Quat(c, 2);
                n.Radius = PbReader.Float(c, 3, 0.5f);
                n.Name = PbReader.String(c, 4);
                n.IsHidden = PbReader.Bool(c, 5);
                break;
            case NodeKind.Finish:
                n.Position = PbReader.Vec3(c, 1);
                n.Radius = PbReader.Float(c, 2, 1f);
                break;
            case NodeKind.Static:
                n.Shape = PbReader.Int(c, 1, ShapeInfo.Cube);
                n.Material = PbReader.Int(c, 2, 8);
                n.Position = PbReader.Vec3(c, 3);
                n.Scale = PbReader.Vec3(c, 4, Vec3.One);
                n.Rotation = PbReader.Quat(c, 5);
                n.Color1 = PbReader.Col(c, 6, new Col(0.72f, 0.72f, 0.72f, 1));
                n.IsNeon = PbReader.Bool(c, 7);
                n.IsTransparent = PbReader.Bool(c, 8);
                n.Color2 = PbReader.Col(c, 9, new Col(0.15f, 0.15f, 0.15f, 1));
                n.IsGrabbable = PbReader.Bool(c, 10);
                n.IsGrapplable = PbReader.Bool(c, 11);
                n.IsPassable = PbReader.Bool(c, 12);
                n.IsGradient = PbReader.Bool(c, 13);
                n.GradientDirection = PbReader.Vec3(c, 14, new Vec3(0, 1, 0));
                n.SpecularBrightness = PbReader.Float(c, 15, 0f);
                n.IsAdditive = PbReader.Bool(c, 16);
                if (n.Shape == 0) n.Shape = ShapeInfo.Cube;
                break;
            case NodeKind.Sign:
                n.Position = PbReader.Vec3(c, 1);
                n.Rotation = PbReader.Quat(c, 2);
                n.Text = PbReader.String(c, 3, "Hello!");
                n.SignScale = PbReader.Float(c, 4, 1f);
                n.SignColor = PbReader.Col(c, 5, Col.White);
                n.HideModel = PbReader.Bool(c, 6);
                n.SignWeight = PbReader.Int(c, 7);
                n.SignNeon = PbReader.Bool(c, 8);
                n.Scale = new Vec3(n.SignScale, n.SignScale, n.SignScale);
                break;
            case NodeKind.Crumbling:
                n.Shape = PbReader.Int(c, 1, ShapeInfo.Cube);
                n.Material = PbReader.Int(c, 2, 7);
                n.Position = PbReader.Vec3(c, 3);
                n.Scale = PbReader.Vec3(c, 4, Vec3.One);
                n.Rotation = PbReader.Quat(c, 5);
                n.StableTime = PbReader.Float(c, 6, 1f);
                n.RespawnTime = PbReader.Float(c, 7, 3f);
                n.IsLocal = PbReader.Bool(c, 8);
                break;
            case NodeKind.Group:
                n.Position = PbReader.Vec3(c, 1);
                n.Scale = PbReader.Vec3(c, 2, Vec3.One);
                n.Rotation = PbReader.Quat(c, 3);
                n.Name = PbReader.String(c, 5);
                n.PhysicsObject = PbReader.Bool(c, 6);
                n.LocalPhysicsObject = PbReader.Bool(c, 7);
                n.Mass = PbReader.Float(c, 8, 1f);
                foreach (var child in c.Where(x => x.Number == 4))
                    n.Children.Add(ReadNode(child.Bytes));
                break;
            case NodeKind.Gravity:
                n.GravityMode = PbReader.Int(c, 1);
                n.Position = PbReader.Vec3(c, 2);
                n.Scale = PbReader.Vec3(c, 3, Vec3.One);
                n.Rotation = PbReader.Quat(c, 4);
                n.GravityDirection = PbReader.Vec3(c, 5, new Vec3(0, -1, 0));
                n.HideParticles = PbReader.Bool(c, 6);
                break;
            case NodeKind.LobbyTerminal:
                n.Position = PbReader.Vec3(c, 2);
                n.Rotation = PbReader.Quat(c, 4);
                break;
            case NodeKind.Trigger:
                n.Shape = PbReader.Int(c, 1, ShapeInfo.Cube);
                n.Position = PbReader.Vec3(c, 2);
                n.Scale = PbReader.Vec3(c, 3, Vec3.One);
                n.Rotation = PbReader.Quat(c, 4);
                n.IsSharedTrigger = PbReader.Bool(c, 5);
                foreach (var sf in c.Where(x => x.Number == 6))
                    n.TriggerSources.Add(ReadTriggerSource(PbReader.Message(sf)));
                foreach (var tf in c.Where(x => x.Number == 7))
                    n.TriggerTargets.Add(ReadTriggerTarget(PbReader.Message(tf)));
                break;
            case NodeKind.ParticleEmitter:
                n.Position = PbReader.Vec3(c, 1);
                n.Scale = PbReader.Vec3(c, 2, Vec3.One);
                n.Rotation = PbReader.Quat(c, 3);
                n.ParticlesPerSecond = (uint)PbReader.Varint(c, 5, 20);
                var life = PbReader.First(c, 6);
                if (life != null) { var m = PbReader.Message(life); n.LifeSpanMin = PbReader.Float(m, 1, 1); n.LifeSpanMax = PbReader.Float(m, 2, 2); }
                n.StartColor = PbReader.Col(c, 7, Col.White);
                n.EndColor = PbReader.Col(c, 8, Col.Clear);
                var ss = PbReader.First(c, 9);
                if (ss != null) { var m = PbReader.Message(ss); n.StartSizeMin = PbReader.Float(m, 1, 0.2f); n.StartSizeMax = PbReader.Float(m, 2, 0.3f); }
                var es = PbReader.First(c, 10);
                if (es != null) { var m = PbReader.Message(es); n.EndSizeMin = PbReader.Float(m, 1); n.EndSizeMax = PbReader.Float(m, 2); }
                n.Velocity = PbReader.Vec3(c, 14);
                n.VelocityMin = PbReader.Vec3(c, 15);
                n.VelocityMax = PbReader.Vec3(c, 16);
                n.AccelerationMin = PbReader.Vec3(c, 17);
                n.AccelerationMax = PbReader.Vec3(c, 18);
                break;
            case NodeKind.Sound:
                n.Position = PbReader.Vec3(c, 1);
                var param = PbReader.First(c, 2);
                if (param != null)
                {
                    var m = PbReader.Message(param);
                    n.SoundVolume = PbReader.Float(m, 1, 1);
                    n.SoundWaveType = PbReader.Int(m, 2);
                    n.SoundAttack = PbReader.Float(m, 3, 0.01f);
                    n.SoundSustain = PbReader.Float(m, 4, 0.3f);
                    n.SoundRelease = PbReader.Float(m, 5, 0.3f);
                    n.SoundFrequency = PbReader.Float(m, 7, 440);
                    n.RawSoundParameters = param.Bytes;
                    // Older GRAB ENGINE exports wrote only the first six parameters.
                    // GRAB then used a 0 Hz low-pass, which is closed, so those blocks
                    // were silent in game. Rebuild those parameter messages instead of
                    // round-tripping the broken bytes. Retail sounds always carry the
                    // real defaults (field 8 = 35 and field 20 = 25000).
                    if (PbReader.First(m, 8) == null && PbReader.First(m, 20) == null)
                        n.RawSoundParameters = null;
                    n.SoundPunch = PbReader.Float(m, 6);
                    n.SoundFrequencyMin = PbReader.Float(m, 8, 35);
                    n.SoundFrequencySlide = PbReader.Float(m, 9);
                    n.SoundFrequencyDeltaSlide = PbReader.Float(m, 10);
                    n.SoundVibratoStrength = PbReader.Float(m, 11);
                    n.SoundVibratoSpeed = PbReader.Float(m, 12);
                    n.SoundPitchJumpMod = PbReader.Float(m, 13, 1);
                    n.SoundPitchJumpSpeed = PbReader.Float(m, 14);
                    n.SoundDutyCycle = PbReader.Float(m, 15, 0.5f);
                    n.SoundDutyCycleSweep = PbReader.Float(m, 16);
                    n.SoundRepeatSpeed = PbReader.Float(m, 17);
                    n.SoundFlangerFrequency = PbReader.Float(m, 18);
                    n.SoundFlangerDepth = PbReader.Float(m, 19);
                    n.SoundLowPass = PbReader.Float(m, 20, 25000);
                    n.SoundHighPass = PbReader.Float(m, 21);
                    n.SoundReverbDelay = PbReader.Float(m, 22);
                    n.SoundReverbDecay = PbReader.Float(m, 23);
                }
                n.SoundName = PbReader.String(c, 3, "sound");
                n.SoundRepeat = PbReader.Bool(c, 4);
                n.SoundVolume = PbReader.Float(c, 5, n.SoundVolume);
                // Proto3 omits false, so a missing field means the retail block is
                // NOT start-active (it waits for a trigger or Code Block).
                n.SoundStartActive = PbReader.Bool(c, 6, false);
                n.Rotation = PbReader.Quat(c, 7);
                n.SoundMaxRange = PbReader.Float(c, 8, 1000);
                n.Scale = new Vec3(n.SoundMaxRange, n.SoundMaxRange, n.SoundMaxRange);
                break;
            case NodeKind.GASM:
                n.Position = PbReader.Vec3(c, 1);
                var prog = PbReader.First(c, 2);
                if (prog != null) n.ImportedProgram = GasmIO.ReadProgram(prog.Bytes);
                n.ImportedConnections = GasmIO.ReadConnections(c);
                n.StartActive = PbReader.Bool(c, 4, true);
                n.Scale = PbReader.Vec3(c, 5, Vec3.One);
                n.Rotation = PbReader.Quat(c, 6);
                n.IsSharedGasm = PbReader.Bool(c, 7);
                n.LateUpdate = PbReader.Bool(c, 8);
                break;
            case NodeKind.Light:
                n.Position = PbReader.Vec3(c, 1);
                n.Rotation = PbReader.Quat(c, 2);
                n.LightType = PbReader.Int(c, 3);
                n.LightColor = PbReader.Col(c, 4, Col.White);
                n.LightIntensity = PbReader.Float(c, 5, 2.3f);
                n.LightRange = PbReader.Float(c, 6, 4.3f);
                n.LightAngle = PbReader.Float(c, 7, 45);
                n.DistanceFalloffShape = PbReader.Float(c, 8, 3);
                n.ConeFalloffShape = PbReader.Float(c, 9);
                n.CreationOrder = (uint)PbReader.Varint(c, 10);
                break;
        }
        // Animation clips and the Start Active index live on the LevelNode message
        // itself (fields 15/16/17), not inside the node-kind content message. Reading
        // them from `c` dropped every clip when a .level was reopened.
        foreach (var af in f.Where(x => x.Number == 15))
            n.Animations.Add(ReadAnimation(PbReader.Message(af)));
        n.ActiveAnimation = PbReader.Int(f, 16);
        n.WantsCreationHistory = PbReader.Bool(f, 17);
        return n;
    }

    private static TriggerSource ReadTriggerSource(List<PbField> f)
    {
        var s = new TriggerSource();
        var basic = PbReader.First(f, 1);
        if (basic != null) s.BasicType = PbReader.Int(PbReader.Message(basic), 1);
        var names = PbReader.First(f, 2);
        if (names != null)
        {
            s.UsesBlockNames = true;
            foreach (var nf in PbReader.Message(names).Where(x => x.Number == 1)) s.BlockNames.Add(nf.AsString());
        }
        var hand = PbReader.First(f, 3);
        if (hand != null)
        {
            s.UsesHand = true;
            var m = PbReader.Message(hand);
            s.Hand = PbReader.Int(m, 1);
            foreach (var b in m.Where(x => x.Number == 2)) s.Buttons.Add((int)b.Varint);
        }
        return s;
    }

    private static TriggerTarget ReadTriggerTarget(List<PbField> f)
    {
        var t = new TriggerTarget { Mode = PbReader.Int(f, 4) };
        PbField? content = null;
        foreach (var x in f)
            if (x.Number is 1 or 2 or 3 or 6 or 7 or 8) { content = x; break; }
        if (content == null) return t;
        t.Kind = content.Number;
        var c = PbReader.Message(content);
        switch (t.Kind)
        {
            case 1:
                t.ObjectId = PbReader.Varint(c, 1);
                t.AnimationName = PbReader.String(c, 2, "idle");
                t.Loop = PbReader.Bool(c, 3);
                t.Reverse = PbReader.Bool(c, 4);
                t.TargetMode = PbReader.Int(c, 10);
                break;
            case 2:
                t.LevelIdentifier = PbReader.String(c, 1);
                t.SpawnPoint = PbReader.String(c, 2);
                break;
            case 3:
                t.ObjectId = PbReader.Varint(c, 1);
                t.TargetMode = PbReader.Int(c, 2);
                t.Repeat = PbReader.Bool(c, 3);
                break;
            case 6:
                t.Ambient = ReadAmbience(c);
                t.ChangeDuration = PbReader.Float(c, 7, 1);
                t.InterpolationType = PbReader.Int(c, 8);
                break;
            case 7:
                t.ObjectId = PbReader.Varint(c, 1);
                t.TargetMode = PbReader.Int(c, 2);
                break;
            case 8:
                t.ObjectId = PbReader.Varint(c, 1);
                t.Color = PbReader.Col(c, 2, Col.White);
                t.Range = PbReader.Float(c, 3, 10);
                t.Brightness = PbReader.Float(c, 4, 2);
                t.FadeDuration = PbReader.Float(c, 5, 0.5f);
                break;
        }
        return t;
    }

    private static GrabAnimation ReadAnimation(List<PbField> f)
    {
        var a = new GrabAnimation
        {
            Name = PbReader.String(f, 1, "idle"),
            Direction = PbReader.Int(f, 3),
            Speed = PbReader.Float(f, 4, 1),
            Interpolation = PbReader.Int(f, 5)
        };
        foreach (var ff in f.Where(x => x.Number == 2))
        {
            var m = PbReader.Message(ff);
            a.Frames.Add(new GrabAnimationFrame
            {
                Time = PbReader.Float(m, 1),
                Position = PbReader.Vec3(m, 2),
                Rotation = PbReader.Quat(m, 3)
            });
        }
        return a;
    }

    private static AmbienceSettings ReadAmbience(List<PbField> f)
    {
        return new AmbienceSettings
        {
            SkyZenithColor = PbReader.Col(f, 1, new Col(0.10f, 0.24f, 0.62f, 1)),
            SkyHorizonColor = PbReader.Col(f, 2, new Col(0.72f, 0.84f, 1f, 1)),
            SunAltitude = PbReader.Float(f, 3, 35),
            SunAzimuth = PbReader.Float(f, 4, 315),
            SunSize = PbReader.Float(f, 5, 1),
            FogDensity = PbReader.Float(f, 6, 0),
            UseAdvancedSunSettings = PbReader.Bool(f, 7),
            SunColor = PbReader.Col(f, 8, new Col(1f, 0.93f, 0.8f, 1)),
            SunBrightness = PbReader.Float(f, 9, 1),
            AmbientColor = PbReader.Col(f, 10, Col.White),
            AmbientBrightness = PbReader.Float(f, 11, 1),
            StarDensity = PbReader.Float(f, 12),
            StarClustering = PbReader.Float(f, 13),
            StarRotationEuler = PbReader.Vec3(f, 14),
            StarSize = PbReader.Float(f, 15, 1),
            StarVisibility = PbReader.Float(f, 16),
            CloudColor = PbReader.Col(f, 17, Col.White),
            CloudShading = PbReader.Float(f, 18, 1),
            CloudDensity = PbReader.Float(f, 19, 1),
            CloudHeight = PbReader.Float(f, 20, 120),
            CloudSpeedX = PbReader.Float(f, 21, 2),
            CloudSpeedZ = PbReader.Float(f, 22, 1)
        };
    }

    private static void WriteNode(PbWriter w, LevelNode n)
    {
        w.Bool(6, n.IsLocked);
        switch (n.Kind)
        {
            case NodeKind.Start:
                w.Message(1, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Quat(2, n.Rotation);
                    m.Float(3, n.Radius);
                    m.String(4, n.Name);
                    m.Bool(5, n.IsHidden);
                });
                break;
            case NodeKind.Finish:
                w.Message(2, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Float(2, n.Radius);
                });
                break;
            case NodeKind.Static:
                w.Message(3, m =>
                {
                    m.Varint(1, (ulong)n.Shape);
                    m.Varint(2, (ulong)n.Material);
                    m.Vec3(3, n.Position);
                    m.Vec3(4, n.Scale);
                    m.Quat(5, n.Rotation);
                    m.Color(6, n.Color1);
                    m.Bool(7, n.IsNeon);
                    m.Bool(8, n.IsTransparent);
                    m.Color(9, n.Color2);
                    m.Bool(10, n.IsGrabbable);
                    m.Bool(11, n.IsGrapplable);
                    m.Bool(12, n.IsPassable);
                    m.Bool(13, n.IsGradient);
                    if (n.IsGradient) m.Vec3(14, n.GradientDirection, true);
                    m.Float(15, n.SpecularBrightness);
                    m.Bool(16, n.IsAdditive);
                });
                break;
            case NodeKind.Sign:
                w.Message(4, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Quat(2, n.Rotation);
                    m.String(3, n.Text);
                    m.Float(4, n.SignScale);
                    m.Color(5, n.SignColor);
                    m.Bool(6, n.HideModel);
                    m.Varint(7, (ulong)n.SignWeight);
                    m.Bool(8, n.SignNeon);
                });
                break;
            case NodeKind.Crumbling:
                w.Message(5, m =>
                {
                    m.Varint(1, (ulong)n.Shape);
                    m.Varint(2, (ulong)n.Material);
                    m.Vec3(3, n.Position);
                    m.Vec3(4, n.Scale);
                    m.Quat(5, n.Rotation);
                    m.Float(6, n.StableTime);
                    m.Float(7, n.RespawnTime);
                    m.Bool(8, n.IsLocal);
                });
                break;
            case NodeKind.Group:
                w.Message(7, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Vec3(2, n.Scale);
                    m.Quat(3, n.Rotation);
                    foreach (var c in n.Children) m.Message(4, cm => WriteNode(cm, c));
                    m.String(5, n.Name);
                    m.Bool(6, n.PhysicsObject);
                    m.Bool(7, n.LocalPhysicsObject);
                    if (n.PhysicsObject || n.LocalPhysicsObject) m.Float(8, n.Mass);
                });
                break;
            case NodeKind.Gravity:
                w.Message(8, m =>
                {
                    m.Varint(1, (ulong)n.GravityMode);
                    m.Vec3(2, n.Position);
                    m.Vec3(3, n.Scale);
                    m.Quat(4, n.Rotation);
                    m.Vec3(5, n.GravityDirection, true);
                    m.Bool(6, n.HideParticles);
                });
                break;
            case NodeKind.LobbyTerminal:
                w.Message(9, m =>
                {
                    m.Vec3(2, n.Position);
                    m.Quat(4, n.Rotation);
                });
                break;
            case NodeKind.Trigger:
                w.Message(10, m =>
                {
                    m.Varint(1, (ulong)n.Shape);
                    m.Vec3(2, n.Position);
                    m.Vec3(3, n.Scale);
                    m.Quat(4, n.Rotation);
                    m.Bool(5, n.IsSharedTrigger);
                    foreach (var s in n.TriggerSources) m.Message(6, sm => WriteTriggerSource(sm, s));
                    foreach (var t in n.TriggerTargets) m.Message(7, tm => WriteTriggerTarget(tm, t));
                });
                break;
            case NodeKind.ParticleEmitter:
                w.Message(11, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Vec3(2, n.Scale);
                    m.Quat(3, n.Rotation);
                    m.Varint(5, n.ParticlesPerSecond);
                    m.Message(6, mm => { mm.Float(1, n.LifeSpanMin); mm.Float(2, n.LifeSpanMax); });
                    m.Color(7, n.StartColor);
                    m.Color(8, n.EndColor);
                    m.Message(9, mm => { mm.Float(1, n.StartSizeMin); mm.Float(2, n.StartSizeMax); });
                    m.Message(10, mm => { mm.Float(1, n.EndSizeMin); mm.Float(2, n.EndSizeMax); });
                    m.Vec3(14, n.Velocity, true);
                    m.Vec3(15, n.VelocityMin);
                    m.Vec3(16, n.VelocityMax);
                    m.Vec3(17, n.AccelerationMin);
                    m.Vec3(18, n.AccelerationMax);
                });
                break;
            case NodeKind.Sound:
                w.Message(12, m =>
                {
                    m.Vec3(1, n.Position);
                    if (n.RawSoundParameters != null) m.Bytes(2, n.RawSoundParameters);
                    else
                    {
                        m.Message(2, pm =>
                        {
                            // Write the complete GRAB synth parameter block. Any field
                            // omitted here falls back to 0 in the game, and for field 20
                            // that means a closed low-pass: an inaudible sound block.
                            pm.Float(1, n.SoundVolume);
                            pm.Varint(2, (ulong)n.SoundWaveType);
                            pm.Float(3, n.SoundAttack);
                            pm.Float(4, n.SoundSustain);
                            pm.Float(5, n.SoundRelease);
                            pm.Float(6, n.SoundPunch);
                            pm.Float(7, n.SoundFrequency);
                            pm.Float(8, n.SoundFrequencyMin > 0 ? n.SoundFrequencyMin : 35f);
                            pm.Float(9, n.SoundFrequencySlide);
                            pm.Float(10, n.SoundFrequencyDeltaSlide);
                            pm.Float(11, n.SoundVibratoStrength);
                            pm.Float(12, n.SoundVibratoSpeed);
                            pm.Float(13, n.SoundPitchJumpMod == 0 ? 1f : n.SoundPitchJumpMod);
                            pm.Float(14, n.SoundPitchJumpSpeed);
                            pm.Float(15, Math.Clamp(n.SoundDutyCycle, 0.01f, 0.99f));
                            pm.Float(16, n.SoundDutyCycleSweep);
                            pm.Float(17, n.SoundRepeatSpeed);
                            pm.Float(18, n.SoundFlangerFrequency);
                            pm.Float(19, n.SoundFlangerDepth);
                            // 25000 Hz is GRAB's "filter off" value. Never export 0: the
                            // retail synth treats it as a closed filter, not as disabled.
                            pm.Float(20, n.SoundLowPass <= 20 ? 25000f : Math.Min(n.SoundLowPass, 26000f));
                            pm.Float(21, Math.Max(0, n.SoundHighPass));
                            pm.Float(22, Math.Max(0, n.SoundReverbDelay));
                            pm.Float(23, Math.Clamp(n.SoundReverbDecay, 0, 1));
                        });
                    }
                    m.String(3, n.SoundName);
                    m.Bool(4, n.SoundRepeat);
                    m.Float(5, n.SoundVolume);
                    m.Bool(6, n.SoundStartActive);
                    m.Quat(7, n.Rotation);
                    m.Float(8, n.SoundMaxRange);
                });
                break;
            case NodeKind.GASM:
                w.Message(13, m =>
                {
                    m.Vec3(1, n.Position);
                    var program = n.Compiled?.Program ?? n.ImportedProgram;
                    if (program != null) GasmIO.WriteProgram(m, 2, program);
                    var connections = n.Compiled?.Connections is { Count: > 0 } cc ? cc : n.ImportedConnections;
                    GasmIO.WriteConnections(m, connections);
                    m.Bool(4, n.StartActive);
                    m.Vec3(5, n.Scale);
                    m.Quat(6, n.Rotation);
                    m.Bool(7, n.IsSharedGasm);
                    m.Bool(8, n.LateUpdate);
                });
                break;
            case NodeKind.Light:
                w.Message(14, m =>
                {
                    m.Vec3(1, n.Position);
                    m.Quat(2, n.Rotation);
                    m.Varint(3, (ulong)n.LightType);
                    m.Color(4, n.LightColor);
                    m.Float(5, n.LightIntensity);
                    m.Float(6, n.LightRange);
                    m.Float(7, n.LightAngle);
                    m.Float(8, n.DistanceFalloffShape);
                    m.Float(9, n.ConeFalloffShape);
                    m.Varint(10, n.CreationOrder);
                });
                break;
        }
        foreach (var a in n.Animations)
        {
            w.Message(15, m =>
            {
                m.String(1, a.Name);
                foreach (var fr in a.Frames)
                    m.Message(2, fm =>
                    {
                        fm.Float(1, fr.Time);
                        fm.Vec3(2, fr.Position);
                        fm.Quat(3, fr.Rotation);
                    });
                m.Varint(3, (ulong)a.Direction);
                m.Float(4, a.Speed);
                m.Varint(5, (ulong)a.Interpolation);
            });
        }
        w.Int32(16, n.ActiveAnimation);
        w.Bool(17, n.WantsCreationHistory);
    }

    private static void WriteTriggerSource(PbWriter w, TriggerSource s)
    {
        if (s.UsesBlockNames)
        {
            w.Message(2, m => { foreach (var n in s.BlockNames) m.String(1, n); });
        }
        else if (s.UsesHand)
        {
            w.Message(3, m =>
            {
                if (s.Hand != 0) m.Varint(1, (ulong)s.Hand);
                foreach (var b in s.Buttons) m.Varint(2, (ulong)b);
            });
        }
        else
        {
            w.Message(1, m => { if (s.BasicType != 0) m.Varint(1, (ulong)s.BasicType); });
        }
    }

    private static void WriteTriggerTarget(PbWriter w, TriggerTarget t)
    {
        w.Varint(4, (ulong)t.Mode);
        switch (t.Kind)
        {
            case 1:
                w.Message(1, m =>
                {
                    m.Varint(1, t.ObjectId);
                    m.String(2, t.AnimationName);
                    m.Bool(3, t.Loop);
                    m.Bool(4, t.Reverse);
                    m.Varint(10, (ulong)t.TargetMode);
                });
                break;
            case 2:
                w.Message(2, m => { m.String(1, t.LevelIdentifier); m.String(2, t.SpawnPoint); });
                break;
            case 3:
                w.Message(3, m => { m.Varint(1, t.ObjectId); m.Varint(2, (ulong)t.TargetMode); m.Bool(3, t.Repeat); });
                break;
            case 6:
                w.Message(6, m =>
                {
                    var a = t.Ambient;
                    m.Color(1, a.SkyZenithColor);
                    m.Color(2, a.SkyHorizonColor);
                    m.Float(3, a.SunAltitude);
                    m.Float(4, a.SunAzimuth);
                    m.Float(5, a.SunSize);
                    m.Float(6, a.FogDensity);
                    m.Float(7, t.ChangeDuration);
                    m.Varint(8, (ulong)t.InterpolationType);
                    m.Bool(9, a.UseAdvancedSunSettings);
                    m.Color(10, a.SunColor);
                    m.Float(11, a.SunBrightness);
                    m.Color(12, a.AmbientColor);
                    m.Float(13, a.AmbientBrightness);
                    m.Float(14, a.StarDensity);
                    m.Float(15, a.StarClustering);
                    m.Vec3(16, a.StarRotationEuler);
                    m.Float(17, a.StarSize);
                    m.Float(18, a.StarVisibility);
                    m.Color(19, a.CloudColor);
                    m.Float(20, a.CloudShading);
                    m.Float(21, a.CloudDensity);
                    m.Float(22, a.CloudHeight);
                    m.Float(23, a.CloudSpeedX);
                    m.Float(24, a.CloudSpeedZ);
                });
                break;
            case 7:
                w.Message(7, m => { m.Varint(1, t.ObjectId); m.Varint(2, (ulong)t.TargetMode); });
                break;
            case 8:
                w.Message(8, m =>
                {
                    m.Varint(1, t.ObjectId);
                    m.Color(2, t.Color);
                    m.Float(3, t.Range);
                    m.Float(4, t.Brightness);
                    m.Float(5, t.FadeDuration);
                });
                break;
        }
    }

    private static void WriteAmbience(PbWriter w, AmbienceSettings a)
    {
        w.Color(1, a.SkyZenithColor);
        w.Color(2, a.SkyHorizonColor);
        w.Float(3, a.SunAltitude);
        w.Float(4, a.SunAzimuth);
        w.Float(5, a.SunSize);
        w.Float(6, a.FogDensity);
        w.Bool(7, a.UseAdvancedSunSettings);
        w.Color(8, a.SunColor);
        w.Float(9, a.SunBrightness);
        w.Color(10, a.AmbientColor);
        w.Float(11, a.AmbientBrightness);
        w.Float(12, a.StarDensity);
        w.Float(13, a.StarClustering);
        w.Vec3(14, a.StarRotationEuler);
        w.Float(15, a.StarSize);
        w.Float(16, a.StarVisibility);
        w.Color(17, a.CloudColor);
        w.Float(18, a.CloudShading);
        w.Float(19, a.CloudDensity);
        w.Float(20, a.CloudHeight);
        w.Float(21, a.CloudSpeedX);
        w.Float(22, a.CloudSpeedZ);
    }
}
