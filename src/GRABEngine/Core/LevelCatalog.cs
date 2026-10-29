using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GrabEngine.Formats;
using Microsoft.Win32;

namespace GrabEngine.Core;

public enum LevelSource
{
    MyLevels,
    SteamInternal,
    SteamWorkshop,
    MetaQuest,
    Other
}

public sealed class LevelEntry
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Creators { get; init; } = "";
    public int ObjectCount { get; init; }
    public LevelSource Source { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public bool Remote { get; init; }
    public string SourceLabel => Source switch
    {
        LevelSource.MyLevels => "My Levels",
        LevelSource.SteamInternal => "Steam",
        LevelSource.SteamWorkshop => "Workshop",
        LevelSource.MetaQuest => "Meta Quest",
        _ => "Level"
    };
}

/// <summary>
/// Finds GRAB levels in the PC install, the user's Documents folder and (when a headset
/// is connected) the Meta Quest level directory. Quest files are pulled into a local cache
/// so thumbnails and opening behave exactly like local levels.
/// </summary>
public static class LevelCatalog
{
    public const string QuestRemoteFolder = "/sdcard/android/data/com.slindev.grab_demo/files/levels/user";
    public static string QuestCacheRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GRAB ENGINE", "LevelLibrary", "Quest");

    public static IReadOnlyList<LevelEntry> ScanLocal()
    {
        var files = new List<(string Path, LevelSource Source)>();
        void AddFolder(string? folder, LevelSource source, bool recurse = true)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(folder, "*.level", option))
                    files.Add((file, source));
            }
            catch { }
        }

        AddFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRAB", "levels"), LevelSource.MyLevels);
        var install = FindGrabInstall();
        if (install != null)
        {
            AddFolder(Path.Combine(install, "Resources", "levels"), LevelSource.SteamInternal);
            AddFolder(Path.Combine(install, "Resources", "levels", "internal"), LevelSource.SteamInternal);
            AddFolder(Path.Combine(install, "steamapps", "workshop", "content", "1815830"), LevelSource.SteamWorkshop);
        }
        foreach (var steam in SteamLibraries())
        {
            AddFolder(Path.Combine(steam, "steamapps", "workshop", "content", "1815830"), LevelSource.SteamWorkshop);
            AddFolder(Path.Combine(steam, "steamapps", "common", "GRAB", "Resources", "levels"), LevelSource.SteamInternal);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LevelEntry>();
        foreach (var (path, source) in files)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { continue; }
            if (!seen.Add(full)) continue;
            result.Add(ReadEntry(full, source, remote: false));
        }
        return result.OrderByDescending(e => e.Modified).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<LevelEntry> ScanQuest(string? adbPath = null, Action<string>? status = null, int timeoutMs = 15000)
    {
        var result = new List<LevelEntry>();
        string adb = string.IsNullOrWhiteSpace(adbPath) ? GrabEngine.UI.InstallDialog.FindAdb() : adbPath;
        if (string.IsNullOrWhiteSpace(adb)) return result;
        try
        {
            status?.Invoke("Checking for a connected Meta Quest...");
            string devices = Run(adb, "devices", null, 4000);
            if (!devices.Split('\n').Any(line => line.Trim().EndsWith("\tdevice", StringComparison.Ordinal) ||
                line.Trim().EndsWith(" device", StringComparison.Ordinal)))
            {
                status?.Invoke("No authorized Meta Quest found.");
                return result;
            }

            status?.Invoke("Reading the Quest level folder...");
            string listing = Run(adb, $"shell ls -1 \"{QuestRemoteFolder}\"", null, timeoutMs);
            var names = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.EndsWith(".level", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
            {
                status?.Invoke("The Quest level folder is empty.");
                return result;
            }

            Directory.CreateDirectory(QuestCacheRoot);
            int pulled = 0;
            foreach (var name in names)
            {
                string local = Path.Combine(QuestCacheRoot, Path.GetFileName(name));
                try
                {
                    status?.Invoke($"Pulling {name} ({++pulled}/{names.Count})...");
                    Run(adb, $"pull \"{QuestRemoteFolder}/{name}\" \"{local}\"", null, 60000);
                    if (File.Exists(local))
                        result.Add(ReadEntry(local, LevelSource.MetaQuest, remote: true));
                }
                catch (Exception ex)
                {
                    status?.Invoke("Could not pull " + name + ": " + ex.Message);
                }
            }
            status?.Invoke($"Found {result.Count} Quest level(s).");
        }
        catch (Exception ex)
        {
            status?.Invoke("Quest scan failed: " + ex.Message);
        }
        return result.OrderByDescending(e => e.Modified).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static LevelEntry ReadEntry(string path, LevelSource source, bool remote)
    {
        var info = new FileInfo(path);
        string title = Path.GetFileNameWithoutExtension(path);
        string creators = "";
        int nodes = 0;
        try
        {
            var level = LevelIO.Load(path);
            if (!string.IsNullOrWhiteSpace(level.Title)) title = level.Title;
            creators = level.Creators;
            nodes = level.AllNodes().Count();
        }
        catch
        {
            // Keep the card useful even when a level came from a newer game version.
        }
        return new LevelEntry
        {
            Path = path,
            FileName = Path.GetFileName(path),
            Title = title,
            Creators = creators,
            ObjectCount = nodes,
            Source = source,
            Size = info.Exists ? info.Length : 0,
            Modified = info.Exists ? info.LastWriteTime : DateTime.MinValue,
            Remote = remote
        };
    }

    public static string DuplicateToMyLevels(LevelEntry entry, string? name = null)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRAB", "levels", "user");
        Directory.CreateDirectory(folder);
        string baseName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(entry.FileName) : name.Trim();
        string target = Path.Combine(folder, Sanitize(baseName) + ".level");
        int suffix = 2;
        while (File.Exists(target))
            target = Path.Combine(folder, Sanitize(baseName) + " " + suffix++ + ".level");
        File.Copy(entry.Path, target);
        return target;
    }

    public static string? FindGrabInstall()
    {
        var candidates = new List<string>();
        try
        {
            var fromEnv = Environment.GetEnvironmentVariable("GRAB_ENGINE_GRAB_DIR");
            if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);
        }
        catch { }
        foreach (var steam in SteamLibraries())
            candidates.Add(Path.Combine(steam, "steamapps", "common", "GRAB"));
        candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\GRAB");
        candidates.Add(@"C:\Program Files\Steam\steamapps\common\GRAB");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try { candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "GRAB")); }
            catch { }
        }
        return candidates.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) &&
            Directory.Exists(Path.Combine(d, "Resources", "levels")));
    }

    public static IReadOnlyList<string> SteamLibraries()
    {
        var result = new List<string>();
        string? steamPath = null;
        try { steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steamPath))
            steamPath = @"C:\Program Files (x86)\Steam";
        if (Directory.Exists(steamPath)) result.Add(steamPath);
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        try
        {
            string text = File.ReadAllText(vdf);
            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                string path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                    result.Add(path);
            }
        }
        catch { }
        return result;
    }

    public static string? FindAdb() => GrabEngine.UI.InstallDialog.FindAdb();

    private static string Run(string exe, string arguments, string? workingFolder, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingFolder ?? Environment.CurrentDirectory
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("adb did not respond.");
        }
        string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("adb exited with code " + process.ExitCode + ": " + text.Trim());
        return text;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "level" : safe;
    }
}
