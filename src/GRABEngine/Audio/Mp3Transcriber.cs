using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GrabEngine.Audio;

public sealed class Mp3ImportSettings
{
    /// <summary>0-100. Higher detail analyses shorter slices and finds more notes.</summary>
    public int Detail { get; set; } = 55;
    /// <summary>Upper bound for generated sound blocks. GRAB levels get heavy quickly.</summary>
    public int MaxNotes { get; set; } = 160;
    public int Polyphony { get; set; } = 2;
    public bool SnapSemitones { get; set; } = true;
    public float MinFrequency { get; set; } = 65f;
    public float MaxFrequency { get; set; } = 1800f;
    public float Sensitivity { get; set; } = 0.10f;
    public float NoiseGate { get; set; } = 0.006f;
    /// <summary>Auto, Square, Sawtooth, Sine or Noise.</summary>
    public string WaveMode { get; set; } = "Auto";
    public float NoteLengthScale { get; set; } = 1.0f;
    public float Volume { get; set; } = 0.7f;
    /// <summary>Use timeline Code Blocks + triggers so the tune actually plays in order.</summary>
    public bool Scheduled { get; set; } = true;
    /// <summary>
    /// Keep the timed note schedule, but add one Music Start trigger that gates it. The
    /// score starts when that trigger is activated and preserves all note timing; it never
    /// fires every sound at once.
    /// </summary>
    public bool SingleTrigger { get; set; }
    public bool StartActive { get; set; } = true;
    public float MaxSeconds { get; set; } = 30f;
}

public sealed class SongNote
{
    public float Start { get; set; }
    public float Duration { get; set; }
    public float Frequency { get; set; }
    public int Midi { get; set; }
    public float Amplitude { get; set; }
    public int Wave { get; set; }

    public string PitchName => Midi < 0 ? "noise" : MidiName(Midi);

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static string MidiName(int midi) => Names[((midi % 12) + 12) % 12] + (midi / 12 - 1);
}

public sealed class SongPlan
{
    public string FileName { get; set; } = "";
    public float Duration { get; set; }
    public float AnalyzedSeconds { get; set; }
    public int Frames { get; set; }
    public List<SongNote> Notes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string Summary =>
        $"{Notes.Count} note event(s) from {AnalyzedSeconds:0.#}s · {Duration:0.#}s source" +
        (Warnings.Count > 0 ? $" · {Warnings[0]}" : "");
}

/// <summary>
/// Turns an MP3/WAV into a score of GRAB sound blocks. Retail levels cannot embed audio
/// data, so the closest possible recreation is an additive oscillator score: each slice
/// of the track is reduced to its strongest pitches, then one sound block per pitch is
/// scheduled by generated triggers and Code Blocks (or all left Start Active for a pad).
/// </summary>
public static class Mp3Transcriber
{
    private const int AnalysisRate = 22050;

    public static SongPlan Analyze(string path, Mp3ImportSettings settings, Action<string>? progress = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);
        var plan = new SongPlan { FileName = Path.GetFileName(path) };

        float[] mono;
        int sourceRate;
        using (var reader = new AudioFileReader(path))
        {
            sourceRate = reader.WaveFormat.SampleRate;
            ISampleProvider monoProvider = reader.WaveFormat.Channels >= 2
                ? new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f }
                : reader;
            var resampled = sourceRate == AnalysisRate ? monoProvider : new WdlResamplingSampleProvider(monoProvider, AnalysisRate);
            mono = ReadAll(resampled, (int)(Math.Max(1, settings.MaxSeconds) * AnalysisRate));
            plan.Duration = (float)reader.TotalTime.TotalSeconds;
        }
        if (mono.Length < 1024) throw new InvalidDataException("That file is too short to analyse.");

        float analyzed = mono.Length / (float)AnalysisRate;
        plan.AnalyzedSeconds = analyzed;
        int notesPerSecond = 2 + (int)Math.Round(Math.Clamp(settings.Detail, 0, 100) / 100.0 * 14);
        int fftSize = settings.MinFrequency < 100 ? 4096 : 2048;
        int hop = Math.Max(256, AnalysisRate / Math.Max(1, notesPerSecond));
        int usableLow = Math.Max(2, (int)(settings.MinFrequency * fftSize / AnalysisRate));
        int usableHigh = Math.Min(fftSize / 2 - 2, (int)(settings.MaxFrequency * fftSize / AnalysisRate));
        if (usableHigh <= usableLow) throw new InvalidDataException("Frequency range is empty.");
        var hann = new float[fftSize];
        for (int i = 0; i < fftSize; i++) hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1)));

        var notes = new List<SongNote>();
        var lastByPitch = new Dictionary<int, SongNote>();
        int frames = 0;
        progress?.Invoke("Analysing " + Path.GetFileName(path) + "...");
        for (int center = fftSize / 2; center + fftSize / 2 < mono.Length; center += hop)
        {
            frames++;
            var re = new float[fftSize];
            var im = new float[fftSize];
            double energy = 0;
            for (int i = 0; i < fftSize; i++)
            {
                float s = mono[center - fftSize / 2 + i] * hann[i];
                re[i] = s;
                energy += s * s;
            }
            float rms = (float)Math.Sqrt(energy / fftSize);
            if (rms < settings.NoiseGate) continue;
            Fft(re, im);
            int count = fftSize / 2;
            var mag = new float[count];
            float max = 0;
            for (int i = usableLow; i <= usableHigh; i++)
            {
                mag[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
                if (mag[i] > max) max = mag[i];
            }
            if (max <= 1e-5f) continue;

            var candidates = new List<(float Freq, float Amp)>();
            float threshold = max * Math.Clamp(settings.Sensitivity, 0.01f, 0.9f);
            for (int i = usableLow + 2; i <= usableHigh - 2; i++)
            {
                if (mag[i] < threshold) continue;
                if (mag[i] < mag[i - 1] || mag[i] < mag[i + 1]) continue;
                if (mag[i] < mag[i - 2] || mag[i] < mag[i + 2]) continue;
                float denom = mag[i - 1] - 2 * mag[i] + mag[i + 1];
                float delta = Math.Abs(denom) > 1e-9f ? 0.5f * (mag[i - 1] - mag[i + 1]) / denom : 0;
                delta = Math.Clamp(delta, -0.5f, 0.5f);
                float freq = (i + delta) * AnalysisRate / (float)fftSize;
                candidates.Add((freq, mag[i]));
            }

            // Drop harmonics that are just overtones of a louder lower peak.
            var kept = new List<(float Freq, float Amp)>();
            foreach (var c in candidates.OrderByDescending(c => c.Amp))
            {
                bool overtone = false;
                foreach (var k in kept)
                {
                    if (k.Amp < c.Amp * 0.45f) continue;
                    for (int h = 2; h <= 6; h++)
                    {
                        if (Math.Abs(c.Freq - k.Freq * h) < k.Freq * 0.035f) { overtone = true; break; }
                    }
                    if (overtone) break;
                }
                if (!overtone) kept.Add(c);
                if (kept.Count >= Math.Clamp(settings.Polyphony, 1, 4) * 2) break;
            }

            float flatness = SpectralFlatness(mag, usableLow, usableHigh);
            int wave = ResolveWave(settings.WaveMode, flatness, max / Math.Max(1e-6f, rms * 18f));
            var chosen = kept.Take(Math.Clamp(settings.Polyphony, 1, 4)).ToList();
            if (chosen.Count == 0 && flatness > 0.34f)
            {
                chosen.Add((settings.MinFrequency * 2.2f, max * 0.35f));
                wave = 3;
            }

            float time = Math.Max(0, (center - fftSize / 2) / (float)AnalysisRate);
            float duration = Math.Clamp(hop / (float)AnalysisRate * Math.Max(0.6f, settings.NoteLengthScale) + 0.05f,
                0.06f, 2.5f);
            foreach (var c in chosen)
            {
                float freq = c.Freq;
                int midi = -1;
                if (settings.SnapSemitones && freq > 30)
                {
                    midi = (int)Math.Round(69 + 12 * Math.Log2(freq / 440.0));
                    freq = (float)(440.0 * Math.Pow(2, (midi - 69) / 12.0));
                }
                if (freq < settings.MinFrequency || freq > settings.MaxFrequency) continue;
                float amp = Math.Clamp(c.Amp / Math.Max(1e-6f, max), 0.05f, 1f);
                amp = MathF.Sqrt(amp);
                if (midi >= 0 && lastByPitch.TryGetValue(midi * 4 + wave, out var last) &&
                    time <= last.Start + last.Duration + hop / (float)AnalysisRate * 0.65f)
                {
                    last.Duration = Math.Max(last.Duration, time + duration - last.Start);
                    last.Amplitude = Math.Max(last.Amplitude, amp);
                    continue;
                }
                var note = new SongNote
                {
                    Start = time,
                    Duration = duration,
                    Frequency = freq,
                    Midi = midi,
                    Amplitude = amp,
                    Wave = wave
                };
                notes.Add(note);
                if (midi >= 0) lastByPitch[midi * 4 + wave] = note;
            }
            if (frames % 80 == 0) progress?.Invoke($"Analysing... {time:0.0}s / {analyzed:0.0}s");
        }

        // Newest-notes limit keeps the generated level importable and playable.
        if (notes.Count > settings.MaxNotes)
        {
            plan.Warnings.Add($"The track needed {notes.Count} notes; keeping the {settings.MaxNotes} strongest.");
            notes = notes.OrderByDescending(n => n.Amplitude).Take(settings.MaxNotes)
                .OrderBy(n => n.Start).ToList();
        }
        plan.Notes = notes;
        plan.Frames = frames;
        if (notes.Count == 0) plan.Warnings.Add("No pitched material was found. Try lowering the noise gate or sensitivity.");
        progress?.Invoke(plan.Summary);
        return plan;
    }

    private static int ResolveWave(string mode, float flatness, float peakiness)
    {
        return mode switch
        {
            "Square" => 0,
            "Sawtooth" => 1,
            "Sine" => 2,
            "Noise" => 3,
            _ => flatness > 0.36f && peakiness < 1.8f ? 3 : 2
        };
    }

    private static float SpectralFlatness(float[] mag, int low, int high)
    {
        double logSum = 0, sum = 0;
        int n = 0;
        for (int i = low; i <= high; i++)
        {
            double m = mag[i] + 1e-8;
            logSum += Math.Log(m);
            sum += m;
            n++;
        }
        if (n == 0 || sum <= 0) return 0;
        double geo = Math.Exp(logSum / n);
        return (float)Math.Clamp(geo / (sum / n), 0, 1);
    }

    private static float[] ReadAll(ISampleProvider provider, int maxSamples)
    {
        var list = new List<float>(Math.Min(maxSamples, 1 << 20));
        var buffer = new float[8192];
        int read;
        while (list.Count < maxSamples && (read = provider.Read(buffer, 0, Math.Min(buffer.Length, maxSamples - list.Count))) > 0)
            for (int i = 0; i < read; i++) list.Add(buffer[i]);
        return list.ToArray();
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            float wRe = (float)Math.Cos(angle), wIm = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j, b = i + j + len / 2;
                    float uRe = re[a], uIm = im[a];
                    float vRe = re[b] * curRe - im[b] * curIm;
                    float vIm = re[b] * curIm + im[b] * curRe;
                    re[a] = uRe + vRe; im[a] = uIm + vIm;
                    re[b] = uRe - vRe; im[b] = uIm - vIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
