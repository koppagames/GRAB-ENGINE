using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GrabEngine.Core;

public sealed class AutosaveInfo
{
    public string Json { get; init; } = "";
    public DateTime SavedUtc { get; init; }
    public string Title { get; init; } = "";
    public string? ProjectPath { get; init; }
}

public sealed class SnapshotInfo
{
    public string FilePath { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTime Time { get; init; }
    public long Bytes { get; init; }
    public string Title { get; init; } = "";
    public string DisplayTime => Time.ToString("yyyy-MM-dd  HH:mm:ss");
    public string DisplaySize => Bytes >= 1024 * 1024
        ? (Bytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
        : Math.Max(1, Bytes / 1024) + " KB";
    public string Display => $"{DisplayTime}   {Label}";
}

/// <summary>
/// Editor autosaves and version snapshots live outside the project so crash
/// recovery and history never pollute the folder the user exports levels from.
/// </summary>
public static class WorkspaceStore
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GRAB ENGINE");
    private static string Autosaves => Path.Combine(Root, "Autosave");
    private static string Histories => Path.Combine(Root, "History");
    public static string HistoryRoot => Histories;

    private sealed class AutosaveMeta
    {
        public string Title { get; set; } = "";
        public string? ProjectPath { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    private static string Key(string? projectPath, string title)
    {
        string source = string.IsNullOrWhiteSpace(projectPath)
            ? "untitled:" + (title ?? "")
            : Path.GetFullPath(projectPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Safe(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var value = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(value) ? "snapshot" : value[..Math.Min(48, value.Length)];
    }

    private static string AutosaveJson(string key) => Path.Combine(Autosaves, key + ".grabproj");
    private static string AutosaveMetaPath(string key) => Path.Combine(Autosaves, key + ".json");

    public static void WriteAutosave(string json, string? projectPath, string title)
    {
        try
        {
            Directory.CreateDirectory(Autosaves);
            string key = Key(projectPath, title);
            File.WriteAllText(AutosaveJson(key), json);
            var meta = new AutosaveMeta { Title = title ?? "", ProjectPath = projectPath, SavedUtc = DateTime.UtcNow };
            File.WriteAllText(AutosaveMetaPath(key), JsonSerializer.Serialize(meta));
        }
        catch
        {
            // Autosave must never interrupt editing (disk full, permissions, etc.).
        }
    }

    /// <summary>The most recent autosave from any project, used at startup so a crash
    /// while a named project was open does not strand its recovery file.</summary>
    public static AutosaveInfo? LatestAutosave()
    {
        try
        {
            if (!Directory.Exists(Autosaves)) return null;
            AutosaveInfo? best = null;
            foreach (var file in Directory.EnumerateFiles(Autosaves, "*.grabproj"))
            {
                var info = ReadAutosave(file);
                if (info != null && (best == null || info.SavedUtc > best.SavedUtc)) best = info;
            }
            return best;
        }
        catch { return null; }
    }

    private static AutosaveInfo? ReadAutosave(string jsonPath)
    {
        try
        {
            string key = Path.GetFileNameWithoutExtension(jsonPath);
            var meta = new AutosaveMeta();
            var metaPath = Path.Combine(Autosaves, key + ".json");
            if (File.Exists(metaPath))
                meta = JsonSerializer.Deserialize<AutosaveMeta>(File.ReadAllText(metaPath)) ?? new AutosaveMeta();
            return new AutosaveInfo
            {
                Json = File.ReadAllText(jsonPath),
                SavedUtc = File.GetLastWriteTimeUtc(jsonPath),
                Title = meta.Title,
                ProjectPath = meta.ProjectPath
            };
        }
        catch { return null; }
    }

    public static AutosaveInfo? LatestAutosave(string? projectPath, string title)
    {
        try
        {
            string key = Key(projectPath, title);
            string jsonPath = AutosaveJson(key);
            if (!File.Exists(jsonPath)) return null;
            var meta = new AutosaveMeta();
            try
            {
                var metaPath = AutosaveMetaPath(key);
                if (File.Exists(metaPath))
                    meta = JsonSerializer.Deserialize<AutosaveMeta>(File.ReadAllText(metaPath)) ?? new AutosaveMeta();
            }
            catch { }
            return new AutosaveInfo
            {
                Json = File.ReadAllText(jsonPath),
                SavedUtc = File.GetLastWriteTimeUtc(jsonPath),
                Title = string.IsNullOrWhiteSpace(meta.Title) ? title : meta.Title,
                ProjectPath = meta.ProjectPath ?? projectPath
            };
        }
        catch { return null; }
    }

    public static void ClearAutosave(string? projectPath, string title)
    {
        try
        {
            string key = Key(projectPath, title);
            File.Delete(AutosaveJson(key));
            File.Delete(AutosaveMetaPath(key));
        }
        catch { }
    }

    public static SnapshotInfo CreateSnapshot(string json, string? projectPath, string title, string label)
    {
        Directory.CreateDirectory(Histories);
        string key = Key(projectPath, title);
        string folder = Path.Combine(Histories, key);
        Directory.CreateDirectory(folder);
        var now = DateTime.Now;
        string file = Path.Combine(folder, now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Safe(label) + ".grabproj");
        File.WriteAllText(file, json);
        var info = new SnapshotInfo
        {
            FilePath = file,
            Label = string.IsNullOrWhiteSpace(label) ? "Snapshot" : label,
            Time = now,
            Bytes = new FileInfo(file).Length,
            Title = title ?? ""
        };
        TrimSnapshots(folder);
        return info;
    }

    private static void TrimSnapshots(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "*.grabproj")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(80)
                .ToList();
            foreach (var file in files) file.Delete();
        }
        catch { }
    }

    public static List<SnapshotInfo> ListSnapshots(string? projectPath, string title)
    {
        var result = new List<SnapshotInfo>();
        try
        {
            string folder = Path.Combine(Histories, Key(projectPath, title));
            if (!Directory.Exists(folder)) return result;
            foreach (var file in Directory.EnumerateFiles(folder, "*.grabproj"))
            {
                try
                {
                    var info = new FileInfo(file);
                    string name = Path.GetFileNameWithoutExtension(file);
                    int split = name.IndexOf('_');
                    string label = split >= 0 ? name[(split + 1)..].Replace('_', ' ') : name;
                    result.Add(new SnapshotInfo
                    {
                        FilePath = file,
                        Label = label,
                        Time = info.LastWriteTime,
                        Bytes = info.Length,
                        Title = title ?? ""
                    });
                }
                catch { }
            }
        }
        catch { }
        return result.OrderByDescending(s => s.Time).ToList();
    }

    public static string ReadSnapshot(string path) => File.ReadAllText(path);

    public static void DeleteSnapshot(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
