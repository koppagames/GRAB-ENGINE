using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.Audio;

public sealed class SpeechImportResult
{
    public LevelNode? Group { get; set; }
    public int Sounds { get; set; }
    public int Triggers { get; set; }
    public int Blocks { get; set; }
    public List<string> Warnings { get; } = new();
    public string Summary => $"{Sounds} sound block(s), {Triggers} trigger(s), {Blocks} Code Block(s)";
}

/// <summary>
/// Writes a phoneme score into a .level as oscillator sound blocks. Each phoneme gets
/// up to three sine blocks at its formant frequencies, a trigger that starts them, and a
/// tiny Code Block that fires that trigger at the right time with real GASM.
/// </summary>
public static class SpeechImporter
{
    public static SpeechImportResult Import(GrabLevel level, SpeechPlan plan, SpeechSettings settings,
        Vec3 origin, Action<string>? progress = null)
    {
        var result = new SpeechImportResult();
        if (plan.Phones.Count == 0)
        {
            result.Warnings.Add("Nothing to speak: no phonemes were generated.");
            return result;
        }

        level.EnsureLayers();
        var group = new LevelNode
        {
            Kind = NodeKind.Group,
            Name = UniqueName(level, "Speech - " + Label(plan.Text)),
            Position = origin,
            LayerId = level.ActiveLayerId
        };
        var used = new HashSet<string>(level.AllNodes().Select(n => n.DisplayName), StringComparer.OrdinalIgnoreCase);
        var pending = new List<(float Start, float End, LevelNode Trigger)>();
        bool shared = settings.SingleTrigger;
        int clarity = Math.Clamp(settings.Clarity, 1, 4);
        float volume = Math.Clamp(settings.Volume, 0.05f, 1.5f);
        float consonantBoost = Math.Clamp(settings.ConsonantBoost, 0.3f, 3f);
        float[] weights = { 1.0f, 0.75f, 0.45f, 0.22f };
        int phoneNumber = 0;

        foreach (var phone in plan.Phones)
        {
            if (phone.Duration <= 0.002f) continue;
            bool voiced = phone.Voiced;
            bool noisy = phone.Noise > 0.05f;
            if (!voiced && !noisy) continue;

            var trigger = new LevelNode
            {
                Kind = NodeKind.Trigger,
                Name = NextName(used, "Spch Trig "),
                Position = new Vec3(0, 0.25f, 0),
                Scale = new Vec3(0.3f, 0.3f, 0.3f),
                Shape = ShapeInfo.Cube,
                LayerId = level.ActiveLayerId
            };
            var receiver = trigger;
            var placedAt = new Vec3(((phoneNumber % 7) - 3) * 0.32f, (phoneNumber / 7) * 0.2f, 0);

            if (voiced)
            {
                float[] formants =
                {
                    phone.F1, phone.F2, phone.F3,
                    phone.F4 > 0 ? phone.F4 : Math.Min(5200f, phone.F3 * 1.3f)
                };
                for (int k = 0; k < clarity; k++)
                {
                    var tone = MakeTone(level, used, result, formants[k],
                        phone.Amp * weights[k] * volume * 0.62f,
                        0.008f, Math.Max(0.02f, phone.Duration - 0.02f), 0.014f,
                        placedAt + new Vec3(k * 0.06f, 0, 0));
                    AddTarget(receiver, tone);
                    group.Children.Add(tone);
                }
            }

            if (noisy)
            {
                float closure = Math.Clamp(phone.Closure, 0f, 0.8f);
                float noiseDuration = Math.Max(0.025f, phone.Duration * (1f - closure));
                var burst = MakeTone(level, used, result, phone.F2, 0,
                    0.004f, 0.01f, 0.008f, placedAt, waveform: 3,
                    volumeOverride: phone.Amp * phone.Noise * volume * 0.5f * consonantBoost,
                    attackOverride: Math.Max(0.004f, phone.Duration * closure),
                    sustainOverride: Math.Max(0.012f, noiseDuration - 0.012f));
                AddTarget(receiver, burst);
                group.Children.Add(burst);
            }

            group.Children.Add(trigger);
            pending.Add((phone.Start, phone.Start + phone.Duration, trigger));
            phoneNumber++;
        }

        if (pending.Count == 0)
        {
            result.Warnings.Add("The score contained no playable phonemes.");
            return result;
        }

        // One optional start trigger. The phoneme timeline stays exactly the same;
        // every scheduler waits for this trigger before starting its slice.
        LevelNode? master = shared
            ? new LevelNode
            {
                Kind = NodeKind.Trigger,
                Name = NextName(used, "Speech Start "),
                Position = new Vec3(0, 0.8f, 2.5f),
                Scale = new Vec3(0.5f, 0.5f, 0.5f),
                Shape = ShapeInfo.Cube,
                LayerId = level.ActiveLayerId
            }
            : null;
        if (master != null)
        {
            master.TriggerSources.Add(new TriggerSource { BasicType = 0 }); // hand
            group.Children.Add(master);
        }

        level.AddNode(group);
        level.RebuildParents();
        var ids = level.BuildObjectIds();
        foreach (var trigger in pending.Select(p => p.Trigger))
        {
            foreach (var target in trigger.TriggerTargets)
            {
                var sound = level.FindByName(target.ObjectName);
                if (sound != null) target.ObjectId = ids.TryGetValue(sound, out var id) ? id : 0;
            }
        }

        float scoreEnd = pending.Max(p => p.End) + 0.2f;
        int made = 0;
        var schedulers = new List<LevelNode>();
        foreach (var (start, end, trigger) in pending)
        {
            var block = new LevelNode
            {
                Kind = NodeKind.GASM,
                Name = NextName(used, "Speech "),
                Position = new Vec3(0, 0.5f, 0),
                Scale = Vec3.One,
                LayerId = level.ActiveLayerId,
                StartActive = true
            };
            string script = BuildSchedulerScript(block.Name, trigger.DisplayName, start,
                Math.Max(0.05f, end - start), master?.DisplayName, scoreEnd);
            block.Script = script;
            group.Children.Add(block);
            level.RebuildParents();
            block.Compiled = new CSharpCompiler { AllowExtendedGasm = EngineSettings.ExtendedGasm }.Compile(level, block, script);
            schedulers.Add(block);
            result.Blocks++;
            if (!block.Compiled.Success)
            {
                var first = block.Compiled.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                result.Warnings.Add($"Speech scheduler '{block.Name}' failed: {first?.Message ?? "unknown compile error"}");
            }
            made++;
            if (made % 8 == 0) progress?.Invoke($"Building speech blocks... {made}/{pending.Count}");
        }

        // Native, visible connection: the start trigger STARTs every scheduler Code Block.
        // The schedulers also poll the trigger, so the whole score still runs if GRAB caps
        // the number of entries a trigger target list can hold.
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
            result.Warnings.Add($"One start trigger ('{master.DisplayName}'): it is connected to {connectedSchedulers} of {schedulers.Count} scheduler Code Block(s) with native START targets; the rest poll it, so the full spoken score still plays when it is activated. Activate it again after the speech ends to replay.");
        result.Warnings.Add("Sound blocks are oscillators, so speech is a formant robot voice, not a recording.");
        progress?.Invoke($"Speech imported: {result.Summary}");
        return result;
    }

    private static LevelNode MakeTone(GrabLevel level, HashSet<string> used, SpeechImportResult result,
        float frequency, float volume, float attack, float sustain, float release, Vec3 position,
        int waveform = 2, float volumeOverride = 0f,
        float attackOverride = 0f, float sustainOverride = 0f)
    {
        var sound = new LevelNode
        {
            Kind = NodeKind.Sound,
            Name = NextName(used, waveform == 3 ? "Spch Noise " : "Spch Tone "),
            Position = position,
            LayerId = level.ActiveLayerId,
            SoundName = waveform == 3 ? "burst" : "voice",
            SoundWaveType = waveform,
            SoundFrequency = Math.Clamp(frequency, 20f, 12000f),
            SoundVolume = Math.Clamp(volumeOverride > 0 ? volumeOverride : volume, 0.02f, 1.6f),
            SoundAttack = Math.Max(0.002f, attackOverride > 0 ? attackOverride : attack),
            SoundSustain = Math.Max(0.012f, sustainOverride > 0 ? sustainOverride : sustain),
            SoundRelease = Math.Clamp(release, 0.004f, 0.5f),
            SoundRepeat = false,
            SoundStartActive = false,
            SoundMaxRange = 1000f
        };
        sound.Scale = new Vec3(sound.SoundMaxRange, sound.SoundMaxRange, sound.SoundMaxRange);
        result.Sounds++;
        return sound;
    }

    private static void AddTarget(LevelNode trigger, LevelNode sound)
    {
        trigger.TriggerTargets.Add(new TriggerTarget
        {
            Kind = 3,
            Mode = 0,
            ObjectName = sound.DisplayName,
            TargetMode = 1, // TriggerTargetSound.Mode.START
            Repeat = false
        });
    }

    private static string BuildSchedulerScript(string className, string triggerName, float start, float hold,
        string? gateName = null, float scoreEnd = 0f)
    {
        string safeClass = "Speech" + new string(className.Where(char.IsLetterOrDigit).ToArray());
        string startText = start.ToString("0.####", CultureInfo.InvariantCulture);
        string endText = (start + hold).ToString("0.####", CultureInfo.InvariantCulture);
        if (gateName == null)
        {
            return $$"""
using Grab;

// Generated by GRAB ENGINE's text-to-speech importer. Real GASM starts this
// phoneme's formant sound blocks and releases them when the phoneme ends.
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

// Generated by GRAB ENGINE's text-to-speech importer. This phoneme waits for
// the Speech Start trigger, then plays its part of the same spoken score.
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

    private static string Label(string text)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3);
        string label = string.Join(" ", words);
        if (label.Length > 22) label = label[..22];
        return string.IsNullOrWhiteSpace(label) ? "voice" : label;
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
