using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

/// <summary>
/// Live preview of GRAB's particle emitters. Particles are simulated on the CPU and
/// drawn as camera-facing soft sprites, using the emitter's real rate, lifetime,
/// velocity/acceleration ranges, size ranges and start/end colour.
/// </summary>
public sealed class ParticleSystem
{
    private const int MaxParticlesPerEmitter = 320;
    private const int MaterialBuckets = 24;

    public Model3DGroup Root { get; } = new();

    private readonly Dictionary<LevelNode, Emitter> _emitters = new();
    private readonly MeshGeometry3D _quad = MeshFactory.Quad(2, 2);
    private readonly Random _rnd = new(20261028);
    private GrabLevel? _level;

    public int ActiveParticles
    {
        get
        {
            int n = 0;
            foreach (var e in _emitters.Values) n += e.Particles.Count;
            return n;
        }
    }

    public int EmitterCount => _emitters.Count;

    public void Reset(GrabLevel? level)
    {
        _level = level;
        _emitters.Clear();
        Root.Children.Clear();
        if (level == null) return;
        foreach (var node in level.AllNodes())
        {
            if (node.Kind != NodeKind.ParticleEmitter) continue;
            var emitter = new Emitter(node, _quad);
            _emitters[node] = emitter;
            Root.Children.Add(emitter.Group);
        }
    }

    public void Advance(double dt, PerspectiveCamera camera)
    {
        if (_level == null) return;
        dt = Math.Clamp(dt, 0, 0.1);
        if (dt <= 0) return;

        var look = camera.LookDirection;
        if (look.LengthSquared < 1e-9) return;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        foreach (var emitter in _emitters.Values)
            emitter.Advance(dt, right, up, look, _rnd);
    }

    private sealed class Emitter
    {
        private readonly LevelNode _node;
        private readonly List<Particle> _particles = new();
        private readonly GeometryModel3D[] _pool;
        private readonly MatrixTransform3D[] _transforms;
        private readonly Material?[] _assigned;
        private readonly Material[] _materials;
        private readonly ScaleTransform3D _hidden = new(0, 0, 0);
        private double _spawn;

        public Model3DGroup Group { get; } = new();
        public List<Particle> Particles => _particles;

        public Emitter(LevelNode node, MeshGeometry3D quad)
        {
            _node = node;
            _pool = new GeometryModel3D[MaxParticlesPerEmitter];
            _transforms = new MatrixTransform3D[MaxParticlesPerEmitter];
            _assigned = new Material?[MaxParticlesPerEmitter];
            for (int i = 0; i < _pool.Length; i++)
            {
                _transforms[i] = new MatrixTransform3D(_hidden.Value);
                var model = new GeometryModel3D(quad, null)
                {
                    BackMaterial = null,
                    Transform = _hidden
                };
                _pool[i] = model;
                Group.Children.Add(model);
            }

            _materials = new Material[MaterialBuckets];
            for (int i = 0; i < _materials.Length; i++)
            {
                double t = i / (double)(_materials.Length - 1);
                var c = ColorLerp(node.StartColor, node.EndColor, t);
                double alpha = Math.Clamp(c.A, 0, 1);
                // GRAB's Particles.ps is an untextured shader: it multiplies the
                // per-particle vertex colour by brightness and alpha. There is no
                // soft sprite or radial falloff, so the preview must use solid quads
                // too. Alpha fading comes from StartColor/EndColor.
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255),
                    (byte)Math.Clamp(c.R * 255, 0, 255), (byte)Math.Clamp(c.G * 255, 0, 255), (byte)Math.Clamp(c.B * 255, 0, 255)));
                brush.Freeze();
                var mat = new EmissiveMaterial(brush);
                mat.Freeze();
                _materials[i] = mat;
            }
        }

        public void Advance(double dt, Vector3D right, Vector3D up, Vector3D look, Random rnd)
        {
            if (_node.HideParticles)
            {
                _particles.Clear();
                HideAll();
                return;
            }

            // Spawn
            _spawn += Math.Max(0, _node.ParticlesPerSecond) * dt;
            int spawn = (int)_spawn;
            _spawn -= spawn;
            for (int i = 0; i < spawn && _particles.Count < MaxParticlesPerEmitter; i++)
                Spawn(rnd);

            // Integrate
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Age += (float)dt;
                if (p.Age >= p.Life)
                {
                    _particles.RemoveAt(i);
                    continue;
                }
                p.Velocity += p.Acceleration * (float)dt;
                p.Position += p.Velocity * (float)dt;
                _particles[i] = p;
            }

            // Build billboards
            int count = Math.Min(_particles.Count, _pool.Length);
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                double t = Math.Clamp(p.Age / Math.Max(p.Life, 1e-4f), 0, 1);
                double size = Math.Max(0.0001, p.StartSize + (p.EndSize - p.StartSize) * t);
                int bucket = Math.Clamp((int)(t * (MaterialBuckets - 1) + 0.5), 0, MaterialBuckets - 1);
                if (!ReferenceEquals(_assigned[i], _materials[bucket]))
                {
                    _assigned[i] = _materials[bucket];
                    _pool[i].Material = _materials[bucket];
                    _pool[i].BackMaterial = _materials[bucket];
                }
                var m = new Matrix3D(
                    right.X * size, right.Y * size, right.Z * size, 0,
                    up.X * size, up.Y * size, up.Z * size, 0,
                    look.X * size, look.Y * size, look.Z * size, 0,
                    p.Position.X, p.Position.Y, p.Position.Z, 1);
                _transforms[i].Matrix = m;
                if (!ReferenceEquals(_pool[i].Transform, _transforms[i])) _pool[i].Transform = _transforms[i];
            }
            for (int i = count; i < _pool.Length; i++)
            {
                if (!ReferenceEquals(_pool[i].Transform, _hidden)) _pool[i].Transform = _hidden;
                _assigned[i] = null;
            }
        }

        private void HideAll()
        {
            for (int i = 0; i < _pool.Length; i++)
            {
                if (!ReferenceEquals(_pool[i].Transform, _hidden)) _pool[i].Transform = _hidden;
                _assigned[i] = null;
            }
        }

        private void Spawn(Random rnd)
        {
            var n = _node;
            var rot = n.WorldRotation;
            var origin = n.WorldPosition;
            var jitter = new Vec3((float)(rnd.NextDouble() - 0.5) * 0.14f,
                (float)(rnd.NextDouble() - 0.5) * 0.14f,
                (float)(rnd.NextDouble() - 0.5) * 0.14f);
            var baseVel = n.Velocity + RandomRange(n.VelocityMin, n.VelocityMax, rnd);
            var accel = RandomRange(n.AccelerationMin, n.AccelerationMax, rnd);
            var p = new Particle
            {
                Age = 0,
                Life = Math.Max(0.05f, Lerp(n.LifeSpanMin, n.LifeSpanMax, (float)rnd.NextDouble())),
                StartSize = Math.Max(0, Lerp(n.StartSizeMin, n.StartSizeMax, (float)rnd.NextDouble())),
                EndSize = Math.Max(0, Lerp(n.EndSizeMin, n.EndSizeMax, (float)rnd.NextDouble())),
                Position = origin + jitter,
                Velocity = rot.Rotate(baseVel),
                Acceleration = rot.Rotate(accel)
            };
            _particles.Add(p);
        }

        private static Vec3 RandomRange(Vec3 min, Vec3 max, Random rnd) => new(
            Lerp(min.X, max.X, (float)rnd.NextDouble()),
            Lerp(min.Y, max.Y, (float)rnd.NextDouble()),
            Lerp(min.Z, max.Z, (float)rnd.NextDouble()));

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    private struct Particle
    {
        public float Age;
        public float Life;
        public float StartSize;
        public float EndSize;
        public Vec3 Position;
        public Vec3 Velocity;
        public Vec3 Acceleration;
    }

    private static Col ColorLerp(Col a, Col b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Col(
            (float)(a.R + (b.R - a.R) * t),
            (float)(a.G + (b.G - a.G) * t),
            (float)(a.B + (b.B - a.B) * t),
            (float)(a.A + (b.A - a.A) * t));
    }
}
