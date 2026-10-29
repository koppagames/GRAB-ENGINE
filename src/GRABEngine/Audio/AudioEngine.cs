using System;
using System.Collections.Generic;
using System.Linq;
using GrabEngine.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GrabEngine.Audio;

/// <summary>
/// Shared real-time audio output for sound blocks, Play mode and the Audio Studio.
/// Uses a single floating point mixer so any number of block sounds can overlap the
/// same way they do in GRAB, with per-sound volume and clean looping.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int OutputRate = SoundSynth.SampleRate;

    private readonly object _gate = new();
    private readonly List<ActiveSound> _active = new();
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;
    private bool _failed;

    public static AudioEngine Shared { get; } = new();

    public float MasterVolume
    {
        get => _master;
        set
        {
            _master = Math.Clamp(value, 0f, 2f);
            lock (_gate)
                foreach (var a in _active) a.Volume.Volume = a.BaseVolume * _master;
        }
    }
    private float _master = 0.9f;

    public bool Available => !_failed;

    public int ActiveCount
    {
        get { lock (_gate) { Prune(); return _active.Count; } }
    }

    private sealed class ActiveSound
    {
        public object Key = new();
        public SoundSampleProvider Source = null!;
        public VolumeSampleProvider Volume = null!;
        public float BaseVolume = 1f;
        public string Label = "";
    }

    private bool EnsureOutput()
    {
        if (_failed) return false;
        if (_output != null) return true;
        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 2))
            {
                ReadFully = true
            };
            _output = new WaveOutEvent { DesiredLatency = 90, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
            return true;
        }
        catch
        {
            _failed = true;
            _output?.Dispose();
            _output = null;
            _mixer = null;
            return false;
        }
    }

    public void PlayNode(LevelNode node, float volumeScale = 1f, bool loop = false, object? key = null,
        string? label = null, float seconds = 0f)
    {
        if (node.Kind != NodeKind.Sound) return;
        var samples = SoundSynth.RenderNode(node, 1f, seconds);
        PlaySamples(samples, Math.Clamp(node.SoundVolume, 0f, 2f) * volumeScale, loop,
            key ?? node, label ?? node.DisplayName);
    }

    public void PlaySamples(float[] mono, float volume, bool loop, object? key = null, string? label = null)
    {
        if (mono.Length == 0) return;
        lock (_gate)
        {
            if (!EnsureOutput()) return;
            var keyValue = key ?? new object();
            StopKeyLocked(keyValue);
            var source = new SoundSampleProvider(mono, loop);
            var stereo = new MonoToStereoSampleProvider(source);
            var volumeProvider = new VolumeSampleProvider(stereo) { Volume = Math.Clamp(volume, 0f, 4f) * _master };
            _active.Add(new ActiveSound
            {
                Key = keyValue,
                Source = source,
                Volume = volumeProvider,
                BaseVolume = Math.Clamp(volume, 0f, 4f),
                Label = label ?? ""
            });
            _mixer!.AddMixerInput(volumeProvider);
        }
    }

    public void Stop(object key)
    {
        lock (_gate)
        {
            StopKeyLocked(key);
        }
    }

    public void StopNode(LevelNode node) => Stop(node);

    public bool IsPlaying(object key)
    {
        lock (_gate)
        {
            Prune();
            return _active.Any(a => Equals(a.Key, key) && !a.Source.Finished);
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var a in _active.ToList())
            {
                try { _mixer?.RemoveMixerInput(a.Volume); } catch { }
            }
            _active.Clear();
        }
    }

    public void Dispose()
    {
        StopAll();
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _mixer = null;
    }

    private void StopKeyLocked(object key)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!Equals(_active[i].Key, key)) continue;
            try { _mixer?.RemoveMixerInput(_active[i].Volume); } catch { }
            _active.RemoveAt(i);
        }
    }

    private void Prune()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!_active[i].Source.Finished) continue;
            try { _mixer?.RemoveMixerInput(_active[i].Volume); } catch { }
            _active.RemoveAt(i);
        }
    }

    private sealed class SoundSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly bool _loop;
        private int _position;
        public bool Finished { get; private set; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 1);

        public SoundSampleProvider(float[] samples, bool loop)
        {
            _samples = samples;
            _loop = loop;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (Finished) return 0;
            int written = 0;
            while (written < count)
            {
                if (_position >= _samples.Length)
                {
                    if (!_loop) { Finished = true; break; }
                    _position = 0;
                }
                int n = Math.Min(count - written, _samples.Length - _position);
                Array.Copy(_samples, _position, buffer, offset + written, n);
                _position += n;
                written += n;
            }
            return written;
        }
    }
}

/// <summary>Plays the sound blocks of the current Play session: start-active sounds on load,
/// trigger-target sounds when GASM or trigger zones fire, with range attenuation.</summary>
public sealed class GameAudio : IDisposable
{
    private readonly GrabLevel _level;
    private readonly AudioEngine _engine = AudioEngine.Shared;
    private readonly Dictionary<LevelNode, float> _baseVolume = new();
    private Vec3 _listener;
    private bool _running;

    public GameAudio(GrabLevel level)
    {
        _level = level;
    }

    public void Start(Vec3 listener)
    {
        _running = true;
        _listener = listener;
        foreach (var node in _level.AllNodes().Where(n => n.Kind == NodeKind.Sound))
        {
            if (!node.SoundStartActive) continue;
            Play(node);
        }
    }

    public void Trigger(LevelNode sound, int mode)
    {
        if (!_running || sound.Kind != NodeKind.Sound) return;
        switch (mode)
        {
            case 0: // STOP
            case 4: // RESET
                _engine.StopNode(sound);
                break;
            case 2: // TOGGLE
                if (_engine.IsPlaying(sound)) _engine.StopNode(sound);
                else Play(sound);
                break;
            case 3: // RESTART
                _engine.StopNode(sound);
                Play(sound);
                break;
            default: // START
                Play(sound);
                break;
        }
    }

    public void UpdateListener(Vec3 position)
    {
        _listener = position;
    }

    private void Play(LevelNode sound)
    {
        float attenuation = 1f;
        if (sound.SoundMaxRange > 0)
        {
            var p = sound.WorldPosition;
            double dx = p.X - _listener.X, dy = p.Y - _listener.Y, dz = p.Z - _listener.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            attenuation = (float)Math.Clamp(1.0 - dist / Math.Max(0.001, sound.SoundMaxRange), 0, 1);
            attenuation = attenuation * attenuation * (3 - 2 * attenuation);
            if (attenuation <= 0.001f) attenuation = 0.001f;
        }
        _baseVolume[sound] = attenuation;
        _engine.PlayNode(sound, attenuation, sound.SoundRepeat, sound, sound.DisplayName);
    }

    public void StopAll()
    {
        _running = false;
        foreach (var sound in _level.AllNodes().Where(n => n.Kind == NodeKind.Sound))
            _engine.StopNode(sound);
        _baseVolume.Clear();
    }

    public void Dispose() => StopAll();
}
