using System;
using System.IO;
using GrabEngine.Core;
using NAudio.Wave;

namespace GrabEngine.Audio;

/// <summary>
/// Renders GRAB sound blocks. A retail sound block is a small monophonic synth:
/// waveform, pitch envelope, amplitude envelope, duty cycle, filters, flanger and
/// reverb. Nothing is sampled, so the editor can reproduce the block closely and
/// export a .level that the real game plays.
///
/// Parameter names and defaults match GRAB's advanced sound editor. The important
/// one is the low-pass: 25000 Hz means "open". A missing / zero cutoff is a closed
/// filter, which is why incomplete parameter blocks are silent in game.
/// </summary>
public static class SoundSynth
{
    public const int SampleRate = 44100;

    public static readonly string[] WaveNames = { "Square", "Sawtooth", "Sine", "Noise" };

    public static float TotalSeconds(LevelNode node)
        => Math.Max(0.03f, Math.Max(0, node.SoundAttack) + Math.Max(0.01f, node.SoundSustain) + Math.Max(0, node.SoundRelease));

    public static float[] RenderNode(LevelNode node, float volumeScale = 1f, float seconds = 0f)
        => RenderCore(new Voice
        {
            WaveType = Math.Clamp((int)node.SoundWaveType, 0, 3),
            Frequency = node.SoundFrequency,
            FrequencyMin = node.SoundFrequencyMin,
            FrequencySlide = node.SoundFrequencySlide,
            FrequencyDeltaSlide = node.SoundFrequencyDeltaSlide,
            VibratoStrength = node.SoundVibratoStrength,
            VibratoSpeed = node.SoundVibratoSpeed,
            PitchJumpMod = node.SoundPitchJumpMod,
            PitchJumpSpeed = node.SoundPitchJumpSpeed,
            Attack = node.SoundAttack,
            Sustain = node.SoundSustain,
            Release = node.SoundRelease,
            Punch = node.SoundPunch,
            DutyCycle = node.SoundDutyCycle,
            DutyCycleSweep = node.SoundDutyCycleSweep,
            RepeatSpeed = node.SoundRepeatSpeed,
            FlangerFrequency = node.SoundFlangerFrequency,
            FlangerDepth = node.SoundFlangerDepth,
            LowPass = node.SoundLowPass,
            HighPass = node.SoundHighPass,
            ReverbDelay = node.SoundReverbDelay,
            ReverbDecay = node.SoundReverbDecay,
            Volume = node.SoundVolume * volumeScale
        }, seconds, 1337);

    /// <summary>Renders one note. <paramref name="seconds"/> above 0 overrides the envelope length
    /// so a scene preview can show the whole tail of a long sound.</summary>
    public static float[] Render(float frequency, int waveType, float attack, float sustain,
        float release, float volume, float seconds = 0f, int seed = 1337)
        => RenderCore(new Voice
        {
            WaveType = Math.Clamp(waveType, 0, 3),
            Frequency = frequency,
            Attack = attack,
            Sustain = sustain,
            Release = release,
            Volume = volume
        }, seconds, seed);

    private struct Voice
    {
        public int WaveType;
        public float Frequency;
        public float FrequencyMin;
        public float FrequencySlide;
        public float FrequencyDeltaSlide;
        public float VibratoStrength;
        public float VibratoSpeed;
        public float PitchJumpMod;
        public float PitchJumpSpeed;
        public float Attack;
        public float Sustain;
        public float Release;
        public float Punch;
        public float DutyCycle;
        public float DutyCycleSweep;
        public float RepeatSpeed;
        public float FlangerFrequency;
        public float FlangerDepth;
        public float LowPass;
        public float HighPass;
        public float ReverbDelay;
        public float ReverbDecay;
        public float Volume;

        public Voice()
        {
            // Retail GRAB defaults for a fresh sound block.
            FrequencyMin = 35f;
            PitchJumpMod = 1f;
            DutyCycle = 0.5f;
            LowPass = 25000f;
        }
    }

    private static float[] RenderCore(Voice v, float seconds, int seed)
    {
        float a = Math.Clamp(v.Attack, 0f, 10f);
        float s = Math.Clamp(v.Sustain, 0.005f, 30f);
        float r = Math.Clamp(v.Release, 0f, 10f);
        float length = seconds > 0 ? Math.Clamp(seconds, 0.02f, 60f) : Math.Max(0.03f, a + s + r);
        int count = Math.Max(1, (int)(length * SampleRate));
        var samples = new float[count];

        double phase = 0;
        double frequency = Math.Clamp(v.Frequency, 20f, 18000f);
        double frequencyMin = Math.Clamp(v.FrequencyMin > 0 ? v.FrequencyMin : 35f, 20f, 18000f);
        // GRAB's slide values are in kHz per second, like the retail editor's
        // "Frequency Slide" field. Delta slide is kHz per second squared.
        double slide = Math.Clamp(v.FrequencySlide, -200f, 200f) * 1000.0;
        double deltaSlide = Math.Clamp(v.FrequencyDeltaSlide, -200f, 200f) * 1000.0;
        double vibrato = Math.Clamp(v.VibratoStrength, 0f, 4f);
        double vibratoSpeed = Math.Clamp(v.VibratoSpeed, 0f, 1000f);
        double pitchJump = Math.Clamp(v.PitchJumpMod, 0f, 8f);
        double pitchJumpSpeed = Math.Clamp(v.PitchJumpSpeed, 0f, 20f);
        double duty = Math.Clamp(v.DutyCycle, 0.01f, 0.99f);
        double dutySweep = Math.Clamp(v.DutyCycleSweep, -20f, 20f);
        float gain = Math.Clamp(v.Volume, 0f, 4f) * 0.55f;

        var rnd = new Random(seed);
        double noise = 0;
        double smoothed = 0;

        // One-pole state for the retail-style low/high-pass.
        double low = 0;
        double high = 0;
        double previous = 0;

        // Simple flanger delay line (up to 20 ms).
        int flangerLength = Math.Max(1, (int)(0.02 * SampleRate));
        var flanger = new float[flangerLength];
        int flangerAt = 0;
        double flangerFrequency = Math.Clamp(v.FlangerFrequency, 0.01f, 40f);
        double flangerDepth = Math.Clamp(v.FlangerDepth, 0f, 1f);

        // Feedback-comb reverb. Reverb Delay is in milliseconds in the retail editor.
        int reverbLength = v.ReverbDelay > 0 ? (int)Math.Clamp(v.ReverbDelay * SampleRate / 1000.0, 1, SampleRate) : 0;
        var reverb = reverbLength > 0 ? new float[reverbLength] : Array.Empty<float>();
        int reverbAt = 0;
        float reverbDecay = Math.Clamp(v.ReverbDecay, 0f, 0.96f);

        double lowPass = v.LowPass <= 20 ? 0 : Math.Min(v.LowPass, 26000f);
        double highPass = Math.Max(0, v.HighPass);
        double nyquist = SampleRate * 0.49;

        for (int i = 0; i < count; i++)
        {
            double t = i / (double)SampleRate;

            // Repeat Speed retriggers the amplitude envelope without restarting pitch.
            double envT = t;
            if (v.RepeatSpeed > 0.01)
                envT = t % (1.0 / Math.Clamp(v.RepeatSpeed, 0.01f, 30f));

            // Amplitude envelope: attack, sustain, release, plus GRAB's punch spike.
            double env;
            if (envT < a && a > 1e-5) env = envT / a;
            else if (envT < a + s) env = 1.0;
            else if (r > 1e-5) env = Math.Max(0, 1.0 - (envT - a - s) / r);
            else env = 0;
            if (v.Punch > 0 && a + s > 1e-5)
                env *= 1.0 + Math.Max(0, v.Punch) * Math.Max(0, 1.0 - envT / (a + s));
            // A very short de-click ramp keeps square/saw edges from snapping.
            env *= Math.Min(1, envT * 400.0);
            env *= Math.Min(1, Math.Max(0, (length - t) * 400.0));

            // Pitch envelope.
            frequency += slide / SampleRate;
            slide += deltaSlide / SampleRate;
            double f = Math.Clamp(frequency, frequencyMin, 18000.0);
            if (vibrato > 0 && vibratoSpeed > 0)
                f *= 1.0 + vibrato * Math.Sin(t * vibratoSpeed * Math.PI * 2.0);
            if (pitchJumpSpeed > 0 && pitchJump > 0)
            {
                // Retail "Pitch Jump Modifier/Speed": alternate the pitch at a steady rate.
                if (Math.Sin(t * pitchJumpSpeed * Math.PI * 2.0) < 0) f *= pitchJump;
            }
            f = Math.Clamp(f, 20.0, 18000.0);

            phase += f / SampleRate;
            if (phase >= 1) phase -= Math.Floor(phase);

            double width = Math.Clamp(duty + dutySweep * t, 0.01, 0.99);
            double value;
            switch (v.WaveType)
            {
                case 1: // sawtooth
                    value = 2.0 * phase - 1.0;
                    break;
                case 2: // sine
                    value = Math.Sin(phase * Math.PI * 2.0);
                    break;
                case 3: // noise
                    noise = rnd.NextDouble() * 2.0 - 1.0;
                    value = noise * 0.8 + smoothed * 0.2;
                    smoothed = value;
                    break;
                default: // square / pulse
                    value = phase < width ? 1.0 : -1.0;
                    break;
            }

            // Flanger is before the filters, like the retail synth's effect chain.
            if (flangerDepth > 0)
            {
                int delay = (int)((0.006 + 0.005 * Math.Sin(t * flangerFrequency * Math.PI * 2.0)) * SampleRate);
                delay = Math.Clamp(delay, 1, flangerLength - 1);
                double delayed = flanger[(flangerAt - delay + flangerLength) % flangerLength];
                flanger[flangerAt] = (float)value;
                flangerAt = (flangerAt + 1) % flangerLength;
                value += delayed * flangerDepth * 0.5;
            }
            else
            {
                flanger[flangerAt] = (float)value;
                flangerAt = (flangerAt + 1) % flangerLength;
            }

            // Low-pass: 25000 Hz (or anything at/above Nyquist) is fully open.
            if (lowPass > 20 && lowPass < nyquist)
            {
                double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * lowPass / SampleRate);
                low += alpha * (value - low);
                value = low;
            }

            // High-pass: 0 is off, matching the retail editor.
            if (highPass > 20 && highPass < nyquist)
            {
                double alpha = Math.Exp(-2.0 * Math.PI * highPass / SampleRate);
                high = alpha * (high + value - previous);
                previous = value;
                value = high;
            }

            if (reverbLength > 0)
            {
                float delayed = reverb[reverbAt];
                reverb[reverbAt] = (float)Math.Clamp(value + delayed * reverbDecay, -2, 2);
                reverbAt = (reverbAt + 1) % reverbLength;
                value += delayed * reverbDecay * 0.6;
            }

            samples[i] = (float)(value * env * gain);
        }
        SoftLimit(samples);
        return samples;
    }

    private static void SoftLimit(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            samples[i] = (float)Math.Tanh(v * 1.15) * 0.9f;
        }
    }

    public static byte[] ToWav(float[] mono, int sampleRate = SampleRate)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(sampleRate, 16, 1)))
        {
            for (int i = 0; i < mono.Length; i++)
            {
                short s = (short)(Math.Clamp(mono[i], -1f, 1f) * short.MaxValue);
                writer.WriteByte((byte)(s & 0xff));
                writer.WriteByte((byte)((s >> 8) & 0xff));
            }
        }
        return ms.ToArray();
    }

    public static string Describe(LevelNode node)
        => $"{WaveNames[Math.Clamp((int)node.SoundWaveType, 0, WaveNames.Length - 1)]} {node.SoundFrequency:0.#} Hz · {TotalSeconds(node):0.##}s";
}
