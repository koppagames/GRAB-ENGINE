using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Audio;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.UI;

public sealed class InspectorContext
{
    public GrabLevel Level { get; init; } = null!;
    public LevelNode? Node { get; init; }
    public IReadOnlyList<LevelNode> Nodes { get; init; } = Array.Empty<LevelNode>();
    public Action Changed { get; init; } = () => { };
    public Action Rebuild { get; init; } = () => { };
    public Action OpenCode { get; init; } = () => { };
    public Action CompileCode { get; init; } = () => { };
    public Action OpenAnimation { get; init; } = () => { };
    public Action? Duplicate { get; init; }
    public Action? Delete { get; init; }
    public Action<string> Status { get; init; } = _ => { };
    public Action<LevelNode> Select { get; init; } = _ => { };
}

public static class InspectorBuilder
{
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(125, 125, 125));
    private static readonly Brush SectionBrush = new SolidColorBrush(Color.FromRgb(56, 56, 56));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(38, 38, 38));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));

    public static void Build(Panel host, InspectorContext ctx)
    {
        host.Children.Clear();
        if (ctx.Nodes.Count > 1)
        {
            BuildMulti(host, ctx);
            return;
        }
        if (ctx.Node == null)
        {
            BuildLevel(host, ctx);
            AddSeparator(host);
            AddSection(host, "AMBIENCE / SKY");
            BuildAmbience(host, ctx);
            return;
        }
        BuildNode(host, ctx, ctx.Node);
    }

    private static void BuildMulti(Panel host, InspectorContext ctx)
    {
        var nodes = ctx.Nodes;
        var active = ctx.Node;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = $"{nodes.Count} objects selected",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = active != null ? $"Active: {active.DisplayName}" : "Ctrl-click to add or remove objects",
            FontSize = 10.5,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 2, 0, 0)
        });
        host.Children.Add(header);
        AddSeparator(host);

        AddSection(host, "SELECTION");
        var counts = nodes.GroupBy(n => NodeKindInfo.Display(n.Kind))
            .Select(g => $"{g.Count()} {g.Key}")
            .OrderBy(s => s);
        Text(host, string.Join("   ", counts));
        Text(host, "Use the Move / Rotate / Scale gizmo to transform the whole selection around its center. Ctrl-click in the scene or hierarchy adds and removes objects.");

        AddSection(host, "SNAP");
        var snapHint = new TextBlock
        {
            Text = "Snaps every selected root to the scene grid. Hold Ctrl while dragging the gizmo to bypass snapping.",
            Foreground = MutedBrush,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 5)
        };
        host.Children.Add(snapHint);
        var snap = SmallButton("Snap Selection to Grid");
        snap.HorizontalAlignment = HorizontalAlignment.Left;
        snap.Click += (_, _) =>
        {
            foreach (var n in nodes)
            {
                if (n.Parent != null && nodes.Contains(n.Parent)) continue;
                var p = n.WorldPosition;
                var s = Math.Max(0.001, ctx.Level.GridSnap);
                var world = new Vec3(MathF.Round(p.X / (float)s) * (float)s,
                    MathF.Round(p.Y / (float)s) * (float)s, MathF.Round(p.Z / (float)s) * (float)s);
                if (n.Parent == null) n.Position = world;
                else
                {
                    var rel = world - n.Parent.WorldPosition;
                    var local = n.Parent.WorldRotation.Inverse().Rotate(rel);
                    var ps = n.Parent.WorldScale;
                    n.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f), local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
                }
            }
            ctx.Changed();
            ctx.Rebuild();
        };
        host.Children.Add(snap);

        var blocks = nodes.Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling).ToList();
        if (blocks.Count > 0)
        {
            AddSection(host, "BLOCK MATERIAL");
            Text(host, $"Applies to {blocks.Count} block(s).");
            int material = blocks[0].Material;
            var mats = MaterialInfo.All.Select(m => m.Name).ToArray();
            int idx = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == material));
            ComboField(host, "Material", mats, idx, v =>
            {
                foreach (var n in blocks) n.Material = MaterialInfo.All[v].Id;
                ctx.Changed();
                ctx.Rebuild();
            });
            if (blocks.All(n => MaterialInfo.IsColorable(n.Material)))
                ColorField(host, "Color 1 (paint)", blocks[0].Color1, v =>
                {
                    foreach (var n in blocks) n.Color1 = v;
                    ctx.Changed();
                }, getNoTexture: () => blocks[0].NoTexture, setNoTexture: v =>
                {
                    foreach (var n in blocks) n.NoTexture = v;
                    ctx.Changed();
                    ctx.Rebuild();
                });
            else
                Text(host, "Some materials use their own fixed texture colour; GRAB only paints Default Colored.");
            var statics = blocks.Where(n => n.Kind == NodeKind.Static).ToList();
            if (statics.Count > 0)
                CheckField(host, "No texture (flat)", statics.All(n => n.NoTexture), v =>
                {
                    foreach (var n in statics) n.NoTexture = v;
                    ctx.Changed();
                    ctx.Rebuild();
                });
        }

        AddSection(host, "ACTIONS");
        var actions = new WrapPanel();
        var dup = SmallButton("Duplicate");
        dup.Margin = new Thickness(0, 0, 4, 4);
        dup.Click += (_, _) => ctx.Duplicate?.Invoke();
        actions.Children.Add(dup);
        var del = SmallButton("Delete");
        del.Margin = new Thickness(0, 0, 4, 4);
        del.Click += (_, _) => ctx.Delete?.Invoke();
        actions.Children.Add(del);
        host.Children.Add(actions);

        host.Children.Add(new ListBox
        {
            ItemsSource = nodes.Select(n => n.DisplayName).ToList(),
            MaxHeight = 170,
            Margin = new Thickness(0, 4, 0, 0),
            Background = FieldBg,
            Foreground = Brushes.White,
            BorderBrush = BorderBrush
        });
    }

    private static void BuildNode(Panel host, InspectorContext ctx, LevelNode n)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var row = new DockPanel();
        var title = new TextBlock { Text = NodeKindInfo.Display(n.Kind), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var tag = new Border { Background = new SolidColorBrush(Color.FromRgb(48, 80, 112)), CornerRadius = new CornerRadius(2), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tag.Child = new TextBlock { Text = n.Kind.ToString(), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(165, 210, 255)) };
        DockPanel.SetDock(tag, Dock.Right);
        row.Children.Add(tag);
        row.Children.Add(title);
        header.Children.Add(row);
        host.Children.Add(header);
        AddSeparator(host);

        AddSection(host, "OBJECT");
        TextField(host, "Name", n.Name, v => { n.Name = v; ctx.Changed(); ctx.Rebuild(); });
        CheckField(host, "Locked", n.IsLocked, v => { n.IsLocked = v; ctx.Changed(); });

        if (n.Kind != NodeKind.Group)
        {
            AddSection(host, "TRANSFORM");
            VectorField(host, "Position", n.Position, v => { n.Position = v; ctx.Changed(); });
            if (n.Kind is not (NodeKind.Finish or NodeKind.Start))
                VectorField(host, "Rotation", n.Rotation.ToEulerDegrees(), v => { n.Rotation = Quat.FromEulerDegrees(v); ctx.Changed(); });
            else if (n.Kind == NodeKind.Start)
                VectorField(host, "Rotation", n.Rotation.ToEulerDegrees(), v => { n.Rotation = Quat.FromEulerDegrees(v); ctx.Changed(); });
            if (n.Kind is NodeKind.Static or NodeKind.Crumbling or NodeKind.Trigger or NodeKind.Gravity or NodeKind.GASM or NodeKind.Group or NodeKind.ParticleEmitter)
                VectorField(host, "Scale", n.Scale, v => { n.Scale = v; ctx.Changed(); });
            if (n.Kind is NodeKind.Start or NodeKind.Finish)
                FloatField(host, "Radius", n.Radius, 0.05f, 20f, 0.05f, 2, v => { n.Radius = v; ctx.Changed(); });
        }
        else
        {
            AddSection(host, "GROUP TRANSFORM");
            VectorField(host, "Position", n.Position, v => { n.Position = v; ctx.Changed(); });
            VectorField(host, "Rotation", n.Rotation.ToEulerDegrees(), v => { n.Rotation = Quat.FromEulerDegrees(v); ctx.Changed(); });
            VectorField(host, "Scale", n.Scale, v => { n.Scale = v; ctx.Changed(); });
            CheckField(host, "Physics Object", n.PhysicsObject, v => { n.PhysicsObject = v; ctx.Changed(); });
            CheckField(host, "Local Physics", n.LocalPhysicsObject, v => { n.LocalPhysicsObject = v; ctx.Changed(); });
            FloatField(host, "Mass", n.Mass, 0.01f, 100, 0.05f, 2, v => { n.Mass = v; ctx.Changed(); });
        }

        switch (n.Kind)
        {
            case NodeKind.Static:
                AddSection(host, "BLOCK");
                ShapeField(host, n, ctx);
                MaterialField(host, n, ctx);
                bool painted = MaterialInfo.IsColorable(n.Material);
                if (painted)
                {
                    ColorField(host, "Color 1 (paint)", n.Color1, v => { n.Color1 = v; ctx.Changed(); },
                        getNoTexture: () => n.NoTexture,
                        setNoTexture: v => { n.NoTexture = v; ctx.Changed(); ctx.Rebuild(); });
                    ColorField(host, "Color 2 (gradient)", n.Color2, v => { n.Color2 = v; ctx.Changed(); });
                }
                else
                {
                    Text(host, MaterialInfo.Display(n.Material) + " uses its own fixed texture colour. GRAB only paints the Default Colored material.");
                }
                CheckField(host, "No texture (flat)", n.NoTexture, v =>
                {
                    n.NoTexture = v;
                    ctx.Changed();
                    ctx.Rebuild();
                });
                if (n.NoTexture)
                    Text(host, "Exports Color 1 as 99999999, GRAB's \"no texture\" value. The block renders as a flat, untextured surface and ignores its paint colour.");
                FloatField(host, "Specularity", n.SpecularBrightness, 0, 10, 0.1f, 2, v => { n.SpecularBrightness = v; ctx.Changed(); });
                CheckField(host, "Neon", n.IsNeon, v => { n.IsNeon = v; ctx.Changed(); });
                CheckField(host, "Transparent", n.IsTransparent, v => { n.IsTransparent = v; ctx.Changed(); });
                CheckField(host, "Additive", n.IsAdditive, v => { n.IsAdditive = v; ctx.Changed(); });
                if (painted)
                {
                    CheckField(host, "Gradient", n.IsGradient, v => { n.IsGradient = v; ctx.Changed(); });
                    if (n.IsGradient)
                        VectorField(host, "Gradient Dir", n.GradientDirection, v => { n.GradientDirection = v; ctx.Changed(); });
                }
                CheckField(host, "Grabbable", n.IsGrabbable, v => { n.IsGrabbable = v; ctx.Changed(); });
                CheckField(host, "Grapplable", n.IsGrapplable, v => { n.IsGrapplable = v; ctx.Changed(); });
                CheckField(host, "Passable", n.IsPassable, v => { n.IsPassable = v; ctx.Changed(); });
                break;
            case NodeKind.Crumbling:
                AddSection(host, "CRUMBLING BLOCK");
                ShapeField(host, n, ctx);
                MaterialField(host, n, ctx);
                FloatField(host, "Stable Time", n.StableTime, 0, 20, 0.1f, 2, v => { n.StableTime = v; ctx.Changed(); });
                FloatField(host, "Respawn Time", n.RespawnTime, 0, 60, 0.1f, 2, v => { n.RespawnTime = v; ctx.Changed(); });
                CheckField(host, "Local Only", n.IsLocal, v => { n.IsLocal = v; ctx.Changed(); });
                break;
            case NodeKind.Start:
                AddSection(host, "SPAWN POINT");
                CheckField(host, "Hidden", n.IsHidden, v => { n.IsHidden = v; ctx.Changed(); });
                Text(host, "This is a spawn point. The level's default spawn is chosen in the Level settings.");
                break;
            case NodeKind.Finish:
                AddSection(host, "FINISH POINT");
                Text(host, "Players touching this radius complete the level.");
                break;
            case NodeKind.Sign:
                AddSection(host, "SIGN");
                TextField(host, "Text", n.Text, v => { n.Text = v; ctx.Changed(); });
                FloatField(host, "Scale", n.SignScale, 0.05f, 20, 0.05f, 2, v => { n.SignScale = v; n.Scale = new Vec3(v, v, v); ctx.Changed(); });
                ColorField(host, "Color", n.SignColor, v => { n.SignColor = v; ctx.Changed(); });
                ComboField(host, "Weight", new[] { "Regular", "Light", "Semibold", "Bold", "Italic" }, n.SignWeight, v => { n.SignWeight = v; ctx.Changed(); });
                CheckField(host, "Hide Model", n.HideModel, v => { n.HideModel = v; ctx.Changed(); });
                CheckField(host, "Neon", n.SignNeon, v => { n.SignNeon = v; ctx.Changed(); });
                break;
            case NodeKind.Gravity:
                AddSection(host, "GRAVITY ZONE");
                ComboField(host, "Mode", new[] { "Default", "No Legs", "Monkey" }, n.GravityMode, v => { n.GravityMode = v; ctx.Changed(); });
                VectorField(host, "Direction", n.GravityDirection, v => { n.GravityDirection = v; ctx.Changed(); });
                CheckField(host, "Hide Particles", n.HideParticles, v => { n.HideParticles = v; ctx.Changed(); });
                break;
            case NodeKind.Trigger:
                BuildTrigger(host, ctx, n);
                break;
            case NodeKind.ParticleEmitter:
                BuildParticles(host, ctx, n);
                break;
            case NodeKind.Sound:
                BuildSound(host, ctx, n);
                break;
            case NodeKind.Light:
                BuildLight(host, ctx, n);
                break;
            case NodeKind.GASM:
                BuildCodeBlock(host, ctx, n);
                break;
            case NodeKind.LobbyTerminal:
                Text(host, "Lobby terminals open the level browser in-game.");
                break;
        }

        if (n.Kind != NodeKind.GASM)
            BuildScriptComponents(host, ctx, n);
        BuildPrefabInstance(host, ctx, n);

        AddSection(host, "ANIMATIONS");
        BuildAnimations(host, ctx, n);
    }

    /// <summary>Unity-style components: every attached C# script is a normal Code
    /// Block shown under its host object and editable here.</summary>
    private static void BuildScriptComponents(Panel host, InspectorContext ctx, LevelNode hostNode)
    {
        var components = ctx.Level.AllNodes()
            .Where(x => x.ComponentHostId == hostNode.EditorId && x.Kind == NodeKind.GASM)
            .ToList();
        AddSection(host, "C# SCRIPT COMPONENTS");
        if (components.Count == 0)
            Text(host, "No scripts attached. Add one to give this object behaviour without a separate scene object.");
        foreach (var component in components)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
                BorderBrush = BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(6),
                Margin = new Thickness(0, 3, 0, 3)
            };
            var body = new StackPanel();
            var head = new DockPanel();
            var remove = SmallButton("✕");
            remove.ToolTip = "Remove this script component";
            remove.Click += (_, _) =>
            {
                foreach (var generated in ctx.Level.AllNodes().Where(x => x.GeneratedFor == component.EditorId || x.ScriptSourceBlock == component.EditorId).ToList())
                    ctx.Level.RemoveNode(generated);
                ctx.Level.RemoveNode(component);
                ctx.Changed();
                ctx.Rebuild();
            };
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            var edit = SmallButton("Edit");
            edit.Margin = new Thickness(0, 0, 4, 0);
            edit.Click += (_, _) => ctx.Select(component);
            DockPanel.SetDock(edit, Dock.Right);
            head.Children.Add(edit);
            head.Children.Add(new TextBlock
            {
                Text = $"⚙ {component.ComponentName ?? ScriptIntrospection.ClassName(component.Script)}",
                Foreground = Brushes.White, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center
            });
            body.Children.Add(head);
            body.Children.Add(new TextBlock
            {
                Text = component.Compiled?.Success == true
                    ? $"{component.Compiled.Program.Instructions.Count} GASM ops · {ScriptIntrospection.ClassName(component.Script)}"
                    : component.Compiled == null ? "Not compiled" : "Has compile errors",
                Foreground = MutedBrush, FontSize = 10, Margin = new Thickness(0, 2, 0, 0)
            });
            card.Child = body;
            host.Children.Add(card);
        }

        var add = SmallButton("+ Add C# Script");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 4, 0, 0);
        add.Click += (_, _) =>
        {
            var component = new LevelNode
            {
                Kind = NodeKind.GASM,
                Name = hostNode.DisplayName + " Script",
                ComponentHostId = hostNode.EditorId,
                ComponentName = "MyScript",
                Position = hostNode.WorldPosition,
                Rotation = hostNode.WorldRotation,
                StartActive = true,
                LayerId = hostNode.LayerId
            };
            component.Script = CodeTemplates.NewScript(ctx.Level, component);
            ctx.Level.AddNode(component);
            ctx.Changed();
            ctx.Select(component);
            ctx.Rebuild();
        };
        host.Children.Add(add);

        if (!string.IsNullOrWhiteSpace(hostNode.ComponentHostId))
        {
            var hostObject = ctx.Level.FindByEditorId(hostNode.ComponentHostId);
            if (hostObject != null)
            {
                var back = SmallButton("Select Host: " + hostObject.DisplayName);
                back.Margin = new Thickness(0, 5, 0, 0);
                back.Click += (_, _) => ctx.Select(hostObject);
                host.Children.Add(back);
            }
        }
    }

    private static string PrefabSourceName(LevelNode n)
        => string.IsNullOrWhiteSpace(n.PrefabSource) ? "" : System.IO.Path.GetFileNameWithoutExtension(n.PrefabSource);

    private static void BuildPrefabInstance(Panel host, InspectorContext ctx, LevelNode n)
    {
        if (string.IsNullOrWhiteSpace(n.PrefabSource)) return;
        var root = ctx.Level.AllNodes().FirstOrDefault(x => x.PrefabInstanceId == n.PrefabInstanceId && x.Parent == null) ?? n;
        AddSection(host, "PREFAB INSTANCE");
        Text(host, $"Source: {PrefabSourceName(n)}" + (n.PrefabInstanceId == root.PrefabInstanceId ? "" : "  (child)"));
        var overrides = PrefabLibrary.GetOverrides(ctx.Level, root);
        Text(host, overrides.Count == 0
            ? "No overrides. This instance matches the prefab asset."
            : $"{overrides.Count} override(s) differ from the prefab asset.");
        foreach (var ov in overrides.Take(30))
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var revert = SmallButton("Revert");
            revert.Click += (_, _) =>
            {
                if (PrefabLibrary.Revert(root, ov.Node, ov.Property))
                {
                    ctx.Changed();
                    ctx.Rebuild();
                }
            };
            DockPanel.SetDock(revert, Dock.Right);
            row.Children.Add(revert);
            row.Children.Add(new TextBlock
            {
                Text = $"{ov.Node.DisplayName} · {ov.Property}",
                Foreground = Brushes.White, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            host.Children.Add(row);
        }
        if (overrides.Count > 30) Text(host, $"+{overrides.Count - 30} more override(s)");

        var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var applyPrefab = SmallButton("Apply to Prefab");
        applyPrefab.Margin = new Thickness(0, 0, 4, 4);
        applyPrefab.ToolTip = "Write this instance's values into the prefab asset";
        applyPrefab.Click += (_, _) =>
        {
            PrefabLibrary.ApplyToPrefab(ctx.Level, root);
            ctx.Changed();
            ctx.Rebuild();
        };
        actions.Children.Add(applyPrefab);
        var applyAll = SmallButton("Apply to All Copies");
        applyAll.Margin = new Thickness(0, 0, 4, 4);
        applyAll.ToolTip = "Copy these values to every other instance of the same prefab";
        applyAll.Click += (_, _) =>
        {
            int updated = PrefabLibrary.ApplyToAllInstances(ctx.Level, root);
            ctx.Status($"Updated {updated} node(s) across prefab copies");
            ctx.Changed();
            ctx.Rebuild();
        };
        actions.Children.Add(applyAll);
        host.Children.Add(actions);

        var revertAll = SmallButton("Revert All Overrides");
        revertAll.Margin = new Thickness(0, 3, 0, 0);
        revertAll.Click += (_, _) =>
        {
            foreach (var ov in PrefabLibrary.GetOverrides(ctx.Level, root))
                PrefabLibrary.Revert(root, ov.Node, ov.Property);
            ctx.Changed();
            ctx.Rebuild();
        };
        host.Children.Add(revertAll);
    }

    private static void BuildScriptFields(Panel host, InspectorContext ctx, LevelNode n)
    {
        var info = ScriptIntrospection.Inspect(n.Script);
        if (info.Fields.Count == 0) return;
        AddSection(host, "SERIALIZED FIELDS  ·  " + info.Name);
        string currentHeader = "";
        foreach (var field in info.Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Header) && field.Header != currentHeader)
            {
                currentHeader = field.Header;
                var header = new TextBlock
                {
                    Text = field.Header.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 190, 235)), Margin = new Thickness(0, 6, 0, 2)
                };
                host.Children.Add(header);
            }
            string label = field.Label + (n.FieldOverrides.ContainsKey(field.Name) ? " *" : "");
            string value = n.FieldOverrides.TryGetValue(field.Name, out var over) && !string.IsNullOrWhiteSpace(over)
                ? over : field.DefaultText;
            void Commit(string text)
            {
                n.FieldOverrides[field.Name] = text;
                ctx.Changed();
                ctx.CompileCode();
            }
            if (field.IsBool)
            {
                bool checkedValue = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1";
                CheckField(host, label, checkedValue, v => Commit(v ? "true" : "false"));
            }
            else if (field.IsColor)
            {
                ColorField(host, label, Col.FromHex(value, Col.White), v => Commit(v.Hex));
            }
            else if (field.IsVector)
            {
                var nums = value.Replace("new", "").Replace("Vector3", "").Trim('(', ')', ' ')
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);
                float x = nums.Length > 0 && float.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var xx) ? xx : 0;
                float y = nums.Length > 1 && float.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var yy) ? yy : 0;
                float z = nums.Length > 2 && float.TryParse(nums[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var zz) ? zz : 0;
                VectorField(host, label, new Vec3(x, y, z), v => Commit($"new Vector3({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})"));
            }
            else if (field.IsString)
            {
                TextField(host, label, value.Trim('"'), v => Commit("\"" + v.Replace("\"", "'") + "\""));
            }
            else if (field.IsEnum)
            {
                TextField(host, label, value, v => Commit(v));
            }
            else
            {
                double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                float min = double.IsNaN(field.Min) ? -100000f : (float)field.Min;
                float max = double.IsNaN(field.Max) ? 100000f : (float)field.Max;
                if (!double.IsNaN(field.Min) && !double.IsNaN(field.Max))
                {
                    var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(number, min, max), Height = 22 };
                    var box = new TextBox { Text = number.ToString("0.###", CultureInfo.InvariantCulture), Width = 62, Margin = new Thickness(5, 0, 0, 0) };
                    var row = new DockPanel();
                    DockPanel.SetDock(box, Dock.Right);
                    row.Children.Add(box);
                    row.Children.Add(slider);
                    slider.ValueChanged += (_, _) =>
                    {
                        string text = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);
                        box.Text = text;
                        Commit(text);
                    };
                    box.LostFocus += (_, _) => Commit(box.Text);
                    host.Children.Add(Theme.Row(label, row));
                }
                else FloatField(host, label, (float)number, min, max, 0.01f, 3, v => Commit(v.ToString("0.###", CultureInfo.InvariantCulture)));
            }
            if (n.FieldOverrides.ContainsKey(field.Name))
            {
                var reset = SmallButton("Reset to script default");
                reset.Margin = new Thickness(0, 0, 0, 3);
                reset.Click += (_, _) =>
                {
                    n.FieldOverrides.Remove(field.Name);
                    ctx.Changed();
                    ctx.CompileCode();
                    ctx.Rebuild();
                };
                host.Children.Add(reset);
            }
            if (!string.IsNullOrWhiteSpace(field.Tooltip))
            {
                var tip = new TextBlock { Text = field.Tooltip, Foreground = MutedBrush, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
                host.Children.Add(tip);
            }
        }
        Text(host, "Field values are compiled into the GASM as constants. * marks an override.");
    }

    private static void ShapeField(Panel host, LevelNode n, InspectorContext ctx)
    {
        var shapes = ShapeInfo.All.Select(s => s.Name).ToArray();
        int idx = Array.FindIndex(ShapeInfo.All, s => s.Id == n.Shape);
        ComboField(host, "Shape", shapes, Math.Max(0, idx), v => { n.Shape = ShapeInfo.All[v].Id; ctx.Changed(); });
    }

    private static void MaterialField(Panel host, LevelNode n, InspectorContext ctx)
    {
        var mats = MaterialInfo.All.Select(m => m.Name).ToArray();
        int idx = Array.FindIndex(MaterialInfo.All, m => m.Id == n.Material);
        ComboField(host, "Material", mats, Math.Max(0, idx), v =>
        {
            n.Material = MaterialInfo.All[v].Id;
            ctx.Changed();
            // Rebuild so the paint-colour row appears only for Default Colored.
            ctx.Rebuild();
        });
    }

    private static void BuildTrigger(Panel host, InspectorContext ctx, LevelNode n)
    {
        AddSection(host, "TRIGGER ZONE");
        ShapeField(host, n, ctx);
        CheckField(host, "Shared", n.IsSharedTrigger, v => { n.IsSharedTrigger = v; ctx.Changed(); });
        AddSection(host, "SOURCES");
        foreach (var s in n.TriggerSources.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var del = SmallButton("✕");
            del.Click += (_, _) => { n.TriggerSources.Remove(s); ctx.Changed(); ctx.Rebuild(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(new TextBlock { Text = s.Display(), Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            host.Children.Add(row);
        }
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var kind = new ComboBox { Width = 100, ItemsSource = new[] { "Hand", "Head", "Grapple", "Feet", "Block", "Spawn" }, SelectedIndex = 0 };
        var btn = SmallButton("+ Add Source");
        btn.Margin = new Thickness(5, 0, 0, 0);
        btn.Click += (_, _) => { n.TriggerSources.Add(new TriggerSource { BasicType = kind.SelectedIndex }); ctx.Changed(); ctx.Rebuild(); };
        add.Children.Add(kind); add.Children.Add(btn);
        host.Children.Add(add);

        AddSection(host, "TARGETS");
        foreach (var t in n.TriggerTargets.ToList())
        {
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(44, 44, 44)), BorderBrush = BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5), Margin = new Thickness(0, 3, 0, 3) };
            var sp = new StackPanel();
            var head = new DockPanel();
            var del = SmallButton("✕");
            del.Click += (_, _) => { n.TriggerTargets.Remove(t); ctx.Changed(); ctx.Rebuild(); };
            DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(del);
            head.Children.Add(new TextBlock { Text = t.Display(), Foreground = Brushes.White, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(head);
            var mode = new ComboBox { Margin = new Thickness(0, 3, 0, 0), ItemsSource = new[] { "On Enter", "On Leave", "Enter + Leave", "None" }, SelectedIndex = Math.Clamp(t.Mode, 0, 3) };
            mode.SelectionChanged += (_, _) => { t.Mode = mode.SelectedIndex; ctx.Changed(); };
            sp.Children.Add(mode);
            if (t.Kind == 1)
            {
                var name = new TextBox { Text = t.AnimationName, Margin = new Thickness(0, 3, 0, 0) };
                name.LostFocus += (_, _) => { t.AnimationName = name.Text; ctx.Changed(); };
                sp.Children.Add(name);
                var flags = new StackPanel { Orientation = Orientation.Horizontal };
                var loop = new CheckBox { Content = "Loop", IsChecked = t.Loop, Foreground = Brushes.White, Margin = new Thickness(0, 2, 8, 0) };
                loop.Checked += (_, _) => { t.Loop = true; ctx.Changed(); };
                loop.Unchecked += (_, _) => { t.Loop = false; ctx.Changed(); };
                var rev = new CheckBox { Content = "Reverse", IsChecked = t.Reverse, Foreground = Brushes.White };
                rev.Checked += (_, _) => { t.Reverse = true; ctx.Changed(); };
                rev.Unchecked += (_, _) => { t.Reverse = false; ctx.Changed(); };
                flags.Children.Add(loop); flags.Children.Add(rev);
                sp.Children.Add(flags);
            }
            else if (t.Kind == 6)
            {
                var dur = new TextBox { Text = t.ChangeDuration.ToString("0.##"), Margin = new Thickness(0, 3, 0, 0), ToolTip = "Change duration (seconds)" };
                dur.LostFocus += (_, _) => { if (float.TryParse(dur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { t.ChangeDuration = v; ctx.Changed(); } };
                sp.Children.Add(dur);
            }
            else if (t.Kind == 8)
            {
                sp.Children.Add(new TextBlock { Text = $"Range {t.Range:0.##} • Brightness {t.Brightness:0.##} • Fade {t.FadeDuration:0.##}s", Foreground = MutedBrush, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
            }
            card.Child = sp;
            host.Children.Add(card);
        }
        var kinds = new[] { "Animation", "GASM", "Sound", "Light", "Ambience" };
        var pickNode = new ComboBox { Width = 130, ItemsSource = ctx.Level.AllNodes().Where(x => x.Kind is NodeKind.GASM or NodeKind.Light or NodeKind.Sound or NodeKind.Static or NodeKind.Crumbling or NodeKind.Sign or NodeKind.Trigger).Select(x => x.DisplayName).ToArray() };
        if (pickNode.Items.Count > 0) pickNode.SelectedIndex = 0;
        var pickKind = new ComboBox { Width = 100, ItemsSource = kinds, SelectedIndex = 0, Margin = new Thickness(4, 0, 0, 0) };
        var addTarget = SmallButton("+ Target");
        addTarget.Margin = new Thickness(4, 0, 0, 0);
        addTarget.Click += (_, _) =>
        {
            var target = new TriggerTarget { Mode = 0 };
            int kindIdx = pickKind.SelectedIndex;
            switch (kindIdx)
            {
                case 0:
                    target.Kind = 1;
                    var obj = ctx.Level.AllNodes().FirstOrDefault(x => x.DisplayName == pickNode.SelectedItem?.ToString());
                    if (obj == null) return;
                    target.ObjectId = ctx.Level.ObjectIdOf(obj);
                    target.ObjectName = obj.DisplayName;
                    target.AnimationName = obj.Animations.FirstOrDefault()?.Name ?? "idle";
                    break;
                case 1:
                    target.Kind = 7;
                    var gasm = ctx.Level.AllNodes().FirstOrDefault(x => x.Kind == NodeKind.GASM && x.DisplayName == pickNode.SelectedItem?.ToString());
                    if (gasm == null) return;
                    target.ObjectId = ctx.Level.ObjectIdOf(gasm);
                    target.ObjectName = gasm.DisplayName;
                    break;
                case 2:
                    target.Kind = 3;
                    var snd = ctx.Level.AllNodes().FirstOrDefault(x => x.Kind == NodeKind.Sound && x.DisplayName == pickNode.SelectedItem?.ToString());
                    if (snd == null) return;
                    target.ObjectId = ctx.Level.ObjectIdOf(snd);
                    target.ObjectName = snd.DisplayName;
                    break;
                case 3:
                    target.Kind = 8;
                    var lgt = ctx.Level.AllNodes().FirstOrDefault(x => x.Kind == NodeKind.Light && x.DisplayName == pickNode.SelectedItem?.ToString());
                    if (lgt == null) return;
                    target.ObjectId = ctx.Level.ObjectIdOf(lgt);
                    target.ObjectName = lgt.DisplayName;
                    break;
                case 4:
                    target.Kind = 6;
                    target.Ambient = ctx.Level.Ambience.Clone();
                    break;
            }
            n.TriggerTargets.Add(target);
            ctx.Changed(); ctx.Rebuild();
        };
        var wrap = new WrapPanel();
        wrap.Children.Add(pickKind); wrap.Children.Add(pickNode); wrap.Children.Add(addTarget);
        host.Children.Add(wrap);
    }

    private static void BuildParticles(Panel host, InspectorContext ctx, LevelNode n)
    {
        AddSection(host, "PARTICLE EMITTER");
        IntField(host, "Per Second", (int)n.ParticlesPerSecond, v => { n.ParticlesPerSecond = (uint)Math.Max(0, v); ctx.Changed(); });
        FloatField(host, "Life Min", n.LifeSpanMin, 0, 30, 0.1f, 2, v => { n.LifeSpanMin = v; ctx.Changed(); });
        FloatField(host, "Life Max", n.LifeSpanMax, 0, 30, 0.1f, 2, v => { n.LifeSpanMax = v; ctx.Changed(); });
        ColorField(host, "Start Color", n.StartColor, v => { n.StartColor = v; ctx.Changed(); });
        ColorField(host, "End Color", n.EndColor, v => { n.EndColor = v; ctx.Changed(); });
        FloatField(host, "Start Size Min", n.StartSizeMin, 0, 10, 0.05f, 2, v => { n.StartSizeMin = v; ctx.Changed(); });
        FloatField(host, "Start Size Max", n.StartSizeMax, 0, 10, 0.05f, 2, v => { n.StartSizeMax = v; ctx.Changed(); });
        FloatField(host, "End Size Min", n.EndSizeMin, 0, 10, 0.05f, 2, v => { n.EndSizeMin = v; ctx.Changed(); });
        FloatField(host, "End Size Max", n.EndSizeMax, 0, 10, 0.05f, 2, v => { n.EndSizeMax = v; ctx.Changed(); });
        VectorField(host, "Velocity", n.Velocity, v => { n.Velocity = v; ctx.Changed(); });
        VectorField(host, "Vel Min", n.VelocityMin, v => { n.VelocityMin = v; ctx.Changed(); });
        VectorField(host, "Vel Max", n.VelocityMax, v => { n.VelocityMax = v; ctx.Changed(); });
        VectorField(host, "Accel Min", n.AccelerationMin, v => { n.AccelerationMin = v; ctx.Changed(); });
        VectorField(host, "Accel Max", n.AccelerationMax, v => { n.AccelerationMax = v; ctx.Changed(); });
        Text(host, "GRAB renders particles as solid camera-facing quads coloured by these start/end colours. Presets use the real emitter fields.");
        var presets = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        foreach (var preset in ParticlePresets)
        {
            var b = SmallButton(preset.Name);
            b.Margin = new Thickness(0, 0, 4, 4);
            b.Click += (_, _) =>
            {
                preset.Apply(n);
                ctx.Changed();
                ctx.Rebuild();
            };
            presets.Children.Add(b);
        }
        host.Children.Add(presets);
    }

    private sealed class ParticlePreset
    {
        public string Name { get; init; } = "";
        public Action<LevelNode> Apply { get; init; } = _ => { };
    }

    private static readonly ParticlePreset[] ParticlePresets =
    {
        new()
        {
            Name = "Smoke",
            Apply = n =>
            {
                n.ParticlesPerSecond = 16; n.LifeSpanMin = 1.8f; n.LifeSpanMax = 2.6f;
                n.StartColor = new Col(0.55f, 0.58f, 0.6f, 0.55f); n.EndColor = new Col(0.22f, 0.24f, 0.26f, 0);
                n.StartSizeMin = 0.25f; n.StartSizeMax = 0.4f; n.EndSizeMin = 1.1f; n.EndSizeMax = 1.8f;
                n.Velocity = new Vec3(0, 0.35f, 0); n.VelocityMin = new Vec3(-0.12f, 0, -0.12f); n.VelocityMax = new Vec3(0.12f, 0.18f, 0.12f);
                n.AccelerationMin = new Vec3(0, 0.08f, 0); n.AccelerationMax = new Vec3(0, 0.12f, 0);
            }
        },
        new()
        {
            Name = "Fire",
            Apply = n =>
            {
                n.ParticlesPerSecond = 70; n.LifeSpanMin = 0.45f; n.LifeSpanMax = 0.85f;
                n.StartColor = new Col(1f, 0.62f, 0.12f, 0.9f); n.EndColor = new Col(0.8f, 0.08f, 0.02f, 0);
                n.StartSizeMin = 0.22f; n.StartSizeMax = 0.36f; n.EndSizeMin = 0.02f; n.EndSizeMax = 0.06f;
                n.Velocity = new Vec3(0, 1.4f, 0); n.VelocityMin = new Vec3(-0.18f, 0.1f, -0.18f); n.VelocityMax = new Vec3(0.18f, 0.5f, 0.18f);
                n.AccelerationMin = new Vec3(0, 0.8f, 0); n.AccelerationMax = new Vec3(0, 1.2f, 0);
            }
        },
        new()
        {
            Name = "Sparks",
            Apply = n =>
            {
                n.ParticlesPerSecond = 38; n.LifeSpanMin = 0.5f; n.LifeSpanMax = 1.2f;
                n.StartColor = new Col(1f, 0.92f, 0.55f, 1f); n.EndColor = new Col(1f, 0.35f, 0.05f, 0);
                n.StartSizeMin = 0.045f; n.StartSizeMax = 0.075f; n.EndSizeMin = 0.01f; n.EndSizeMax = 0.02f;
                n.Velocity = new Vec3(0, 0.9f, 0); n.VelocityMin = new Vec3(-1.1f, -0.2f, -1.1f); n.VelocityMax = new Vec3(1.1f, 1.6f, 1.1f);
                n.AccelerationMin = new Vec3(0, -2.2f, 0); n.AccelerationMax = new Vec3(0, -1.5f, 0);
            }
        },
        new()
        {
            Name = "Snow",
            Apply = n =>
            {
                n.ParticlesPerSecond = 26; n.LifeSpanMin = 4f; n.LifeSpanMax = 7f;
                n.StartColor = new Col(0.92f, 0.97f, 1f, 0.9f); n.EndColor = new Col(1f, 1f, 1f, 0);
                n.StartSizeMin = 0.045f; n.StartSizeMax = 0.09f; n.EndSizeMin = 0.045f; n.EndSizeMax = 0.09f;
                n.Velocity = new Vec3(0, -0.45f, 0); n.VelocityMin = new Vec3(-0.18f, 0, -0.18f); n.VelocityMax = new Vec3(0.18f, 0.05f, 0.18f);
                n.AccelerationMin = new Vec3(0, -0.1f, 0); n.AccelerationMax = new Vec3(0, -0.04f, 0);
            }
        },
        new()
        {
            Name = "Portal",
            Apply = n =>
            {
                n.ParticlesPerSecond = 45; n.LifeSpanMin = 1f; n.LifeSpanMax = 1.8f;
                n.StartColor = new Col(0.55f, 0.2f, 1f, 0.85f); n.EndColor = new Col(0.1f, 0.6f, 1f, 0);
                n.StartSizeMin = 0.12f; n.StartSizeMax = 0.22f; n.EndSizeMin = 0.6f; n.EndSizeMax = 1f;
                n.Velocity = new Vec3(0, 0.55f, 0); n.VelocityMin = new Vec3(-0.25f, 0, -0.25f); n.VelocityMax = new Vec3(0.25f, 0.3f, 0.25f);
                n.AccelerationMin = new Vec3(0, 0.2f, 0); n.AccelerationMax = new Vec3(0, 0.4f, 0);
            }
        },
        new()
        {
            Name = "Magic Dust",
            Apply = n =>
            {
                n.ParticlesPerSecond = 14; n.LifeSpanMin = 2f; n.LifeSpanMax = 3.5f;
                n.StartColor = new Col(0.45f, 1f, 0.85f, 0.9f); n.EndColor = new Col(0.25f, 0.4f, 1f, 0);
                n.StartSizeMin = 0.05f; n.StartSizeMax = 0.1f; n.EndSizeMin = 0.15f; n.EndSizeMax = 0.25f;
                n.Velocity = new Vec3(0, 0.2f, 0); n.VelocityMin = new Vec3(-0.08f, -0.02f, -0.08f); n.VelocityMax = new Vec3(0.08f, 0.1f, 0.08f);
                n.AccelerationMin = new Vec3(0, 0.05f, 0); n.AccelerationMax = new Vec3(0, 0.08f, 0);
            }
        }
    };

    private static void BuildSound(Panel host, InspectorContext ctx, LevelNode n)
    {
        AddSection(host, "SOUND");
        TextField(host, "Name", n.SoundName, v => { n.SoundName = v; ctx.Changed(); });
        FloatField(host, "Volume", n.SoundVolume, 0, 10, 0.05f, 2, v => { n.SoundVolume = v; n.RawSoundParameters = null; ctx.Changed(); });
        CheckField(host, "Repeat", n.SoundRepeat, v => { n.SoundRepeat = v; ctx.Changed(); });
        CheckField(host, "Start Active", n.SoundStartActive, v => { n.SoundStartActive = v; ctx.Changed(); });
        FloatField(host, "Max Range", n.SoundMaxRange, 0, 2000, 5, 0, v => { n.SoundMaxRange = v; ctx.Changed(); });
        FloatField(host, "Frequency", n.SoundFrequency, 20, 4000, 1, 0, v => { n.SoundFrequency = v; n.RawSoundParameters = null; ctx.Changed(); });
        ComboField(host, "Wave", new[] { "Square", "Sawtooth", "Sine", "Noise" }, (int)n.SoundWaveType, v => { n.SoundWaveType = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Attack", n.SoundAttack, 0, 2, 0.01f, 2, v => { n.SoundAttack = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Sustain", n.SoundSustain, 0, 2, 0.01f, 2, v => { n.SoundSustain = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Release", n.SoundRelease, 0, 2, 0.01f, 2, v => { n.SoundRelease = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Punch", n.SoundPunch, 0, 2, 0.05f, 2, v => { n.SoundPunch = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Frequency Min", n.SoundFrequencyMin, 20, 4000, 1, 0, v => { n.SoundFrequencyMin = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Square Duty", n.SoundDutyCycle, 0.01f, 0.99f, 0.01f, 2, v => { n.SoundDutyCycle = v; n.RawSoundParameters = null; ctx.Changed(); });
        FloatField(host, "Low Pass (25000 = off)", n.SoundLowPass, 0, 26000, 100, 0, v =>
        {
            // GRAB treats a 0 Hz low-pass as a closed filter, not as disabled.
            n.SoundLowPass = v > 0 && v < 20 ? 25000 : v;
            n.RawSoundParameters = null;
            ctx.Changed();
            ctx.Rebuild();
        });
        FloatField(host, "High Pass (0 = off)", n.SoundHighPass, 0, 20000, 50, 0, v => { n.SoundHighPass = v; n.RawSoundParameters = null; ctx.Changed(); });
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        var preview = SmallButton("▶ Preview");
        preview.Click += (_, _) => AudioEngine.Shared.PlayNode(n, 1f, n.SoundRepeat, "inspector:" + n.EditorId, n.DisplayName);
        buttons.Children.Add(preview);
        var stop = SmallButton("■ Stop");
        stop.Margin = new Thickness(5, 0, 0, 0);
        stop.Click += (_, _) => AudioEngine.Shared.StopAll();
        buttons.Children.Add(stop);
        host.Children.Add(buttons);
        Text(host, "GRAB sound blocks are oscillators: waveform + pitch envelope + attack/sustain/release + filters. Low Pass 25000 Hz is fully open; 0 Hz is a closed filter and the block is silent in game. Open Tools → Audio Studio for frequency slide, vibrato, pitch jump, flanger, reverb and every other retail parameter.");
    }

    private static void BuildLight(Panel host, InspectorContext ctx, LevelNode n)
    {
        AddSection(host, "LIGHT");
        ComboField(host, "Type", new[] { "Point", "Spot" }, n.LightType, v => { n.LightType = v; ctx.Changed(); });
        ColorField(host, "Color", n.LightColor, v => { n.LightColor = v; ctx.Changed(); });
        FloatField(host, "Intensity", n.LightIntensity, 0, 20, 0.05f, 2, v => { n.LightIntensity = v; ctx.Changed(); });
        FloatField(host, "Range", n.LightRange, 0, 100, 0.1f, 2, v => { n.LightRange = v; ctx.Changed(); });
        if (n.LightType == 1) FloatField(host, "Angle", n.LightAngle, 1, 179, 1, 0, v => { n.LightAngle = v; ctx.Changed(); });
        FloatField(host, "Distance Falloff", n.DistanceFalloffShape, 0, 10, 0.1f, 2, v => { n.DistanceFalloffShape = v; ctx.Changed(); });
        FloatField(host, "Cone Falloff", n.ConeFalloffShape, 0, 10, 0.1f, 2, v => { n.ConeFalloffShape = v; ctx.Changed(); });
    }

    private static void BuildCodeBlock(Panel host, InspectorContext ctx, LevelNode n)
    {
        AddSection(host, "C# CODE BLOCK");
        var status = n.Compiled == null ? "Not compiled" : n.Compiled.Success ? $"Compiled: {n.Compiled.Program.Instructions.Count} GASM ops" : $"{n.Compiled.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)";
        Text(host, status);
        CheckField(host, "Start Active", n.StartActive, v => { n.StartActive = v; ctx.Changed(); });
        CheckField(host, "Shared", n.IsSharedGasm, v => { n.IsSharedGasm = v; ctx.Changed(); });
        CheckField(host, "Late Update", n.LateUpdate, v => { n.LateUpdate = v; ctx.Changed(); });
        var buttons = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var open = SmallButton("Open C# Editor");
        open.Click += (_, _) => ctx.OpenCode();
        var compile = SmallButton("Compile");
        compile.Margin = new Thickness(5, 0, 0, 0);
        compile.Click += (_, _) => ctx.CompileCode();
        buttons.Children.Add(open); buttons.Children.Add(compile);
        host.Children.Add(buttons);
        var info = ScriptIntrospection.Inspect(n.Script);
        if (info.Lifecycle.Count > 0)
            Text(host, "Lifecycle: " + string.Join(", ", info.Lifecycle));
        if (info.Events.Count > 0)
            Text(host, "Events: " + string.Join(", ", info.Events));
        if (!string.IsNullOrWhiteSpace(n.ComponentHostId))
        {
            var componentHost = ctx.Level.FindByEditorId(n.ComponentHostId);
            if (componentHost != null)
            {
                var back = SmallButton("Component of " + componentHost.DisplayName);
                back.Margin = new Thickness(0, 4, 0, 0);
                back.Click += (_, _) => ctx.Select(componentHost);
                host.Children.Add(back);
            }
        }
        BuildScriptFields(host, ctx, n);
        Text(host, "C# is translated to real GASM instructions, registers and connections. Set methods/properties use the same memory the in-game editor uses.");
    }

    private static void BuildAnimations(Panel host, InspectorContext ctx, LevelNode n)
    {
        if (n.Animations.Count > 0)
            CheckField(host, "Start Active", n.ActiveAnimation >= 0, v =>
            {
                n.ActiveAnimation = v ? Math.Clamp(n.ActiveAnimation, 0, n.Animations.Count - 1) : -1;
                ctx.Changed();
            });

        var open = SmallButton("Open Animation Timeline");
        open.Margin = new Thickness(0, 2, 0, 6);
        open.Click += (_, _) => ctx.OpenAnimation();
        host.Children.Add(open);

        foreach (var a in n.Animations.ToList())
        {
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)), BorderBrush = BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5), Margin = new Thickness(0, 2, 0, 2) };
            var sp = new StackPanel();
            var head = new DockPanel();
            var del = SmallButton("✕");
            del.Click += (_, _) => { n.Animations.Remove(a); ctx.Changed(); ctx.Rebuild(); };
            DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(del);
            head.Children.Add(new TextBlock { Text = a.Name + $"  ({a.Frames.Count} frames · {AnimationSampler.Duration(a):0.##}s)", Foreground = Brushes.White, FontSize = 11 });
            sp.Children.Add(head);
            var name = new TextBox { Text = a.Name, Margin = new Thickness(0, 2, 0, 0) };
            name.LostFocus += (_, _) => { a.Name = name.Text; ctx.Changed(); };
            sp.Children.Add(name);
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            var speed = new TextBox { Text = a.Speed.ToString("0.##"), Width = 50, ToolTip = "Speed" };
            speed.LostFocus += (_, _) => { if (float.TryParse(speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { a.Speed = v; ctx.Changed(); } };
            var dir = new ComboBox { Width = 75, ItemsSource = new[] { "Restart", "PingPong" }, SelectedIndex = Math.Clamp(a.Direction, 0, 1), Margin = new Thickness(3, 0, 0, 0) };
            dir.SelectionChanged += (_, _) => { a.Direction = dir.SelectedIndex; ctx.Changed(); };
            var cap = SmallButton("Capture");
            cap.Margin = new Thickness(3, 0, 0, 0);
            cap.Click += (_, _) =>
            {
                float t = a.Frames.Count == 0 ? 0 : a.Frames.Max(f => f.Time) + 0.25f;
                a.Frames.Add(new GrabAnimationFrame { Time = t, Position = n.Position, Rotation = n.Rotation });
                ctx.Changed(); ctx.Rebuild();
            };
            wrap.Children.Add(speed); wrap.Children.Add(dir); wrap.Children.Add(cap);
            sp.Children.Add(wrap);
            card.Child = sp;
            host.Children.Add(card);
        }
        var add = SmallButton("+ New Animation");
        add.Margin = new Thickness(0, 2, 0, 0);
        add.Click += (_, _) =>
        {
            n.Animations.Add(new GrabAnimation
            {
                Name = "anim" + (n.Animations.Count + 1),
                Frames = { new GrabAnimationFrame { Time = 0, Position = n.Position, Rotation = n.Rotation } }
            });
            ctx.Changed(); ctx.Rebuild();
        };
        host.Children.Add(add);
    }

    private static void BuildLevel(Panel host, InspectorContext ctx)
    {
        AddSection(host, "LEVEL");
        TextField(host, "Title", ctx.Level.Title, v => { ctx.Level.Title = v; ctx.Changed(); });
        TextField(host, "Creators", ctx.Level.Creators, v => { ctx.Level.Creators = v; ctx.Changed(); });
        MultilineField(host, "Description", ctx.Level.Description, v => { ctx.Level.Description = v; ctx.Changed(); });
        TextField(host, "Tags", string.Join(", ", ctx.Level.Tags), v =>
        {
            ctx.Level.Tags = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            ctx.Changed();
        });
        IntField(host, "Version", ctx.Level.FormatVersion, v => { ctx.Level.FormatVersion = Math.Max(1, v); ctx.Changed(); });
        IntField(host, "Complexity", (int)ctx.Level.Complexity, v => { ctx.Level.Complexity = (uint)Math.Max(0, v); ctx.Changed(); });
        IntField(host, "Checkpoints", (int)ctx.Level.MaxCheckpointCount, v => { ctx.Level.MaxCheckpointCount = (uint)Math.Max(0, v); ctx.Changed(); });
        CheckField(host, "Unlisted", ctx.Level.Unlisted, v => { ctx.Level.Unlisted = v; ctx.Changed(); });
        CheckField(host, "Show Replays", ctx.Level.ShowReplays, v => { ctx.Level.ShowReplays = v; ctx.Changed(); });
        CheckField(host, "Disable Practice", ctx.Level.DisablePracticeMode, v => { ctx.Level.DisablePracticeMode = v; ctx.Changed(); });
        var starts = ctx.Level.AllNodes().Where(x => x.Kind == NodeKind.Start).ToList();
        var names = starts.Select(x => x.DisplayName).ToArray();
        int idx = starts.FindIndex(x => ctx.Level.ObjectIdOf(x) == ctx.Level.DefaultSpawnPointId);
        ComboField(host, "Default Spawn", names.Length == 0 ? new[] { "(none)" } : names, Math.Max(0, idx), v =>
        {
            if (v >= 0 && v < starts.Count) ctx.Level.DefaultSpawnPointId = ctx.Level.ObjectIdOf(starts[v]);
            ctx.Changed();
        });
    }

    private static void BuildAmbience(Panel host, InspectorContext ctx)
    {
        var a = ctx.Level.Ambience;
        AddSection(host, "SKY");
        ColorField(host, "Zenith", a.SkyZenithColor, v => { a.SkyZenithColor = v; ctx.Changed(); });
        ColorField(host, "Horizon", a.SkyHorizonColor, v => { a.SkyHorizonColor = v; ctx.Changed(); });
        AddSection(host, "SUN");
        FloatField(host, "Altitude", a.SunAltitude, 0, 90, 0.5f, 1, v => { a.SunAltitude = v; ctx.Changed(); });
        FloatField(host, "Azimuth", a.SunAzimuth, 0, 360, 1, 0, v => { a.SunAzimuth = v; ctx.Changed(); });
        FloatField(host, "Size", a.SunSize, 0.05f, 20, 0.05f, 2, v => { a.SunSize = v; ctx.Changed(); });
        CheckField(host, "Advanced Sun", a.UseAdvancedSunSettings, v => { a.UseAdvancedSunSettings = v; ctx.Changed(); });
        ColorField(host, "Sun Color", a.SunColor, v => { a.SunColor = v; ctx.Changed(); });
        FloatField(host, "Sun Brightness", a.SunBrightness, 0, 10, 0.05f, 2, v => { a.SunBrightness = v; ctx.Changed(); });
        AddSection(host, "AMBIENT / FOG");
        ColorField(host, "Ambient", a.AmbientColor, v => { a.AmbientColor = v; ctx.Changed(); });
        FloatField(host, "Ambient Brightness", a.AmbientBrightness, 0, 5, 0.05f, 2, v => { a.AmbientBrightness = v; ctx.Changed(); });
        FloatField(host, "Fog Density", a.FogDensity, 0, 1, 0.005f, 3, v => { a.FogDensity = v; ctx.Changed(); });
        AddSection(host, "STARS");
        FloatField(host, "Star Density", a.StarDensity, 0, 1, 0.01f, 2, v => { a.StarDensity = v; ctx.Changed(); });
        FloatField(host, "Clustering", a.StarClustering, 0, 1, 0.01f, 2, v => { a.StarClustering = v; ctx.Changed(); });
        FloatField(host, "Size", a.StarSize, 0.1f, 10, 0.1f, 2, v => { a.StarSize = v; ctx.Changed(); });
        FloatField(host, "Visibility", a.StarVisibility, 0, 1, 0.01f, 2, v => { a.StarVisibility = v; ctx.Changed(); });
        VectorField(host, "Rotation", a.StarRotationEuler, v => { a.StarRotationEuler = v; ctx.Changed(); });
        AddSection(host, "CLOUDS");
        ColorField(host, "Cloud Color", a.CloudColor, v => { a.CloudColor = v; ctx.Changed(); });
        FloatField(host, "Shading", a.CloudShading, 0, 2, 0.01f, 2, v => { a.CloudShading = v; ctx.Changed(); });
        FloatField(host, "Density", a.CloudDensity, 0, 1, 0.01f, 2, v => { a.CloudDensity = v; ctx.Changed(); });
        FloatField(host, "Height", a.CloudHeight, 0, 500, 1, 0, v => { a.CloudHeight = v; ctx.Changed(); });
        FloatField(host, "Speed X", a.CloudSpeedX, -50, 50, 0.1f, 1, v => { a.CloudSpeedX = v; ctx.Changed(); });
        FloatField(host, "Speed Z", a.CloudSpeedZ, -50, 50, 0.1f, 1, v => { a.CloudSpeedZ = v; ctx.Changed(); });
    }

    // ---------- UI primitives ----------
    private static void AddSection(Panel host, string title)
    {
        var b = new Border { Background = SectionBrush, Margin = new Thickness(0, 10, 0, 4), Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(2) };
        b.Child = new TextBlock { Text = title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)) };
        host.Children.Add(b);
    }

    private static void AddSeparator(Panel host)
    {
        host.Children.Add(new Border { Height = 1, Background = BorderBrush, Margin = new Thickness(0, 4, 0, 4) });
    }

    private static Grid Row(string label)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), MinHeight = 24 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, Foreground = LabelBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(lbl, 0);
        g.Children.Add(lbl);
        return g;
    }

    private static void Text(Panel host, string text)
    {
        host.Children.Add(new TextBlock { Text = text, Foreground = MutedBrush, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) });
    }

    private static void TextField(Panel host, string label, string value, Action<string> set)
    {
        var g = Row(label);
        var tb = new TextBox { Text = value ?? "", VerticalAlignment = VerticalAlignment.Center };
        tb.LostFocus += (_, _) => set(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { set(tb.Text); Keyboard.ClearFocus(); } };
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        host.Children.Add(g);
    }

    private static void MultilineField(Panel host, string label, string value, Action<string> set)
    {
        var g = Row(label);
        var tb = new TextBox { Text = value ?? "", AcceptsReturn = true, Height = 52, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        tb.LostFocus += (_, _) => set(tb.Text);
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        host.Children.Add(g);
    }

    private static void IntField(Panel host, string label, int value, Action<int> set)
    {
        var g = Row(label);
        var tb = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center };
        void Apply() { if (int.TryParse(tb.Text, out var v)) set(v); }
        tb.LostFocus += (_, _) => Apply();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        host.Children.Add(g);
    }

    private static void FloatField(Panel host, string label, float value, float min, float max, float step, int decimals, Action<float> set)
    {
        var g = Row(label);
        var sp = new DockPanel();
        var tb = new TextBox { Text = value.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture), Width = 58, Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(tb, Dock.Right);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        slider.ValueChanged += (_, _) =>
        {
            if (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin)
            {
                tb.Text = slider.Value.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture);
                set((float)slider.Value);
            }
            else tb.Text = slider.Value.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture);
        };
        void ApplyText()
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(v, min, max);
                slider.Value = v;
                tb.Text = v.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture);
                set(v);
            }
        }
        tb.LostFocus += (_, _) => ApplyText();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyText(); Keyboard.ClearFocus(); } };
        sp.Children.Add(tb);
        sp.Children.Add(slider);
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        host.Children.Add(g);
    }

    private static void CheckField(Panel host, string label, bool value, Action<bool> set)
    {
        var g = Row(label);
        var cb = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        Grid.SetColumn(cb, 1); g.Children.Add(cb);
        host.Children.Add(g);
    }

    private static void VectorField(Panel host, string label, Vec3 value, Action<Vec3> set)
    {
        var g = Row(label);
        var sp = new UniformGrid { Columns = 3 };
        var boxes = new TextBox[3];
        for (int i = 0; i < 3; i++)
        {
            var tb = new TextBox { Text = value[i].ToString("0.###", CultureInfo.InvariantCulture), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0), Tag = i, VerticalContentAlignment = VerticalAlignment.Center };
            boxes[i] = tb;
            tb.LostFocus += (_, _) =>
            {
                var vals = new float[3];
                for (int k = 0; k < 3; k++)
                    vals[k] = float.TryParse(boxes[k].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : value[k];
                set(new Vec3(vals[0], vals[1], vals[2]));
            };
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    var vals = new float[3];
                    for (int k = 0; k < 3; k++)
                        vals[k] = float.TryParse(boxes[k].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : value[k];
                    set(new Vec3(vals[0], vals[1], vals[2]));
                    Keyboard.ClearFocus();
                }
            };
            sp.Children.Add(tb);
        }
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        host.Children.Add(g);
    }

    private static void ColorField(Panel host, string label, Col value, Action<Col> set,
        Func<bool>? getNoTexture = null, Action<bool>? setNoTexture = null)
    {
        var g = Row(label);
        var sp = new DockPanel();
        var tb = new TextBox { Text = value.HexAlpha, VerticalContentAlignment = VerticalAlignment.Center };
        Col last = value;
        void Apply()
        {
            var c = Col.FromHex(tb.Text, last);
            if (c.Hex == last.Hex && c.Ai == last.Ai) return;
            last = c;
            set(c);
        }
        // The swatch opens the colour picker; picking commits straight away instead of
        // waiting for a focus change the click on the swatch would otherwise swallow.
        var preview = ColorInputs.CreateSwatch(tb, _ => Apply(), getNoTexture, setNoTexture);
        preview.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(preview, Dock.Left);
        tb.LostFocus += (_, _) => Apply();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        sp.Children.Add(preview); sp.Children.Add(tb);
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        host.Children.Add(g);
    }

    private static void ComboField(Panel host, string label, string[] items, int index, Action<int> set)
    {
        var g = Row(label);
        var cb = new ComboBox { ItemsSource = items, VerticalContentAlignment = VerticalAlignment.Center };
        cb.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, items.Length - 1));
        cb.SelectionChanged += (_, _) => set(cb.SelectedIndex);
        Grid.SetColumn(cb, 1); g.Children.Add(cb);
        host.Children.Add(g);
    }

    private static Button SmallButton(string text)
    {
        var b = new Button { Content = text, Padding = new Thickness(7, 2, 7, 2), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(FrameworkElement.StyleProperty, "SmallButton");
        return b;
    }
}
