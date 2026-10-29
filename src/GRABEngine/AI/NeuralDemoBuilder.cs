using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GrabEngine.Code;
using GrabEngine.Core;
using GrabEngine.Formats;

namespace GrabEngine.AI;

public sealed class NeuralDemoFiles
{
    public string ProjectPath { get; init; } = "";
    public string LevelPath { get; init; } = "";
    public string ReadmePath { get; init; } = "";
    public string Script { get; init; } = "";
    public int Instructions { get; init; }
    public int Connections { get; init; }
    public int ContinuationPages { get; init; }
}

/// <summary>
/// Turns a trained network into a real GRAB level. The network is baked into a
/// normal C# Code Block as constants; the compiler emits the GASM that moves the
/// ball by writing its Position registers. Nothing in the level needs the editor,
/// so the exported .level runs in retail GRAB.
/// </summary>
public static class NeuralDemoBuilder
{
    public const string ScriptClassName = "NeuralBall";
    public const string BallName = "Ball";
    public const string TargetName = "Target";

    public static string BuildScript(NeuralNetwork net, NeuralTrainingReport? report = null)
    {
        if (net.Inputs != 4 || net.Outputs != 2)
            throw new ArgumentException("The generated controller expects a 4-input, 2-output network.");
        int inputs = net.Inputs;
        int hidden = net.Hidden;
        int outputs = net.Outputs;
        int w1 = inputs * hidden;
        int b1 = w1;
        int w2 = b1 + hidden;
        int b2 = w2 + hidden * outputs;

        string F(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture) + "f";
        string Name(string prefix, int a, int b) => prefix + a + "_" + b;

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// GRAB ENGINE — Neural Lab. This is ordinary C# that compiles to real GASM.");
        sb.AppendLine(hidden == 0
            ? "// A 4-input -> 2-output neural policy drives a kinematic ball toward"
            : "// A 4-input -> ReLU hidden -> 2-output network drives a kinematic ball toward");
        sb.AppendLine("// the target. The trained weights are constants, so the whole controller runs");
        sb.AppendLine("// inside GRAB with no editor or external process.");
        if (report != null)
        {
            sb.AppendLine($"// Trained {report.GenerationsRun} generations, validation success {report.SuccessRate:P0}.");
            sb.AppendLine("// " + report.StopReason.Replace("\n", " "));
        }
        sb.AppendLine("public class " + ScriptClassName + " : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine($"    GameObject ball = GameObject.Find(\"{BallName}\");");
        sb.AppendLine($"    GameObject target = GameObject.Find(\"{TargetName}\");");
        sb.AppendLine();
        sb.AppendLine("    float vx = 0.0f;");
        sb.AppendLine("    float vz = 0.0f;");
        sb.AppendLine("    bool arrived = false;");
        sb.AppendLine();
        sb.AppendLine("    const float Arena = 6.5f;");
        sb.AppendLine("    const float Accel = 22.0f;");
        sb.AppendLine("    const float Drag = 3.0f;");
        sb.AppendLine();
        if (hidden == 0)
        {
            sb.AppendLine("    // Single-layer neural policy: acceleration is a weighted sum of the");
            sb.AppendLine("    // normalized target direction and current velocity, plus a bias.");
            for (int o = 0; o < outputs; o++)
            {
                for (int i = 0; i < inputs; i++)
                    sb.AppendLine($"    const float {Name("O", o, i)} = {F(net.Weights[o * inputs + i])};");
                sb.AppendLine($"    const float O{o}_B = {F(net.Weights[inputs * outputs + o])};");
            }
        }
        else
        {
            sb.AppendLine("    // Hidden layer: ReLU(dx*w0 + dz*w1 + vx*w2 + vz*w3 + bias).");
            for (int h = 0; h < hidden; h++)
            {
                for (int i = 0; i < inputs; i++)
                    sb.AppendLine($"    const float {Name("H", h, i)} = {F(net.Weights[h * inputs + i])};");
                sb.AppendLine($"    const float H{h}_B = {F(net.Weights[b1 + h])};");
            }
            sb.AppendLine();
            sb.AppendLine("    // Output layer: linear acceleration in X/Z, clamped to [-1, 1].");
            for (int o = 0; o < outputs; o++)
            {
                for (int h = 0; h < hidden; h++)
                    sb.AppendLine($"    const float {Name("O", o, h)} = {F(net.Weights[w2 + o * hidden + h])};");
                sb.AppendLine($"    const float O{o}_B = {F(net.Weights[b2 + o])};");
            }
        }
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (arrived)");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        float dx = (target.Position.X - ball.Position.X) / 13.0f;");
        sb.AppendLine("        float dz = (target.Position.Z - ball.Position.Z) / 13.0f;");
        sb.AppendLine("        float nx = vx / 7.5f;");
        sb.AppendLine("        float nz = vz / 7.5f;");
        sb.AppendLine();
        if (hidden == 0)
        {
            for (int o = 0; o < outputs; o++)
            {
                sb.AppendLine($"        float {(o == 0 ? "ax" : "az")} = dx * {Name("O", o, 0)} + dz * {Name("O", o, 1)} + nx * {Name("O", o, 2)} + nz * {Name("O", o, 3)} + O{o}_B;");
            }
        }
        else
        {
            for (int h = 0; h < hidden; h++)
            {
                sb.AppendLine($"        float h{h} = Mathf.Max(0.0f,");
                sb.AppendLine($"            dx * {Name("H", h, 0)} + dz * {Name("H", h, 1)} + nx * {Name("H", h, 2)} + nz * {Name("H", h, 3)} + H{h}_B);");
            }
            sb.AppendLine();
            for (int o = 0; o < outputs; o++)
            {
                var terms = Enumerable.Range(0, hidden).Select(h => $"h{h} * {Name("O", o, h)}");
                sb.AppendLine($"        float {(o == 0 ? "ax" : "az")} = " + string.Join(" + ", terms) + $" + O{o}_B;");
            }
        }
        sb.AppendLine("        ax = Mathf.Clamp(ax, -1.0f, 1.0f);");
        sb.AppendLine("        az = Mathf.Clamp(az, -1.0f, 1.0f);");
        sb.AppendLine();
        sb.AppendLine("        vx = vx + ax * Accel * Time.DeltaTime;");
        sb.AppendLine("        vz = vz + az * Accel * Time.DeltaTime;");
        sb.AppendLine("        float damping = 1.0f - Drag * Time.DeltaTime;");
        sb.AppendLine("        vx = vx * damping;");
        sb.AppendLine("        vz = vz * damping;");
        sb.AppendLine();
        sb.AppendLine("        float px = ball.Position.X + vx * Time.DeltaTime;");
        sb.AppendLine("        float pz = ball.Position.Z + vz * Time.DeltaTime;");
        sb.AppendLine();
        sb.AppendLine("        if (px < -Arena) { px = -Arena; vx = 0.0f; }");
        sb.AppendLine("        if (px > Arena) { px = Arena; vx = 0.0f; }");
        sb.AppendLine("        if (pz < -Arena) { pz = -Arena; vz = 0.0f; }");
        sb.AppendLine("        if (pz > Arena) { pz = Arena; vz = 0.0f; }");
        sb.AppendLine();
        sb.AppendLine("        // Pin the finished ball to the target. This also keeps a split");
        sb.AppendLine("        // continuation program from drifting it after the arrival frame.");
        sb.AppendLine("        if (arrived)");
        sb.AppendLine("        {");
        sb.AppendLine("            ball.Position.X = target.Position.X;");
        sb.AppendLine("            ball.Position.Z = target.Position.Z;");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            ball.Position.X = px;");
        sb.AppendLine("            ball.Position.Z = pz;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        float rx = target.Position.X - px;");
        sb.AppendLine("        float rz = target.Position.Z - pz;");
        sb.AppendLine($"        if (rx * rx + rz * rz < {F(NeuralArena.SuccessRadius * NeuralArena.SuccessRadius)})");
        sb.AppendLine("        {");
        sb.AppendLine("            arrived = true;");
        sb.AppendLine("            vx = 0.0f;");
        sb.AppendLine("            vz = 0.0f;");
        sb.AppendLine("            ball.Position.X = target.Position.X;");
        sb.AppendLine("            ball.Position.Z = target.Position.Z;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static GrabLevel BuildLevel(NeuralTrainingReport report)
    {
        var net = report.Network;
        var level = new GrabLevel
        {
            Title = "Neural Ball — Learned Target Seeking",
            Creators = "GRAB ENGINE — Neural Lab",
            Description = $"A 4-{net.Hidden}-2 neural network trained with a genetic algorithm for {report.GenerationsRun} generations. " +
                          $"Validation success {report.SuccessRate:P0}. {report.StopReason}",
            FormatVersion = 25,
            ShowReplays = true
        };
        level.EditorLayers.Add(new EditorLayer { Id = GrabLevel.DefaultLayerId, Name = "Arena", Color = "#4A90D9" });
        level.EditorLayers.Add(new EditorLayer { Id = "ai", Name = "Neural Network", Color = "#35C6A5" });
        level.EditorLayers.Add(new EditorLayer { Id = "lights", Name = "Lighting", Color = "#D9A441" });

        var floor = new LevelNode
        {
            Name = "Arena Floor",
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = 8,
            Position = new Vec3(0, -0.5f, 0),
            Scale = new Vec3(15, 1, 15),
            Color1 = new Col(0.07f, 0.09f, 0.14f),
            LayerId = GrabLevel.DefaultLayerId
        };
        level.AddNode(floor);

        AddWall(level, "Wall North", new Vec3(0, 1, -7.25f), new Vec3(15, 2, 0.5f));
        AddWall(level, "Wall South", new Vec3(0, 1, 7.25f), new Vec3(15, 2, 0.5f));
        AddWall(level, "Wall West", new Vec3(-7.25f, 1, 0), new Vec3(0.5f, 2, 15));
        AddWall(level, "Wall East", new Vec3(7.25f, 1, 0), new Vec3(0.5f, 2, 15));

        // Neon corner posts make the arena readable at night and show off GRAB's
        // additive/neon block material.
        var posts = new[]
        {
            (-7.05f, -7.05f), (7.05f, -7.05f), (-7.05f, 7.05f), (7.05f, 7.05f)
        };
        int postIndex = 1;
        foreach (var (x, z) in posts)
        {
            level.AddNode(new LevelNode
            {
                Name = "Post " + postIndex++,
                Kind = NodeKind.Static,
                Shape = ShapeInfo.Cube,
                Material = 8,
                Position = new Vec3(x, 2.2f, z),
                Scale = new Vec3(0.22f, 4.4f, 0.22f),
                Color1 = new Col(0.15f, 0.75f, 1f),
                IsNeon = true
            });
        }

        var target = new LevelNode
        {
            Name = TargetName,
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = 8,
            Position = new Vec3(NeuralArena.TargetX, 0.7f, NeuralArena.TargetZ),
            Scale = new Vec3(1.4f, 1.4f, 1.4f),
            Color1 = new Col(0.2f, 1f, 0.42f),
            IsNeon = true
        };
        level.AddNode(target);

        var ball = new LevelNode
        {
            Name = BallName,
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Sphere,
            Material = 8,
            Position = new Vec3(NeuralArena.DemoStartX, 0.5f, NeuralArena.DemoStartZ),
            Scale = new Vec3(1, 1, 1),
            Color1 = new Col(0.05f, 0.85f, 1f),
            IsNeon = true
        };
        level.AddNode(ball);

        level.AddNode(new LevelNode
        {
            Name = "Target Glow",
            Kind = NodeKind.Light,
            Position = new Vec3(NeuralArena.TargetX, 3.4f, NeuralArena.TargetZ),
            LightColor = new Col(0.3f, 1f, 0.5f),
            LightIntensity = 4.0f,
            LightRange = 7.5f,
            DistanceFalloffShape = 2.2f,
            LayerId = "lights"
        });
        level.AddNode(new LevelNode
        {
            Name = "Arena Fill",
            Kind = NodeKind.Light,
            Position = new Vec3(0, 7.5f, 0),
            LightColor = new Col(0.55f, 0.7f, 1f),
            LightIntensity = 1.35f,
            LightRange = 20f,
            DistanceFalloffShape = 1.4f,
            LayerId = "lights"
        });

        level.AddNode(new LevelNode
        {
            Name = "Goal Sparks",
            Kind = NodeKind.ParticleEmitter,
            Position = new Vec3(NeuralArena.TargetX, 1.6f, NeuralArena.TargetZ),
            ParticlesPerSecond = 26,
            LifeSpanMin = 0.8f,
            LifeSpanMax = 1.9f,
            StartColor = new Col(0.35f, 1f, 0.5f, 1f),
            EndColor = new Col(0.35f, 1f, 0.5f, 0f),
            StartSizeMin = 0.12f,
            StartSizeMax = 0.24f,
            EndSizeMin = 0f,
            EndSizeMax = 0.02f,
            Velocity = new Vec3(0, 1.1f, 0),
            VelocityMin = new Vec3(-0.35f, 0.5f, -0.35f),
            VelocityMax = new Vec3(0.35f, 1.8f, 0.35f),
            LayerId = "lights"
        });

        var sign = new LevelNode
        {
            Name = "Neural Ball Sign",
            Kind = NodeKind.Sign,
            Position = new Vec3(0, 3.3f, -7.05f),
            Rotation = Quat.Identity,
            Text = $"NEURAL BALL\n{(net.Hidden == 0 ? "linear network" : net.Hidden + "-neuron network")}\n{report.GenerationsRun} generations\nsuccess {report.SuccessRate:P0}\n{ShortStop(report.StopReason)}",
            SignScale = 2.2f,
            SignColor = new Col(0.55f, 0.95f, 1f),
            SignNeon = true
        };
        level.AddNode(sign);

        // Spawn just behind the ball so the player sees it set off, and put the finish
        // under the target so the level's goal and the network's goal are the same place.
        var start = new LevelNode
        {
            Name = "Start",
            Kind = NodeKind.Start,
            Position = new Vec3(NeuralArena.DemoStartX - 1.1f, 0, NeuralArena.DemoStartZ - 1.1f),
            Radius = 0.5f,
            Rotation = Quat.FromEulerDegrees(new Vec3(0, 45, 0))
        };
        level.AddNode(start);
        level.AddNode(new LevelNode
        {
            Name = "Finish",
            Kind = NodeKind.Finish,
            Position = new Vec3(NeuralArena.TargetX, 0, NeuralArena.TargetZ),
            Radius = 1.2f
        });

        var script = BuildScript(net, report);
        var block = new LevelNode
        {
            Name = "Neural Ball",
            Kind = NodeKind.GASM,
            Position = new Vec3(0, 0.2f, -8.6f),
            Script = script,
            StartActive = true,
            LayerId = "ai"
        };
        level.AddNode(block);
        level.DefaultSpawnPointId = level.ObjectIdOf(start);
        level.EnsureNames();

        var compiled = new CSharpCompiler().Compile(level, block, script);
        block.Compiled = compiled;
        if (!compiled.Success)
        {
            var errors = string.Join("\n", compiled.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException("The generated neural controller did not compile:\n" + errors);
        }
        return level;
    }

    private static string ShortStop(string reason)
    {
        if (reason.StartsWith("Solved", StringComparison.OrdinalIgnoreCase)) return "target reached";
        if (reason.StartsWith("Early stop", StringComparison.OrdinalIgnoreCase)) return "early stopped";
        return "generation limit";
    }

    private static void AddWall(GrabLevel level, string name, Vec3 position, Vec3 scale)
    {
        level.AddNode(new LevelNode
        {
            Name = name,
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = 8,
            Position = position,
            Scale = scale,
            Color1 = new Col(0.14f, 0.17f, 0.24f)
        });
    }

    private static AmbienceSettings NightAmbience() => new()
    {
        SkyZenithColor = new Col(0.012f, 0.018f, 0.055f),
        SkyHorizonColor = new Col(0.09f, 0.12f, 0.21f),
        SunAltitude = 11f,
        SunAzimuth = 302f,
        SunSize = 1.15f,
        FogDensity = 0.05f,
        UseAdvancedSunSettings = true,
        SunColor = new Col(0.55f, 0.66f, 0.95f),
        SunBrightness = 0.32f,
        AmbientColor = new Col(0.5f, 0.62f, 1f),
        AmbientBrightness = 0.52f,
        StarDensity = 0.85f,
        StarClustering = 0.35f,
        StarRotationEuler = new Vec3(14, 28, 0),
        StarSize = 1.15f,
        StarVisibility = 0.95f,
        CloudColor = new Col(0.24f, 0.29f, 0.42f),
        CloudShading = 0.85f,
        CloudDensity = 0.22f,
        CloudHeight = 110f,
        CloudSpeedX = 1.4f,
        CloudSpeedZ = 0.55f
    };

    public static NeuralDemoFiles SaveDemo(NeuralTrainingReport report, string projectPath)
    {
        var level = BuildLevel(report);
        level.Ambience = NightAmbience();
        string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(dir);
        string levelPath = Path.ChangeExtension(projectPath, ".level");
        string readmePath = Path.Combine(dir, "README.md");

        ProjectIO.Save(level, projectPath);
        File.WriteAllBytes(levelPath, LevelIO.Write(level));

        var readme = new StringBuilder();
        readme.AppendLine("# Neural Ball — GRAB ENGINE Neural Lab");
        readme.AppendLine();
        readme.AppendLine("A trained neural network that steers a ball to the target cube inside a walled arena.");
        readme.AppendLine();
        readme.AppendLine("## Files");
        readme.AppendLine();
        readme.AppendLine("- `" + Path.GetFileName(projectPath) + "` — open this in GRAB ENGINE.");
        readme.AppendLine("- `" + Path.GetFileName(levelPath) + "` — the same level exported to real GRAB.");
        readme.AppendLine();
        readme.AppendLine("## Network");
        readme.AppendLine();
        readme.AppendLine("- 4 inputs: normalized target direction (X, Z) and ball velocity (X, Z).");
        readme.AppendLine(report.Network.Hidden == 0
            ? "- Single-layer neural policy, 2 linear outputs (acceleration X/Z)."
            : FormattableString.Invariant($"- {report.Network.Hidden} ReLU hidden neurons, 2 linear outputs (acceleration X/Z)."));
        readme.AppendLine(FormattableString.Invariant($"- {report.Network.WeightCount} weights baked into the Code Block as constants."));
        readme.AppendLine();
        readme.AppendLine("## Training");
        readme.AppendLine();
        readme.AppendLine(FormattableString.Invariant($"- Population {report.Options.Population}, {report.GenerationsRun} generations, seed {report.Options.Seed}."));
        readme.AppendLine(FormattableString.Invariant($"- Validation success: {report.SuccessRate:P0} across {report.Validation.Count} starts."));
        readme.AppendLine("- Stop reason: " + report.StopReason);
        readme.AppendLine(FormattableString.Invariant($"- Demo run: {report.DemoRun?.Describe() ?? "n/a"}"));
        readme.AppendLine();
        readme.AppendLine("The network learns in the editor. GASM has no runtime learning opcode, so the trained weights are baked into ordinary GASM instructions; the exported level therefore behaves the same in retail GRAB.");
        readme.AppendLine();
        readme.AppendLine("To retrain, open **Tools → Neural Lab…**, adjust the population/generations/patience, and press **Train**. Early stopping stops a run that stops making progress.");
        File.WriteAllText(readmePath, readme.ToString());

        return new NeuralDemoFiles
        {
            ProjectPath = projectPath,
            LevelPath = levelPath,
            ReadmePath = readmePath,
            Script = level.AllNodes().First(n => n.Kind == NodeKind.GASM).Script,
            Instructions = level.AllNodes().First(n => n.Kind == NodeKind.GASM).Compiled?.Program.Instructions.Count ?? 0,
            Connections = level.AllNodes().First(n => n.Kind == NodeKind.GASM).Compiled?.Connections.Count ?? 0,
            ContinuationPages = level.AllNodes().First(n => n.Kind == NodeKind.GASM).Compiled?.Continuations.Count ?? 0
        };
    }

}
