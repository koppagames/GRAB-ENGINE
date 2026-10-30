using System;
using System.IO;
using System.Text.Json;

namespace GrabEngine.Core;

/// <summary>
/// Editor-wide options that are not part of a project file. They live next to the
/// autosaves so opening a different project, or a new level, keeps them.
/// </summary>
public static class EngineSettings
{
    private sealed class Data
    {
        public bool ExtendedGasm { get; set; } = true;
        public bool ExtendedTriggerTargets { get; set; } = true;
    }

    private static readonly Data Current = Load();

    private static string FilePath => Path.Combine(WorkspaceStore.Root, "settings.json");

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch
        {
            // A corrupt settings file must never stop the editor from starting.
        }
        return new Data();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// GRAB's GASM runtime keeps instructions, connections and registers in dynamic
    /// arrays. The 150-instruction / 10-connection / 480-register numbers are limits of
    /// the retail editor's GASM screen, not of the interpreter: the editor truncates a
    /// pasted program at 150, but a .level file that already contains a longer program
    /// loads and runs. With this on, a script is emitted as one Code Block instead of
    /// being split across continuation pages that each stay under the editor's numbers.
    /// </summary>
    public static bool ExtendedGasm
    {
        get => Current.ExtendedGasm;
        set
        {
            if (Current.ExtendedGasm == value) return;
            Current.ExtendedGasm = value;
            Save();
        }
    }

    /// <summary>
    /// A trigger stores its target list as a repeated protobuf field, so the file itself
    /// has no maximum. The retail editor stops adding targets at 17 (its settings screen
    /// prints "Connections: n / …" and refuses more with "The max connection limit is
    /// reached"), but the loader and the trigger runtime walk the whole list. With this
    /// on, one trigger can START every object it needs — a whole song, a whole scene.
    /// </summary>
    public static bool ExtendedTriggerTargets
    {
        get => Current.ExtendedTriggerTargets;
        set
        {
            if (Current.ExtendedTriggerTargets == value) return;
            Current.ExtendedTriggerTargets = value;
            Save();
        }
    }
}
