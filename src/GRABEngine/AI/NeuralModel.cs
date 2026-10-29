using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.AI;

public enum NeuralActivation
{
    Linear,
    ReLU,
    LeakyReLU,
    Tanh,
    Sigmoid
}

public enum NeuralSourceKind
{
    Object,
    Player,
    Level,
    Time
}

/// <summary>
/// Every scalar a network can read from or write to. The list deliberately covers
/// GRAB's programmable registers plus the useful properties of each object kind.
/// </summary>
public enum NeuralProperty
{
    PositionX, PositionY, PositionZ,
    RotationX, RotationY, RotationZ,
    ScaleX, ScaleY, ScaleZ,
    ColorR, ColorG, ColorB,
    LightR, LightG, LightB, LightBrightness, LightRange,
    SignControl, SignSelect, SignNumber,
    TriggerActive, TriggerPlayerId, TriggerPart, TriggerForce,
    PhysicsEnabled, PhysicsState,
    PlayerPosX, PlayerPosY, PlayerPosZ,
    PlayerRotX, PlayerRotY, PlayerRotZ,
    PlayerVelX, PlayerVelY, PlayerVelZ,
    PlayerColorR, PlayerColorG, PlayerColorB,
    PlayerId, PlayerLocalId,
    LevelVar1, LevelVar2, LevelVar3, LevelVar4, LevelVar5,
    TimeSeconds, TimeDelta, TimeFrame,
    /// <summary>Appended last so old serialized property numbers keep their meaning.</summary>
    SignWrite
}

public enum NeuralWriteMode
{
    Set,
    Add
}

public enum NeuralTrainingMode
{
    Editor,
    Runtime,
    Both
}

public enum NeuralTask
{
    ReachTarget
}

/// <summary>
/// How the target moves while the editor trainer runs an episode. Static matches
/// the old behaviour (one fixed goal). The moving modes force the controller to
/// read the target's *current* position instead of memorising its starting point.
/// </summary>
public enum NeuralTargetMotion
{
    Static,
    Orbit,
    RandomWalk,
    Teleport,
    /// <summary>Appended last so old serialized target-mode numbers keep their meaning.</summary>
    Animated
}

public static class NeuralPropertyInfo
{
    private static readonly NeuralProperty[] ObjectProperties =
    {
        NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ,
        NeuralProperty.RotationX, NeuralProperty.RotationY, NeuralProperty.RotationZ,
        NeuralProperty.ScaleX, NeuralProperty.ScaleY, NeuralProperty.ScaleZ,
        NeuralProperty.ColorR, NeuralProperty.ColorG, NeuralProperty.ColorB,
        NeuralProperty.LightR, NeuralProperty.LightG, NeuralProperty.LightB,
        NeuralProperty.LightBrightness, NeuralProperty.LightRange,
        NeuralProperty.SignControl, NeuralProperty.SignSelect, NeuralProperty.SignNumber, NeuralProperty.SignWrite,
        NeuralProperty.TriggerActive, NeuralProperty.TriggerPlayerId, NeuralProperty.TriggerPart,
        NeuralProperty.TriggerForce,
        NeuralProperty.PhysicsEnabled, NeuralProperty.PhysicsState
    };

    private static readonly NeuralProperty[] PlayerProperties =
    {
        NeuralProperty.PlayerPosX, NeuralProperty.PlayerPosY, NeuralProperty.PlayerPosZ,
        NeuralProperty.PlayerRotX, NeuralProperty.PlayerRotY, NeuralProperty.PlayerRotZ,
        NeuralProperty.PlayerVelX, NeuralProperty.PlayerVelY, NeuralProperty.PlayerVelZ,
        NeuralProperty.PlayerColorR, NeuralProperty.PlayerColorG, NeuralProperty.PlayerColorB,
        NeuralProperty.PlayerId, NeuralProperty.PlayerLocalId
    };

    private static readonly NeuralProperty[] LevelProperties =
    {
        NeuralProperty.LevelVar1, NeuralProperty.LevelVar2, NeuralProperty.LevelVar3,
        NeuralProperty.LevelVar4, NeuralProperty.LevelVar5
    };

    private static readonly NeuralProperty[] TimeProperties =
    {
        NeuralProperty.TimeSeconds, NeuralProperty.TimeDelta, NeuralProperty.TimeFrame
    };

    public static IReadOnlyList<NeuralProperty> AllFor(NeuralSourceKind source) => source switch
    {
        NeuralSourceKind.Object => ObjectProperties,
        NeuralSourceKind.Player => PlayerProperties,
        NeuralSourceKind.Level => LevelProperties,
        _ => TimeProperties
    };

    public static bool IsObject(NeuralProperty p) => ObjectProperties.Contains(p);
    public static bool IsPlayer(NeuralProperty p) => PlayerProperties.Contains(p);
    public static bool IsLevel(NeuralProperty p) => LevelProperties.Contains(p);
    public static bool IsTime(NeuralProperty p) => TimeProperties.Contains(p);

    public static bool IsVector(NeuralProperty p) => p is
        NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ or
        NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ or
        NeuralProperty.ScaleX or NeuralProperty.ScaleY or NeuralProperty.ScaleZ or
        NeuralProperty.ColorR or NeuralProperty.ColorG or NeuralProperty.ColorB or
        NeuralProperty.LightR or NeuralProperty.LightG or NeuralProperty.LightB or
        NeuralProperty.PlayerPosX or NeuralProperty.PlayerPosY or NeuralProperty.PlayerPosZ or
        NeuralProperty.PlayerRotX or NeuralProperty.PlayerRotY or NeuralProperty.PlayerRotZ or
        NeuralProperty.PlayerVelX or NeuralProperty.PlayerVelY or NeuralProperty.PlayerVelZ or
        NeuralProperty.PlayerColorR or NeuralProperty.PlayerColorG or NeuralProperty.PlayerColorB;

    public static bool IsReadable(NeuralProperty p, LevelNode node)
    {
        if (p is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ)
            return node.Kind != NodeKind.Finish;
        if (p is NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ)
            return node.Kind != NodeKind.Finish;
        if (p is NeuralProperty.ScaleX or NeuralProperty.ScaleY or NeuralProperty.ScaleZ)
            return node.Kind != NodeKind.Finish;
        if (p is NeuralProperty.ColorR or NeuralProperty.ColorG or NeuralProperty.ColorB)
            return node.Kind is NodeKind.Static or NodeKind.Crumbling or NodeKind.Light;
        if (p is NeuralProperty.LightR or NeuralProperty.LightG or NeuralProperty.LightB or
            NeuralProperty.LightBrightness or NeuralProperty.LightRange)
            return node.Kind == NodeKind.Light;
        if (p is NeuralProperty.SignControl or NeuralProperty.SignSelect or NeuralProperty.SignNumber)
            return node.Kind == NodeKind.Sign;
        if (p is NeuralProperty.TriggerActive or NeuralProperty.TriggerPlayerId or
            NeuralProperty.TriggerPart or NeuralProperty.TriggerForce)
            return node.Kind == NodeKind.Trigger;
        if (p is NeuralProperty.PhysicsEnabled or NeuralProperty.PhysicsState)
            return node.Kind is NodeKind.Static or NodeKind.Crumbling;
        return false;
    }

    public static bool IsWritable(NeuralProperty p, LevelNode node)
    {
        if (p is NeuralProperty.ScaleX or NeuralProperty.ScaleY or NeuralProperty.ScaleZ) return false;
        if (p is NeuralProperty.LightBrightness or NeuralProperty.LightRange)
            return node.Kind == NodeKind.Light;
        if (p is NeuralProperty.LightR or NeuralProperty.LightG or NeuralProperty.LightB)
            return node.Kind == NodeKind.Light;
        if (p is NeuralProperty.ColorR or NeuralProperty.ColorG or NeuralProperty.ColorB)
            return node.Kind is NodeKind.Static or NodeKind.Crumbling;
        if (p is NeuralProperty.SignControl or NeuralProperty.SignSelect or NeuralProperty.SignNumber or NeuralProperty.SignWrite)
            return node.Kind == NodeKind.Sign;
        if (p is NeuralProperty.TriggerActive or NeuralProperty.TriggerPlayerId or
            NeuralProperty.TriggerPart)
            return false;
        if (p == NeuralProperty.TriggerForce) return node.Kind == NodeKind.Trigger;
        if (p == NeuralProperty.PhysicsEnabled) return node.Kind is NodeKind.Static or NodeKind.Crumbling;
        if (p == NeuralProperty.PhysicsState) return false;
        if (p is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ)
            return node.Kind is not (NodeKind.Finish or NodeKind.Start or NodeKind.GASM);
        if (p is NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ)
            return node.Kind is not (NodeKind.Finish or NodeKind.Start or NodeKind.GASM);
        return false;
    }

    public static bool IsWritableForPlayer(NeuralProperty p) => p is
        NeuralProperty.PlayerVelX or NeuralProperty.PlayerVelY or NeuralProperty.PlayerVelZ;

    public static bool IsWritableForLevel(NeuralProperty p) => IsLevel(p);

    public static string Display(NeuralProperty p) => p switch
    {
        NeuralProperty.PositionX => "Position X",
        NeuralProperty.PositionY => "Position Y",
        NeuralProperty.PositionZ => "Position Z",
        NeuralProperty.RotationX => "Rotation X",
        NeuralProperty.RotationY => "Rotation Y",
        NeuralProperty.RotationZ => "Rotation Z",
        NeuralProperty.ScaleX => "Scale X",
        NeuralProperty.ScaleY => "Scale Y",
        NeuralProperty.ScaleZ => "Scale Z",
        NeuralProperty.ColorR => "Color R",
        NeuralProperty.ColorG => "Color G",
        NeuralProperty.ColorB => "Color B",
        NeuralProperty.LightR => "Light R",
        NeuralProperty.LightG => "Light G",
        NeuralProperty.LightB => "Light B",
        NeuralProperty.LightBrightness => "Brightness",
        NeuralProperty.LightRange => "Range",
        NeuralProperty.SignControl => "Sign Control",
        NeuralProperty.SignSelect => "Sign Select",
        NeuralProperty.SignNumber => "Sign Number",
        NeuralProperty.SignWrite => "Sign Write (ASCII code)",
        NeuralProperty.TriggerActive => "Trigger Active",
        NeuralProperty.TriggerPlayerId => "Trigger Player Id",
        NeuralProperty.TriggerPart => "Trigger Part",
        NeuralProperty.TriggerForce => "Trigger Force",
        NeuralProperty.PhysicsEnabled => "Physics Enabled",
        NeuralProperty.PhysicsState => "Physics State",
        NeuralProperty.PlayerPosX => "Player X",
        NeuralProperty.PlayerPosY => "Player Y",
        NeuralProperty.PlayerPosZ => "Player Z",
        NeuralProperty.PlayerRotX => "Player Rot X",
        NeuralProperty.PlayerRotY => "Player Rot Y",
        NeuralProperty.PlayerRotZ => "Player Rot Z",
        NeuralProperty.PlayerVelX => "Player Vel X",
        NeuralProperty.PlayerVelY => "Player Vel Y",
        NeuralProperty.PlayerVelZ => "Player Vel Z",
        NeuralProperty.PlayerColorR => "Player Color R",
        NeuralProperty.PlayerColorG => "Player Color G",
        NeuralProperty.PlayerColorB => "Player Color B",
        NeuralProperty.PlayerId => "Player Id",
        NeuralProperty.PlayerLocalId => "Player Local Id",
        NeuralProperty.LevelVar1 => "Register Var1",
        NeuralProperty.LevelVar2 => "Register Var2",
        NeuralProperty.LevelVar3 => "Register Var3",
        NeuralProperty.LevelVar4 => "Register Var4",
        NeuralProperty.LevelVar5 => "Register Var5",
        NeuralProperty.TimeSeconds => "Time",
        NeuralProperty.TimeDelta => "Time Delta",
        NeuralProperty.TimeFrame => "Frame",
        _ => p.ToString()
    };

    public static (float Min, float Max) DefaultRange(NeuralProperty p) => p switch
    {
        NeuralProperty.ColorR or NeuralProperty.ColorG or NeuralProperty.ColorB or
        NeuralProperty.LightR or NeuralProperty.LightG or NeuralProperty.LightB or
        NeuralProperty.PlayerColorR or NeuralProperty.PlayerColorG or NeuralProperty.PlayerColorB
            => (0f, 1f),
        NeuralProperty.TriggerActive or NeuralProperty.TriggerForce or NeuralProperty.PhysicsEnabled
            => (0f, 1f),
        NeuralProperty.SignWrite => (0f, 255f),
        NeuralProperty.TimeDelta => (0f, 0.1f),
        _ => (-1f, 1f)
    };

    public static float SuggestedScale(NeuralProperty p) => p switch
    {
        NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ => 0.1f,
        NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ => 0.02f,
        NeuralProperty.TimeSeconds or NeuralProperty.TimeFrame => 0.01f,
        NeuralProperty.PlayerId or NeuralProperty.PlayerLocalId => 0.1f,
        _ => 1f
    };

    public static NeuralProperty AxisProperty(NeuralProperty vector, int component) => vector switch
    {
        NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ =>
            component == 0 ? NeuralProperty.PositionX : component == 1 ? NeuralProperty.PositionY : NeuralProperty.PositionZ,
        NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ =>
            component == 0 ? NeuralProperty.RotationX : component == 1 ? NeuralProperty.RotationY : NeuralProperty.RotationZ,
        NeuralProperty.PlayerPosX or NeuralProperty.PlayerPosY or NeuralProperty.PlayerPosZ =>
            component == 0 ? NeuralProperty.PlayerPosX : component == 1 ? NeuralProperty.PlayerPosY : NeuralProperty.PlayerPosZ,
        NeuralProperty.PlayerVelX or NeuralProperty.PlayerVelY or NeuralProperty.PlayerVelZ =>
            component == 0 ? NeuralProperty.PlayerVelX : component == 1 ? NeuralProperty.PlayerVelY : NeuralProperty.PlayerVelZ,
        _ => vector
    };
}

public sealed class NeuralBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public NeuralSourceKind Source { get; set; } = NeuralSourceKind.Object;
    public string ObjectId { get; set; } = "";
    public NeuralProperty Property { get; set; } = NeuralProperty.PositionX;
    public string RelativeToId { get; set; } = "";
    public float Scale { get; set; } = 1f;
    public float Offset { get; set; }
    public bool Clamp { get; set; } = true;
    public float Min { get; set; } = -1f;
    public float Max { get; set; } = 1f;
    public NeuralWriteMode WriteMode { get; set; } = NeuralWriteMode.Set;

    public NeuralBinding Clone() => (NeuralBinding)MemberwiseClone();

    public string Describe(GrabLevel? level)
    {
        string source = Source switch
        {
            NeuralSourceKind.Object => level?.FindByEditorId(ObjectId)?.DisplayName ?? "Missing object",
            NeuralSourceKind.Player => "Player",
            NeuralSourceKind.Level => "Level registers",
            _ => "Time"
        };
        string text = source + " · " + NeuralPropertyInfo.Display(Property);
        if (Source == NeuralSourceKind.Object && !string.IsNullOrEmpty(RelativeToId))
            text += "  (relative to " + (level?.FindByEditorId(RelativeToId)?.DisplayName ?? "?") + ")";
        return text;
    }
}

public sealed class NeuralLayer
{
    public int Neurons { get; set; } = 8;
    public NeuralActivation Activation { get; set; } = NeuralActivation.ReLU;

    public NeuralLayer Clone() => (NeuralLayer)MemberwiseClone();
}

public sealed class NeuralTrainingSettings
{
    public NeuralTrainingMode Mode { get; set; } = NeuralTrainingMode.Editor;
    public NeuralTask Task { get; set; } = NeuralTask.ReachTarget;
    public string ControlObjectId { get; set; } = "";
    public string GoalObjectId { get; set; } = "";
    public float SuccessRadius { get; set; } = 0.65f;
    public NeuralTargetMotion TargetMotion { get; set; } = NeuralTargetMotion.Static;
    /// <summary>Orbit: degrees per second. RandomWalk: world units per second.</summary>
    public float TargetSpeed { get; set; } = 90f;
    public float TargetRadius { get; set; } = 2.5f;
    public int TargetTeleportFrames { get; set; } = 90;
    public float MoveSpeed { get; set; } = 6f;
    public float RotationSpeed { get; set; } = 90f;
    public float StartSpread { get; set; } = 2.5f;
    public int Starts { get; set; } = 4;
    public int Population { get; set; } = 64;
    public int Generations { get; set; } = 50;
    public int Patience { get; set; } = 14;
    public int EpisodeSteps { get; set; } = 360;
    public int Seed { get; set; } = 20261029;
    public float MutationRate { get; set; } = 0.25f;
    public float MutationScale { get; set; } = 0.35f;
    public bool StopWhenSolved { get; set; } = true;
    public bool AutoApplyAfterTraining { get; set; }
    public int RuntimeIntervalFrames { get; set; } = 12;
    public float RuntimeStep { get; set; } = 0.04f;
    public int RuntimeTuneLimit { get; set; } = 12;

    public NeuralTrainingSettings Clone() => (NeuralTrainingSettings)MemberwiseClone();
}

public sealed class NeuralLabConfig
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Neural Controller";
    public List<NeuralLayer> HiddenLayers { get; set; } = new() { new NeuralLayer() };
    public NeuralActivation OutputActivation { get; set; } = NeuralActivation.Linear;
    public List<NeuralBinding> Inputs { get; set; } = new();
    public List<NeuralBinding> Outputs { get; set; } = new();
    public float[] Weights { get; set; } = Array.Empty<float>();
    public NeuralTrainingSettings Training { get; set; } = new();
    public string CodeBlockId { get; set; } = "";
    public string TrainerBlockId { get; set; } = "";
    public string RuntimeBusPrefix { get; set; } = "";
    public bool StartActive { get; set; } = true;
    public bool LateUpdate { get; set; }

    public int[] LayerSizes
    {
        get
        {
            var sizes = new List<int> { Math.Max(1, Inputs.Count) };
            foreach (var layer in HiddenLayers)
                sizes.Add(Math.Clamp(layer.Neurons, 1, 32));
            sizes.Add(Math.Max(1, Outputs.Count));
            return sizes.ToArray();
        }
    }

    public NeuralActivation ActivationForTransition(int transition)
    {
        if (transition < HiddenLayers.Count) return HiddenLayers[transition].Activation;
        return OutputActivation;
    }

    public int WeightCount
    {
        get
        {
            var sizes = LayerSizes;
            int count = 0;
            for (int i = 0; i < sizes.Length - 1; i++)
                count += sizes[i] * sizes[i + 1] + sizes[i + 1];
            return count;
        }
    }

    public int InputCount => Math.Max(1, Inputs.Count);
    public int OutputCount => Math.Max(1, Outputs.Count);

    public string Architecture => string.Join(" → ", LayerSizes);

    public NeuralLabConfig Clone()
    {
        var copy = (NeuralLabConfig)MemberwiseClone();
        copy.HiddenLayers = HiddenLayers.Select(l => l.Clone()).ToList();
        copy.Inputs = Inputs.Select(b => b.Clone()).ToList();
        copy.Outputs = Outputs.Select(b => b.Clone()).ToList();
        copy.Weights = (float[])Weights.Clone();
        copy.Training = Training.Clone();
        return copy;
    }

    public void EnsureWeights(bool randomizeIfMissing = true, int seed = 20261029)
    {
        int count = WeightCount;
        if (Weights == null || Weights.Length != count || Weights.Any(w => !float.IsFinite(w)))
        {
            if (!randomizeIfMissing)
            {
                Weights = new float[count];
                return;
            }
            RandomizeWeights(seed);
        }
    }

    public void RandomizeWeights(int seed)
    {
        var rng = new Random(seed);
        var sizes = LayerSizes;
        int count = WeightCount;
        var weights = new float[count];
        int at = 0;
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            int inputs = sizes[layer], outputs = sizes[layer + 1];
            float limit = MathF.Sqrt(6f / Math.Max(1, inputs + outputs));
            for (int o = 0; o < outputs; o++)
            {
                for (int i = 0; i < inputs; i++)
                    weights[at++] = (float)((rng.NextDouble() * 2.0 - 1.0) * limit);
                weights[at++] = (float)((rng.NextDouble() * 2.0 - 1.0) * 0.25);
            }
        }
        Weights = weights;
    }

    public void ZeroWeights() => Weights = new float[WeightCount];

    public float[] Forward(IReadOnlyList<float> input)
    {
        EnsureWeights();
        return NeuralMath.Forward(Weights, LayerSizes, Enumerable.Range(0, Math.Max(0, LayerSizes.Length - 1))
            .Select(ActivationForTransition).ToArray(), input);
    }

    public void EnsureValid()
    {
        HiddenLayers ??= new List<NeuralLayer>();
        Inputs ??= new List<NeuralBinding>();
        Outputs ??= new List<NeuralBinding>();
        Training ??= new NeuralTrainingSettings();
        Weights ??= Array.Empty<float>();
        if (HiddenLayers.Count > 4) HiddenLayers = HiddenLayers.Take(4).ToList();
        foreach (var layer in HiddenLayers) layer.Neurons = Math.Clamp(layer.Neurons, 1, 32);
        if (string.IsNullOrWhiteSpace(Name)) Name = "Neural Controller";
        Training.RuntimeTuneLimit = Math.Clamp(Training.RuntimeTuneLimit, 1, NeuralRuntimeLayout.MaxTunedWeights);
        if (!Enum.IsDefined(Training.TargetMotion))
            Training.TargetMotion = NeuralTargetMotion.Static;
        Training.TargetSpeed = Math.Clamp(Training.TargetSpeed, 0f, 720f);
        Training.TargetRadius = Math.Clamp(Training.TargetRadius, 0.05f, 100f);
        Training.TargetTeleportFrames = Math.Clamp(Training.TargetTeleportFrames, 2, 600);
        if (string.IsNullOrWhiteSpace(RuntimeBusPrefix))
            RuntimeBusPrefix = "nn" + Guid.NewGuid().ToString("N")[..8];
        EnsureWeights();
    }

    public List<string> Validate(GrabLevel level, bool includeTraining)
    {
        var errors = new List<string>();
        if (Inputs.Count == 0) errors.Add("Add at least one input binding.");
        if (Outputs.Count == 0) errors.Add("Add at least one output binding.");
        if (Inputs.Count > 24) errors.Add("GRAB GASM works best with 24 or fewer inputs; split the network into two controllers for larger models.");
        if (Outputs.Count > 12) errors.Add("Use 12 or fewer outputs per controller so the generated Code Block can write them every frame.");
        if (HiddenLayers.Count > 4) errors.Add("Four hidden layers is the maximum for one exported network.");
        if (WeightCount > 600) errors.Add("This network needs " + WeightCount + " weights. Keep it under 600 so GRAB's Code Block register budget is respected.");
        foreach (var binding in Inputs.Concat(Outputs))
        {
            if (!binding.Enabled) continue;
            if (binding.Source == NeuralSourceKind.Object)
            {
                var node = level.FindByEditorId(binding.ObjectId);
                if (node == null) { errors.Add("A binding points at an object that was deleted."); continue; }
                bool ok = binding.Enabled && (Inputs.Contains(binding)
                    ? NeuralPropertyInfo.IsReadable(binding.Property, node)
                    : NeuralPropertyInfo.IsWritable(binding.Property, node));
                if (!ok) errors.Add($"'{NeuralPropertyInfo.Display(binding.Property)}' is not available on {node.DisplayName}.");
            }
            else if (binding.Source == NeuralSourceKind.Player && Outputs.Contains(binding) &&
                     !NeuralPropertyInfo.IsWritableForPlayer(binding.Property))
            {
                errors.Add("Player position and colour are read-only in GRAB; use Player velocity for output.");
            }
            else if (binding.Source == NeuralSourceKind.Time && Outputs.Contains(binding))
            {
                errors.Add("Time can only be an input.");
            }
        }

        if (NeuralRuntimeLayout.IsRuntime(this))
        {
            int sceneObjects = CountSceneObjects();
            int buses = NeuralRuntimeLayout.For(this).BusCount;
            int allowedScene = NeuralRuntimeLayout.MaxSceneConnections - buses;
            if (sceneObjects > allowedScene)
                errors.Add($"This controller uses {sceneObjects} scene objects plus {buses} weight bus{(buses == 1 ? "" : "es")}, but a GRAB Code Block allows {NeuralRuntimeLayout.MaxSceneConnections} connections. Lower 'Weights tuned in game' or remove {sceneObjects - allowedScene} object binding{(sceneObjects - allowedScene == 1 ? "" : "s")}.");
        }

        if (includeTraining)
        {
            if (string.IsNullOrWhiteSpace(Training.ControlObjectId))
                errors.Add("Choose the controlled object on the Training tab.");
            else if (level.FindByEditorId(Training.ControlObjectId) == null)
                errors.Add("The controlled object no longer exists.");
            if (string.IsNullOrWhiteSpace(Training.GoalObjectId))
                errors.Add("Choose a goal object on the Training tab.");
            else if (level.FindByEditorId(Training.GoalObjectId) == null)
                errors.Add("The goal object no longer exists.");
            if (!string.IsNullOrWhiteSpace(Training.ControlObjectId) &&
                Training.ControlObjectId == Training.GoalObjectId)
                errors.Add("Choose two different objects: one target to chase and one controlled object to move.");
            if (!Outputs.Any(b => b.Enabled && b.Source == NeuralSourceKind.Object &&
                                  b.Property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ &&
                                  b.ObjectId == Training.ControlObjectId))
                errors.Add("The controlled object needs at least one Position output for target training.");
        }
        return errors;
    }

    private int CountSceneObjects()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in Inputs.Concat(Outputs))
        {
            if (!binding.Enabled || binding.Source != NeuralSourceKind.Object) continue;
            if (!string.IsNullOrWhiteSpace(binding.ObjectId)) ids.Add(binding.ObjectId);
            if (!string.IsNullOrWhiteSpace(binding.RelativeToId)) ids.Add(binding.RelativeToId);
        }
        if (Training.Mode is NeuralTrainingMode.Runtime or NeuralTrainingMode.Both)
        {
            if (!string.IsNullOrWhiteSpace(Training.ControlObjectId)) ids.Add(Training.ControlObjectId);
            if (!string.IsNullOrWhiteSpace(Training.GoalObjectId)) ids.Add(Training.GoalObjectId);
        }
        return ids.Count;
    }

    /// <summary>Builds a sensible steer-to-target controller for the current level.</summary>
    public static NeuralLabConfig CreateStarter(GrabLevel level, string? controlId = null, string? goalId = null)
    {
        var config = new NeuralLabConfig
        {
            Name = "Neural Brain",
            HiddenLayers = new List<NeuralLayer> { new() { Neurons = 8, Activation = NeuralActivation.ReLU } },
            OutputActivation = NeuralActivation.Linear,
            Training = new NeuralTrainingSettings
            {
                Mode = NeuralTrainingMode.Both,
                Starts = 4,
                Population = 48,
                Generations = 40,
                EpisodeSteps = 300
            }
        };

        var nodes = level.AllNodes().Where(n => n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM)).ToList();
        var goal = goalId != null ? level.FindByEditorId(goalId) : null;
        goal ??= nodes.FirstOrDefault(n => n.DisplayName.Contains("Target", StringComparison.OrdinalIgnoreCase));
        var control = controlId != null ? level.FindByEditorId(controlId) : null;
        control ??= nodes.FirstOrDefault(n => !ReferenceEquals(n, goal) &&
                                              n.Kind is NodeKind.Static or NodeKind.Crumbling or NodeKind.Group &&
                                              !n.DisplayName.Contains("Floor", StringComparison.OrdinalIgnoreCase) &&
                                              !n.DisplayName.Contains("Ground", StringComparison.OrdinalIgnoreCase));
        control ??= nodes.FirstOrDefault(n => !ReferenceEquals(n, goal));
        goal ??= nodes.FirstOrDefault(n => !ReferenceEquals(n, control) &&
                                          !n.DisplayName.Contains("Floor", StringComparison.OrdinalIgnoreCase) &&
                                          !n.DisplayName.Contains("Ground", StringComparison.OrdinalIgnoreCase));
        goal ??= nodes.FirstOrDefault(n => !ReferenceEquals(n, control));
        if (goal != null) config.Training.GoalObjectId = goal.EditorId;
        if (control != null) config.Training.ControlObjectId = control.EditorId;

        if (control != null)
        {
            foreach (var prop in new[] { NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ })
            {
                config.Inputs.Add(new NeuralBinding
                {
                    Source = NeuralSourceKind.Object,
                    ObjectId = goal?.EditorId ?? "",
                    RelativeToId = control.EditorId,
                    Property = prop,
                    Scale = 0.1f,
                    Min = -1f,
                    Max = 1f
                });
                config.Outputs.Add(new NeuralBinding
                {
                    Source = NeuralSourceKind.Object,
                    ObjectId = control.EditorId,
                    Property = prop,
                    WriteMode = NeuralWriteMode.Add,
                    Min = -1f,
                    Max = 1f
                });
            }
        }
        config.EnsureWeights();
        return config;
    }
}

/// <summary>
/// Canonical weight layout: for each transition, rows are output-major and every
/// row stores its input weights followed by that neuron's bias. Keeping every
/// reader on this one layout avoids the classic off-by-one that appears when a
/// bias is interleaved but callers assume a plain matrix.
/// </summary>
public static class NeuralLayout
{
    public static int FieldCount(int[] sizes, int transition)
        => sizes[transition] * sizes[transition + 1] + sizes[transition + 1];

    public static int LayerOffset(int[] sizes, int transition)
    {
        int at = 0;
        for (int i = 0; i < transition; i++) at += FieldCount(sizes, i);
        return at;
    }

    public static int WeightIndex(int[] sizes, int transition, int neuron, int input)
        => LayerOffset(sizes, transition) + neuron * (sizes[transition] + 1) + input;

    public static int BiasIndex(int[] sizes, int transition, int neuron)
        => LayerOffset(sizes, transition) + neuron * (sizes[transition] + 1) + sizes[transition];
}

public static class NeuralMath
{
    public static int WeightCount(int[] sizes)
    {
        int total = 0;
        for (int i = 0; i < sizes.Length - 1; i++)
            total += sizes[i] * sizes[i + 1] + sizes[i + 1];
        return total;
    }

    public static float Activate(float x, NeuralActivation activation) => activation switch
    {
        NeuralActivation.ReLU => x > 0f ? x : 0f,
        NeuralActivation.LeakyReLU => x > 0f ? x : x * 0.01f,
        NeuralActivation.Tanh => Clamp(x * (27f + x * x) / (27f + 9f * x * x), -1f, 1f),
        NeuralActivation.Sigmoid => 0.5f + 0.5f * Clamp((x * 0.5f) * (27f + x * x * 0.25f) /
                                                        (27f + 9f * x * x * 0.25f), -1f, 1f),
        _ => x
    };

    private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    public static float[] Forward(float[] weights, int[] sizes, NeuralActivation[] activations, IReadOnlyList<float> input)
    {
        int count = WeightCount(sizes);
        if (weights.Length != count)
            throw new ArgumentException($"Expected {count} weights, got {weights.Length}.");
        var values = new float[sizes[0]];
        for (int i = 0; i < values.Length; i++)
            values[i] = i < input.Count ? input[i] : 0f;
        int at = 0;
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            int inCount = sizes[layer], outCount = sizes[layer + 1];
            var next = new float[outCount];
            for (int o = 0; o < outCount; o++)
            {
                float sum = 0f;
                for (int i = 0; i < inCount; i++)
                    sum += values[i] * weights[at++];
                sum += weights[at++];
                next[o] = Activate(sum, activations[layer]);
            }
            values = next;
        }
        return values;
    }
}

public sealed class NeuralLabGeneration
{
    public int Generation { get; set; }
    public float BestFitness { get; set; }
    public float AverageFitness { get; set; }
    public float BestDistance { get; set; }
    public float SuccessRate { get; set; }
    public bool Solved { get; set; }
}

public sealed class NeuralLabEpisode
{
    public float InitialDistance { get; set; }
    public float FinalDistance { get; set; }
    public float BestDistance { get; set; }
    public int Steps { get; set; }
    public bool Reached { get; set; }
    public float Fitness { get; set; }
    public Vec3 Start { get; set; }
    public Vec3 End { get; set; }
    public Vec3 GoalStart { get; set; }
    public Vec3 GoalEnd { get; set; }
    public float AverageDistance { get; set; }

    public string Describe()
    {
        string text = FormattableString.Invariant(
            $"({Start.X:0.0},{Start.Y:0.0},{Start.Z:0.0}) -> ({End.X:0.00},{End.Y:0.00},{End.Z:0.00})  distance {InitialDistance:0.00} -> {FinalDistance:0.00}  {(Reached ? "REACHED" : "missed")}  steps {Steps}");
        if ((GoalStart - GoalEnd).Length >= 1e-4f)
            text += FormattableString.Invariant($"  target {GoalStart.X:0.0},{GoalStart.Z:0.0} -> {GoalEnd.X:0.0},{GoalEnd.Z:0.0}");
        return text;
    }
}

public sealed class NeuralLabReport
{
    public NeuralTrainingSettings Options { get; set; } = new();
    public int Inputs { get; set; }
    public int Outputs { get; set; }
    public string Architecture { get; set; } = "";
    public List<NeuralLabGeneration> History { get; } = new();
    public List<NeuralLabEpisode> Validation { get; } = new();
    public float[] BestWeights { get; set; } = Array.Empty<float>();
    public int GenerationsRun { get; set; }
    public float BestFitness { get; set; }
    public float BestDistance { get; set; }
    public float SuccessRate { get; set; }
    public bool Solved { get; set; }
    public string StopReason { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public NeuralLabEpisode? DemoRun { get; set; }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("GRAB ENGINE — Neural Lab training report");
        sb.AppendLine($"network: {Architecture}  ({BestWeights.Length} weights)");
        sb.AppendLine($"training: {Options.Mode}   target: {Options.TargetMotion}   population {Options.Population}   generations {GenerationsRun}/{Options.Generations}   patience {Options.Patience}   seed {Options.Seed}");
        sb.AppendLine($"best distance: {BestDistance:0.000}   validation success: {SuccessRate:P0}   solved: {Solved}   elapsed: {ElapsedSeconds:0.0}s");
        sb.AppendLine("stop: " + StopReason);
        if (DemoRun != null) sb.AppendLine("demo: " + DemoRun.Describe());
        sb.AppendLine();
        sb.AppendLine("generation  best-fitness  best-distance  success  average-fitness");
        foreach (var g in History)
            sb.AppendLine($"{g.Generation,10}  {g.BestFitness,12:0.00}  {g.BestDistance,13:0.000}  {g.SuccessRate,7:P0}  {g.AverageFitness,14:0.00}");
        return sb.ToString();
    }
}

/// <summary>
/// A tiny in-memory scene used by the trainer. It reads every configured binding
/// from the current level, then applies network outputs with the same Set/Add
/// semantics the generated GASM uses. This keeps training and export aligned.
/// </summary>
internal sealed class NeuralSceneState
{
    private readonly GrabLevel _level;
    private readonly NeuralTrainingSettings _settings;
    private readonly Dictionary<string, LevelNode> _nodes;
    private readonly Dictionary<string, Vec3> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vec3> _rotations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _levelVars = new(StringComparer.Ordinal);

    public float Time { get; set; }

    public NeuralSceneState(GrabLevel level, NeuralTrainingSettings settings)
    {
        _level = level;
        _settings = settings;
        _nodes = level.AllNodes().ToDictionary(n => n.EditorId, n => n, StringComparer.Ordinal);
        foreach (var node in level.AllNodes())
        {
            _positions[node.EditorId] = node.Position;
            _rotations[node.EditorId] = node.Rotation.ToEulerDegrees();
        }
    }

    public LevelNode? Node(string id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public Vec3 Position(string id) => _positions.TryGetValue(id, out var p) ? p : Vec3.Zero;
    public Vec3 Rotation(string id) => _rotations.TryGetValue(id, out var r) ? r : Vec3.Zero;

    public Vec3 GoalPosition(string id) => Position(id);

    public void SetPosition(string id, Vec3 position)
    {
        _positions[id] = position;
    }

    public float Evaluate(NeuralBinding binding)
    {
        float raw = Read(binding.Source, binding.ObjectId, binding.Property);
        if (binding.Source == NeuralSourceKind.Object && !string.IsNullOrEmpty(binding.RelativeToId))
            raw -= Read(NeuralSourceKind.Object, binding.RelativeToId, binding.Property);
        float value = raw * binding.Scale + binding.Offset;
        if (binding.Clamp) value = Math.Clamp(value, binding.Min, binding.Max);
        return value;
    }

    private float Read(NeuralSourceKind source, string objectId, NeuralProperty property)
    {
        if (source == NeuralSourceKind.Time)
        {
            return property switch
            {
                NeuralProperty.TimeDelta => 1f / 60f,
                NeuralProperty.TimeFrame => Time * 60f,
                _ => Time
            };
        }
        if (source == NeuralSourceKind.Level)
        {
            string key = property.ToString();
            return _levelVars.TryGetValue(key, out float v) ? v : 0f;
        }
        if (source == NeuralSourceKind.Player)
            return 0f;
        if (!_nodes.TryGetValue(objectId, out var node)) return 0f;

        bool HasStored(NeuralProperty p) => _values.ContainsKey(objectId + "/" + p);
        float Stored(NeuralProperty p) => _values.TryGetValue(objectId + "/" + p, out float v) ? v : 0f;

        switch (property)
        {
            case NeuralProperty.PositionX: return Position(objectId).X;
            case NeuralProperty.PositionY: return Position(objectId).Y;
            case NeuralProperty.PositionZ: return Position(objectId).Z;
            case NeuralProperty.RotationX: return Rotation(objectId).X;
            case NeuralProperty.RotationY: return Rotation(objectId).Y;
            case NeuralProperty.RotationZ: return Rotation(objectId).Z;
            case NeuralProperty.ScaleX: return node.Scale.X;
            case NeuralProperty.ScaleY: return node.Scale.Y;
            case NeuralProperty.ScaleZ: return node.Scale.Z;
            case NeuralProperty.ColorR: return HasStored(property) ? Stored(property) : node.Color1.R;
            case NeuralProperty.ColorG: return HasStored(property) ? Stored(property) : node.Color1.G;
            case NeuralProperty.ColorB: return HasStored(property) ? Stored(property) : node.Color1.B;
            case NeuralProperty.LightR: return HasStored(property) ? Stored(property) : node.LightColor.R;
            case NeuralProperty.LightG: return HasStored(property) ? Stored(property) : node.LightColor.G;
            case NeuralProperty.LightB: return HasStored(property) ? Stored(property) : node.LightColor.B;
            case NeuralProperty.LightBrightness: return HasStored(property) ? Stored(property) : node.LightIntensity;
            case NeuralProperty.LightRange: return HasStored(property) ? Stored(property) : node.LightRange;
            case NeuralProperty.SignControl: return Stored(property);
            case NeuralProperty.SignSelect: return Stored(property);
            case NeuralProperty.SignNumber: return Stored(property);
            case NeuralProperty.SignWrite: return Stored(property);
            case NeuralProperty.TriggerActive: return node.TriggerTargets.Count > 0 ? 1f : 0f;
            case NeuralProperty.TriggerPlayerId: return 0f;
            case NeuralProperty.TriggerPart: return 0f;
            case NeuralProperty.TriggerForce: return Stored(property);
            case NeuralProperty.PhysicsEnabled: return node.PhysicsObject ? 1f : 0f;
            case NeuralProperty.PhysicsState: return Stored(property);
            default: return 0f;
        }
    }

    public void Apply(NeuralBinding output, float networkValue, float dt)
    {
        if (!output.Enabled) return;
        float value = networkValue * output.Scale + output.Offset;
        if (output.Clamp) value = Math.Clamp(value, output.Min, output.Max);
        if (output.Source == NeuralSourceKind.Level)
        {
            _levelVars[output.Property.ToString()] = value;
            return;
        }
        if (output.Source != NeuralSourceKind.Object) return;
        if (!_nodes.ContainsKey(output.ObjectId)) return;

        int axis = output.Property switch
        {
            NeuralProperty.PositionX or NeuralProperty.RotationX => 0,
            NeuralProperty.PositionY or NeuralProperty.RotationY => 1,
            NeuralProperty.PositionZ or NeuralProperty.RotationZ => 2,
            _ => -1
        };
        if (output.Property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ)
        {
            var p = Position(output.ObjectId);
            p[axis] = output.WriteMode == NeuralWriteMode.Add
                ? p[axis] + value * _settings.MoveSpeed * dt
                : value;
            p.X = Math.Clamp(p.X, -200f, 200f);
            p.Y = Math.Clamp(p.Y, -100f, 300f);
            p.Z = Math.Clamp(p.Z, -200f, 200f);
            _positions[output.ObjectId] = p;
            return;
        }
        if (output.Property is NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ)
        {
            var r = Rotation(output.ObjectId);
            r[axis] = output.WriteMode == NeuralWriteMode.Add
                ? r[axis] + value * _settings.RotationSpeed * dt
                : value;
            _rotations[output.ObjectId] = r;
            return;
        }
        if (NeuralPropertyInfo.IsObject(output.Property))
            _values[output.ObjectId + "/" + output.Property] = value;
    }
}

/// <summary>
/// Genetic search over a configurable network. Fitness is the distance travelled
/// toward the goal plus a bonus for reaching it, measured on every training start.
/// </summary>
public sealed class NeuralLabTrainer
{
    private readonly NeuralLabConfig _config;
    private readonly GrabLevel _level;

    public NeuralLabTrainer(NeuralLabConfig config, GrabLevel level)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _level = level ?? throw new ArgumentNullException(nameof(level));
    }

    private sealed class Genome
    {
        public float[] Weights = Array.Empty<float>();
        public float Fitness;
    }

    public NeuralLabReport Train(Action<NeuralLabGeneration>? progress = null, System.Threading.CancellationToken token = default)
    {
        var options = _config.Training;
        var control = _level.FindByEditorId(options.ControlObjectId);
        var goal = _level.FindByEditorId(options.GoalObjectId);
        if (control == null) throw new InvalidOperationException("The controlled object no longer exists.");
        if (goal == null) throw new InvalidOperationException("The goal object no longer exists.");
        if (_config.Inputs.Count == 0) throw new InvalidOperationException("Add at least one input binding first.");
        if (_config.Outputs.Count == 0) throw new InvalidOperationException("Add at least one output binding first.");
        if (!_config.Outputs.Any(o => o.Enabled && o.ObjectId == control.EditorId &&
                                      o.Property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ))
            throw new InvalidOperationException("The controlled object needs a Position output so training can move it.");

        _config.EnsureWeights();
        var rng = new Random(options.Seed);
        int weightCount = _config.WeightCount;
        var starts = BuildStarts(control.Position, options, rng);
        var sizes = _config.LayerSizes;
        var activations = Enumerable.Range(0, sizes.Length - 1).Select(_config.ActivationForTransition).ToArray();
        var population = new List<Genome>(Math.Max(4, options.Population));
        for (int i = 0; i < Math.Max(4, options.Population); i++)
        {
            var weights = i == 0
                ? (float[])_config.Weights.Clone()
                : RandomWeights(weightCount, rng);
            population.Add(new Genome { Weights = weights });
        }

        var report = new NeuralLabReport
        {
            Options = options.Clone(),
            Inputs = _config.Inputs.Count,
            Outputs = _config.Outputs.Count,
            Architecture = _config.Architecture,
            BestWeights = (float[])_config.Weights.Clone()
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float bestEver = float.MinValue;
        int stale = 0;
        bool solved = false;
        string stopReason = "Generation limit reached.";

        for (int generation = 0; generation < options.Generations; generation++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var genome in population)
            {
                float total = 0;
                for (int s = 0; s < starts.Count; s++)
                    total += RunEpisode(genome.Weights, sizes, activations, starts[s], options, token).Fitness;
                genome.Fitness = total / Math.Max(1, starts.Count);
            }

            var ranked = population.OrderByDescending(g => g.Fitness).ToList();
            var champion = ranked[0];
            if (champion.Fitness > bestEver + 0.01f)
            {
                bestEver = champion.Fitness;
                stale = 0;
                report.BestWeights = (float[])champion.Weights.Clone();
            }
            else stale++;

            var validation = new List<NeuralLabEpisode>();
            foreach (var start in starts)
                validation.Add(RunEpisode(report.BestWeights, sizes, activations, start, options, token));
            float success = validation.Count == 0 ? 0 : validation.Count(e => e.Reached) / (float)validation.Count;
            float meanDistance = validation.Count == 0 ? 0 : validation.Average(e => e.FinalDistance);
            bool allReached = validation.Count > 0 && validation.All(e => e.Reached);
            report.Validation.Clear();
            report.Validation.AddRange(validation);
            report.BestFitness = bestEver;
            report.BestDistance = meanDistance;
            report.SuccessRate = success;
            report.Solved = allReached;

            var stat = new NeuralLabGeneration
            {
                Generation = generation + 1,
                BestFitness = ranked[0].Fitness,
                AverageFitness = (float)population.Average(g => g.Fitness),
                BestDistance = meanDistance,
                SuccessRate = success,
                Solved = allReached
            };
            report.History.Add(stat);
            progress?.Invoke(stat);
            if (allReached)
            {
                solved = true;
                if (options.StopWhenSolved) { stopReason = "Solved — the controller reached the goal from every training start."; break; }
            }
            if (stale >= options.Patience)
            {
                stopReason = $"Early stop — fitness did not improve for {options.Patience} generations.";
                break;
            }

            int elite = Math.Max(2, population.Count / 8);
            var next = new List<Genome>(population.Count);
            for (int i = 0; i < elite; i++) next.Add(new Genome { Weights = (float[])ranked[i].Weights.Clone() });
            while (next.Count < population.Count)
            {
                if (rng.NextDouble() < 0.04)
                {
                    next.Add(new Genome { Weights = RandomWeights(weightCount, rng) });
                    continue;
                }
                var a = Tournament(ranked, rng);
                var b = Tournament(ranked, rng);
                var child = Crossover(a.Weights, b.Weights, rng);
                Mutate(child, rng, options.MutationRate, options.MutationScale);
                next.Add(new Genome { Weights = child });
            }
            population = next;
        }

        report.GenerationsRun = report.History.Count;
        report.Solved = solved;
        if (solved && stopReason == "Generation limit reached.")
            stopReason = "Generation limit reached — the controller had already solved every start.";
        report.StopReason = stopReason;
        report.ElapsedSeconds = clock.Elapsed.TotalSeconds;
        report.BestDistance = report.Validation.Count > 0 ? report.Validation.Average(e => e.FinalDistance) : 0;
        report.SuccessRate = report.Validation.Count > 0 ? report.Validation.Count(e => e.Reached) / (float)report.Validation.Count : 0;
        report.DemoRun = starts.Count > 0
            ? RunEpisode(report.BestWeights, sizes, activations, starts[0], options, token)
            : null;
        return report;
    }

    private static List<Vec3> BuildStarts(Vec3 origin, NeuralTrainingSettings options, Random rng)
    {
        int count = Math.Clamp(options.Starts, 1, 12);
        var starts = new List<Vec3> { origin };
        for (int i = 1; i < count; i++)
        {
            double angle = rng.NextDouble() * Math.PI * 2;
            double radius = options.StartSpread * (0.35 + rng.NextDouble() * 0.65);
            starts.Add(new Vec3(origin.X + (float)Math.Cos(angle) * (float)radius,
                origin.Y, origin.Z + (float)Math.Sin(angle) * (float)radius));
        }
        return starts;
    }

    private NeuralLabEpisode RunEpisode(float[] weights, int[] sizes, NeuralActivation[] activations,
        Vec3 start, NeuralTrainingSettings options, System.Threading.CancellationToken token)
    {
        var state = new NeuralSceneState(_level, options);
        var controlId = options.ControlObjectId;
        var goalId = options.GoalObjectId;
        state.SetPosition(controlId, start);
        bool movingTarget = options.TargetMotion != NeuralTargetMotion.Static;
        int motionSeed = options.Seed ^ start.X.GetHashCode() ^ (start.Z.GetHashCode() * 397);
        var goalNode = _level.FindByEditorId(goalId);
        var motion = new GoalMotion(state.GoalPosition(goalId), options,
            new Random(motionSeed), goalNode?.Animations.FirstOrDefault());
        var goal = motion.Position;
        state.SetPosition(goalId, goal);
        var goalStart = goal;
        float initial = (start - goal).Length;
        float best = initial;
        float final = initial;
        float distanceSum = 0f;
        bool reached = initial <= options.SuccessRadius;
        int steps = 0;
        var inputBuffer = new float[_config.Inputs.Count];
        var controlStart = start;

        for (int step = 0; step < options.EpisodeSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            state.Time = step / 60f;
            // Move the goal before reading inputs. This is the important part for
            // moving targets: the network sees where the target is now, never a
            // cached starting position.
            goal = motion.Step(state.Position(controlId));
            state.SetPosition(goalId, goal);
            for (int i = 0; i < _config.Inputs.Count; i++)
                inputBuffer[i] = _config.Inputs[i].Enabled ? state.Evaluate(_config.Inputs[i]) : 0f;
            var outputs = NeuralMath.Forward(weights, sizes, activations, inputBuffer);
            for (int i = 0; i < _config.Outputs.Count; i++)
                state.Apply(_config.Outputs[i], i < outputs.Length ? outputs[i] : 0f, 1f / 60f);
            var p = state.Position(controlId);
            final = (p - goal).Length;
            distanceSum += final;
            if (final < best) best = final;
            steps = step + 1;
            if (!movingTarget && final <= options.SuccessRadius)
            {
                reached = true;
                break;
            }
        }

        if (movingTarget)
        {
            // A moving goal cannot be "solved" by driving to where it used to be.
            // Reward staying close for the whole episode, and only count the run
            // as reached when the controller is close on the final frame.
            reached = final <= options.SuccessRadius;
        }

        float averageDistance = steps > 0 ? distanceSum / steps : final;
        float fitness = movingTarget
            ? -averageDistance * 8f - final * 4f + (initial - best) * 2f
            : (initial - final) * 10f + (initial - best) * 2f - final * 2f;
        if (reached) fitness += 100f + (options.EpisodeSteps - steps) * 0.02f;
        return new NeuralLabEpisode
        {
            InitialDistance = initial,
            FinalDistance = final,
            BestDistance = best,
            AverageDistance = averageDistance,
            Steps = steps,
            Reached = reached,
            Fitness = fitness,
            Start = controlStart,
            End = state.Position(controlId),
            GoalStart = goalStart,
            GoalEnd = goal
        };
    }

    /// <summary>Small deterministic target path used by the editor trainer.</summary>
    private sealed class GoalMotion
    {
        private readonly NeuralTrainingSettings _options;
        private readonly Random _rng;
        private readonly Vec3 _anchor;
        private readonly float _radius;
        private readonly GrabAnimation? _clip;
        private Vec3 _velocity;
        private float _angle;
        private float _time;

        public Vec3 Position { get; private set; }

        public GoalMotion(Vec3 anchor, NeuralTrainingSettings options, Random rng, GrabAnimation? clip)
        {
            _anchor = anchor;
            _options = options;
            _rng = rng;
            _clip = clip != null && clip.Frames.Count > 0 ? clip : null;
            _radius = Math.Clamp(options.TargetRadius, 0.05f, 100f);
            Position = anchor;
            _angle = 0f;
            _velocity = Vec3.Zero;
        }

        public Vec3 Step(Vec3 control)
        {
            float dt = 1f / 60f;
            switch (_options.TargetMotion)
            {
                case NeuralTargetMotion.Static:
                    return _anchor;
                case NeuralTargetMotion.Animated:
                    if (_clip == null) return _anchor;
                    _time += dt;
                    Position = AnimationSampler.Sample(_clip, _time, loop: true).Position;
                    return Position;
                case NeuralTargetMotion.Orbit:
                    _angle += Math.Clamp(_options.TargetSpeed, 0f, 720f) * MathF.PI / 180f * dt;
                    Position = _anchor + new Vec3(MathF.Cos(_angle) * _radius, 0f, MathF.Sin(_angle) * _radius);
                    return Position;
                case NeuralTargetMotion.Teleport:
                    if (_step % Math.Max(1, _options.TargetTeleportFrames) == 0)
                        Position = RandomPoint();
                    _step++;
                    return Position;
                default:
                    if (_step == 0)
                        _velocity = RandomPoint() - _anchor;
                    if (_velocity.Length < 0.0001f)
                        _velocity = new Vec3(1f, 0f, 0f);
                    _velocity = _velocity.Normalized * Math.Clamp(_options.TargetSpeed, 0.05f, 80f);
                    Position += _velocity * dt;
                    var offset = Position - _anchor;
                    if (offset.Length > _radius)
                    {
                        // Reflect at the training bubble so the goal stays near the
                        // level instead of wandering out of the playable area.
                        var normalDir = offset.Normalized;
                        _velocity = _velocity - normalDir * (2f * (_velocity.X * normalDir.X + _velocity.Y * normalDir.Y + _velocity.Z * normalDir.Z));
                        Position = _anchor + normalDir * _radius;
                    }
                    _step++;
                    return Position;
            }
        }

        private int _step;

        private Vec3 RandomPoint()
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float distance = _radius * (float)Math.Sqrt(_rng.NextDouble());
            return _anchor + new Vec3(MathF.Cos(angle) * distance, 0f, MathF.Sin(angle) * distance);
        }
    }

    private static float[] RandomWeights(int count, Random rng)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = (float)((rng.NextDouble() * 2 - 1) * 0.8);
        return result;
    }

    private static float[] Crossover(float[] a, float[] b, Random rng)
    {
        var result = new float[Math.Min(a.Length, b.Length)];
        for (int i = 0; i < result.Length; i++) result[i] = rng.NextDouble() < 0.5 ? a[i] : b[i];
        return result;
    }

    private static void Mutate(float[] weights, Random rng, float rate, float scale)
    {
        for (int i = 0; i < weights.Length; i++)
        {
            if (rng.NextDouble() >= rate) continue;
            weights[i] += (float)(Gaussian(rng) * scale);
            weights[i] = Math.Clamp(weights[i], -8f, 8f);
        }
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static Genome Tournament(List<Genome> ranked, Random rng)
    {
        var best = ranked[rng.Next(ranked.Count)];
        for (int i = 0; i < 2; i++)
        {
            var contender = ranked[rng.Next(ranked.Count)];
            if (contender.Fitness > best.Fitness) best = contender;
        }
        return best;
    }
}
