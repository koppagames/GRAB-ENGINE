using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GrabEngine.Controls;
using GrabEngine.Core;
using GrabEngine.UI;

namespace GRABEngine;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

