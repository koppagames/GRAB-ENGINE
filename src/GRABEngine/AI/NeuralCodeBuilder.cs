using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.AI;

public sealed class NeuralLabApplyResult
{
    public bool Success { get; set; }
    public string Source { get; set; } = "";
    public string Error { get; set; } = "";
    public GasmCompileResult? Compile { get; set; }
    public GasmCompileResult? TrainerCompile { get; set; }
    public LevelNode? Block { get; set; }
    public LevelNode? TrainerBlock { get; set; }
    public int Pages { get; set; }
    public int Connections { get; set; }
    public int Instructions { get; set; }
    public int TrainerPages { get; set; }
    public int TrainerConnections { get; set; }
    public int TrainerInstructions { get; set; }
    public int WeightBuses { get; set; }

    public string Summary => Success
        ? $"{Pages} page(s), {Instructions} instructions, {Connections} connection(s)" +
          (TrainerPages > 0
              ? $" + trainer {TrainerPages} page(s), {TrainerInstructions} instructions, {TrainerConnections} connection(s)"
              : "")
        : Error;
}

/// <summary>
/// Turns a Neural Lab graph into ordinary C#. The compiler then turns that C# into
/// GRAB's real GASM, so the resulting Code Block runs in the retail game. All
/// property bindings become normal GRAB register connections; no editor-side
/// runtime is required.
/// </summary>
public static class NeuralCodeBuilder
{
    public const string Marker = "// GRAB ENGINE — Neural Lab controller";
    public const string ClassName = "NeuralController";

    private sealed class ObjectRef
    {
        public LevelNode? Node;
        public string FindName = "";
        public string Label = "";
        public string Variable = "";
        public string Type = "GameObject";
    }

    public static string BuildSource(GrabLevel level, NeuralLabConfig config)
    {
        var errors = config.Validate(level, includeTraining: false);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors));
        config.EnsureWeights();

        level.EnsureNames();
        bool runtime = NeuralRuntimeLayout.IsRuntime(config);
        var layout = runtime ? NeuralRuntimeLayout.For(config) : null;
        var refs = CollectObjects(level, config);
        if (layout != null)
        {
            for (int i = 0; i < layout.BusCount; i++)
            {
                var bus = level.AllNodes().FirstOrDefault(n => n.Name == layout.BusName(i) &&
                                                               n.GeneratedFor == layout.BusMarker && n.Generated);
                // A code preview can be generated before Apply creates the buses.
                // The source text is still valid; Apply ensures the objects exist
                // before it sends this source through the compiler.
                refs.Add(new ObjectRef
                {
                    Node = bus,
                    FindName = layout.BusName(i),
                    Label = "neural weight bus",
                    Variable = layout.BusVariable(i),
                    Type = "PhysicsBlock"
                });
            }
        }
        if (refs.Count > 9)
        {
            var names = string.Join(", ", refs.Select(r => r.Label));
            throw new InvalidOperationException(
                $"This controller touches {refs.Count} scene objects ({names}). GRAB allows 10 connections per Code Block, " +
                "and a split controller needs one for its continuation bus. Keep it to 9 objects or fewer. " +
                (layout != null
                    ? $"Runtime tuning adds {layout.BusCount} hidden weight bus(es); lower 'Max tuned weights' or switch to Editor training."
                    : ""));
        }

        var control = runtime ? level.FindByEditorId(config.Training.ControlObjectId) : null;
        var goal = runtime ? level.FindByEditorId(config.Training.GoalObjectId) : null;
        if (runtime && control == null)
            throw new InvalidOperationException("Runtime training needs a controlled object. Choose one on the Training tab.");
        if (runtime && goal == null)
            throw new InvalidOperationException("Runtime training needs a goal object. Choose one on the Training tab.");
        if (runtime && (goal!.Kind is NodeKind.Finish or NodeKind.Start or NodeKind.GASM))
            throw new InvalidOperationException("GRAB Code Blocks cannot connect to Start, Finish or another Code Block. Pick a normal scene object as the runtime goal.");
        if (control != null && control.Kind is NodeKind.Finish or NodeKind.Start or NodeKind.GASM)
            throw new InvalidOperationException("Choose a normal scene object as the controlled object.");
        if (runtime && config.WeightCount > 600)
            throw new InvalidOperationException("This network is too large for one controller. Keep it under 600 weights, or switch Training mode to Editor.");

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine(Marker);
        sb.AppendLine("// This is ordinary C# compiled to real GASM.");
        sb.AppendLine($"// Network: {config.Architecture}  ({config.WeightCount} weights)");
        sb.AppendLine($"// Inputs: {config.Inputs.Count}   Outputs: {config.Outputs.Count}   Mode: {config.Training.Mode}");
        foreach (var input in config.Inputs)
            sb.AppendLine("// IN  " + input.Describe(level));
        foreach (var output in config.Outputs)
            sb.AppendLine("// OUT " + output.Describe(level) + (output.WriteMode == NeuralWriteMode.Add ? " [add]" : " [set]"));
        sb.AppendLine($"public class {ClassName} : CodeBlock");
        sb.AppendLine("{");

        foreach (var obj in refs)
            sb.AppendLine($"    {obj.Type} {obj.Variable} = GameObject.Find<{obj.Type}>(\"{Escape(obj.FindName)}\");");
        if (refs.Count > 0) sb.AppendLine();

        var sizes = config.LayerSizes;
        int at = 0;
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            int inCount = sizes[layer], outCount = sizes[layer + 1];
            int layerStart = at;
            for (int o = 0; o < outCount; o++)
            {
                int rowStart = layerStart + o * (inCount + 1);
                for (int i = 0; i < inCount; i++)
                {
                    string name = FieldName(layer, o, i, bias: false);
                    if (layout == null || !layout.IsTuned(layer, rowStart + i - layerStart))
                        sb.AppendLine($"    const float {name} = {F(config.Weights[rowStart + i])};");
                }
                string bias = FieldName(layer, o, 0, bias: true);
                if (layout == null || !layout.IsTuned(layer, rowStart + inCount - layerStart))
                    sb.AppendLine($"    const float {bias} = {F(config.Weights[rowStart + inCount])};");
            }
            at += outCount * (inCount + 1);
            if (layout == null || layer != sizes.Length - 2) sb.AppendLine();
        }

        if (runtime)
        {
            sb.AppendLine("    // Runtime-tunable output weights are read from hidden weight bus");
            sb.AppendLine("    // objects so the separate in-game trainer can update them safely.");
            sb.AppendLine();
        }

        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");

        for (int i = 0; i < config.Inputs.Count; i++)
        {
            var binding = config.Inputs[i];
            string name = "x" + i;
            if (!binding.Enabled)
            {
                sb.AppendLine($"        float {name} = 0.0f;");
                continue;
            }
            string expression = ReadExpression(binding, refs);
            if (Math.Abs(binding.Scale - 1f) > 0.000001f || Math.Abs(binding.Offset) > 0.000001f)
                sb.AppendLine($"        float {name} = ({expression}) * {F(binding.Scale)} + {F(binding.Offset)};");
            else
                sb.AppendLine($"        float {name} = {expression};");
            if (binding.Clamp)
                sb.AppendLine($"        {name} = Mathf.Clamp({name}, {F(binding.Min)}, {F(binding.Max)});");
        }
        if (config.Inputs.Count > 0) sb.AppendLine();

        var layerValues = new List<string>();
        for (int i = 0; i < config.Inputs.Count; i++) layerValues.Add("x" + i);
        at = 0;
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            int inCount = sizes[layer], outCount = sizes[layer + 1];
            var next = new List<string>();
            for (int o = 0; o < outCount; o++)
            {
                string name = "n" + layer + "_" + o;
                int row = o * (inCount + 1);
                sb.AppendLine($"        float {name} = 0.0f;");
                for (int i = 0; i < inCount; i++)
                {
                    string weight = layout != null && layout.IsTuned(layer, row + i)
                        ? layout.FieldRead(row + i)
                        : FieldName(layer, o, i, false);
                    sb.AppendLine($"        {name} = {name} + {layerValues[i]} * {weight};");
                }
                string bias = layout != null && layout.IsTuned(layer, row + inCount)
                    ? layout.FieldRead(row + inCount)
                    : FieldName(layer, o, 0, true);
                sb.AppendLine($"        {name} = {name} + {bias};");
                var activation = config.ActivationForTransition(layer);
                switch (activation)
                {
                    case NeuralActivation.ReLU:
                        sb.AppendLine($"        {name} = Mathf.Max(0.0f, {name});");
                        break;
                    case NeuralActivation.LeakyReLU:
                        sb.AppendLine($"        {name} = Mathf.Max({name}, {name} * 0.01f);");
                        break;
                    case NeuralActivation.Tanh:
                    {
                        string t = "t" + layer + "_" + o;
                        sb.AppendLine($"        float {t} = {name} * {name};");
                        sb.AppendLine($"        {name} = {name} * (27.0f + {t}) / (27.0f + 9.0f * {t});");
                        sb.AppendLine($"        {name} = Mathf.Clamp({name}, -1.0f, 1.0f);");
                        break;
                    }
                    case NeuralActivation.Sigmoid:
                    {
                        string t = "t" + layer + "_" + o;
                        sb.AppendLine($"        float {t} = {name} * 0.5f;");
                        sb.AppendLine($"        float s{layer}_{o} = {t} * {t};");
                        sb.AppendLine($"        {name} = 0.5f + 0.5f * ({t} * (27.0f + s{layer}_{o}) / (27.0f + 9.0f * s{layer}_{o}));");
                        sb.AppendLine($"        {name} = Mathf.Clamp({name}, 0.0f, 1.0f);");
                        break;
                    }
                }
                next.Add(name);
            }
            sb.AppendLine();
            layerValues = next;
        }

        for (int i = 0; i < config.Outputs.Count; i++)
        {
            var output = config.Outputs[i];
            string raw = i < layerValues.Count ? layerValues[i] : "0.0f";
            string value = "v" + i;
            if (Math.Abs(output.Scale - 1f) > 0.000001f || Math.Abs(output.Offset) > 0.000001f)
                sb.AppendLine($"        float {value} = {raw} * {F(output.Scale)} + {F(output.Offset)};");
            else
                sb.AppendLine($"        float {value} = {raw};");
            if (output.Clamp)
                sb.AppendLine($"        {value} = Mathf.Clamp({value}, {F(output.Min)}, {F(output.Max)});");
            if (output.Enabled) WriteExpression(sb, output, value, refs, config);
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ReadExpression(NeuralBinding binding, List<ObjectRef> refs)
    {
        string core;
        if (binding.Source == NeuralSourceKind.Object)
        {
            var obj = refs.First(r => r.Node != null && r.Node.EditorId == binding.ObjectId);
            core = obj.Variable + "." + ReadMember(binding.Property);
            if (!string.IsNullOrEmpty(binding.RelativeToId))
            {
                var rel = refs.FirstOrDefault(r => r.Node != null && r.Node.EditorId == binding.RelativeToId);
                if (rel != null) core = core + " - " + rel.Variable + "." + ReadMember(binding.Property);
            }
        }
        else if (binding.Source == NeuralSourceKind.Player)
            core = PlayerMember(binding.Property);
        else if (binding.Source == NeuralSourceKind.Level)
            core = "Level.Var" + (int)(binding.Property - NeuralProperty.LevelVar1 + 1);
        else
            core = binding.Property switch
            {
                NeuralProperty.TimeDelta => "Time.DeltaTime",
                NeuralProperty.TimeFrame => "Time.FrameCount",
                _ => "Time.Time"
            };
        return core;
    }

    private static string ReadMember(NeuralProperty p) => p switch
    {
        NeuralProperty.PositionX => "Position.X",
        NeuralProperty.PositionY => "Position.Y",
        NeuralProperty.PositionZ => "Position.Z",
        NeuralProperty.RotationX => "Rotation.X",
        NeuralProperty.RotationY => "Rotation.Y",
        NeuralProperty.RotationZ => "Rotation.Z",
        NeuralProperty.ScaleX => "Scale.X",
        NeuralProperty.ScaleY => "Scale.Y",
        NeuralProperty.ScaleZ => "Scale.Z",
        NeuralProperty.ColorR or NeuralProperty.LightR => "Color.R",
        NeuralProperty.ColorG or NeuralProperty.LightG => "Color.G",
        NeuralProperty.ColorB or NeuralProperty.LightB => "Color.B",
        NeuralProperty.LightBrightness => "Brightness",
        NeuralProperty.LightRange => "Range",
        NeuralProperty.SignControl => "Control",
        NeuralProperty.SignSelect => "Select",
        NeuralProperty.SignNumber => "Number",
        NeuralProperty.SignWrite => "Write",
        NeuralProperty.TriggerActive => "Active",
        NeuralProperty.TriggerPlayerId => "PlayerId",
        NeuralProperty.TriggerPart => "Part",
        NeuralProperty.TriggerForce => "ForceActive",
        NeuralProperty.PhysicsEnabled => "Physics.Enabled",
        NeuralProperty.PhysicsState => "Physics.State",
        _ => "Position.X"
    };

    private static string PlayerMember(NeuralProperty p) => p switch
    {
        NeuralProperty.PlayerPosX => "Player.Pos.X",
        NeuralProperty.PlayerPosY => "Player.Pos.Y",
        NeuralProperty.PlayerPosZ => "Player.Pos.Z",
        NeuralProperty.PlayerRotX => "Player.Rot.X",
        NeuralProperty.PlayerRotY => "Player.Rot.Y",
        NeuralProperty.PlayerRotZ => "Player.Rot.Z",
        NeuralProperty.PlayerVelX => "Player.Vel.X",
        NeuralProperty.PlayerVelY => "Player.Vel.Y",
        NeuralProperty.PlayerVelZ => "Player.Vel.Z",
        NeuralProperty.PlayerColorR => "Player.Col.R",
        NeuralProperty.PlayerColorG => "Player.Col.G",
        NeuralProperty.PlayerColorB => "Player.Col.B",
        NeuralProperty.PlayerId => "Player.Id",
        NeuralProperty.PlayerLocalId => "Player.LocalId",
        _ => "Player.Pos.X"
    };

    private static void WriteExpression(StringBuilder sb, NeuralBinding output, string value,
        List<ObjectRef> refs, NeuralLabConfig config)
    {
        if (output.Source == NeuralSourceKind.Level)
        {
            string member = "Level.Var" + (int)(output.Property - NeuralProperty.LevelVar1 + 1);
            sb.AppendLine($"        {member} = {(output.WriteMode == NeuralWriteMode.Add ? member + " + " + value : value)};");
            return;
        }
        if (output.Source == NeuralSourceKind.Player)
        {
            string member = PlayerMember(output.Property);
            if (output.Property is not (NeuralProperty.PlayerVelX or NeuralProperty.PlayerVelY or NeuralProperty.PlayerVelZ))
                return;
            sb.AppendLine($"        {member} = {(output.WriteMode == NeuralWriteMode.Add ? member + " + " + value : value)};");
            return;
        }
        if (output.Source != NeuralSourceKind.Object) return;
        var obj = refs.FirstOrDefault(r => r.Node != null && r.Node.EditorId == output.ObjectId);
        if (obj == null) return;
        if (output.Property == NeuralProperty.SignWrite)
        {
            // One ASCII code per frame. This is the dynamic text escape hatch:
            // several signs driven by different outputs can spell messages, and a
            // network can select characters from its input state.
            sb.AppendLine($"        {obj.Variable}.Control = 1;");
            sb.AppendLine($"        {obj.Variable}.Select = 0;");
            sb.AppendLine($"        {obj.Variable}.Write = {value};");
            return;
        }
        string target = obj.Variable + "." + ReadMember(output.Property);
        bool add = output.WriteMode == NeuralWriteMode.Add;
        if (output.Property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ)
        {
            string expr = add
                ? $"{target} + {value} * {F(config.Training.MoveSpeed)} * Time.DeltaTime"
                : value;
            sb.AppendLine($"        {target} = {expr};");
            return;
        }
        if (output.Property is NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ)
        {
            string expr = add
                ? $"{target} + {value} * {F(config.Training.RotationSpeed)} * Time.DeltaTime"
                : value;
            sb.AppendLine($"        {target} = {expr};");
            return;
        }
        if (add)
        {
            if (output.Clamp)
                sb.AppendLine($"        {target} = Mathf.Clamp({target} + {value}, {F(output.Min)}, {F(output.Max)});");
            else
                sb.AppendLine($"        {target} = {target} + {value};");
        }
        else
        {
            sb.AppendLine($"        {target} = {value};");
        }
    }

    private static List<ObjectRef> CollectObjects(GrabLevel level, NeuralLabConfig config)
    {
        var ids = new List<string>();
        void Add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
        }
        foreach (var b in config.Inputs.Concat(config.Outputs))
        {
            if (!b.Enabled || b.Source != NeuralSourceKind.Object) continue;
            Add(b.ObjectId);
            Add(b.RelativeToId);
        }
        if (config.Training.Mode is NeuralTrainingMode.Runtime or NeuralTrainingMode.Both)
        {
            Add(config.Training.ControlObjectId);
            Add(config.Training.GoalObjectId);
        }
        var result = new List<ObjectRef>();
        for (int i = 0; i < ids.Count; i++)
        {
            var node = level.FindByEditorId(ids[i]);
            if (node == null) throw new InvalidOperationException("A Neural Lab binding points at a deleted object. Re-select it.");
            result.Add(new ObjectRef
            {
                Node = node,
                FindName = node.Name,
                Label = node.DisplayName,
                Variable = "o" + i,
                Type = ObjectType(node)
            });
        }
        return result;
    }

    private static string ObjectType(LevelNode node) => node.Kind switch
    {
        NodeKind.Sign => "Sign",
        NodeKind.Light => "Light",
        NodeKind.Trigger => "Trigger",
        NodeKind.Static or NodeKind.Crumbling => "PhysicsBlock",
        _ => "GameObject"
    };

    public static NeuralLabApplyResult Apply(GrabLevel level, NeuralLabConfig config)
    {
        var result = new NeuralLabApplyResult();
        config.EnsureValid();
        bool runtime = NeuralRuntimeLayout.IsRuntime(config);
        var control = level.FindByEditorId(config.Training.ControlObjectId);

        // Weight buses must exist before BuildSource resolves the generated Find()
        // calls and before the compiler assigns GRAB object ids.
        if (runtime)
        {
            EnsureNeuralLayer(level);
            var buses = NeuralRuntimeBuses.Ensure(level, config);
            config.RuntimeBusPrefix = NeuralRuntimeLayout.For(config).Prefix;
            NeuralRuntimeBuses.WriteWeights(level, config, buses);
            result.WeightBuses = buses.Count;
        }
        else
        {
            NeuralRuntimeBuses.Remove(level, config);
        }

        string source;
        try
        {
            source = BuildSource(level, config);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
        result.Source = source;

        var existing = level.AllNodes().FirstOrDefault(n => n.EditorId == config.CodeBlockId && n.Kind == NodeKind.GASM)
                       ?? level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM && n.Script.Contains(Marker, StringComparison.Ordinal));
        string oldScript = existing?.Script ?? "";
        GasmCompileResult? oldCompiled = existing?.Compiled;
        bool created = false;
        if (existing == null)
        {
            existing = new LevelNode
            {
                Name = string.IsNullOrWhiteSpace(config.Name) ? "Neural Brain" : config.Name,
                Kind = NodeKind.GASM,
                Position = (control?.Position ?? new Vec3(0, 2, 0)) + new Vec3(0, 2, 0),
                LayerId = EnsureNeuralLayer(level),
                StartActive = config.StartActive,
                LateUpdate = config.LateUpdate
            };
            level.AddNode(existing);
            created = true;
        }

        existing.Script = source;
        existing.StartActive = config.StartActive;
        existing.LateUpdate = config.LateUpdate;
        config.CodeBlockId = existing.EditorId;
        var compile = new CSharpCompiler().Compile(level, existing, source);
        existing.Compiled = compile;
        result.Compile = compile;
        result.Block = existing;
        result.Pages = 1 + compile.Continuations.Count;
        result.Instructions = compile.Program.Instructions.Count;
        result.Connections = compile.Connections.Count;
        result.Success = compile.Success;
        if (!compile.Success)
        {
            result.Error = string.Join("\n", compile.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            if (created)
            {
                level.RemoveNode(existing);
            }
            else
            {
                existing.Script = oldScript;
                existing.Compiled = oldCompiled;
            }
            return result;
        }

        if (runtime)
        {
            try
            {
                var trainerSource = NeuralTrainerBlock.BuildSource(level, config);
                result.TrainerBlock = EnsureTrainerBlock(level, config, trainerSource, control);
                var trainerCompile = new CSharpCompiler().Compile(level, result.TrainerBlock, trainerSource);
                result.TrainerBlock.Compiled = trainerCompile;
                result.TrainerCompile = trainerCompile;
                result.TrainerPages = 1 + trainerCompile.Continuations.Count;
                result.TrainerInstructions = trainerCompile.Program.Instructions.Count;
                result.TrainerConnections = trainerCompile.Connections.Count;
                if (!trainerCompile.Success)
                {
                    result.Success = false;
                    result.Error = "The in-game trainer did not compile:\n" + string.Join("\n", trainerCompile.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    return result;
                }
                config.TrainerBlockId = result.TrainerBlock.EditorId;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "Could not create the in-game trainer: " + ex.Message;
                return result;
            }
        }
        else
        {
            RemoveTrainerBlock(level, config);
        }

        level.NeuralLab = config.Clone();
        level.NeuralLab.CodeBlockId = existing.EditorId;
        level.IsDirty = true;
        return result;
    }

    private static LevelNode EnsureTrainerBlock(GrabLevel level, NeuralLabConfig config, string source, LevelNode? control)
    {
        var existing = level.AllNodes().FirstOrDefault(n => n.EditorId == config.TrainerBlockId && n.Kind == NodeKind.GASM)
                       ?? level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM && n.Script.Contains(NeuralTrainerBlock.Marker, StringComparison.Ordinal));
        if (existing == null)
        {
            existing = new LevelNode
            {
                Name = (string.IsNullOrWhiteSpace(config.Name) ? "Neural Brain" : config.Name) + " Trainer",
                Kind = NodeKind.GASM,
                Position = (control?.Position ?? new Vec3(0, 2, 0)) + new Vec3(0, 3.4f, 0),
                LayerId = EnsureNeuralLayer(level),
                StartActive = true,
                LateUpdate = true
            };
            level.AddNode(existing);
        }
        existing.Script = source;
        existing.StartActive = true;
        existing.LateUpdate = true;
        existing.LayerId = EnsureNeuralLayer(level);
        return existing;
    }

    private static void RemoveTrainerBlock(GrabLevel level, NeuralLabConfig config)
    {
        var trainer = level.AllNodes().FirstOrDefault(n => n.EditorId == config.TrainerBlockId && n.Kind == NodeKind.GASM)
                      ?? level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM && n.Script.Contains(NeuralTrainerBlock.Marker, StringComparison.Ordinal));
        if (trainer != null) level.RemoveNode(trainer);
        config.TrainerBlockId = "";
    }

    private static string EnsureNeuralLayer(GrabLevel level)
    {
        level.EnsureLayers();
        var layer = level.EditorLayers.FirstOrDefault(l => l.Id == "neural");
        if (layer == null)
        {
            layer = new EditorLayer { Id = "neural", Name = "Neural", Color = "#35C6A5" };
            level.EditorLayers.Add(layer);
        }
        return layer.Id;
    }

    private static string FieldName(int layer, int neuron, int input, bool bias) =>
        bias ? $"b{layer}_{neuron}" : $"w{layer}_{neuron}_{input}";

    private static string F(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture) + "f";

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
