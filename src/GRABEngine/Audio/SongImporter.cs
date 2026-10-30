using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.Audio;

public sealed class SongImportResult
{
    public LevelNode? Group { get; set; }
    public int Sounds { get; set; }
    public int Triggers { get; set; }
    public int Blocks { get; set; }
    public List<string> Warnings { get; } = new();
    public string Summary =>
        $"{Sounds} sound block(s), {Triggers} trigger(s), {Blocks} Code Block(s)";
}

/// <summary>
/// Writes a transcribed song into a .level. The retail format has no audio payload,
/// so every note becomes a real Sound block. In scheduled mode each time slice gets a
/// trigger targeting those sounds and a small Code Block that forces the trigger on/off
/// with SLEEP/Update timing, which the real game runs as normal GASM.
/// </summary>
public static class SongImporter
{
    public static SongImportResult Import(GrabLevel level, SongPlan plan, Mp3ImportSettings settings,
        Vec3 origin, Action<string>? progress = null)
    {
        var result = new SongImportResult();
        if (plan.Notes.Count == 0)
        {
            result.Warnings.Add("Nothing to import: the analyser found no notes.");
            return result;
        }
        level.EnsureLayers();
        var group = new LevelNode
        {
            Kind = NodeKind.Group,
            Name = UniqueName(level, "Music - " + System.IO.Path.GetFileNameWithoutExtension(plan.FileName)),
            Position = origin,
            LayerId = level.ActiveLayerId
        };
        var used = new HashSet<string>(level.AllNodes().Select(n => n.DisplayName), StringComparer.OrdinalIgnoreCase);

        if (!settings.Scheduled)
        {
            foreach (var note in plan.Notes)
            {
                var sound = MakeSound(level, note, settings, used, result);
                sound.SoundStartActive = settings.StartActive;
                group.Children.Add(sound);
            }
            level.AddNode(group);
            level.RebuildParents();
            result.Group = group;
            result.Sounds = group.Children.Count;
            level.IsDirty = true;
            return result;
        }

        // One optional start trigger. It does NOT own the sound blocks: the timed
        // per-slice triggers and scheduler Code Blocks below still play the exact score.
        // The schedulers simply wait for this trigger, so the whole song starts when the
        // player touches it (or another script sets its ForceActive) and it can replay
        // after the score finishes.
        LevelNode? master = null;
        if (settings.SingleTrigger)
        {
            master = new LevelNode
            {
                Kind = NodeKind.Trigger,
                Name = NextName(used, "Music Start "),
                Position = new Vec3(0, 0.8f, 2.5f),
                Scale = new Vec3(0.5f, 0.5f, 0.5f),
                Shape = ShapeInfo.Cube,
                LayerId = level.ActiveLayerId
            };
            master.TriggerSources.Add(new TriggerSource { BasicType = 0 }); // hand
            group.Children.Add(master);
        }

        // Group note events into small chords so one trigger can start a whole slice.
        var events = plan.Notes
            .GroupBy(n => (int)MathF.Round(n.Start * 40f))
            .OrderBy(g => g.Key)
            .ToList();
        var pending = new List<(float Start, float End, LevelNode Trigger)>();
        foreach (var groupOfNotes in events)
        {
            var slice = groupOfNotes.OrderByDescending(n => n.Amplitude).Take(4).ToList();
            float start = slice.Min(n => n.Start);
            float end = slice.Max(n => n.Start + Math.Max(0.08f, n.Duration * 0.92f));
            var trigger = new LevelNode
            {
                Kind = NodeKind.Trigger,
                Name = NextName(used, "Music Trig "),
                Position = new Vec3(0, 0.25f, 0),
                Scale = new Vec3(0.3f, 0.3f, 0.3f),
                Shape = ShapeInfo.Cube,
                LayerId = level.ActiveLayerId
            };
            foreach (var note in slice)
            {
                var sound = MakeSound(level, note, settings, used, result);
                sound.SoundStartActive = false;
                sound.Position = new Vec3(((result.Sounds % 7) - 3) * 0.35f, (result.Sounds / 7) * 0.22f, 0);
                trigger.TriggerTargets.Add(new TriggerTarget
                {
                    Kind = 3,
                    Mode = 0,
                    ObjectName = sound.DisplayName,
                    TargetMode = 1, // TriggerTargetSound.Mode.START
                    Repeat = false
                });
                group.Children.Add(sound);
            }
            group.Children.Add(trigger);
            pending.Add((start, end, trigger));
        }

        level.AddNode(group);
        level.RebuildParents();
        var ids = level.BuildObjectIds();
        foreach (var (_, _, trigger) in pending)
        {
            foreach (var target in trigger.TriggerTargets)
            {
                var sound = level.FindByName(target.ObjectName);
                if (sound != null) target.ObjectId = ids.TryGetValue(sound, out var id) ? id : 0;
            }
        }

        float scoreEnd = pending.Count > 0 ? pending.Max(p => p.End) + 0.2f : 0f;
        int made = 0;
        var schedulers = new List<LevelNode>();
        foreach (var (start, end, trigger) in pending)
        {
            var block = new LevelNode
            {
                Kind = NodeKind.GASM,
                Name = NextName(used, "Music Event "),
                Position = new Vec3(0, 0.5f, 0),
                Scale = Vec3.One,
                LayerId = level.ActiveLayerId,
                StartActive = true
            };
            string script = BuildSchedulerScript(block.Name, trigger.DisplayName, start,
                Math.Max(0.09f, end - start), master?.DisplayName, scoreEnd);
            block.Script = script;
            group.Children.Add(block);
            level.RebuildParents();
            var compiled = new CSharpCompiler { AllowExtendedGasm = EngineSettings.ExtendedGasm }.Compile(level, block, script);
            block.Compiled = compiled;
            schedulers.Add(block);
            result.Blocks++;
            if (!compiled.Success)
            {
                var first = compiled.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                result.Warnings.Add($"Scheduler '{block.Name}' failed: {first?.Message ?? "unknown compile error"}");
            }
            made++;
            if (made % 8 == 0) progress?.Invoke($"Building music blocks... {made}/{pending.Count}");
        }

        // Native, visible connection: the start trigger STARTs every scheduler Code Block,
        // not just "some trigger the blocks happen to read". The schedulers still poll the
        // trigger as a fallback, so playback is complete even if GRAB caps the target list.
        int connectedSchedulers = 0;
        if (master != null && schedulers.Count > 0)
        {
            var finalIds = level.BuildObjectIds();
            foreach (var scheduler in schedulers.Take(GrabLimits.TriggerTargetBudget))
            {
                master.TriggerTargets.Add(new TriggerTarget
                {
                    Kind = 7,              // TriggerTargetGASM
                    Mode = 0,              // On Enter
                    TargetMode = 1,        // TriggerTargetGASM.Mode.START
                    ObjectName = scheduler.DisplayName,
                    ObjectId = finalIds.TryGetValue(scheduler, out var schedulerId) ? schedulerId : 0
                });
                connectedSchedulers++;
            }
        }

        level.EnsureNames();
        level.RebuildParents();
        level.IsDirty = true;
        result.Group = group;
        result.Triggers = pending.Count + (master != null ? 1 : 0);
        if (master != null)
            result.Warnings.Add($"One start trigger ('{master.DisplayName}'): it is connected to {connectedSchedulers} of {schedulers.Count} scheduler Code Block(s) with native START targets; the rest poll it, so the full timed score still plays when it is activated. Touch it in game or set its ForceActive. Activate it again after the score ends to replay.");
        result.Warnings.Add("Sound blocks are synthesised oscillators. The retail level format cannot embed an MP3.");
        progress?.Invoke($"Music imported: {result.Summary}");
        return result;
    }

    private static LevelNode MakeSound(GrabLevel level, SongNote note, Mp3ImportSettings settings,
        HashSet<string> used, SongImportResult result)
    {
        float duration = Math.Clamp(note.Duration * Math.Max(0.4f, settings.NoteLengthScale), 0.06f, 3f);
        float attack = note.Wave == 3 ? 0.003f : 0.012f;
        float release = Math.Clamp(duration * 0.35f, 0.04f, 0.6f);
        float sustain = Math.Max(0.03f, duration - attack - release);
        var sound = new LevelNode
        {
            Kind = NodeKind.Sound,
            Name = NextName(used, "Tone "),
            Position = Vec3.Zero,
            LayerId = level.ActiveLayerId,
            SoundName = "mp3",
            SoundWaveType = note.Wave,
            SoundFrequency = Math.Clamp(note.Frequency, 20f, 12000f),
            SoundVolume = Math.Clamp(note.Amplitude * settings.Volume, 0.03f, 1.5f),
            SoundAttack = attack,
            SoundSustain = sustain,
            SoundRelease = release,
            SoundRepeat = false,
            SoundStartActive = false,
            SoundMaxRange = 1000f
        };
        sound.Scale = new Vec3(sound.SoundMaxRange, sound.SoundMaxRange, sound.SoundMaxRange);
        result.Sounds++;
        return sound;
    }

    private static string BuildSchedulerScript(string className, string triggerName, float start, float hold,
        string? gateName = null, float scoreEnd = 0f)
    {
        string safeClass = "Music" + new string(className.Where(char.IsLetterOrDigit).ToArray());
        string startText = start.ToString("0.####", CultureInfo.InvariantCulture);
        string endText = (start + hold).ToString("0.####", CultureInfo.InvariantCulture);
        if (gateName == null)
        {
            return $$"""
using Grab;

// Generated by GRAB ENGINE's MP3 importer. Real GASM drives a trigger that
// starts the sound blocks for this slice of the song.
public class {{safeClass}} : CodeBlock
{
    GameObject trigger = GameObject.Find<Trigger>("{{triggerName}}");
    float time;
    int stage;

    void Update()
    {
        time += Time.DeltaTime;
        if (stage == 0 && time >= {{startText}}f)
        {
            trigger.ForceActive = 1;
            stage = 1;
        }
        if (stage == 1 && time >= {{endText}}f)
        {
            trigger.ForceActive = 0;
            stage = 2;
        }
    }
}
""";
        }

        string scoreEndText = scoreEnd.ToString("0.####", CultureInfo.InvariantCulture);
        return $$"""
using Grab;

// Generated by GRAB ENGINE's MP3 importer. This slice waits for the Music
// Start trigger, then plays its part of the same timed score. Activate the
// start trigger again after the score ends to replay.
public class {{safeClass}} : CodeBlock
{
    GameObject trigger = GameObject.Find<Trigger>("{{triggerName}}");
    GameObject gate = GameObject.Find<Trigger>("{{gateName}}");
    float time;
    int stage;
    int started;

    void Update()
    {
        if (started == 0)
        {
            if (gate.Active == 1)
            {
                started = 1;
            }
            else
            {
                if (gate.ForceActive == 1) started = 1;
            }
            if (started == 1)
            {
                time = 0;
                stage = 0;
            }
        }
        if (started == 1)
        {
            time += Time.DeltaTime;
            if (stage == 0 && time >= {{startText}}f)
            {
                trigger.ForceActive = 1;
                stage = 1;
            }
            if (stage == 1 && time >= {{endText}}f)
            {
                trigger.ForceActive = 0;
                stage = 2;
            }
            if (stage == 2)
            {
                if (time >= {{scoreEndText}}f)
                {
                    if (gate.Active == 0)
                    {
                        if (gate.ForceActive == 0)
                        {
                            started = 0;
                            time = 0;
                            stage = 0;
                        }
                    }
                }
            }
        }
    }
}
""";
    }

    private static string UniqueName(GrabLevel level, string baseName)
    {
        var used = new HashSet<string>(level.AllNodes().Select(n => n.DisplayName), StringComparer.OrdinalIgnoreCase);
        string name = baseName;
        int i = 2;
        while (!used.Add(name)) name = baseName + " " + i++;
        return name;
    }

    private static string NextName(HashSet<string> used, string prefix)
    {
        int i = 1;
        string name;
        do { name = prefix + i++; } while (!used.Add(name));
        return name;
    }
}
