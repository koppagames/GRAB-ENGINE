using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GrabEngine.Core;

namespace GrabEngine.Code;

internal sealed class RegisterRef
{
    public GasmConnection Connection = null!;
    public GasmProperty Property = null!;
    public int Component;
}

/// <summary>A named GASM working register as shown in the debugger watch list.</summary>
public sealed record DebugVariable(string Name, float Value);

/// <summary>
/// A live play-mode instance of the real GASM virtual machine. Each Code Block runs its
/// compiled program one instruction at a time, yielding on SLEEP and END just like GRAB.
/// Object, player, trigger, light and sign registers are wired back to the level model so
/// scripts visibly drive the scene.
/// </summary>
public sealed class PlaySession
{
    public GrabLevel Level { get; }
    public float DeltaTime { get; private set; }
    public double Now { get; private set; }
    public bool AnyChange { get; private set; }
    public bool AmbienceDirty { get; private set; }
    public List<string> Messages { get; } = new();

    public Vec3 PlayerPosition { get; set; } = new(0, 1.2f, 4);
    public float PlayerYaw { get; set; }
    public Vec3 PlayerVelocity { get; set; }
    public int PlayerId { get; private set; }
    public Col PlayerColor { get; set; } = new(0.35f, 0.7f, 1f, 1f);

    /// <summary>Horizontal velocity requested by the editor's input controller.</summary>
    public Vec3 DesiredVelocity { get; set; }
    public bool Grounded { get; private set; }
    public float PlayerSpeed => MathF.Sqrt(PlayerVelocity.X * PlayerVelocity.X + PlayerVelocity.Z * PlayerVelocity.Z);
    public float WalkPhase { get; private set; }
    public float PlayerBob => Grounded ? MathF.Sin(WalkPhase) * 0.025f : 0f;

    private const float PlayerRadius = 0.32f;
    private const float PlayerHalfHeight = 0.85f;
    private const float Gravity = 22f;
    private Vec3 _spawn;
    private float _spawnYaw;
    private float _coyoteUntil;
    private float _jumpBufferUntil;
    private double _respawnCooldownUntil;
    private bool _completed;
    private readonly List<PlayerCollider> _colliders = new();

    private readonly Dictionary<ulong, LevelNode> _nodes = new();
    private readonly HashSet<LevelNode> _consoleSigns = new();
    private readonly Dictionary<LevelNode, float> _colorSelect = new();
    private readonly Dictionary<LevelNode, float> _signSelect = new();
    private readonly Dictionary<LevelNode, bool> _force = new();
    private readonly Dictionary<LevelNode, bool> _active = new();
    private readonly Dictionary<LevelNode, int> _playerIdInTrigger = new();
    private readonly Dictionary<LevelNode, AnimationPlayback> _animations = new();

    /// <summary>Raised when a trigger starts or stops a Sound target. The editor's
    /// GameAudio listens so trigger-driven sounds are audible in Play mode.</summary>
    public event Action<LevelNode, int>? SoundTarget;
    private readonly float[] _lobbyVars = new float[5];
    private readonly List<GasmState> _states = new();
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.Ordinal);
    private readonly Random _rng = new(12345);
    private GasmState? _debugState;
    private (GasmState State, int Pc)? _ignoreBreak;

    /// <summary>Breakpoints are active during normal Play mode too; stepping is only
    /// used when the editor has explicitly paused the session.</summary>
    public bool DebuggerEnabled { get; set; } = true;
    public bool DebugPaused { get; private set; }
    public LevelNode? DebugNode => _debugState?.Node;
    public int DebugLine => _debugState?.CurrentSourceLine ?? 0;
    public int DebugInstruction => _debugState?.CurrentIndex ?? 0;
    public int DebugInstructionCount => _debugState?.InstructionCount ?? 0;
    public string DebugReason { get; private set; } = "";
    private Vec3 _teleport = Vec3.Zero;
    private float _teleportYaw;

    public PlaySession(GrabLevel level)
    {
        Level = level;
        var ids = level.BuildObjectIds();
        foreach (var kv in ids) _nodes[kv.Value] = kv.Key;
        var spawn = level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Start);
        if (spawn != null)
        {
            PlayerPosition = spawn.WorldPosition + new Vec3(0, 1.2f, 0);
            // Use the rotated forward vector instead of Euler Y: a 180 degree yaw is a
            // gimbal-degenerate case and ToEulerDegrees() reports it as 0.
            var facing = spawn.WorldRotation.Rotate(new Vec3(0, 0, 1));
            PlayerYaw = MathF.Atan2(facing.X, facing.Z) * 180f / MathF.PI;
        }
        _spawn = PlayerPosition;
        _spawnYaw = PlayerYaw;
        foreach (var node in level.AllNodes())
        {
            if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling) || node.IsPassable) continue;
            var scale = node.WorldScale;
            _colliders.Add(new PlayerCollider
            {
                Center = node.WorldPosition,
                Rotation = node.WorldRotation,
                Half = new Vec3(Math.Abs(scale.X) * 0.5f, Math.Abs(scale.Y) * 0.5f, Math.Abs(scale.Z) * 0.5f),
                IsLava = node.Material is 3 or 6
            });
        }
        foreach (var node in level.AllNodes())
        {
            if (node.Kind != NodeKind.GASM || !node.StartActive) continue;
            GasmProgram? program = node.Compiled?.Success == true ? node.Compiled.Program : node.ImportedProgram;
            if (program == null) continue;
            var connections = node.Compiled?.Success == true ? node.Compiled.Connections : node.ImportedConnections;
            // Console signs use a reserved "cn" connection name so they remain
            // identifiable after a level round-trip (Sign nodes do not store names).
            foreach (var c in connections)
                if (c.Type == GasmConnectionType.Node && c.Name.StartsWith("cn", StringComparison.Ordinal) &&
                    _nodes.TryGetValue(c.ObjectId, out var sign) && sign.Kind == NodeKind.Sign)
                    _consoleSigns.Add(sign);
            _states.Add(new GasmState(this, node, program, connections));
        }

        // GRAB's Animate-menu Start Active setting is stored as the node's
        // activeAnimation field: -1 means "wait for a trigger", any other value is
        // the clip that starts playing with the level. Honour it in Play mode too.
        foreach (var node in level.AllNodes())
        {
            if (node.Animations.Count == 0 || node.ActiveAnimation < 0 || node.ActiveAnimation >= node.Animations.Count)
                continue;
            var clip = node.Animations[node.ActiveAnimation];
            if (clip.Frames.Count == 0) continue;
            _animations[node] = new AnimationPlayback { Clip = clip, Time = 0, Loop = true, Reverse = false };
        }
    }

    public int ProgramCount => _states.Count;
    /// <summary>Registers preserved by the last hot reload.</summary>
    public int LastReloadedRegisters { get; private set; }
    public int PlayerIdInTrigger(LevelNode t) => _playerIdInTrigger.TryGetValue(t, out var id) ? id : 255;

    /// <summary>
    /// Rebuilds the running GASM states from the current compiled programs without
    /// resetting the player, physics or level. This is what makes C# hot reload in
    /// Play mode possible: edit a Code Block, compile it, and the new program starts
    /// on the next frame.
    /// </summary>
    public void ReloadPrograms()
    {
        var previous = _states.GroupBy(s => s.Node.EditorId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _states.Clear();
        _consoleSigns.Clear();
        int carried = 0;
        foreach (var node in Level.AllNodes())
        {
            if (node.Kind != NodeKind.GASM || !node.StartActive) continue;
            GasmProgram? program = node.Compiled?.Success == true ? node.Compiled.Program : node.ImportedProgram;
            if (program == null) continue;
            var connections = node.Compiled?.Success == true ? node.Compiled.Connections : node.ImportedConnections;
            foreach (var c in connections)
                if (c.Type == GasmConnectionType.Node && c.Name.StartsWith("cn", StringComparison.Ordinal) &&
                    _nodes.TryGetValue(c.ObjectId, out var sign) && sign.Kind == NodeKind.Sign)
                    _consoleSigns.Add(sign);
            var state = new GasmState(this, node, program, connections);
            if (previous.TryGetValue(node.EditorId, out var old)) carried += state.CarryFrom(old);
            _states.Add(state);
        }
        LastReloadedRegisters = carried;
        DebugPaused = false;
        _debugState = null;
        AnyChange = true;
    }

    private sealed class AnimationPlayback
    {
        public GrabAnimation Clip = null!;
        public float Time;
        public bool Loop;
        public bool Reverse;
    }

    private sealed class PlayerCollider
    {
        public Vec3 Center;
        public Quat Rotation;
        public Vec3 Half;
        public bool IsLava;
    }

    public void Step(float dt)
    {
        if (DebugPaused) return;
        dt = Math.Clamp(dt, 0f, 0.1f);
        DeltaTime = dt;
        Now += dt;
        AnyChange = false;
        AmbienceDirty = false;

        // Substep the capsule so fast falls and thin ledges stay stable at 60 fps.
        float remaining = dt;
        int guard = 8;
        while (remaining > 1e-5f && guard-- > 0)
        {
            float step = Math.Min(remaining, 1f / 120f);
            PhysicsStep(step);
            remaining -= step;
        }

        UpdateTriggers();
        foreach (var s in _states)
        {
            s.ClearPendingBreak();
            s.Step(Now, dt);
            if (!s.PendingBreak) continue;
            _debugState = s;
            DebugPaused = true;
            DebugReason = $"Breakpoint at line {s.CurrentSourceLine}";
            AnyChange = true;
            return;
        }
        UpdateAnimations(dt);

        var finish = Level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Finish);
        if (!_completed && finish != null)
        {
            var offset = PlayerPosition - finish.WorldPosition;
            float reach = Math.Max(0.6f, finish.Radius) + 0.35f;
            if (offset.X * offset.X + offset.Z * offset.Z <= reach * reach && Math.Abs(offset.Y) < 1.6f)
            {
                _completed = true;
                Messages.Add("Level complete!");
            }
        }
    }

    public void Jump()
    {
        _jumpBufferUntil = (float)Now + 0.14f;
    }

    private void PhysicsStep(float dt)
    {
        if (Now <= _jumpBufferUntil && (Grounded || Now <= _coyoteUntil))
        {
            PlayerVelocity = new Vec3(PlayerVelocity.X, 7.1f, PlayerVelocity.Z);
            Grounded = false;
            _jumpBufferUntil = 0;
            _coyoteUntil = 0;
        }

        float targetX = DesiredVelocity.X;
        float targetZ = DesiredVelocity.Z;
        float accel = Grounded ? 46f : 15f;
        PlayerVelocity = new Vec3(
            MoveTowards(PlayerVelocity.X, targetX, accel * dt),
            PlayerVelocity.Y - Gravity * dt,
            MoveTowards(PlayerVelocity.Z, targetZ, accel * dt));

        PlayerPosition += PlayerVelocity * dt;
        Grounded = false;
        ResolveCollisions();

        if (Grounded)
        {
            if (PlayerVelocity.Y < 0) PlayerVelocity = new Vec3(PlayerVelocity.X, 0, PlayerVelocity.Z);
            _coyoteUntil = (float)Now + 0.12f;
        }

        if (PlayerPosition.Y < _spawn.Y - 35f) Respawn("fell out of the level");
        else CheckHazards();

        float speed = PlayerSpeed;
        if (speed > 0.25f)
        {
            float targetYaw = MathF.Atan2(PlayerVelocity.X, PlayerVelocity.Z) * 180f / MathF.PI;
            PlayerYaw = LerpAngle(PlayerYaw, targetYaw, 1f - MathF.Exp(-12f * dt));
            WalkPhase += dt * speed * 1.9f;
        }
        else
        {
            WalkPhase += dt * 1.2f;
        }
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta) return target;
        return current + Math.Sign(target - current) * maxDelta;
    }

    private static float LerpAngle(float a, float b, float t)
    {
        float delta = (b - a + 540f) % 360f - 180f;
        return a + delta * Math.Clamp(t, 0, 1);
    }

    private void ResolveCollisions()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            bool hit = false;
            foreach (var collider in _colliders)
            {
                if (!CapsulePush(collider, out var normal, out float push)) continue;
                PlayerPosition += normal * (push + 0.0005f);
                hit = true;
                float into = PlayerVelocity.X * normal.X + PlayerVelocity.Y * normal.Y + PlayerVelocity.Z * normal.Z;
                if (into < 0)
                    PlayerVelocity -= normal * into;
                if (normal.Y > 0.55f) Grounded = true;
                AnyChange = true;
            }
            if (!hit) break;
        }
    }

    /// <summary>Approximates the player capsule with spheres along its vertical axis and
    /// pushes it out of the closest point on each oriented block box.</summary>
    private bool CapsulePush(PlayerCollider collider, out Vec3 normal, out float push)
    {
        normal = Vec3.Zero;
        push = 0;
        float best = 0;
        float segment = Math.Max(0.01f, PlayerHalfHeight - PlayerRadius);
        var inv = collider.Rotation.Inverse();
        for (int i = 0; i < 5; i++)
        {
            float t = i / 4f * 2f - 1f;
            var sample = PlayerPosition + new Vec3(0, t * segment, 0);
            var local = inv.Rotate(sample - collider.Center);
            var closest = new Vec3(
                Math.Clamp(local.X, -collider.Half.X, collider.Half.X),
                Math.Clamp(local.Y, -collider.Half.Y, collider.Half.Y),
                Math.Clamp(local.Z, -collider.Half.Z, collider.Half.Z));
            var delta = local - closest;
            float distance = delta.Length;
            Vec3 localNormal;
            float amount;
            if (distance > 1e-5f)
            {
                if (distance >= PlayerRadius) continue;
                localNormal = delta / distance;
                amount = PlayerRadius - distance;
            }
            else
            {
                // The sample is inside the box: escape through the closest face.
                float dx = collider.Half.X - Math.Abs(local.X);
                float dy = collider.Half.Y - Math.Abs(local.Y);
                float dz = collider.Half.Z - Math.Abs(local.Z);
                if (dx <= dy && dx <= dz)
                    localNormal = new Vec3(Math.Sign(local.X) == 0 ? 1 : Math.Sign(local.X), 0, 0);
                else if (dy <= dz)
                    localNormal = new Vec3(0, Math.Sign(local.Y) == 0 ? 1 : Math.Sign(local.Y), 0);
                else
                    localNormal = new Vec3(0, 0, Math.Sign(local.Z) == 0 ? 1 : Math.Sign(local.Z));
                amount = PlayerRadius + Math.Min(dx, Math.Min(dy, dz));
            }
            if (amount <= best) continue;
            best = amount;
            normal = collider.Rotation.Rotate(localNormal).Normalized;
            push = amount;
        }
        return best > 1e-5f;
    }

    private void CheckHazards()
    {
        if (Now < _respawnCooldownUntil) return;
        float segment = Math.Max(0.01f, PlayerHalfHeight - PlayerRadius);
        foreach (var collider in _colliders)
        {
            if (!collider.IsLava) continue;
            var inv = collider.Rotation.Inverse();
            for (int i = 0; i < 5; i++)
            {
                float t = i / 4f * 2f - 1f;
                var local = inv.Rotate(PlayerPosition + new Vec3(0, t * segment, 0) - collider.Center);
                var closest = new Vec3(
                    Math.Clamp(local.X, -collider.Half.X, collider.Half.X),
                    Math.Clamp(local.Y, -collider.Half.Y, collider.Half.Y),
                    Math.Clamp(local.Z, -collider.Half.Z, collider.Half.Z));
                if ((local - closest).Length < 0.18f)
                {
                    Respawn("touched lava");
                    return;
                }
            }
        }
    }

    private void Respawn(string reason)
    {
        PlayerPosition = _spawn;
        PlayerYaw = _spawnYaw;
        PlayerVelocity = Vec3.Zero;
        DesiredVelocity = Vec3.Zero;
        Grounded = false;
        _respawnCooldownUntil = Now + 0.6;
        Messages.Add("Respawned — " + reason);
    }

    // ---------------- debugger ----------------

    public void SetBreakpoints(string editorId, IEnumerable<int> lines)
    {
        if (string.IsNullOrEmpty(editorId)) return;
        var set = new HashSet<int>(lines.Where(l => l > 0));
        if (set.Count == 0) _breakpoints.Remove(editorId);
        else _breakpoints[editorId] = set;
    }

    public IReadOnlyList<(LevelNode Node, int Line)> BreakpointList()
    {
        var result = new List<(LevelNode, int)>();
        foreach (var state in _states)
            if (_breakpoints.TryGetValue(state.Node.EditorId, out var lines))
                foreach (var line in lines.OrderBy(l => l)) result.Add((state.Node, line));
        return result;
    }

    /// <summary>Selects which script the debugger steps and shows variables for.</summary>
    public bool SetDebugTarget(LevelNode? node)
    {
        var target = node == null ? null : _states.FirstOrDefault(s => ReferenceEquals(s.Node, node));
        target ??= _debugState ?? _states.FirstOrDefault(s => !s.Ended && s.Node.StartActive) ?? _states.FirstOrDefault();
        if (target == null) return false;
        _debugState = target;
        return true;
    }

    public void PauseDebug()
    {
        _debugState ??= _states.FirstOrDefault(s => !s.Ended) ?? _states.FirstOrDefault();
        DebugPaused = _debugState != null;
        DebugReason = "Paused";
    }

    public void ContinueDebug()
    {
        if (_debugState != null) _ignoreBreak = (_debugState, _debugState.CurrentIndex);
        DebugPaused = false;
        DebugReason = "";
    }

    public bool StepDebugLine()
    {
        _debugState ??= _states.FirstOrDefault(s => !s.Ended) ?? _states.FirstOrDefault();
        if (_debugState == null) return false;
        if (_debugState.Ended)
        {
            DebugReason = "Program ended";
            return false;
        }
        _ignoreBreak = null;
        DebugPaused = true;
        _debugState.StepSourceLine(Now);
        DebugReason = "Stepped one source line";
        return true;
    }

    public bool StepDebugInstruction()
    {
        _debugState ??= _states.FirstOrDefault(s => !s.Ended) ?? _states.FirstOrDefault();
        if (_debugState == null) return false;
        if (_debugState.Ended)
        {
            DebugReason = "Program ended";
            return false;
        }
        _ignoreBreak = null;
        DebugPaused = true;
        _debugState.StepOne(Now);
        DebugReason = "Stepped one GASM instruction";
        return true;
    }

    public IReadOnlyList<DebugVariable> DebugVariables(LevelNode? node = null)
    {
        var state = node == null ? _debugState : _states.FirstOrDefault(s => ReferenceEquals(s.Node, node));
        state ??= _debugState;
        return state?.Variables() ?? Array.Empty<DebugVariable>();
    }

    internal bool ShouldBreak(GasmState state, int pc, int line)
    {
        if (!DebuggerEnabled || line <= 0) return false;
        if (_ignoreBreak is { } ignore && ReferenceEquals(ignore.State, state) && ignore.Pc == pc)
        {
            _ignoreBreak = null;
            return false;
        }
        return _breakpoints.TryGetValue(state.Node.EditorId, out var lines) && lines.Contains(line);
    }

    private void UpdateAnimations(float dt)
    {
        if (_animations.Count == 0) return;
        foreach (var pair in _animations.ToList())
        {
            var node = pair.Key;
            var state = pair.Value;
            var clip = state.Clip;
            if (clip.Frames.Count == 0)
            {
                _animations.Remove(node);
                continue;
            }
            state.Time += dt * MathF.Max(0.01f, MathF.Abs(clip.Speed)) * (state.Reverse ? -1f : 1f);
            bool pingPong = clip.Direction == 1;
            bool loop = state.Loop || pingPong;
            float duration = AnimationSampler.Duration(clip);
            if (!loop)
            {
                if (state.Time > duration || state.Time < 0)
                {
                    state.Time = Math.Clamp(state.Time, 0, duration);
                    _animations.Remove(node);
                }
            }
            var (position, rotation) = AnimationSampler.Sample(clip, state.Time, loop);
            if (node.Kind != NodeKind.Finish) node.Position = position;
            node.Rotation = rotation;
            node.ActiveAnimation = Math.Max(0, node.Animations.IndexOf(clip));
            AnyChange = true;
        }
    }

    /// <summary>Starts one of the clips authored on a scene object.</summary>
    public bool PlayAnimation(LevelNode node, string clipName, bool loop = true, bool reverse = false)
    {
        var clip = node.Animations.FirstOrDefault(a => string.Equals(a.Name, clipName, StringComparison.OrdinalIgnoreCase));
        if (clip == null && !string.IsNullOrWhiteSpace(clipName))
            clip = node.Animations.FirstOrDefault();
        if (clip == null)
        {
            _animations.Remove(node);
            return false;
        }
        _animations[node] = new AnimationPlayback
        {
            Clip = clip,
            Time = reverse ? AnimationSampler.Duration(clip) : 0f,
            Loop = loop,
            Reverse = reverse
        };
        node.ActiveAnimation = Math.Max(0, node.Animations.IndexOf(clip));
        if (node.Kind != NodeKind.Finish) node.Position = clip.Frames[0].Position;
        node.Rotation = clip.Frames[0].Rotation;
        AnyChange = true;
        return true;
    }

    public void StopAnimation(LevelNode node)
    {
        if (_animations.Remove(node)) AnyChange = true;
    }

    private void StopAnimations(LevelNode trigger)
    {
        foreach (var target in trigger.TriggerTargets)
        {
            if (target.Kind == 3)
            {
                if (target.Mode is 1 or 2 && ResolveTarget(target) is { Kind: NodeKind.Sound } sound)
                    SoundTarget?.Invoke(sound, target.TargetMode);
                continue;
            }
            if (target.Kind != 1) continue;
            var node = ResolveTarget(target);
            if (node != null) StopAnimation(node);
        }
    }

    private LevelNode? ResolveTarget(TriggerTarget target)
    {
        if (target.ObjectId != 0 && _nodes.TryGetValue(target.ObjectId, out var node)) return node;
        if (!string.IsNullOrWhiteSpace(target.ObjectName)) return Level.FindByName(target.ObjectName);
        return null;
    }

    public void ResetState()
    {
        _debugState = null;
        DebugPaused = false;
        _ignoreBreak = null;
        _colorSelect.Clear();
        _signSelect.Clear();
        _force.Clear();
        _active.Clear();
        _animations.Clear();
        foreach (var s in _states) s.Reset();
    }

    private void UpdateTriggers()
    {
        foreach (var t in Level.AllNodes())
        {
            if (t.Kind != NodeKind.Trigger) continue;
            // A trigger with no sources is script-only, exactly like the retail levels'
            // helper triggers: the game never activates it from a player hand/head/block,
            // only ForceActive (or a GASM write) can. Without this check, every stacked
            // sound-slice trigger at an import point fires at once the moment the player
            // stands on it, so a whole song plays as one chord.
            bool inside = IsInside(t);
            bool forced = _force.TryGetValue(t, out var f) && f;
            bool was = _active.TryGetValue(t, out var a) && a;
            bool now = forced || inside;
            _active[t] = now;
            _playerIdInTrigger[t] = now ? 0 : 255;
            if (now != was)
            {
                if (now) ApplyTargets(t);
                else StopAnimations(t);
            }
        }
    }

    private void ApplyTargets(LevelNode trigger)
    {
        foreach (var target in trigger.TriggerTargets)
        {
            switch (target.Kind)
            {
                case 1:
                    if (ResolveTarget(target) is { } animationTarget)
                    {
                        if (string.IsNullOrWhiteSpace(target.AnimationName) || AnimationSampler.IsStopClip(
                                animationTarget.Animations.FirstOrDefault(a => string.Equals(a.Name, target.AnimationName, StringComparison.OrdinalIgnoreCase))))
                            StopAnimation(animationTarget);
                        else
                            PlayAnimation(animationTarget, target.AnimationName, target.Loop, target.Reverse);
                    }
                    break;
                case 3:
                    // ONENTER targets act on enter; ONLEAVE and ONENTERONLEAVE wait for
                    // the leave event in StopTriggerTargets. TargetMode uses GRAB's
                    // TriggerTargetSound.Mode: STOP, START, TOGGLE, RESTART, RESET.
                    if (target.Mode is 0 or 2 && ResolveTarget(target) is { Kind: NodeKind.Sound } sound)
                        SoundTarget?.Invoke(sound, target.TargetMode);
                    break;
                case 6:
                    Level.Ambience = target.Ambient.Clone();
                    AmbienceDirty = true;
                    AnyChange = true;
                    break;
                case 8:
                    if (_nodes.TryGetValue(target.ObjectId, out var light) && light.Kind == NodeKind.Light)
                    {
                        light.LightColor = target.Color;
                        light.LightRange = target.Range;
                        light.LightIntensity = target.Brightness;
                        AnyChange = true;
                    }
                    break;
            }
        }
    }

    public void TriggerForce(LevelNode t, bool on)
    {
        bool was = _active.TryGetValue(t, out var a) && a;
        _force[t] = on;
        bool now = on || IsInside(t);
        _active[t] = now;
        if (now && !was) ApplyTargets(t);
        else if (!now && was) StopAnimations(t);
    }

    private bool IsInside(LevelNode t)
    {
        if (t.TriggerSources.Count == 0) return false;
        var c = t.WorldPosition;
        var s = t.WorldScale;
        return Math.Abs(PlayerPosition.X - c.X) <= Math.Abs(s.X) * 0.5f + 0.35f
            && Math.Abs(PlayerPosition.Y - c.Y) <= Math.Abs(s.Y) * 0.5f + 1.1f
            && Math.Abs(PlayerPosition.Z - c.Z) <= Math.Abs(s.Z) * 0.5f + 0.35f;
    }

    // ---------------- register plumbing ----------------

    internal LevelNode? NodeFor(GasmConnection c)
        => c.Type == GasmConnectionType.Node && _nodes.TryGetValue(c.ObjectId, out var n) ? n : null;

    internal float ReadSlot(RegisterRef r)
    {
        var c = r.Connection;
        if (c.Type == GasmConnectionType.Player) return ReadPlayer(r.Component);
        if (c.Type == GasmConnectionType.Lobby) return r.Component >= 0 && r.Component < 5 ? _lobbyVars[r.Component] : 0;
        var n = NodeFor(c);
        if (n == null) return 0;
        switch (r.Property.Kind)
        {
            case GasmPropertyKind.Position:
                return Comp(n.WorldPosition, r.Component);
            case GasmPropertyKind.Rotation:
                return Comp(n.WorldRotation.ToEulerDegrees(), r.Component);
            case GasmPropertyKind.Scale:
                return Comp(n.WorldScale, r.Component);
            case GasmPropertyKind.Color:
                if (r.Component == 3) return _colorSelect.TryGetValue(n, out var sel) ? sel : 0;
                return Channel(PaintOf(n), r.Component);
            case GasmPropertyKind.Light:
                if (r.Component <= 2) return Channel(n.LightColor, r.Component);
                if (r.Component == 3) return n.LightIntensity;
                return n.LightRange;
            case GasmPropertyKind.TriggerActive:
                if (r.Component == 0) return (_active.TryGetValue(n, out var a) && a) ? 1 : 0;
                if (r.Component == 1) return PlayerIdInTrigger(n);
                if (r.Component == 2) return (_force.TryGetValue(n, out var f) && f) ? 1 : 0;
                return 0;
            case GasmPropertyKind.Sign:
                if (r.Component == 1) return _signSelect.TryGetValue(n, out var ss) ? ss : 0;
                return 0;
            case GasmPropertyKind.Physics:
                if (r.Component == 0) return n.PhysicsObject ? 1 : 0;
                return 0;
        }
        return 0;
    }

    internal void WriteSlot(RegisterRef r, float value)
    {
        var c = r.Connection;
        if (c.Type == GasmConnectionType.Player) { WritePlayer(r.Component, value); return; }
        if (c.Type == GasmConnectionType.Lobby)
        {
            if (r.Component >= 0 && r.Component < 5) _lobbyVars[r.Component] = value;
            return;
        }
        var n = NodeFor(c);
        if (n == null) return;
        switch (r.Property.Kind)
        {
            case GasmPropertyKind.Position:
                SetWorldComponent(n, r.Component, value);
                break;
            case GasmPropertyKind.Rotation:
            {
                var e = n.WorldRotation.ToEulerDegrees();
                if (r.Component < 0 || r.Component > 2) break;
                e[r.Component] = value;
                var world = Quat.FromEulerDegrees(e);
                n.Rotation = n.Parent != null ? n.Parent.WorldRotation.Inverse() * world : world;
                break;
            }
            case GasmPropertyKind.Color:
            {
                if (r.Component == 3) { _colorSelect[n] = value; break; }
                var col = PaintOf(n);
                SetChannel(ref col, r.Component, value);
                if (_colorSelect.TryGetValue(n, out var s) && s >= 0.5f) n.Color2 = col; else n.Color1 = col;
                break;
            }
            case GasmPropertyKind.Light:
                if (r.Component <= 2)
                {
                    var col = n.LightColor;
                    SetChannel(ref col, r.Component, value);
                    n.LightColor = col;
                }
                else if (r.Component == 3) n.LightIntensity = Math.Clamp(value, 0, 10);
                else if (r.Component == 4) n.LightRange = Math.Clamp(value, 0, 100);
                break;
            case GasmPropertyKind.TriggerActive:
                if (r.Component == 2) TriggerForce(n, value != 0);
                break;
            case GasmPropertyKind.Physics:
                if (r.Component == 0) n.PhysicsObject = value != 0;
                break;
            case GasmPropertyKind.Sign:
                WriteSign(n, r.Component, value);
                break;
        }
        AnyChange = true;
    }

    private void WriteSign(LevelNode n, int component, float value)
    {
        bool console = IsConsoleSign(n);
        bool numberMode = _signSelect.TryGetValue(n, out var s) && s >= 0.5f;
        switch (component)
        {
            case 0:
                if (n.Text.Length >= 150)
                {
                    if (!console) return;
                    // A console line longer than the sign is still reported in full:
                    // flush the visible chunk and keep typing on a fresh sign.
                    FlushConsolePartial(n);
                    n.Text = "";
                }
                if (numberMode)
                {
                    n.Text = Trim(n.Text + value.ToString("0.###", CultureInfo.InvariantCulture));
                    if (console) FlushConsoleLine(n);
                }
                else
                {
                    int code = (int)MathF.Round(value);
                    if (code == 10)
                    {
                        n.Text = Trim(n.Text + "\n");
                        if (console) FlushConsoleLine(n);
                    }
                    else if (code >= 32 && code < 127) n.Text = Trim(n.Text + (char)code);
                }
                break;
            case 1:
                _signSelect[n] = value >= 0.5f ? 1 : 0;
                break;
            case 2:
                if (value >= 0.5f && value < 1.5f)
                {
                    if (console) FlushConsolePartial(n);
                    n.Text = "";
                }
                else if (value >= 1.5f && value < 2.5f && n.Text.Length > 0) n.Text = n.Text[..^1];
                break;
            case 3:
                n.Text = Trim(value.ToString("0.###", CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>
    /// Console.Write/WriteLine compile to writes on a generated sign. Mirror every
    /// completed line into the engine's Console tab while Play mode runs.
    /// </summary>
    private bool IsConsoleSign(LevelNode n) => _consoleSigns.Contains(n) ||
        (n.Kind == NodeKind.Sign && n.Name.StartsWith("GASM Console", StringComparison.OrdinalIgnoreCase));

    private void FlushConsoleLine(LevelNode n)
    {
        if (n.Text.Length == 0 || n.Text[^1] != '\n') return;
        // A Write without WriteLine may leave earlier completed lines in the sign.
        int previous = n.Text.LastIndexOf('\n', n.Text.Length - 2);
        Messages.Add(n.Text[(previous + 1)..^1]);
    }

    private void FlushConsolePartial(LevelNode n)
    {
        if (n.Text.Length == 0 || n.Text[^1] == '\n') return;
        int previous = n.Text.LastIndexOf('\n');
        string pending = n.Text[(previous + 1)..];
        if (pending.Length > 0) Messages.Add(pending);
    }

    private static string Trim(string s) => s.Length > 150 ? s[..150] : s;

    private Col PaintOf(LevelNode n)
    {
        if (_colorSelect.TryGetValue(n, out var s) && s >= 0.5f) return n.Color2;
        return n.Color1;
    }

    private static float Comp(Vec3 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => 0 };
    private static float Channel(Col c, int i) => i switch
    {
        0 => c.R * 255f, 1 => c.G * 255f, 2 => c.B * 255f, 3 => c.A * 255f, _ => 0
    };

    private static void SetChannel(ref Col c, int i, float v)
    {
        v = Math.Clamp(v, 0, 255) / 255f;
        if (i == 0) c.R = v; else if (i == 1) c.G = v; else if (i == 2) c.B = v; else if (i == 3) c.A = v;
    }

    private static void SetWorldComponent(LevelNode n, int component, float value)
    {
        if (component < 0 || component > 2) return;
        var world = n.WorldPosition;
        world[component] = value;
        if (n.Parent == null) { n.Position = world; return; }
        var rel = world - n.Parent.WorldPosition;
        var local = n.Parent.WorldRotation.Inverse().Rotate(rel);
        var ps = n.Parent.WorldScale;
        n.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f), local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
    }

    private float ReadPlayer(int i) => i switch
    {
        0 => 0, 1 => 0, 2 => 0, 3 => 0,
        4 => 1, 5 => 1, 6 => PlayerId,
        7 => PlayerPosition.X, 8 => PlayerPosition.Y, 9 => PlayerPosition.Z,
        10 => 0, 11 => PlayerYaw, 12 => 0, 13 => 0,
        14 => 0, 15 => PlayerColor.R * 255f, 16 => PlayerColor.G * 255f, 17 => PlayerColor.B * 255f,
        18 => 0,
        19 => PlayerVelocity.X, 20 => PlayerVelocity.Y, 21 => PlayerVelocity.Z,
        _ => 0
    };

    private void WritePlayer(int i, float v)
    {
        switch (i)
        {
            case 14: break;
            case 15: PlayerColor = new Col(Math.Clamp(v, 0, 255) / 255f, PlayerColor.G, PlayerColor.B, 1); break;
            case 16: PlayerColor = new Col(PlayerColor.R, Math.Clamp(v, 0, 255) / 255f, PlayerColor.B, 1); break;
            case 17: PlayerColor = new Col(PlayerColor.R, PlayerColor.G, Math.Clamp(v, 0, 255) / 255f, 1); break;
            case 19: PlayerVelocity = new Vec3(v, PlayerVelocity.Y, PlayerVelocity.Z); break;
            case 20: PlayerVelocity = new Vec3(PlayerVelocity.X, v, PlayerVelocity.Z); break;
            case 21: PlayerVelocity = new Vec3(PlayerVelocity.X, PlayerVelocity.Y, v); break;
            case 22: _teleport.X = v; break;
            case 23: _teleport.Y = v; break;
            case 24: _teleport.Z = v; break;
            case 25: _teleportYaw = v; break;
            case 26:
                if (v != 0) { PlayerPosition = _teleport; PlayerYaw = _teleportYaw; }
                break;
            case 27:
                if (v != 0) PlayerVelocity = Vec3.Zero;
                break;
        }
    }

    internal float Random(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
}

internal sealed class GasmState
{
    private readonly PlaySession _s;
    private readonly GasmProgram _p;
    private readonly float[] _work;
    private readonly Dictionary<int, int> _labelPos = new();
    private readonly Dictionary<int, RegisterRef> _inputs = new();
    private readonly Dictionary<int, RegisterRef> _outputs = new();
    private readonly Dictionary<int, RegisterRef> _inouts = new();

    public LevelNode Node { get; }
    private int _pc;
    private bool _ended;
    private bool _halted;
    private double _sleepUntil;

    public int CurrentIndex => _pc;
    public int InstructionCount => _p.Instructions.Count;
    public bool Ended => _ended || _halted;
    public bool PendingBreak { get; private set; }
    public int NextSourceLine => _pc >= 0 && _pc < _p.Instructions.Count ? _p.Instructions[_pc].SourceLine : 0;
    public int CurrentSourceLine => NextSourceLine;
    public void ClearPendingBreak() => PendingBreak = false;

    public GasmState(PlaySession s, LevelNode node, GasmProgram program, List<GasmConnection> connections)
    {
        _s = s;
        Node = node;
        _p = program;
        _work = new float[Math.Max(8, program.WorkingRegisters.Count)];
        for (int i = 0; i < _work.Length; i++) _work[i] = 0;
        for (int i = 0; i < program.Instructions.Count; i++)
        {
            var ins = program.Instructions[i];
            if (ins.Op == GasmOp.Label && ins.Operands.Count > 0)
                _labelPos[ins.Operands[0].Index] = i;
        }
        foreach (var c in connections)
        foreach (var prop in c.Properties)
        {
            for (int i = 0; i < prop.Components.Count; i++)
            {
                var comp = prop.Components[i];
                if (comp.Input >= 0) _inputs[comp.Input] = new RegisterRef { Connection = c, Property = prop, Component = i };
                if (comp.Output >= 0) _outputs[comp.Output] = new RegisterRef { Connection = c, Property = prop, Component = i };
                if (comp.InOut >= 0) _inouts[comp.InOut] = new RegisterRef { Connection = c, Property = prop, Component = i };
            }
        }
    }

    /// <summary>
    /// Copies live working registers from the previous program of the same Code Block.
    /// Register indices can move when the script changes, so values are matched by the
    /// compiler's working-register names (R0, time, v_health_1...). The instruction
    /// pointer deliberately restarts, because a new program can shift label addresses.
    /// </summary>
    public int CarryFrom(GasmState old)
    {
        int copied = 0;
        int n = Math.Min(_p.WorkingRegisters.Count, old._p.WorkingRegisters.Count);
        for (int i = 0; i < n; i++)
        {
            string name = _p.WorkingRegisters[i].Name;
            int j = old._p.WorkingRegisters.FindIndex(r => r.Name == name);
            if (j < 0 || j >= old._work.Length) continue;
            _work[i] = old._work[j];
            if (_work[i] != 0f) copied++;
        }
        return copied;
    }

    public void Reset()
    {
        Array.Clear(_work);
        _pc = 0;
        _ended = false;
        _halted = false;
        _sleepUntil = 0;
    }

    public void Step(double now, float dt)
    {
        if (_ended || _halted) return;
        if (now < _sleepUntil) return;
        int budget = 4000;
        while (budget-- > 0)
        {
            if (_pc < 0 || _pc >= _p.Instructions.Count) { _ended = true; return; }
            var ins = _p.Instructions[_pc];
            if (_s.ShouldBreak(this, _pc, ins.SourceLine))
            {
                PendingBreak = true;
                return;
            }
            _pc++;
            bool yield = Execute(ins);
            if (_halted || _ended || yield) return;
        }
    }

    /// <summary>Executes exactly one GASM instruction, ignoring breakpoints. Used by
    /// the debugger's Step Instruction command.</summary>
    public void StepOne(double now)
    {
        if (_ended || _halted) return;
        if (_pc < 0 || _pc >= _p.Instructions.Count) { _ended = true; return; }
        var ins = _p.Instructions[_pc];
        _pc++;
        Execute(ins);
    }

    /// <summary>Executes the instructions belonging to the next C# source line and
    /// stops before the line changes, so Step Line behaves like a source debugger.</summary>
    public void StepSourceLine(double now)
    {
        if (_ended || _halted) return;
        int line = NextSourceLine;
        StepOne(now);
        int guard = 6000;
        while (!Ended && line > 0 && NextSourceLine == line && guard-- > 0)
            StepOne(now);
    }

    public IReadOnlyList<DebugVariable> Variables()
    {
        var raw = new List<(string Base, int Component, float Value)>();
        for (int i = 0; i < _p.WorkingRegisters.Count && i < _work.Length; i++)
        {
            string name = _p.WorkingRegisters[i].Name;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__t", StringComparison.Ordinal)) continue;
            if (name.StartsWith("v_", StringComparison.Ordinal) || name.StartsWith("f_", StringComparison.Ordinal)
                || name.StartsWith("p_", StringComparison.Ordinal))
                name = name[2..];
            int component = -1;
            int split = name.LastIndexOf('_');
            if (split > 0 && int.TryParse(name[(split + 1)..], out int n))
            {
                name = name[..split];
                component = n;
            }
            raw.Add((name.Replace('_', ' '), component, _work[i]));
        }
        var counts = raw.GroupBy(r => r.Base).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<DebugVariable>();
        foreach (var item in raw)
        {
            // Multiple registers sharing a base are a vector/array. A lone register
            // ending in _0 is just the compiler's unique storage key, not an index.
            string label = counts[item.Base] > 1 && item.Component >= 0
                ? $"{item.Base}[{item.Component}]"
                : item.Base;
            result.Add(new DebugVariable(label, item.Value));
        }
        return result;
    }

    private bool Execute(GasmInstruction ins)
    {
        var op = ins.Operands;
        switch (ins.Op)
        {
            case GasmOp.Noop:
            case GasmOp.Label:
                return false;
            case GasmOp.Set:
                if (op.Count >= 2) Write(op[0], Read(op[1]));
                return false;
            case GasmOp.Swap:
                if (op.Count >= 2) { float a = Read(op[0]), b = Read(op[1]); Write(op[0], b); Write(op[1], a); }
                return false;
            case GasmOp.Add:
                if (op.Count >= 3) Write(op[0], Read(op[1]) + Read(op[2]));
                return false;
            case GasmOp.Sub:
                if (op.Count >= 3) Write(op[0], Read(op[1]) - Read(op[2]));
                return false;
            case GasmOp.Mul:
                if (op.Count >= 3) Write(op[0], Read(op[1]) * Read(op[2]));
                return false;
            case GasmOp.Div:
                if (op.Count >= 3)
                {
                    float b = Read(op[2]);
                    if (Math.Abs(b) < 1e-12f) { Fail("DIV by zero"); return true; }
                    Write(op[0], Read(op[1]) / b);
                }
                return false;
            case GasmOp.Mod:
                if (op.Count >= 3)
                {
                    float b = Read(op[2]);
                    if (Math.Abs(b) < 1e-12f) { Fail("MOD by zero"); return true; }
                    Write(op[0], MathF.IEEERemainder(Read(op[1]), b));
                }
                return false;
            case GasmOp.Equal:
                if (op.Count >= 3) Write(op[0], Math.Abs(Read(op[1]) - Read(op[2])) < 1e-6f ? 1 : 0);
                return false;
            case GasmOp.Less:
                if (op.Count >= 3) Write(op[0], Read(op[1]) < Read(op[2]) ? 1 : 0);
                return false;
            case GasmOp.Greater:
                if (op.Count >= 3) Write(op[0], Read(op[1]) > Read(op[2]) ? 1 : 0);
                return false;
            case GasmOp.And:
                if (op.Count >= 3) Write(op[0], Math.Abs(Read(op[1])) > 1e-6f && Math.Abs(Read(op[2])) > 1e-6f ? 1 : 0);
                return false;
            case GasmOp.Or:
                if (op.Count >= 3) Write(op[0], Math.Abs(Read(op[1])) > 1e-6f || Math.Abs(Read(op[2])) > 1e-6f ? 1 : 0);
                return false;
            case GasmOp.Not:
                if (op.Count >= 2) Write(op[0], Math.Abs(Read(op[1])) < 1e-6f ? 1 : 0);
                return false;
            case GasmOp.Goto:
                if (op.Count >= 1) Jump(op[0]);
                return false;
            case GasmOp.If:
                if (op.Count >= 2 && Math.Abs(Read(op[0])) > 1e-6f) Jump(op[1]);
                return false;
            case GasmOp.Sleep:
            {
                double ms = op.Count >= 1 ? Read(op[0]) : 0;
                if (ms > 0.01) _sleepUntil = _s.Now + ms / 1000.0;
                return true;
            }
            case GasmOp.End:
                _ended = true;
                return true;
            case GasmOp.Rand:
                if (op.Count >= 3) Write(op[0], _s.Random(Read(op[1]), Read(op[2])));
                return false;
            case GasmOp.Floor:
                if (op.Count >= 2) Write(op[0], MathF.Floor(Read(op[1])));
                return false;
            case GasmOp.Sin:
                if (op.Count >= 2) Write(op[0], MathF.Sin(Read(op[1]) * MathF.PI / 180f));
                return false;
            case GasmOp.Cos:
                if (op.Count >= 2) Write(op[0], MathF.Cos(Read(op[1]) * MathF.PI / 180f));
                return false;
            case GasmOp.Sqrt:
                if (op.Count >= 2)
                {
                    float v = Read(op[1]);
                    if (v < 0) { Fail("SQRT of a negative number"); return true; }
                    Write(op[0], MathF.Sqrt(v));
                }
                return false;
            case GasmOp.Atan2:
                if (op.Count >= 3) Write(op[0], MathF.Atan2(Read(op[1]), Read(op[2])) * 180f / MathF.PI);
                return false;
            case GasmOp.Pow:
                if (op.Count >= 3) Write(op[0], MathF.Pow(Read(op[1]), Read(op[2])));
                return false;
            case GasmOp.Abs:
                if (op.Count >= 2) Write(op[0], MathF.Abs(Read(op[1])));
                return false;
            case GasmOp.VecSet:
                if (op.Count >= 2) for (int i = 0; i < 3; i++) WriteVec(op[0], i, ReadVec(op[1], i));
                return false;
            case GasmOp.VecAdd:
            case GasmOp.VecSub:
            case GasmOp.VecMul:
                if (op.Count >= 3)
                    for (int i = 0; i < 3; i++)
                    {
                        float a = ReadVec(op[1], i), b = ReadVec(op[2], i);
                        WriteVec(op[0], i, ins.Op == GasmOp.VecAdd ? a + b : ins.Op == GasmOp.VecSub ? a - b : a * b);
                    }
                return false;
            case GasmOp.VecScl:
                if (op.Count >= 3)
                    for (int i = 0; i < 3; i++) WriteVec(op[0], i, ReadVec(op[1], i) * Read(op[2]));
                return false;
            case GasmOp.VecLen:
                if (op.Count >= 2)
                {
                    float x = ReadVec(op[1], 0), y = ReadVec(op[1], 1), z = ReadVec(op[1], 2);
                    Write(op[0], MathF.Sqrt(x * x + y * y + z * z));
                }
                return false;
            case GasmOp.VecDot:
                if (op.Count >= 3)
                {
                    float d = ReadVec(op[1], 0) * ReadVec(op[2], 0) + ReadVec(op[1], 1) * ReadVec(op[2], 1) + ReadVec(op[1], 2) * ReadVec(op[2], 2);
                    Write(op[0], d);
                }
                return false;
        }
        return false;
    }

    private void Fail(string message)
    {
        _halted = true;
        _s.Messages.Add($"{Node.DisplayName}: {message}");
    }

    private void Jump(GasmOperand label)
    {
        if (label.Kind != GasmOperandKind.Label) return;
        if (_labelPos.TryGetValue(label.Index, out var pos)) _pc = pos;
    }

    private float Read(GasmOperand o) => o.Kind switch
    {
        GasmOperandKind.Working => o.Index >= 0 && o.Index < _work.Length ? _work[o.Index] : 0,
        GasmOperandKind.Constant => o.Value,
        GasmOperandKind.Special => Special(o.Index),
        GasmOperandKind.Input => _inputs.TryGetValue(o.Index, out var ri) ? _s.ReadSlot(ri) : 0,
        GasmOperandKind.Output => _outputs.TryGetValue(o.Index, out var ro) ? _s.ReadSlot(ro) : 0,
        GasmOperandKind.InOut => _inouts.TryGetValue(o.Index, out var rw) ? _s.ReadSlot(rw) : 0,
        _ => 0
    };

    private void Write(GasmOperand o, float v)
    {
        switch (o.Kind)
        {
            case GasmOperandKind.Working:
                if (o.Index >= 0 && o.Index < _work.Length) _work[o.Index] = v;
                break;
            case GasmOperandKind.Output:
                if (_outputs.TryGetValue(o.Index, out var ro)) _s.WriteSlot(ro, v);
                break;
            case GasmOperandKind.InOut:
                if (_inouts.TryGetValue(o.Index, out var rw)) _s.WriteSlot(rw, v);
                break;
        }
    }

    private float ReadVec(GasmOperand o, int k)
    {
        if (o.Kind == GasmOperandKind.Working)
            return o.Index + k < _work.Length ? _work[o.Index + k] : 0;
        var map = o.Kind == GasmOperandKind.Input ? _inputs : o.Kind == GasmOperandKind.InOut ? _inouts : null;
        if (map != null && map.TryGetValue(o.Index + k, out var r)) return _s.ReadSlot(r);
        return 0;
    }

    private void WriteVec(GasmOperand o, int k, float v)
    {
        switch (o.Kind)
        {
            case GasmOperandKind.Working:
                if (o.Index + k < _work.Length) _work[o.Index + k] = v;
                break;
            case GasmOperandKind.InOut:
                if (_inouts.TryGetValue(o.Index + k, out var r)) _s.WriteSlot(r, v);
                break;
            case GasmOperandKind.Output:
                if (_outputs.TryGetValue(o.Index + k, out var ro)) _s.WriteSlot(ro, v);
                break;
        }
    }

    private float Special(int i) => i switch
    {
        0 => _pc,
        1 => _halted ? 1 : 0,
        2 => 0,
        3 => (float)Math.Max(0, (_sleepUntil - _s.Now) * 1000.0),
        4 => _s.DeltaTime,
        _ => 0
    };
}
