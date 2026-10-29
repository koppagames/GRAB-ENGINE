using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.AI;

/// <summary>
/// Runtime training cannot keep dozens of mutable weights in a single GASM page:
/// GRAB allows 150 instructions and 10 connections per Code Block. This layout
/// stores six floats per hidden scene object: Position XYZ and Rotation XYZ. Both
/// are ordinary GASM InOut float registers, so they keep full precision; using
/// rotation for three more slots halves the number of scene connections without
/// degrading the weight resolution. The controller reads the buses and a separate
/// trainer block writes them, so both blocks stay inside the real game's limits and
/// the values survive Play mode and export.
/// </summary>
public sealed class NeuralRuntimeLayout
{
    public const string BusMarkerPrefix = "nnw:";
    public const int MaxSceneConnections = 9;
    // Two weight buses cover 12 runtime-tuned values. That leaves plenty of room for
    // object bindings and the compiler's continuation/spill buses on every page.
    public const int MaxTunedWeights = 12;
    public const int SlotsPerBus = 6;
    // The middle weight is stored below the level so GRAB's own import pass hides
    // these helper objects (it recognises tiny blocks parked below y = -500).
    public const float BusParkOffset = 2000f;

    public string Prefix { get; init; } = "";
    public int Transition { get; init; }
    public int FieldCount { get; init; }
    public int TunedFieldCount { get; init; }
    public int BusCount { get; init; }
    public int[] TunedFields { get; init; } = Array.Empty<int>();

    public bool IsTuned(int transition, int field) =>
        transition == Transition && IsTunedField(field);

    public bool IsTunedField(int field) => Array.IndexOf(TunedFields, field) >= 0;

    public int SlotForField(int field) => Array.IndexOf(TunedFields, field);

    public int BusForSlot(int slot) => slot / SlotsPerBus;
    public int SlotInBus(int slot) => slot % SlotsPerBus;

    public string BusName(int bus) => "__NN_" + Prefix + "_" + (bus + 1).ToString(CultureInfo.InvariantCulture);
    public string BusVariable(int bus) => "nnw" + bus.ToString(CultureInfo.InvariantCulture);
    public string BusMarker => BusMarkerPrefix + Prefix;

    public string SlotRead(int slot)
    {
        int part = SlotInBus(slot);
        if (part < 3)
        {
            string member = BusVariable(BusForSlot(slot)) + ".Position." + AxisName(part);
            // Parentheses are required: this expression is multiplied by an input in
            // the generated forward pass, and x * bus.Y + offset would add the offset
            // once instead of scaling the restored weight.
            return part == 1 ? "(" + member + " + " + F(BusParkOffset) + ")" : member;
        }
        string rotationMember = BusVariable(BusForSlot(slot)) + ".Rotation." + AxisName(part - 3);
        return rotationMember;
    }

    public string SlotWrite(int slot, string value)
    {
        int part = SlotInBus(slot);
        if (part < 3)
        {
            string member = BusVariable(BusForSlot(slot)) + ".Position." + AxisName(part);
            return part == 1
                ? member + " = " + value + " - " + F(BusParkOffset)
                : member + " = " + value;
        }
        string rotationMember = BusVariable(BusForSlot(slot)) + ".Rotation." + AxisName(part - 3);
        return rotationMember + " = " + value;
    }

    private static string F(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture) + "f";

    public string FieldRead(int field)
    {
        int slot = SlotForField(field);
        return slot >= 0 ? SlotRead(slot) : "0.0f";
    }

    public string FieldWrite(int field, string value)
    {
        int slot = SlotForField(field);
        return slot >= 0 ? SlotWrite(slot, value) : "";
    }

    public static string AxisName(int axis) => axis == 0 ? "X" : axis == 1 ? "Y" : "Z";

    public static NeuralRuntimeLayout For(NeuralLabConfig config)
    {
        var sizes = config.LayerSizes;
        int transition = Math.Max(0, sizes.Length - 2);
        int inCount = sizes[transition];
        int outCount = sizes[transition + 1];
        int fields = inCount * outCount + outCount;
        int tuned = Math.Clamp(config.Training.RuntimeTuneLimit, 1, Math.Min(fields, MaxTunedWeights));
        // Spread the tunable slots across every output neuron instead of spending the
        // whole budget on the first output. Column 0 is the bias, then the inputs.
        var tunedFields = new List<int>(tuned);
        int columns = inCount + 1;
        for (int slot = 0; slot < tuned; slot++)
        {
            int row = slot % outCount;
            int column = slot / outCount;
            if (column >= columns) break;
            int field = row * columns + (column == 0 ? inCount : column - 1);
            if (!tunedFields.Contains(field)) tunedFields.Add(field);
        }
        return new NeuralRuntimeLayout
        {
            Prefix = string.IsNullOrWhiteSpace(config.RuntimeBusPrefix)
                ? "nn" + Guid.NewGuid().ToString("N")[..8]
                : config.RuntimeBusPrefix,
            Transition = transition,
            FieldCount = fields,
            TunedFieldCount = tunedFields.Count,
            BusCount = (tunedFields.Count + SlotsPerBus - 1) / SlotsPerBus,
            TunedFields = tunedFields.ToArray()
        };
    }

    public static bool IsRuntime(NeuralLabConfig config) =>
        config.Training.Mode is NeuralTrainingMode.Runtime or NeuralTrainingMode.Both;

    public string ConnectionSummary(int sceneObjects) =>
        $"{sceneObjects + BusCount}/{NeuralRuntimeLayout.MaxSceneConnections} connections " +
        $"({BusCount} weight bus{(BusCount == 1 ? "" : "es")})";
}

public static class NeuralRuntimeBuses
{
    public static List<LevelNode> Ensure(GrabLevel level, NeuralLabConfig config)
    {
        var layout = NeuralRuntimeLayout.For(config);
        config.RuntimeBusPrefix = layout.Prefix;
        var existing = level.AllNodes()
            .Where(n => n.GeneratedFor == layout.BusMarker && n.Generated)
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

        var buses = new List<LevelNode>();
        for (int i = 0; i < layout.BusCount; i++)
        {
            var node = existing.FirstOrDefault(n => n.Name == layout.BusName(i))
                       ?? existing.ElementAtOrDefault(i);
            if (node == null || node.Name != layout.BusName(i))
            {
                if (node == null)
                {
                    node = new LevelNode
                    {
                        Name = layout.BusName(i),
                        Kind = NodeKind.Static,
                        Shape = ShapeInfo.Cube,
                        Material = 8,
                        Scale = new Vec3(0.01f, 0.01f, 0.01f),
                        Color1 = new Col(0f, 0f, 0f, 1f),
                        HideModel = true,
                        IsPassable = true,
                        Generated = true,
                        GeneratedFor = layout.BusMarker,
                        LayerId = "neural"
                    };
                    level.AddNode(node);
                }
                else
                {
                    node.Name = layout.BusName(i);
                }
            }
            node.GeneratedFor = layout.BusMarker;
            node.Kind = NodeKind.Static;
            node.Shape = ShapeInfo.Cube;
            node.HideModel = true;
            node.IsPassable = true;
            node.Scale = new Vec3(0.01f, 0.01f, 0.01f);
            node.Generated = true;
            buses.Add(node);
        }
        // Keep only the buses this layout still needs.
        foreach (var stale in existing.Where(n => !buses.Contains(n)).ToList())
            level.RemoveNode(stale);
        WriteWeights(level, config, buses);
        return buses;
    }

    public static void WriteWeights(GrabLevel level, NeuralLabConfig config, List<LevelNode> buses)
    {
        var layout = NeuralRuntimeLayout.For(config);
        for (int i = 0; i < buses.Count; i++)
        {
            int first = i * NeuralRuntimeLayout.SlotsPerBus;
            buses[i].Position = new Vec3(
                SlotWeight(config, layout, first),
                SlotWeight(config, layout, first + 1) - NeuralRuntimeLayout.BusParkOffset,
                SlotWeight(config, layout, first + 2));
            buses[i].Rotation = Quat.FromEulerDegrees(new Vec3(
                SlotWeight(config, layout, first + 3),
                SlotWeight(config, layout, first + 4),
                SlotWeight(config, layout, first + 5)));
            buses[i].Color1 = new Col(0f, 0f, 0f, 1f);
            buses[i].Scale = new Vec3(0.01f, 0.01f, 0.01f);
            buses[i].HideModel = true;
            buses[i].IsPassable = true;
        }
    }

    private static float SlotWeight(NeuralLabConfig config, NeuralRuntimeLayout layout, int slot)
    {
        if (slot < 0 || slot >= layout.TunedFieldCount) return 0f;
        int field = layout.TunedFields[slot];
        return field >= 0 && field < config.Weights.Length ? config.Weights[field] : 0f;
    }

    public static void Remove(GrabLevel level, NeuralLabConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RuntimeBusPrefix)) return;
        var marker = NeuralRuntimeLayout.BusMarkerPrefix + config.RuntimeBusPrefix;
        foreach (var node in level.AllNodes().Where(n => n.GeneratedFor == marker && n.Generated).ToList())
            level.RemoveNode(node);
    }
}

public static class NeuralTrainerBlock
{
    public const string Marker = "// GRAB ENGINE — Neural Lab in-game trainer";
    public const string ClassName = "NeuralTrainer";

    public static string BuildSource(GrabLevel level, NeuralLabConfig config)
    {
        var layout = NeuralRuntimeLayout.For(config);
        var control = level.FindByEditorId(config.Training.ControlObjectId)
                      ?? throw new InvalidOperationException("Runtime training needs a controlled object.");
        var goal = level.FindByEditorId(config.Training.GoalObjectId)
                   ?? throw new InvalidOperationException("Runtime training needs a goal object.");
        if (control.Kind is NodeKind.Start or NodeKind.Finish or NodeKind.GASM)
            throw new InvalidOperationException("Choose a normal scene object as the controlled object.");
        if (goal.Kind is NodeKind.Start or NodeKind.Finish or NodeKind.GASM)
            throw new InvalidOperationException("Choose a normal scene object as the runtime goal.");

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine(Marker);
        sb.AppendLine("// Runs in Play mode and in retail GRAB. It nudges one shared weight at a time");
        sb.AppendLine("// and keeps the nudge only when the controlled object gets closer to the goal.");
        sb.AppendLine($"public class {ClassName} : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine($"    {ObjectType(control)} nnControl = GameObject.Find<{ObjectType(control)}>(\"{Escape(control.Name)}\");");
        sb.AppendLine($"    {ObjectType(goal)} nnGoal = GameObject.Find<{ObjectType(goal)}>(\"{Escape(goal.Name)}\");");
        for (int i = 0; i < layout.BusCount; i++)
            sb.AppendLine($"    PhysicsBlock {layout.BusVariable(i)} = GameObject.Find<PhysicsBlock>(\"{Escape(layout.BusName(i))}\");");
        sb.AppendLine();
        sb.AppendLine("    int nnHold = 0;");
        sb.AppendLine("    int nnActive = 0;");
        sb.AppendLine("    int nnCooldown = 0;");
        sb.AppendLine("    int nnRevert = 0;");
        sb.AppendLine("    int nnProbe = 0;");
        sb.AppendLine("    int nnIndex = 0;");
        sb.AppendLine("    int nnNext = 0;");
        sb.AppendLine("    float nnSaved = 0.0f;");
        sb.AppendLine("    float nnBefore = 0.0f;");
        sb.AppendLine($"    float nnStep = {F(config.Training.RuntimeStep)};");
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        sb.AppendLine("        float nnDx = nnGoal.Position.X - nnControl.Position.X;");
        sb.AppendLine("        float nnDy = nnGoal.Position.Y - nnControl.Position.Y;");
        sb.AppendLine("        float nnDz = nnGoal.Position.Z - nnControl.Position.Z;");
        sb.AppendLine("        float nnReward = -(nnDx * nnDx + nnDy * nnDy + nnDz * nnDz);");
        sb.AppendLine("        if (nnActive == 1)");
        sb.AppendLine("        {");
        sb.AppendLine("            nnHold = nnHold - 1;");
        sb.AppendLine("            if (nnHold <= 0)");
        sb.AppendLine("            {");
        sb.AppendLine("                nnActive = 0;");
        sb.AppendLine("                nnCooldown = 1;");
        sb.AppendLine("                if (nnReward <= nnBefore) nnRevert = 1;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        // One top-level statement per slot. The compiler can split between these
        // statements, unlike one enormous nested if/else tree.
        for (int i = 0; i < layout.TunedFieldCount; i++)
            sb.AppendLine($"        if (nnRevert == 1 && nnIndex == {i}) {layout.SlotWrite(i, "nnSaved")};");
        sb.AppendLine("        if (nnRevert == 1) nnRevert = 0;");
        sb.AppendLine("        if (nnCooldown == 1) nnCooldown = 0;");
        sb.AppendLine("        if (nnActive == 0 && nnCooldown == 0 && nnRevert == 0 && nnProbe == 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            nnBefore = nnReward;");
        sb.AppendLine("            nnIndex = nnNext;");
        sb.AppendLine("            nnNext = nnNext + 1;");
        sb.AppendLine($"            if (nnNext >= {layout.TunedFieldCount}) nnNext = 0;");
        sb.AppendLine("            nnProbe = 1;");
        sb.AppendLine("        }");
        for (int i = 0; i < layout.TunedFieldCount; i++)
        {
            sb.AppendLine($"        if (nnProbe == 1 && nnIndex == {i})");
            sb.AppendLine("        {");
            sb.AppendLine($"            nnSaved = {layout.SlotRead(i)};");
            sb.AppendLine($"            {layout.SlotWrite(i, layout.SlotRead(i) + " + nnStep")};");
            sb.AppendLine("            nnProbe = 0;");
            sb.AppendLine("            nnActive = 1;");
            sb.AppendLine($"            nnHold = {Math.Max(1, config.Training.RuntimeIntervalFrames)};");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        if (nnProbe == 1) nnProbe = 0;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ObjectType(LevelNode node) => node.Kind switch
    {
        NodeKind.Sign => "Sign",
        NodeKind.Light => "Light",
        NodeKind.Trigger => "Trigger",
        NodeKind.Static or NodeKind.Crumbling => "PhysicsBlock",
        _ => "GameObject"
    };

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture) + "f";
}
