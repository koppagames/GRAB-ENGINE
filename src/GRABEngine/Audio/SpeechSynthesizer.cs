using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GrabEngine.Audio;

/// <summary>
/// Settings for the text-to-speech importer. Speech is rebuilt out of GRAB sound blocks,
/// so "clarity" is how many formant oscillators each phoneme gets (1 = buzzy,
/// 3 = the clearest vowels the format allows).
/// </summary>
public sealed class SpeechSettings
{
    /// <summary>Deep, Neutral or Bright. Changes pitch and the formant scale together.</summary>
    public string Voice { get; set; } = "Neutral";
    /// <summary>Base speaking pitch in Hz (a male voice is around 110-130).</summary>
    public float PitchHz { get; set; } = 120f;
    /// <summary>Speaking rate in words per minute. 165 is a relaxed conversational pace.</summary>
    public int RateWpm { get; set; } = 165;
    /// <summary>How many formant sound blocks each voiced phoneme gets (1-4).</summary>
    public int Clarity { get; set; } = 3;
    /// <summary>0 = flat robot, 1 = strong sentence melody.</summary>
    public float Intonation { get; set; } = 0.7f;
    /// <summary>Scales the gaps at commas and full stops.</summary>
    public float PauseScale { get; set; } = 1f;
    public float Volume { get; set; } = 0.9f;
    public float MaxSeconds { get; set; } = 20f;
    /// <summary>One of <see cref="PresetNames"/>; picking a preset changes every clarity field.</summary>
    public string ClarityPreset { get; set; } = "Clear";
    /// <summary>Resonator sharpness: above 1 narrows the formants so vowels separate.</summary>
    public float FormantSharpness { get; set; } = 1.35f;
    /// <summary>Pushes F2 away from F1 so vowels are more distinct (1 = natural).</summary>
    public float FormantContrast { get; set; } = 1.18f;
    /// <summary>Loudness boost for fricatives, plosives and other noise phonemes.</summary>
    public float ConsonantBoost { get; set; } = 1.6f;
    /// <summary>Glottal spectral tilt: above 1 is brighter and crisper.</summary>
    public float Brightness { get; set; } = 1.12f;
    /// <summary>Silence inserted between words, in seconds.</summary>
    public float WordGap { get; set; } = 0.04f;
    /// <summary>Stretches vowels so they are easier to identify.</summary>
    public float VowelLength { get; set; } = 1.12f;
    /// <summary>Spell initialisms letter by letter (API becomes A P I).</summary>
    public bool SpellAcronyms { get; set; } = true;
    /// <summary>
    /// Keep the timed phoneme schedule, but add one Speech Start trigger that gates it. The
    /// speech starts when that trigger is activated and preserves all phoneme timing; it never
    /// fires every sound at once.
    /// </summary>
    public bool SingleTrigger { get; set; }
}

/// <summary>One phoneme in the spoken score. Frequencies are already voice-scaled.</summary>
public sealed class SpeechPhone
{
    public string Symbol { get; set; } = "";
    public string Word { get; set; } = "";
    public float Start { get; set; }
    public float Duration { get; set; }
    public float F1 { get; set; }
    public float F2 { get; set; }
    public float F3 { get; set; }
    /// <summary>Optional fourth formant, used by the clearest clarity setting.</summary>
    public float F4 { get; set; }
    public float Amp { get; set; } = 1f;
    public bool Voiced { get; set; } = true;
    public float Noise { get; set; }
    /// <summary>Fraction of the phoneme that is silent before a plosive burst.</summary>
    public float Closure { get; set; }
    public float PitchHz { get; set; } = 120f;
    public bool Stressed { get; set; }
    public float End => Start + Duration;
}

public sealed class SpeechPlan
{
    public string Text { get; set; } = "";
    public string Voice { get; set; } = "Neutral";
    public float FormantScale { get; set; } = 1f;
    public float PitchScale { get; set; } = 1f;
    public List<SpeechPhone> Phones { get; set; } = new();
    public List<string> Warnings { get; } = new();
    public float Duration { get; set; }
    public string Summary =>
        $"{Phones.Count} phoneme(s) · {Duration:0.0}s · {Voice} voice · {Phones.Count(p => p.Voiced)} voiced";
}

/// <summary>
/// A small Klatt-style formant voice plus an English letter-to-sound front end.
/// The studio preview filters a glottal pulse through three resonators so the text is
/// understandable; the same phoneme score is then written to the level as sound blocks,
/// which is what the retail game can actually play.
/// </summary>
public static class SpeechSynthesizer
{
    public const int SampleRate = SoundSynth.SampleRate;
    public static readonly string[] VoiceNames = { "Deep", "Neutral", "Bright" };
    public static readonly string[] ClarityNames =
    {
        "1 formant (buzzy, smallest)", "2 formants (lean)", "3 formants (clear)",
        "4 formants (clearest, biggest)"
    };

    public static readonly string[] PresetNames = { "Natural", "Clear", "Ultra clear", "Max clarity", "Robot" };

    /// <summary>Applies one of the clarity presets in place. The UI then refreshes its fields.</summary>
    public static void ApplyPreset(SpeechSettings settings, string preset)
    {
        switch (preset)
        {
            case "Natural":
                settings.Clarity = 3;
                settings.FormantSharpness = 1.0f;
                settings.FormantContrast = 1.0f;
                settings.ConsonantBoost = 1.0f;
                settings.Brightness = 1.0f;
                settings.WordGap = 0.018f;
                settings.VowelLength = 1.0f;
                settings.Intonation = 0.65f;
                settings.RateWpm = 165;
                break;
            case "Ultra clear":
                settings.Clarity = 4;
                settings.FormantSharpness = 1.6f;
                settings.FormantContrast = 1.32f;
                settings.ConsonantBoost = 2.0f;
                settings.Brightness = 1.2f;
                settings.WordGap = 0.055f;
                settings.VowelLength = 1.22f;
                settings.Intonation = 0.75f;
                settings.RateWpm = 140;
                break;
            case "Max clarity":
                settings.Clarity = 4;
                settings.FormantSharpness = 1.95f;
                settings.FormantContrast = 1.45f;
                settings.ConsonantBoost = 2.5f;
                settings.Brightness = 1.3f;
                settings.WordGap = 0.075f;
                settings.VowelLength = 1.35f;
                settings.Intonation = 0.8f;
                settings.RateWpm = 120;
                break;
            case "Robot":
                settings.Clarity = 2;
                settings.FormantSharpness = 0.9f;
                settings.FormantContrast = 1.0f;
                settings.ConsonantBoost = 1.0f;
                settings.Brightness = 0.9f;
                settings.WordGap = 0.015f;
                settings.VowelLength = 0.95f;
                settings.Intonation = 0.25f;
                settings.RateWpm = 165;
                break;
            default: // Clear
                settings.ClarityPreset = "Clear";
                settings.Clarity = 3;
                settings.FormantSharpness = 1.35f;
                settings.FormantContrast = 1.18f;
                settings.ConsonantBoost = 1.6f;
                settings.Brightness = 1.12f;
                settings.WordGap = 0.04f;
                settings.VowelLength = 1.12f;
                settings.Intonation = 0.7f;
                settings.RateWpm = 155;
                break;
        }
        settings.ClarityPreset = preset;
    }

    public const string SampleText = "Hello! This is GRAB ENGINE speaking with sound blocks.";

    private sealed record PhoneDef(float F1, float F2, float F3, float Duration, bool Voiced, float Noise, float Amp, float Closure = 0f);

    private static readonly Dictionary<string, PhoneDef> Defs = new(StringComparer.OrdinalIgnoreCase)
    {
        // vowels
        ["IY"] = new(270, 2290, 3010, 0.125f, true, 0f, 1.00f),
        ["IH"] = new(390, 1990, 2550, 0.095f, true, 0f, 1.00f),
        ["EH"] = new(530, 1840, 2480, 0.105f, true, 0f, 1.00f),
        ["AE"] = new(660, 1720, 2410, 0.125f, true, 0f, 1.00f),
        ["AA"] = new(730, 1090, 2440, 0.130f, true, 0f, 1.00f),
        ["AO"] = new(570, 840, 2410, 0.130f, true, 0f, 1.00f),
        ["UH"] = new(440, 1020, 2240, 0.095f, true, 0f, 1.00f),
        ["UW"] = new(300, 870, 2240, 0.120f, true, 0f, 1.00f),
        ["AH"] = new(640, 1190, 2390, 0.095f, true, 0f, 1.00f),
        ["ER"] = new(490, 1350, 1690, 0.115f, true, 0f, 1.00f),
        ["AX"] = new(500, 1500, 2500, 0.060f, true, 0f, 0.85f),
        // nasals and liquids
        ["M"] = new(250, 1100, 2200, 0.070f, true, 0f, 0.55f),
        ["N"] = new(250, 1700, 2600, 0.065f, true, 0f, 0.55f),
        ["NG"] = new(250, 2000, 2700, 0.070f, true, 0f, 0.55f),
        ["L"] = new(380, 1300, 2700, 0.070f, true, 0f, 0.70f),
        ["R"] = new(420, 1250, 1650, 0.070f, true, 0f, 0.70f),
        ["W"] = new(300, 610, 2200, 0.060f, true, 0f, 0.60f),
        ["Y"] = new(260, 2070, 3000, 0.060f, true, 0f, 0.60f),
        // voiced fricatives / affricates
        ["V"] = new(350, 1100, 2400, 0.070f, true, 0.35f, 0.50f),
        ["DH"] = new(300, 1400, 2500, 0.060f, true, 0.30f, 0.55f),
        ["Z"] = new(300, 1600, 2600, 0.090f, true, 0.75f, 0.40f),
        ["ZH"] = new(300, 1800, 2600, 0.090f, true, 0.70f, 0.40f),
        ["JH"] = new(300, 1800, 2500, 0.075f, true, 0.50f, 0.50f, 0.45f),
        ["B"] = new(300, 900, 2200, 0.055f, true, 0.25f, 0.50f, 0.60f),
        ["D"] = new(300, 1600, 2600, 0.050f, true, 0.25f, 0.50f, 0.60f),
        ["G"] = new(300, 1800, 2400, 0.055f, true, 0.25f, 0.50f, 0.60f),
        // voiceless
        ["P"] = new(400, 1100, 2200, 0.060f, false, 1f, 0.50f, 0.65f),
        ["T"] = new(400, 1800, 2700, 0.055f, false, 1f, 0.50f, 0.65f),
        ["K"] = new(400, 1900, 2500, 0.060f, false, 1f, 0.50f, 0.65f),
        ["CH"] = new(1600, 3000, 4500, 0.080f, false, 1f, 0.55f, 0.45f),
        ["F"] = new(1200, 4000, 6500, 0.090f, false, 1f, 0.35f),
        ["TH"] = new(1200, 3500, 6000, 0.080f, false, 1f, 0.30f),
        ["S"] = new(1400, 5500, 7500, 0.100f, false, 1f, 0.35f),
        ["SH"] = new(1600, 3000, 4500, 0.100f, false, 1f, 0.40f),
        ["HH"] = new(700, 1500, 2500, 0.060f, false, 1f, 0.30f)
    };

    private static readonly HashSet<string> Vowels = new(StringComparer.OrdinalIgnoreCase)
    {
        // Diphthongs are listed because the letter-to-sound rules test for a nucleus
        // before they are expanded into two target vowels.
        "IY", "IH", "EH", "AE", "AA", "AO", "UH", "UW", "AH", "ER", "AX",
        "EY", "AY", "OY", "AW", "OW"
    };

    public static bool IsVowel(string symbol) => Vowels.Contains(symbol);

    public static float VoicePitchScale(string voice) => voice switch
    {
        "Deep" => 0.82f,
        "Bright" => 1.2f,
        _ => 1f
    };

    public static float VoiceFormantScale(string voice) => voice switch
    {
        "Deep" => 0.9f,
        "Bright" => 1.1f,
        _ => 1f
    };

    // ------------------------------------------------------------------ plan

    public static SpeechPlan Build(string text, SpeechSettings settings, Action<string>? progress = null)
    {
        var plan = new SpeechPlan
        {
            Text = (text ?? "").Trim(),
            Voice = VoiceNames.Contains(settings.Voice) ? settings.Voice : "Neutral"
        };
        plan.FormantScale = VoiceFormantScale(plan.Voice);
        plan.PitchScale = VoicePitchScale(plan.Voice);
        if (plan.Text.Length == 0) return plan;

        var sentences = SplitSentences(Normalize(plan.Text));
        float rateScale = Math.Clamp(165f / Math.Clamp(settings.RateWpm, 70, 300), 0.55f, 2.4f);
        float pauseScale = Math.Clamp(settings.PauseScale, 0.2f, 3f);
        float intonation = Math.Clamp(settings.Intonation, 0f, 1f);
        float contrast = Math.Clamp(settings.FormantContrast, 0.7f, 1.8f);
        float vowelLength = Math.Clamp(settings.VowelLength, 0.5f, 2.5f);
        float wordGap = Math.Clamp(settings.WordGap, 0f, 0.2f);
        float basePitch = Math.Clamp(settings.PitchHz, 70f, 320f) * plan.PitchScale;
        var random = new Random(1234);
        float t = 0.05f;
        int wordIndex = 0;

        foreach (var sentence in sentences)
        {
            var chunks = new List<(string Word, List<string> Phones, int Stress)>();
            foreach (var word in sentence.Words)
            {
                var pronunciation = WordPhones(word, settings.SpellAcronyms);
                if (pronunciation.Phones.Count > 0) chunks.Add(pronunciation);
            }
            if (chunks.Count == 0)
            {
                t += 0.2f * pauseScale;
                continue;
            }

            int total = chunks.Sum(c => c.Phones.Count);
            int index = 0;
            foreach (var chunk in chunks)
            {
                for (int phoneInWord = 0; phoneInWord < chunk.Phones.Count; phoneInWord++)
                {
                    string symbol = chunk.Phones[phoneInWord];
                    if (!Defs.TryGetValue(symbol, out var def)) continue;
                    bool stressed = IsVowel(symbol) && phoneInWord == chunk.Stress;
                    float duration = def.Duration * rateScale;
                    if (IsVowel(symbol)) duration *= (stressed ? 1.12f : 0.82f) * vowelLength;
                    if (index == total - 1) duration *= 1.18f;

                    float progressInSentence = total <= 1 ? 0f : index / (float)(total - 1);
                    float semitones = (0.55f - progressInSentence) * 2.2f * intonation;
                    if (stressed) semitones += 1.3f * intonation;
                    if (sentence.Terminator == '?')
                        semitones += Math.Clamp((progressInSentence - 0.6f) / 0.4f, 0f, 1f) * 3.4f * intonation;
                    semitones += (float)(random.NextDouble() - 0.5) * 0.3f * intonation;
                    float pitch = basePitch * MathF.Pow(2f, semitones / 12f);

                    float f1 = def.F1 * plan.FormantScale;
                    // Contrast separates the vowel, but never enough to push F2 past F3:
                    // an inverted formant pair destroys the vowel identity we are lifting.
                    float rawF2 = def.F1 + (def.F2 - def.F1) * contrast;
                    float f2 = Math.Min(rawF2, Math.Min(2800f, def.F3 * 0.95f)) * plan.FormantScale;
                    float f3 = def.F3 * plan.FormantScale;
                    var phone = new SpeechPhone
                    {
                        Symbol = symbol,
                        Word = chunk.Word,
                        Start = t,
                        Duration = Math.Max(0.02f, duration),
                        F1 = f1,
                        F2 = f2,
                        F3 = f3,
                        F4 = def.Voiced ? Math.Min(5200f, f3 * 1.3f) : 0f,
                        Amp = def.Amp * (stressed ? 1.1f : 1f),
                        Voiced = def.Voiced,
                        Noise = def.Noise,
                        Closure = def.Closure,
                        PitchHz = pitch,
                        Stressed = stressed
                    };
                    plan.Phones.Add(phone);
                    t += phone.Duration;
                    index++;
                    if (t >= settings.MaxSeconds)
                    {
                        plan.Warnings.Add($"Stopped at {settings.MaxSeconds:0.#}s; raise Max seconds to speak the whole text.");
                        break;
                    }
                }
                if (t >= settings.MaxSeconds) break;
                t += wordGap; // gap between words keeps consonants separated
                wordIndex++;
                if (wordIndex % 12 == 0) progress?.Invoke($"Speaking... {t:0.0}s");
            }
            if (t >= settings.MaxSeconds) break;
            t += PauseFor(sentence.Terminator) * pauseScale;
        }

        plan.Duration = t;
        if (plan.Phones.Count == 0) plan.Warnings.Add("No pronounceable words were found.");
        progress?.Invoke(plan.Summary);
        return plan;
    }

    // -------------------------------------------------------- voice preview

    /// <summary>
    /// Formant synthesis: glottal pulses (or noise) through three resonators. This is the
    /// "studio" rendition of the score, i.e. what the voice should sound like before it is
    /// reduced to oscillator sound blocks.
    /// </summary>
    public static float[] RenderVoice(SpeechPlan plan, SpeechSettings settings)
    {
        int length = Math.Max(1, (int)(Math.Min(settings.MaxSeconds + 0.2f, plan.Duration + 0.2f) * SampleRate));
        var buffer = new float[length];
        if (plan.Phones.Count == 0) return buffer;

        var r1 = new Resonator();
        var r2 = new Resonator();
        var r3 = new Resonator();
        var r4 = new Resonator();
        var random = new Random(99);
        const int block = 64;
        int phoneIndex = 0;
        float phase = 0f;
        float glottal = 0f;
        float smoothedAmp = 0f;
        float sharpness = Math.Clamp(settings.FormantSharpness, 0.4f, 2.2f);
        float consonantBoost = Math.Clamp(settings.ConsonantBoost, 0.3f, 3f);
        float decay = Math.Clamp(0.980f + 0.0074f * Math.Clamp(settings.Brightness, 0.5f, 2f), 0.975f, 0.996f);

        for (int start = 0; start < length; start += block)
        {
            float time = start / (float)SampleRate;
            while (phoneIndex + 1 < plan.Phones.Count && time >= plan.Phones[phoneIndex + 1].Start) phoneIndex++;
            var phone = plan.Phones[Math.Min(phoneIndex, plan.Phones.Count - 1)];

            // Formants glide through the whole phoneme: in from the previous sound and
            // out towards the next one. Consonant transitions are most of intelligibility.
            var prevPhone = plan.Phones[Math.Max(0, phoneIndex - 1)];
            var nextPhoneF = plan.Phones[Math.Min(phoneIndex + 1, plan.Phones.Count - 1)];
            float progress = phone.Duration > 0.001f
                ? Math.Clamp((time - phone.Start) / phone.Duration, 0f, 1f)
                : 1f;
            float wPrev = progress < 0.35f ? 1f - progress / 0.35f : 0f;
            float wNext = progress > 0.6f ? (progress - 0.6f) / 0.4f : 0f;
            float wHere = Math.Max(0f, 1f - wPrev - wNext);
            float f1 = phone.F1 * wHere + prevPhone.F1 * wPrev + nextPhoneF.F1 * wNext;
            float f2 = phone.F2 * wHere + prevPhone.F2 * wPrev + nextPhoneF.F2 * wNext;
            float f3 = phone.F3 * wHere + prevPhone.F3 * wPrev + nextPhoneF.F3 * wNext;
            float f4 = F4Of(phone) * wHere + F4Of(prevPhone) * wPrev + F4Of(nextPhoneF) * wNext;

            float closureEnd = phone.Start + phone.Duration * Math.Clamp(phone.Closure, 0f, 0.9f);
            bool inClosure = phone.Closure > 0f && time < closureEnd;
            float phoneEnd = phone.Start + phone.Duration;
            bool afterEnd = time >= phoneEnd; // word gaps and punctuation must be silent
            float amp = (inClosure || afterEnd) ? 0f : phone.Amp;
            float sinceStart = time - phone.Start;
            if (sinceStart < 0.008f) amp *= Math.Max(0f, sinceStart / 0.008f);
            if (phoneEnd - time < 0.012f) amp *= Math.Max(0f, (phoneEnd - time) / 0.012f);
            // The release ramp already reaches zero, so gate the tail hard instead of
            // letting the envelope smoothing bleed a word into the next gap.
            if (afterEnd) smoothedAmp = 0f;
            else smoothedAmp += (amp - smoothedAmp) * 0.35f;

            // Glide the pitch towards the next phoneme instead of stepping it, and widen
            // the resonators for unvoiced sounds so fricatives are noisy, not whistled.
            var nextPhone = plan.Phones[Math.Min(phoneIndex + 1, plan.Phones.Count - 1)];
            float glide = phone.Duration > 0.001f
                ? Math.Clamp((time - phone.Start) / phone.Duration, 0f, 1f)
                : 1f;
            float f0 = Math.Clamp(phone.PitchHz + (nextPhone.PitchHz - phone.PitchHz) * glide, 60f, 500f);
            float noiseAmount = inClosure ? 0f : Math.Clamp(phone.Noise, 0f, 1f);
            bool voiced = phone.Voiced && !inClosure;

            float bandwidth = (voiced ? 1f : 3.2f) / sharpness;
            r1.Set(f1, 80f * bandwidth);
            r2.Set(f2, 110f * bandwidth);
            r3.Set(f3, 140f * bandwidth);
            r4.Set(f4, 170f * bandwidth);

            float w1 = voiced ? 1.0f : 0.45f;
            float w2 = voiced ? 0.75f : 1.0f;
            float w3 = voiced ? 0.45f : 0.7f;
            float w4 = voiced ? 0.2f : 0.28f;
            float voicedGain = voiced ? 1f : 0f;
            float noiseGain = noiseAmount * consonantBoost;

            // A decaying glottal pulse has the same spectral tilt as a real larynx
            // (about -12 dB/octave), which is what makes the formants stand out.
            for (int i = 0; i < block && start + i < length; i++)
            {
                phase += f0 / SampleRate;
                if (phase >= 1f)
                {
                    phase -= (float)Math.Floor(phase);
                    glottal = 1f;
                }
                float pulse = glottal * glottal;
                glottal *= decay;
                float white = (float)(random.NextDouble() * 2.0 - 1.0);
                float source = pulse * voicedGain + white * (noiseGain * 0.9f + (voiced ? 0.02f : 0f));
                float sample = r1.Process(source) * w1 + r2.Process(source) * w2 +
                               r3.Process(source) * w3 + r4.Process(source) * w4;
                buffer[start + i] = sample * smoothedAmp;
            }
        }

        // Small fades and normalisation keep the preview comfortable and click free.
        Fade(buffer, 0.006f);
        Normalize(buffer, 0.85f);
        return buffer;
    }

    /// <summary>
    /// Renders the score using the same oscillator sound blocks that will be written into
    /// the level, so the preview matches what GRAB will actually play.
    /// </summary>
    public static float[] RenderSoundBlocks(SpeechPlan plan, SpeechSettings settings)
    {
        int length = Math.Max(1, (int)(Math.Min(settings.MaxSeconds + 0.2f, plan.Duration + 0.2f) * SampleRate));
        var buffer = new float[length];
        if (plan.Phones.Count == 0) return buffer;

        int clarity = Math.Clamp(settings.Clarity, 1, 4);
        float volume = Math.Clamp(settings.Volume, 0.05f, 1.5f);
        float consonantBoost = Math.Clamp(settings.ConsonantBoost, 0.3f, 3f);
        float[] weights = { 1.0f, 0.75f, 0.45f, 0.22f };

        foreach (var phone in plan.Phones)
        {
            float duration = Math.Clamp(phone.Duration, 0.03f, 2.5f);
            int offset = (int)(phone.Start * SampleRate);
            if (offset >= buffer.Length) break;

            if (phone.Voiced)
            {
                float[] freqs =
                {
                    phone.F1, phone.F2, phone.F3,
                    phone.F4 > 0 ? phone.F4 : Math.Min(5200f, phone.F3 * 1.3f)
                };
                for (int k = 0; k < clarity; k++)
                {
                    float amp = Math.Clamp(phone.Amp * weights[k] * volume * 0.6f, 0.02f, 1.5f);
                    var tone = SoundSynth.Render(freqs[k], 2, 0.01f, Math.Max(0.02f, duration - 0.03f), 0.02f,
                        amp, duration, 17 + k);
                    Mix(buffer, tone, offset);
                }
            }
            if (phone.Noise > 0.05f)
            {
                float closure = Math.Clamp(phone.Closure, 0f, 0.8f);
                float noiseStart = phone.Start + duration * closure;
                float noiseLength = Math.Max(0.02f, duration * (1f - closure));
                float amp = Math.Clamp(phone.Amp * phone.Noise * volume * 0.45f * consonantBoost, 0.02f, 1.5f);
                var burst = SoundSynth.Render(phone.F2, 3, 0.004f, Math.Max(0.015f, noiseLength - 0.01f), 0.008f,
                    amp, noiseLength, 31);
                Mix(buffer, burst, (int)(noiseStart * SampleRate));
            }
        }

        Normalize(buffer, 0.9f);
        return buffer;
    }

    private static float F4Of(SpeechPhone phone) =>
        phone.F4 > 0 ? phone.F4 : Math.Min(7500f, phone.F3 * 1.25f);

    private static void Mix(float[] buffer, float[] samples, int offset)
    {
        if (offset < 0) return;
        for (int i = 0; i < samples.Length && offset + i < buffer.Length; i++)
            buffer[offset + i] += samples[i];
    }

    private static void Normalize(float[] buffer, float peakTarget)
    {
        float peak = 0f;
        foreach (var s in buffer) peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 1e-6f) return;
        // Formant resonators are quiet by nature, so normalise upwards as well as down.
        float gain = Math.Clamp(peakTarget / peak, 0.05f, 24f);
        for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
    }

    private static void Fade(float[] buffer, float seconds)
    {
        int n = Math.Min(buffer.Length / 2, (int)(seconds * SampleRate));
        for (int i = 0; i < n; i++)
        {
            float g = i / (float)n;
            buffer[i] *= g;
            buffer[buffer.Length - 1 - i] *= g;
        }
    }

    private sealed class Resonator
    {
        private float _y1, _y2, _b1, _b2, _a0 = 1f;

        public void Set(float frequency, float bandwidth)
        {
            double f = Math.Clamp(frequency, 80.0, SampleRate * 0.45);
            double r = Math.Exp(-Math.PI * bandwidth / SampleRate);
            double theta = 2 * Math.PI * f / SampleRate;
            _b1 = (float)(2 * r * Math.Cos(theta));
            _b2 = (float)(-r * r);
            _a0 = (float)((1 - r) * Math.Sqrt(Math.Max(0.01, 1 - 2 * r * Math.Cos(2 * theta) + r * r)));
        }

        public float Process(float x)
        {
            float y = _a0 * x + _b1 * _y1 + _b2 * _y2;
            if (!float.IsFinite(y)) y = 0f;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }

    // ----------------------------------------------------------- text → sound

    private static float PauseFor(char terminator) => terminator switch
    {
        '.' or '!' or '?' => 0.3f,
        ',' => 0.15f,
        ';' or ':' => 0.2f,
        _ => 0.1f
    };

    private sealed record Sentence(List<string> Words, char Terminator);

    private static List<Sentence> SplitSentences(string text)
    {
        var result = new List<Sentence>();
        var words = new List<string>();
        var current = new StringBuilder();
        var terminators = new HashSet<char> { '.', '!', '?', ';', ':' };

        foreach (char c in text)
        {
            if (char.IsLetter(c) || c == '\'')
            {
                current.Append(c);
                continue;
            }
            if (c == '-')
            {
                Flush();
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }
            if (terminators.Contains(c))
            {
                Flush();
                result.Add(new Sentence(words.ToList(), c));
                words.Clear();
                continue;
            }
            if (c == ',')
            {
                Flush();
                if (words.Count > 0) result.Add(new Sentence(words.ToList(), ','));
                words.Clear();
            }
        }
        Flush();
        if (words.Count > 0) result.Add(new Sentence(words.ToList(), '.'));
        return result;

        void Flush()
        {
            if (current.Length == 0) return;
            // Keep the original casing: it is how all-caps initialisms (API, CPU) are detected.
            words.Add(current.ToString());
            current.Clear();
        }
    }

    private static string Normalize(string text)
    {
        string s = text.Replace("\r", " ").Replace("\n", ". ").Replace("→", " to ").Replace("&", " and ")
            .Replace("%", " percent ").Replace("+", " plus ").Replace("=", " equals ")
            .Replace("#", " number ").Replace("@", " at ").Replace("$", " dollars ")
            .Replace("°", " degrees ").Replace("…", "...");
        // U.S.A. / A.P.I. → USA / API so the initialism is spelled instead of split into sentences.
        s = Regex.Replace(s, @"\b(?:[A-Za-z]\.){2,}", m => m.Value.Replace(".", ""));
        foreach (var (from, to) in Abbreviations)
            s = Regex.Replace(s, $@"\b{Regex.Escape(from)}\.", to, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\d[\d,]*", m => NumberToWords(m.Value.Replace(",", "")));
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    private static readonly (string From, string To)[] Abbreviations =
    {
        ("mr", "mister"), ("mrs", "missus"), ("ms", "miss"), ("dr", "doctor"), ("prof", "professor"),
        ("st", "saint"), ("vs", "versus"), ("etc", "et cetera"), ("e.g", "for example"), ("i.e", "that is"),
        ("approx", "approximately"), ("dept", "department"), ("gov", "government"), ("jr", "junior")
    };

    private static string NumberToWords(string digits)
    {
        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return digits;
        if (value == 0) return " zero ";
        if (value < 0 || value > 999_999_999) return " " + digits + " ";
        var parts = new List<string>();
        if (value >= 1_000_000)
        {
            parts.Add(NumberBelowThousand(value / 1_000_000) + " million");
            value %= 1_000_000;
        }
        if (value >= 1000)
        {
            parts.Add(NumberBelowThousand(value / 1000) + " thousand");
            value %= 1000;
        }
        if (value > 0) parts.Add(NumberBelowThousand(value));
        return " " + string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) + " ";
    }

    private static string NumberBelowThousand(long value)
    {
        string[] ones = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        string[] tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        var parts = new List<string>();
        if (value >= 100)
        {
            parts.Add(ones[value / 100] + " hundred");
            value %= 100;
        }
        if (value >= 20) parts.Add(tens[value / 10] + (value % 10 > 0 ? " " + ones[value % 10] : ""));
        else if (value > 0) parts.Add(ones[value]);
        return string.Join(" ", parts);
    }

    // ------------------------------------------------------ English letter rules

    // Initialisms that are spelled letter by letter, even when typed in lower case.
    private static readonly HashSet<string> Initialisms = new(StringComparer.OrdinalIgnoreCase)
    {
        "API", "CPU", "GPU", "RAM", "ROM", "USB", "SSD", "HDD", "HTML", "CSS", "XML",
        "SQL", "HTTP", "HTTPS", "URL", "URI", "SDK", "GUI", "CLI", "AI", "ML", "OS", "PC",
        "FPS", "RPG", "NPC", "PVP", "MMO", "DLC", "HUD", "VFX", "SFX", "LOD", "UV", "FOV",
        "RGB", "MP3", "MP4", "WAV", "OGG", "TTS", "STT", "ADB", "VR", "AR", "XR", "IO",
        "BIOS", "ASCII", "UTF", "AABB", "SDF", "GASM", "ID", "UI", "UX", "IP", "DNS", "FOV"
    };

    // Upper-case words that are pronounced as words rather than spelled.
    private static readonly HashSet<string> SayAsWord = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRAB", "NASA", "LASER", "RADAR", "SCUBA", "NATO", "SIM", "GIF", "JPEG", "MIDI",
        "JSON", "LOL", "BRB", "ASAP", "AWOL", "PIN", "CAPTCHA"
    };

    /// <summary>How each letter of the alphabet is spoken.</summary>
    private static readonly Dictionary<char, string[]> LetterNames = new()
    {
        ['a'] = new[] { "EY" }, ['b'] = new[] { "B", "IY" }, ['c'] = new[] { "S", "IY" },
        ['d'] = new[] { "D", "IY" }, ['e'] = new[] { "IY" }, ['f'] = new[] { "EH", "F" },
        ['g'] = new[] { "JH", "IY" }, ['h'] = new[] { "EY", "CH" }, ['i'] = new[] { "AY" },
        ['j'] = new[] { "JH", "EY" }, ['k'] = new[] { "K", "EY" }, ['l'] = new[] { "EH", "L" },
        ['m'] = new[] { "EH", "M" }, ['n'] = new[] { "EH", "N" }, ['o'] = new[] { "OW" },
        ['p'] = new[] { "P", "IY" }, ['q'] = new[] { "K", "Y", "UW" }, ['r'] = new[] { "AA", "R" },
        ['s'] = new[] { "EH", "S" }, ['t'] = new[] { "T", "IY" }, ['u'] = new[] { "Y", "UW" },
        ['v'] = new[] { "V", "IY" }, ['w'] = new[] { "D", "AH", "B", "AH", "L", "Y", "UW" },
        ['x'] = new[] { "EH", "K", "S" }, ['y'] = new[] { "W", "AY" }, ['z'] = new[] { "Z", "IY" }
    };

    private static (string Word, List<string> Phones, int Stress) WordPhones(string word, bool spellAcronyms)
    {
        string letters = new string(word.Where(char.IsLetter).ToArray());
        // APIs / API's: spell the initialism and keep the plural sound.
        bool pluralSuffix = letters.Length > 2 && (letters[^1] == 's' || letters[^1] == 'S') &&
                            letters[..^1].Length >= 2 && letters[..^1].All(char.IsUpper);
        if (pluralSuffix) letters = letters[..^1];
        bool allCaps = letters.Length >= 2 && letters.Length <= 6 && letters.All(char.IsUpper);
        bool knownInitialism = Initialisms.Contains(letters) && letters.Length >= 2;
        bool knownWord = Dictionary.ContainsKey(word.ToLowerInvariant().Replace("'", ""));
        if (spellAcronyms && (knownInitialism || (allCaps && !knownWord && !SayAsWord.Contains(letters))))
        {
            var spelledPhones = SpellLetters(letters);
            if (pluralSuffix) spelledPhones.Add("Z");
            var spelled = Expand(spelledPhones);
            return (word, spelled, StressIndex(spelled));
        }

        string key = word.ToLowerInvariant().Replace("'", "").Replace("’", "");
        List<string> phones;
        if (Dictionary.TryGetValue(key, out var dict)) phones = dict.ToList();
        else phones = Rules(key);
        var cleaned = Expand(phones);
        return (word, cleaned, StressIndex(cleaned));
    }

    private static List<string> SpellLetters(string letters)
    {
        var list = new List<string>();
        foreach (char c in letters)
            if (LetterNames.TryGetValue(char.ToLowerInvariant(c), out var names)) list.AddRange(names);
        return list;
    }

    private static List<string> Expand(List<string> phones)
    {
        // Split diphthongs into two target vowels so the level gets a real glide.
        var expanded = new List<string>();
        foreach (var p in phones)
        {
            switch (p)
            {
                case "EY": expanded.Add("EH"); expanded.Add("IY"); break;
                case "AY": expanded.Add("AA"); expanded.Add("IY"); break;
                case "OY": expanded.Add("AO"); expanded.Add("IY"); break;
                case "AW": expanded.Add("AA"); expanded.Add("UW"); break;
                case "OW": expanded.Add("AO"); expanded.Add("UW"); break;
                default: expanded.Add(p); break;
            }
        }
        return expanded.Where(Defs.ContainsKey).ToList();
    }

    private static int StressIndex(List<string> phones)
    {
        var vowelIndices = new List<int>();
        for (int i = 0; i < phones.Count; i++) if (IsVowel(phones[i])) vowelIndices.Add(i);
        if (vowelIndices.Count <= 1) return vowelIndices.Count == 0 ? -1 : vowelIndices[0];
        if (vowelIndices.Count == 2) return vowelIndices[0];
        return vowelIndices[^2];
    }

    private static List<string> Rules(string w)
    {
        var outp = new List<string>();
        int i = 0, n = w.Length;
        bool syllables = w.Count(c => "aeiouy".IndexOf(c) >= 0) > 1;

        while (i < n)
        {
            char c = w[i];
            string rest = w[i..];

            if (rest == "e" && n > 2) { i++; continue; }
            if (rest == "ed" && i > 0)
            {
                if (outp.Count > 0 && (outp[^1] == "T" || outp[^1] == "D")) { outp.Add("IH"); outp.Add("D"); }
                else outp.Add(EndsUnvoiced(outp) ? "T" : "D");
                i += 2;
                continue;
            }
            if (rest == "es" && i > 0)
            {
                bool sibilant = outp.Count > 0 && outp[^1] is "S" or "Z" or "SH" or "ZH" or "CH" or "JH";
                if (sibilant) { outp.Add("IH"); outp.Add("Z"); }
                else outp.Add(EndsUnvoiced(outp) ? "S" : "Z");
                i += 2;
                continue;
            }
            if (rest == "s" && i > 0)
            {
                outp.Add(EndsUnvoiced(outp) ? "S" : "Z");
                i++;
                continue;
            }

            if (rest.StartsWith("tion")) { Add(outp, "SH", "AX", "N"); i += 4; continue; }
            if (rest.StartsWith("sion")) { Add(outp, "ZH", "AX", "N"); i += 4; continue; }
            if (rest.StartsWith("cious") || rest.StartsWith("tious")) { Add(outp, "SH", "AX", "S"); i += 5; continue; }
            if (rest.StartsWith("cial") || rest.StartsWith("tial")) { Add(outp, "SH", "AX", "L"); i += 4; continue; }
            if (rest.StartsWith("ture")) { Add(outp, "CH", "ER"); i += 4; continue; }
            if (rest.StartsWith("ough")) { Add(outp, "AO"); i += 4; continue; }
            if (rest.StartsWith("augh")) { Add(outp, "AO"); i += 4; continue; }
            if (rest.StartsWith("eigh")) { Add(outp, "EY"); i += 4; continue; }
            if (rest.StartsWith("igh")) { Add(outp, "AY"); i += 3; continue; }
            if (rest.StartsWith("tch")) { Add(outp, "CH"); i += 3; continue; }
            if (rest.StartsWith("dge")) { Add(outp, "JH"); i += 3; continue; }
            if (rest.StartsWith("ch")) { Add(outp, "CH"); i += 2; continue; }
            if (rest.StartsWith("sh")) { Add(outp, "SH"); i += 2; continue; }
            if (rest.StartsWith("th")) { Add(outp, "TH"); i += 2; continue; }
            if (rest.StartsWith("ph")) { Add(outp, "F"); i += 2; continue; }
            if (rest.StartsWith("wh")) { Add(outp, "W"); i += 2; continue; }
            if (rest.StartsWith("ck")) { Add(outp, "K"); i += 2; continue; }
            if (rest.StartsWith("ng")) { Add(outp, "NG"); i += 2; continue; }
            if (rest.StartsWith("qu")) { Add(outp, "K", "W"); i += 2; continue; }
            if (rest.StartsWith("kn") && i == 0) { Add(outp, "N"); i += 2; continue; }
            if (rest.StartsWith("wr") && i == 0) { Add(outp, "R"); i += 2; continue; }
            if (rest.StartsWith("gn") && i == 0) { Add(outp, "N"); i += 2; continue; }
            if (rest.StartsWith("ps") && i == 0) { Add(outp, "S"); i += 2; continue; }
            if (rest.StartsWith("mb") && i + 2 == n) { Add(outp, "M"); i += 2; continue; }
            if (rest.StartsWith("gh")) { i += 2; continue; }

            if (rest.StartsWith("ee")) { Add(outp, "IY"); i += 2; continue; }
            if (rest.StartsWith("ea")) { Add(outp, "IY"); i += 2; continue; }
            if (rest.StartsWith("oo")) { Add(outp, "UW"); i += 2; continue; }
            if (rest.StartsWith("ou")) { Add(outp, "AW"); i += 2; continue; }
            if (rest.StartsWith("ow")) { Add(outp, i + 2 >= n ? "OW" : "AW"); i += 2; continue; }
            if (rest.StartsWith("oi") || rest.StartsWith("oy")) { Add(outp, "OY"); i += 2; continue; }
            if (rest.StartsWith("ai") || rest.StartsWith("ay")) { Add(outp, "EY"); i += 2; continue; }
            if (rest.StartsWith("au") || rest.StartsWith("aw")) { Add(outp, "AO"); i += 2; continue; }
            if (rest.StartsWith("ei")) { Add(outp, "EY"); i += 2; continue; }
            if (rest.StartsWith("ie")) { Add(outp, "IY"); i += 2; continue; }
            if (rest.StartsWith("oa")) { Add(outp, "OW"); i += 2; continue; }
            if (rest.StartsWith("ue") || rest.StartsWith("ui") || rest.StartsWith("ew") || rest.StartsWith("eu"))
            {
                Add(outp, "UW"); i += 2; continue;
            }
            if (rest.StartsWith("er") || rest.StartsWith("ir") || rest.StartsWith("ur")) { Add(outp, "ER"); i += 2; continue; }
            if (rest.StartsWith("ar")) { Add(outp, "AA", "R"); i += 2; continue; }
            if (rest.StartsWith("or")) { Add(outp, "AO", "R"); i += 2; continue; }

            if ("aeiou".IndexOf(c) >= 0 && i + 2 < n && w[i + 2] == 'e' && i + 3 == n &&
                "bcdfgklmnprstvz".IndexOf(w[i + 1]) >= 0)
            {
                Add(outp, c switch { 'a' => "EY", 'e' => "IY", 'i' => "AY", 'o' => "OW", _ => "UW" });
                i++;
                continue;
            }

            if ("aeiou".IndexOf(c) >= 0)
            {
                Add(outp, c switch { 'a' => "AE", 'e' => "EH", 'i' => "IH", 'o' => "AA", _ => "AH" });
                i++;
                continue;
            }

            if (c == 'y')
            {
                bool final = i + 1 == n || (i + 2 == n && w[i + 1] == 's');
                Add(outp, final ? (syllables ? "IY" : "AY") : "IH");
                i++;
                continue;
            }

            if (i + 1 < n && w[i + 1] == c && "bcdfgklmnprstvz".IndexOf(c) >= 0)
            {
                EmitConsonant(outp, c);
                i += 2;
                continue;
            }

            if ("bcdfghjklmnpqrstvwxz".IndexOf(c) >= 0)
            {
                if ((c == 'c' || c == 'g') && i + 1 < n && "eiy".IndexOf(w[i + 1]) >= 0 &&
                    !(c == 'g' && i == 0 && (w.StartsWith("get") || w.StartsWith("give") || w.StartsWith("girl") || w.StartsWith("gift"))))
                {
                    Add(outp, c == 'c' ? "S" : "JH");
                }
                else EmitConsonant(outp, c);
                i++;
                continue;
            }

            i++; // unknown character
        }

        // Never leave a word with no vowel-like nucleus; stick a schwa in.
        if (!outp.Any(IsVowel)) outp.Add("AX");
        return outp;

        static void EmitConsonant(List<string> list, char ch)
        {
            switch (ch)
            {
                case 'b': list.Add("B"); break;
                case 'c': list.Add("K"); break;
                case 'd': list.Add("D"); break;
                case 'f': list.Add("F"); break;
                case 'g': list.Add("G"); break;
                case 'h': list.Add("HH"); break;
                case 'j': list.Add("JH"); break;
                case 'k': list.Add("K"); break;
                case 'l': list.Add("L"); break;
                case 'm': list.Add("M"); break;
                case 'n': list.Add("N"); break;
                case 'p': list.Add("P"); break;
                case 'q': list.Add("K"); break;
                case 'r': list.Add("R"); break;
                case 's': list.Add("S"); break;
                case 't': list.Add("T"); break;
                case 'v': list.Add("V"); break;
                case 'w': list.Add("W"); break;
                case 'x': list.Add("K"); list.Add("S"); break;
                case 'z': list.Add("Z"); break;
            }
        }
    }

    private static bool EndsUnvoiced(List<string> phones) =>
        phones.Count > 0 && phones[^1] is "P" or "T" or "K" or "F" or "TH";

    private static void Add(List<string> list, params string[] items)
    {
        foreach (var item in items) list.Add(item);
    }

    // A compact pronunciation dictionary for the words rules alone get wrong.
    private static readonly Dictionary<string, string[]> Dictionary = BuildDictionary(@"
a=AX
an=AE N
the=DH AX
this=DH IH S
that=DH AE T
these=DH IY Z
those=DH OW Z
is=IH Z
are=AA R
was=W AH Z
were=W ER
be=B IY
been=B IH N
am=AE M
do=D UW
does=D AH Z
did=D IH D
have=HH AE V
has=HH AE Z
had=HH AE D
can=K AE N
could=K UH D
will=W IH L
would=W UH D
should=SH UH D
may=M EY
might=M AY T
must=M AH S T
not=N AA T
no=N OW
yes=Y EH S
i=AY
you=Y UW
he=HH IY
she=SH IY
it=IH T
we=W IY
they=DH EY
me=M IY
him=HH IH M
her=HH ER
us=AH S
them=DH EH M
my=M AY
your=Y AO R
his=HH IH Z
its=IH T S
our=AW ER
their=DH EH R
and=AE N D
or=AO R
but=B AH T
so=S OW
if=IH F
then=DH EH N
than=DH AE N
because=B IH K AO Z
as=AE Z
at=AE T
by=B AY
for=F AO R
from=F R AH M
in=IH N
into=IH N T UW
of=AH V
on=AA N
to=T UW
with=W IH DH
without=W IH DH AW T
about=AX B AW T
over=OW V ER
under=AH N D ER
up=AH P
down=D AW N
out=AW T
off=AO F
again=AX G EH N
just=JH AH S T
very=V EH R IY
really=R IY L IY
hello=HH AH L OW
hi=HH AY
hey=HH EY
good=G UH D
great=G R EY T
bad=B AE D
please=P L IY Z
thanks=TH AE NG K S
thank=TH AE NG K
sorry=S AA R IY
okay=OW K EY
ok=OW K EY
one=W AH N
two=T UW
three=TH R IY
four=F AO R
five=F AY V
six=S IH K S
seven=S EH V AX N
eight=EY T
nine=N AY N
ten=T EH N
zero=Z IH R OW
hundred=HH AH N D R AX D
thousand=TH AW Z AX N
million=M IH L Y AX N
time=T AY M
world=W ER L D
engine=EH N JH AX N
sound=S AW N D
block=B L AA K
blocks=B L AA K S
speech=S P IY CH
text=T EH K S T
voice=V OY S
grab=G R AE B
level=L EH V AX L
editor=EH D IH T ER
work=W ER K
works=W ER K S
make=M EY K
makes=M EY K S
made=M EY D
new=N UW
now=N AW
here=HH IY R
there=DH EH R
where=W EH R
what=W AH T
when=W EH N
why=W AY
how=HH AW
who=HH UW
which=W IH CH
youre=Y UW R
dont=D OW N T
doesnt=D AH Z AX N T
cant=K AE N T
wont=W OW N T
isnt=IH Z AX N T
im=AY M
ive=AY V
thats=DH AE T S
lets=L EH T S
there's=DH EH R Z
havent=HH AE V AX N T
computer=K AX M P Y UW T ER
machine=M AX SH IY N
robot=R OW B AA T
music=M Y UW Z IH K
audio=AO D IY OW
language=L AE NG G W IH JH
english=IH NG G L IH SH
sentence=S EH N T AX N S
word=W ER D
words=W ER D S
read=R IY D
write=R AY T
lazy=L EY Z IY
floor=F L AO R
door=D AO R
lava=L AA V AX
warning=W AO R N IH NG
today=T AX D EY
jungle=JH AH NG G AX L
done=D AH N
open=OW P AX N
cow=K AW
secret=S IY K R AX T
morning=M AO R N IH NG
welcome=W EH L K AX M
lady=L EY D IY
science=S AY AX N S
call=K AO L
all=AO L
small=S M AO L
wall=W AO L
fall=F AO L
talk=T AO K
walk=W AO K
ready=R EH D IY
does=D AH Z


talk=T AO K
say=S EY
says=S EH Z
said=S EH D
one's=W AH N Z
people=P IY P AX L
something=S AH M TH IH NG
everything=EH V R IY TH IH NG
nothing=N AH TH IH NG
anyone=EH N IY W AH N
everyone=EH V R IY W AH N
");

    private static Dictionary<string, string[]> BuildDictionary(string raw)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim().Replace("'", "");
            var phones = trimmed[(eq + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            dict[key] = phones;
        }
        return dict;
    }
}
