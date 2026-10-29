using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

/// <summary>
/// Limits enforced by the retail GRAB level format / editor. Keeping these in one
/// place lets the compiler and the exporter reject levels the game would truncate,
/// disconnect or refuse to load.
/// </summary>
public static class GrabLimits
{
    /// <summary>Retail Code Block program limit.</summary>
    public const int MaxInstructions = 150;

    /// <summary>Maximum number of connections on one Code Block (v0.76.1+).</summary>
    public const int MaxConnections = 10;

    /// <summary>Maximum targets one Trigger can connect to.</summary>
    public const int MaxTriggerTargets = 17;

    /// <summary>
    /// Connection names in retail levels are four characters (blk1, trg1, lght...).
    /// Longer names can be truncated by the game, which breaks every register that
    /// uses the name.
    /// </summary>
    public const int MaxConnectionNameLength = 4;

    /// <summary>Signs stop appending after this many characters.</summary>
    public const int MaxSignCharacters = 150;

    /// <summary>Retail editor limit for transparent/additive blocks.</summary>
    public const int MaxTransparentBlocks = 20;

    /// <summary>Retail editor limit for physics groups.</summary>
    public const int MaxPhysicsGroups = 100;

    /// <summary>Complexity allowed inside one physics group.</summary>
    public const int MaxPhysicsGroupComplexity = 100;

    /// <summary>Animation frames per object.</summary>
    public const int MaxAnimationFrames = 1000;

    /// <summary>Named start/spawn points per level.</summary>
    public const int MaxSpawnPoints = 10;

    /// <summary>Lights that may overlap before the newest one is disabled.</summary>
    public const int MaxOverlappingLights = 3;

    /// <summary>
    /// Checks the parts of a level the retail editor / importer will truncate.
    /// Code Block instruction and connection limits are reported by the compiler
    /// (they are per script); these are the level-wide rules.
    /// </summary>
    public static List<(LevelNode? Node, DiagnosticSeverity Severity, string Message)> ValidateLevel(GrabLevel level)
    {
        var issues = new List<(LevelNode?, DiagnosticSeverity, string)>();
        var nodes = level.AllNodes().ToList();

        int transparent = nodes.Count(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && n.IsTransparent);
        if (transparent > MaxTransparentBlocks)
        {
            issues.Add((null, DiagnosticSeverity.Warning,
                $"GRAB shows at most {MaxTransparentBlocks} transparent blocks at once; this level has {transparent}. Some will not render in game."));
        }

        int physicsGroups = nodes.Count(n => n.Kind == NodeKind.Group && (n.PhysicsObject || n.LocalPhysicsObject));
        if (physicsGroups > MaxPhysicsGroups)
        {
            issues.Add((null, DiagnosticSeverity.Warning,
                $"GRAB allows {MaxPhysicsGroups} physics groups; this level has {physicsGroups}."));
        }

        int spawns = nodes.Count(n => n.Kind == NodeKind.Start);
        if (spawns > MaxSpawnPoints)
        {
            issues.Add((null, DiagnosticSeverity.Warning,
                $"GRAB allows {MaxSpawnPoints} named spawn points; this level has {spawns}."));
        }

        foreach (var n in nodes)
        {
            if (n.Kind == NodeKind.Sign && n.Text.Length > MaxSignCharacters)
            {
                issues.Add((n, DiagnosticSeverity.Error,
                    $"The sign is {n.Text.Length} characters; GRAB stops at {MaxSignCharacters}."));
            }

            int frames = n.Animations.Sum(x => x.Frames.Count);
            if (frames > MaxAnimationFrames)
            {
                issues.Add((n, DiagnosticSeverity.Error,
                    $"'{n.DisplayName}' has {frames} animation frames; GRAB allows {MaxAnimationFrames}."));
            }

            if (n.Kind == NodeKind.GASM && n.Compiled != null)
            {
                int instructions = n.Compiled.Program.Instructions.Count;
                if (instructions > MaxInstructions)
                {
                    issues.Add((n, DiagnosticSeverity.Error,
                        $"'{n.DisplayName}' compiles to {instructions} GASM instructions; GRAB allows {MaxInstructions}."));
                }
                if (n.Compiled.Connections.Count > MaxConnections)
                {
                    issues.Add((n, DiagnosticSeverity.Error,
                        $"'{n.DisplayName}' has {n.Compiled.Connections.Count} object connections; GRAB allows {MaxConnections}."));
                }
            }
        }

        // The game disables overlapping lights past the third one.
        foreach (var light in nodes.Where(n => n.Kind == NodeKind.Light))
        {
            var p = light.WorldPosition;
            int overlapping = 0;
            foreach (var other in nodes.Where(n => n.Kind == NodeKind.Light))
            {
                if (ReferenceEquals(light, other)) continue;
                var q = other.WorldPosition;
                double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                if (dx * dx + dy * dy + dz * dz < 2.25) overlapping++;
            }
            if (overlapping > MaxOverlappingLights)
            {
                issues.Add((light, DiagnosticSeverity.Warning,
                    $"{overlapping + 1} lights overlap here; GRAB disables the newest past {MaxOverlappingLights}."));
            }
        }

        return issues;
    }
}
