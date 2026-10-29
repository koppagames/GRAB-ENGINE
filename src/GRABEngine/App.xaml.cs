using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GrabEngine.AI;
using GrabEngine.Code;
using GrabEngine.Controls;
using GrabEngine.Core;
using GrabEngine.Formats;
using GrabEngine.UI;

namespace GRABEngine;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--train-neural"))
        {
            RunNeuralTraining(e.Args);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--shot") || e.Args.Contains("--shot-animation") || e.Args.Contains("--shot-play") || e.Args.Contains("--shot-brush") || e.Args.Contains("--shot-build"))
        {
            string shotFlag = e.Args.Contains("--shot-animation") ? "--shot-animation"
                : e.Args.Contains("--shot-play") ? "--shot-play"
                : e.Args.Contains("--shot-build") ? "--shot-build"
                : e.Args.Contains("--shot-brush") ? "--shot-brush" : "--shot";
            var win = new MainWindow { Width = 1600, Height = 1000, WindowState = WindowState.Normal };
            win.Show();
            if (shotFlag == "--shot-animation") win.ShowAnimationForScreenshot();
            if (shotFlag == "--shot-play") win.StartPlayForScreenshot();
            if (shotFlag == "--shot-brush") win.ShowBrushForScreenshot();
            if (shotFlag == "--shot-build") win.ShowBuildForScreenshot();
            // Let the scene view animate for a moment so clouds, particles and the
            // play-mode camera are in their live state when the frame is captured.
            var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(shotFlag == "--shot-play" ? 2400 : shotFlag is "--shot-brush" or "--shot-build" ? 1600 : 1200) };
            shotTimer.Tick += (_, _) =>
            {
                shotTimer.Stop();
                try
                {
                    win.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)Math.Max(1, win.ActualWidth), (int)Math.Max(1, win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(win);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    string path = e.Args.SkipWhile(a => a != shotFlag).Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "shot.png");
                    using (var fs = File.Create(path)) enc.Save(fs);
                }
                catch { }
                win.Close();
                Shutdown();
            };
            shotTimer.Start();
            return;
        }
        if (e.Args.Contains("--shot-project"))
        {
            int at = Array.IndexOf(e.Args, "--shot-project");
            string project = e.Args.ElementAtOrDefault(at + 1) ?? "";
            string outPath = e.Args.ElementAtOrDefault(at + 2) ?? Path.Combine(AppContext.BaseDirectory, "project.png");
            var win = new MainWindow { Width = 1600, Height = 1000, WindowState = WindowState.Normal };
            win.Show();
            if (File.Exists(project)) win.OpenProjectForScreenshot(project);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    win.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)Math.Max(1, win.ActualWidth), (int)Math.Max(1, win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(win);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = File.Create(outPath);
                    enc.Save(fs);
                }
                catch { }
                win.Close();
                Shutdown();
            };
            timer.Start();
            return;
        }
        if (e.Args.Contains("--shot-window"))
        {
            int at = Array.IndexOf(e.Args, "--shot-window");
            string which = e.Args.ElementAtOrDefault(at + 1) ?? "export";
            string outPath = e.Args.ElementAtOrDefault(at + 2) ?? Path.Combine(AppContext.BaseDirectory, "window.png");
            var owner = new MainWindow { Width = 1500, Height = 900, WindowState = WindowState.Normal };
            owner.Show();
            var level = GrabLevel.CreateDefault();
            level.EnsureNames();
            var shotSign = level.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Sign);
            if (which == "animation" && shotSign == null)
            {
                shotSign = new LevelNode { Kind = NodeKind.Sign, Name = "Sign 1", Position = new Vec3(0, 1.5f, 2) };
                level.AddNode(shotSign);
            }
            if (which == "animation" && shotSign != null)
            {
                shotSign.Animations.Add(new GrabAnimation
                {
                    Name = "spin",
                    Speed = 1f,
                    Interpolation = 1,
                    Frames =
                    {
                        new GrabAnimationFrame { Time = 0f, Position = shotSign.Position, Rotation = Quat.Identity },
                        new GrabAnimationFrame { Time = 0.8f, Position = shotSign.Position + new Vec3(0, 1, 0), Rotation = Quat.FromEulerDegrees(new Vec3(0, 180, 0)) },
                        new GrabAnimationFrame { Time = 1.6f, Position = shotSign.Position + new Vec3(0, 2, 0), Rotation = Quat.FromEulerDegrees(new Vec3(0, 360, 0)) }
                    }
                });
            }
            if (which is "audio" or "audio-mp3")
                level.AddNode(new LevelNode
                {
                    Kind = NodeKind.Sound, Name = "Beep", Position = new Vec3(0, 2, 0),
                    SoundWaveType = 2, SoundFrequency = 440, SoundVolume = 0.7f,
                    SoundAttack = 0.01f, SoundSustain = 0.3f, SoundRelease = 0.25f
                });
            Window target = which switch
            {
                "install" => new InstallDialog(owner, level.Title),
                "glb" => new GlbImportDialog(owner),
                "animation" => CreateAnimationShot(owner, level, shotSign ?? level.Nodes.FirstOrDefault()),
                "add" => new AddObjectWindow(owner),
                "docs" => new DocsWindow(owner),
                "audio" => new AudioStudioWindow(owner, level, () => new Vec3(0, 1, 0)),
                "audio-mp3" => new AudioStudioWindow(owner, level, () => new Vec3(0, 1, 0), 1),
                "audio-speech" => new AudioStudioWindow(owner, level, () => new Vec3(0, 1, 0), 2),
                "library" => new LevelLibraryWindow(owner, _ => { }, _ => { }),
                "terrain" => new TerrainStudioWindow(owner, level),
                "neural" => new NeuralLabWindow(owner),
                "structure" => new StructureStudioWindow(owner, level),
                "history" => new VersionHistoryWindow(owner, null, level.Title),
                _ => new ExportDialog(owner, level, Path.Combine(Path.GetTempPath(), "shot.level"))
            };
            target.Owner = owner;
            target.Show();
            var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            shotTimer.Tick += (_, _) =>
            {
                shotTimer.Stop();
                try
                {
                    target.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)Math.Max(1, target.ActualWidth), (int)Math.Max(1, target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(target);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(outPath)) enc.Save(fs);
                }
                catch { }
                target.Close();
                owner.Close();
                Shutdown();
            };
            shotTimer.Start();
            return;
        }
        if (e.Args.Contains("--selftest"))
        {
            string result;
            try
            {
                var window = new MainWindow();
                result = window.RunSelfTest();
            }
            catch (Exception ex)
            {
                result = "FAIL  exception: " + ex;
            }
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), result); } catch { }
            Shutdown();
            return;
        }
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// Headless training entry point used to build and verify the demo project:
    /// GRABEngine.exe --train-neural path\to\demo.grabproj [--generations N] [--population N]
    /// The trained network is saved, compiled to GASM, simulated, and the results are
    /// written next to the project as a .training.txt report.
    /// </summary>
    private static void RunNeuralTraining(string[] args)
    {
        int at = Array.IndexOf(args, "--train-neural");
        string outPath = args.ElementAtOrDefault(at + 1) ?? "";
        if (string.IsNullOrWhiteSpace(outPath) || outPath.StartsWith("--", StringComparison.Ordinal))
            outPath = Path.Combine(Environment.CurrentDirectory, "projects", "Neural Ball", "Neural Ball.grabproj");
        outPath = Path.GetFullPath(outPath);
        string logPath = Path.ChangeExtension(outPath, ".training.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? Environment.CurrentDirectory);

        int IntArg(string name, int fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && int.TryParse(args.ElementAtOrDefault(i + 1), out int value) ? value : fallback;
        }

        var options = new NeuralTrainingOptions
        {
            Population = Math.Clamp(IntArg("--population", 96), 8, 400),
            Hidden = Math.Clamp(IntArg("--hidden", 4), 0, 12),
            Generations = Math.Clamp(IntArg("--generations", 80), 1, 500),
            Patience = Math.Clamp(IntArg("--patience", 18), 1, 200),
            MaxSteps = Math.Clamp(IntArg("--steps", NeuralArena.DefaultMaxSteps), 60, 3000),
            Seed = IntArg("--seed", 20261029),
            StopWhenSolved = !args.Contains("--no-stop-on-solved")
        };

        var sb = new StringBuilder();
        sb.AppendLine("GRAB ENGINE — Neural Lab headless run");
        sb.AppendLine(FormattableString.Invariant($"output: {outPath}"));
        sb.AppendLine(FormattableString.Invariant($"settings: population={options.Population} hidden={options.Hidden} generations={options.Generations} patience={options.Patience} seed={options.Seed}"));
        sb.AppendLine();
        try
        {
            var trainer = new NeuralTrainer(options);
            var report = trainer.Train(stat =>
            {
                Console.WriteLine(FormattableString.Invariant($"gen {stat.Generation}/{options.Generations}  best={stat.BestFitness:0.00}  dist={stat.BestDistance:0.000}  success={stat.SuccessRate:P0}"));
                sb.AppendLine(FormattableString.Invariant($"gen {stat.Generation,3}  best={stat.BestFitness,8:0.00}  dist={stat.BestDistance,6:0.000}  success={stat.SuccessRate,4:P0}"));
            });
            sb.AppendLine();
            sb.AppendLine(report.Describe());
            sb.AppendLine();

            var files = NeuralDemoBuilder.SaveDemo(report, outPath);
            sb.AppendLine("=== generated files ===");
            sb.AppendLine("project: " + files.ProjectPath);
            sb.AppendLine("level:   " + files.LevelPath);
            sb.AppendLine("readme:  " + files.ReadmePath);
            sb.AppendLine(FormattableString.Invariant($"GASM instructions: {files.Instructions} / {GrabLimits.MaxInstructions}"));
            sb.AppendLine(FormattableString.Invariant($"GASM connections: {files.Connections} / {GrabLimits.MaxConnections}"));
            sb.AppendLine(FormattableString.Invariant($"continuation pages: {files.ContinuationPages}"));
            sb.AppendLine();

            // Recompile the freshly saved project exactly as the editor would.
            var project = ProjectIO.Load(files.ProjectPath);
            var block = project.AllNodes().First(n => n.Kind == NodeKind.GASM);
            var compile = new CSharpCompiler().Compile(project, block, block.Script);
            block.Compiled = compile;
            sb.AppendLine("project recompile: " + (compile.Success ? "OK" : "FAILED"));
            var allPages = new[] { compile }.Concat(compile.Continuations).ToList();
            sb.AppendLine("pages: " + allPages.Count + "  sizes: " + string.Join(", ",
                allPages.Select((p, i) => i + "=" + p.Program.Instructions.Count)));
            sb.AppendLine("registers page0: " + compile.Program.WorkingRegisters.Count + "  in: " +
                          compile.Program.InputRegisters.Count + "  out: " + compile.Program.OutputRegisters.Count +
                          "  inout: " + compile.Program.InOutRegisters.Count);
            foreach (var d in compile.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
                sb.AppendLine("  " + d);

            var idNames = project.BuildObjectIds().ToDictionary(kv => kv.Value, kv => kv.Key.DisplayName);
            foreach (var pair in allPages.Select((page, index) => (page, index)))
            {
                sb.AppendLine("page " + pair.index + " connections: " + string.Join(", ",
                    pair.page.Connections.Select(c => c.Name + "->" +
                        (idNames.TryGetValue(c.ObjectId, out var n) ? n : c.ObjectId.ToString(CultureInfo.InvariantCulture)))));
            }

            var projectPlay = new PlaySession(project);
            var projectBall = project.FindByName(NeuralDemoBuilder.BallName);
            var projectTarget = project.FindByName(NeuralDemoBuilder.TargetName);
            var bus2 = project.FindByName("GASM Bus 2");
            for (int i = 0; i < 600; i++)
            {
                projectPlay.Step(1f / 60f);
                if (i < 12 || i is 20 or 40 or 60 or 90 or 120 or 180 or 240 or 300 or 420 or 599 || (i >= 110 && i <= 130))
                {
                    sb.AppendLine(FormattableString.Invariant(
                        $"  frame {i}: ball=({projectBall?.WorldPosition.X:0.000},{projectBall?.WorldPosition.Z:0.000}) bus2=({bus2?.Position.X:0.000},{bus2?.Position.Y:0.000},{bus2?.Position.Z:0.000})"));
                    if (i >= 110 && i <= 130)
                    {
                        var page7Node = project.AllNodes().FirstOrDefault(n => n.Generated && n.Kind == NodeKind.GASM && n.PageIndex == 7);
                        if (page7Node != null)
                        {
                            var vars = projectPlay.DebugVariables(page7Node)
                                .Where(v => v.Name is "vx" or "vz" or "px" or "pz" or "arrived")
                                .Select(v => v.Name + "=" + v.Value.ToString("0.000", CultureInfo.InvariantCulture));
                            sb.AppendLine("    page7: " + string.Join("  ", vars));
                        }
                    }
                }
            }
            float projectDistance = projectBall != null && projectTarget != null
                ? FlatDistance(projectBall.WorldPosition, projectTarget.WorldPosition) : -1;
            sb.AppendLine(FormattableString.Invariant(
                $"project play 10s: ball=({projectBall?.WorldPosition.X:0.00},{projectBall?.WorldPosition.Z:0.00}) distance={projectDistance:0.000}"));

            // The exported .level carries the program itself, so this is the retail path.
            // Static node names are not stored by GRAB's level format, so identify the
            // ball and target by shape and position after the round-trip.
            var exported = LevelIO.Load(files.LevelPath);
            var levelPlay = new PlaySession(exported);
            for (int i = 0; i < 600; i++) levelPlay.Step(1f / 60f);
            var levelBall = exported.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Sphere);
            var levelTarget = exported.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static &&
                Math.Abs(n.WorldPosition.X - NeuralArena.TargetX) < 0.01f &&
                Math.Abs(n.WorldPosition.Z - NeuralArena.TargetZ) < 0.01f);
            float levelDistance = levelBall != null && levelTarget != null
                ? FlatDistance(levelBall.WorldPosition, levelTarget.WorldPosition) : -1;
            sb.AppendLine(FormattableString.Invariant(
                $"exported level play 10s: ball=({levelBall?.WorldPosition.X:0.00},{levelBall?.WorldPosition.Z:0.00}) distance={levelDistance:0.000}"));
            sb.AppendLine(FormattableString.Invariant($"reached target (< {NeuralArena.SuccessRadius:0.0}) in project: {projectDistance >= 0 && projectDistance <= NeuralArena.SuccessRadius}"));
            sb.AppendLine(FormattableString.Invariant($"reached target (< {NeuralArena.SuccessRadius:0.0}) in exported .level: {levelDistance >= 0 && levelDistance <= NeuralArena.SuccessRadius}"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("TRAINING FAILED");
            sb.AppendLine(ex.ToString());
        }
        try { File.WriteAllText(logPath, sb.ToString()); } catch { }
    }

    private static float FlatDistance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static Window CreateAnimationShot(Window owner, GrabLevel level, LevelNode? target)
    {
        // Exercise the same preview path the editor uses so the crash/screenshot test
        // covers SceneView.PreviewPose, not just the animation controls.
        var scene = new SceneView();
        scene.SetLevel(level, frame: false);
        var panel = new AnimationPanel(new AnimationWindowContext
        {
            Level = level,
            Selected = target,
            PreviewPose = scene.PreviewPose,
            ClearPreview = scene.ClearPreviewPose
        });
        var window = new Window
        {
            Title = "Animation - " + (target?.DisplayName ?? "Timeline"),
            Width = 1180,
            Height = 860,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Theme.Panel,
            Foreground = Theme.Text,
            Content = panel
        };
        window.Closed += (_, _) => scene.ClearPreviewPose();
        return window;
    }
}

