using System;
using System.Collections.Generic;
using System.Linq;
using GrabEngine.Rendering;

namespace GrabEngine.Core;

public sealed class TerrainBiome
{
    public string Name { get; set; } = "Grass";
    public int TopMaterial { get; set; } = 8;
    public Col TopColor { get; set; } = new(0.33f, 0.55f, 0.28f, 1);
    public int BodyMaterial { get; set; } = 8;
    public Col BodyColor { get; set; } = new(0.38f, 0.36f, 0.33f, 1);
    public int CliffMaterial { get; set; } = 8;
    public Col CliffColor { get; set; } = new(0.30f, 0.29f, 0.28f, 1);
    /// <summary>Normalised height band (0 = base, 1 = peak).</summary>
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; } = 1f;
    public float MaxSlope { get; set; } = 99f;
    public bool IsSnow { get; set; }

    public TerrainBiome Clone() => (TerrainBiome)MemberwiseClone();

    public static List<TerrainBiome> Defaults() => new()
    {
        new TerrainBiome
        {
            Name = "Sand", TopMaterial = 8, TopColor = new(0.76f, 0.68f, 0.45f, 1),
            BodyMaterial = 8, BodyColor = new(0.55f, 0.48f, 0.35f, 1),
            CliffMaterial = 8, CliffColor = new(0.45f, 0.4f, 0.3f, 1),
            MinHeight = 0f, MaxHeight = 0.13f, MaxSlope = 1.4f
        },
        new TerrainBiome
        {
            Name = "Grass", TopMaterial = 8, TopColor = new(0.33f, 0.55f, 0.28f, 1),
            BodyMaterial = 8, BodyColor = new(0.38f, 0.36f, 0.33f, 1),
            CliffMaterial = 8, CliffColor = new(0.30f, 0.29f, 0.28f, 1),
            MinHeight = 0.08f, MaxHeight = 0.68f, MaxSlope = 1.05f
        },
        new TerrainBiome
        {
            Name = "Rock", TopMaterial = 8, TopColor = new(0.34f, 0.33f, 0.32f, 1),
            BodyMaterial = 8, BodyColor = new(0.30f, 0.29f, 0.28f, 1),
            CliffMaterial = 8, CliffColor = new(0.25f, 0.24f, 0.23f, 1),
            MinHeight = 0f, MaxHeight = 1f, MaxSlope = 99f
        },
        new TerrainBiome
        {
            Name = "Snow", TopMaterial = 10, TopColor = new(0.88f, 0.92f, 0.96f, 1),
            BodyMaterial = 10, BodyColor = new(0.82f, 0.86f, 0.9f, 1),
            CliffMaterial = 8, CliffColor = new(0.55f, 0.57f, 0.6f, 1),
            MinHeight = 0.7f, MaxHeight = 1f, MaxSlope = 99f, IsSnow = true
        }
    };
}

public sealed class TerrainSettings
{
    public int Width { get; set; } = 32;
    public int Depth { get; set; } = 32;
    public float CellSize { get; set; } = 1f;
    public float BaseHeight { get; set; }
    public float HeightScale { get; set; } = 7f;
    public float NoiseScale { get; set; } = 0.075f;
    public int Octaves { get; set; } = 4;
    public int Seed { get; set; } = 2026;
    public float Roughness { get; set; } = 1.15f;
    public bool Caves { get; set; }
    public float CaveThreshold { get; set; } = 0.63f;
    public int CaveOctaves { get; set; } = 2;
    public int ChunkSize { get; set; } = 8;
    public bool ReplaceExisting { get; set; } = true;

    public int TopMaterial { get; set; } = 8;
    public int BodyMaterial { get; set; } = 8;
    public int CliffMaterial { get; set; } = 8;
    public int SnowMaterial { get; set; } = 10;
    public Col TopColor { get; set; } = new(0.33f, 0.55f, 0.28f, 1);
    public Col BodyColor { get; set; } = new(0.38f, 0.36f, 0.33f, 1);
    public Col CliffColor { get; set; } = new(0.30f, 0.29f, 0.28f, 1);
    public Col SnowColor { get; set; } = new(0.88f, 0.92f, 0.96f, 1);
    public float SnowLine { get; set; } = 0.72f;

    // ---------------- biome / auto material ----------------
    public bool AutoMaterials { get; set; }
    public List<TerrainBiome> Biomes { get; set; } = TerrainBiome.Defaults();

    // ---------------- water ----------------
    public bool Water { get; set; }
    public float WaterLevel { get; set; } = 0.8f;
    public Col WaterColor { get; set; } = new(0.18f, 0.48f, 0.7f, 0.55f);
    public int ShoreMaterial { get; set; } = 8;
    public Col ShoreColor { get; set; } = new(0.76f, 0.68f, 0.45f, 1);

    // ---------------- path / road ----------------
    public List<Vec3> PathPoints { get; set; } = new();
    public float PathWidth { get; set; } = 3f;
    public int PathMaterial { get; set; } = 8;
    public Col PathColor { get; set; } = new(0.52f, 0.47f, 0.4f, 1);
    public bool PathFlatten { get; set; } = true;
    public float PathSmooth { get; set; } = 0.5f;

    public TerrainSettings Clone()
    {
        var c = (TerrainSettings)MemberwiseClone();
        c.Biomes = Biomes.Select(b => b.Clone()).ToList();
        c.PathPoints = new List<Vec3>(PathPoints);
        return c;
    }
}

public sealed class TerrainResult
{
    public int Blocks { get; init; }
    public int Groups { get; init; }
    public int Columns { get; init; }
    public int Caves { get; init; }
    public int WaterBlocks { get; init; }
    public int PathBlocks { get; init; }
    public GrabLevel Level { get; init; } = null!;
    public List<LevelNode> TerrainGroups { get; init; } = new();
    public string Summary => $"{Columns} terrain columns  •  {Blocks} blocks  •  {Groups} chunk group(s)" +
        (Caves > 0 ? $"  •  {Caves} cave opening(s)" : "") +
        (WaterBlocks > 0 ? $"  •  water at {WaterBlocks:0.#} m" : "") +
        (PathBlocks > 0 ? $"  •  {PathBlocks} path cell(s)" : "");
}

/// <summary>
/// Converts a heightmap into ordinary GRAB blocks. Each solid vertical run becomes one
/// scaled cube, so a 32×32 terrain is a few hundred objects rather than thousands of
/// individual cells. Caves, biomes, water, paths and erosion are all handled here so
/// the retail format never sees anything except normal blocks.
/// </summary>
public static class TerrainBuilder
{
    public static float[,] GenerateHeights(TerrainSettings settings, float[,]? existing = null)
    {
        int w = Math.Clamp(settings.Width, 2, 128);
        int d = Math.Clamp(settings.Depth, 2, 128);
        var heights = existing != null && existing.GetLength(0) == w && existing.GetLength(1) == d
            ? (float[,])existing.Clone()
            : new float[w, d];
        int seed = settings.Seed;
        double ox = seed * 0.173 + 31.7;
        double oz = seed * 0.271 + 9.3;
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < d; z++)
            {
                double nx = (x + ox) * settings.NoiseScale;
                double nz = (z + oz) * settings.NoiseScale;
                double n = ProceduralNoise.Fbm2(nx, nz, Math.Clamp(settings.Octaves, 1, 8));
                // Shape the noise into readable hills: a broad valley plus a few plateaus.
                double shaped = Math.Pow(Math.Clamp(n, 0, 1), Math.Clamp(settings.Roughness, 0.5f, 2.5f));
                double plateau = Math.Floor(shaped * 7) / 7;
                heights[x, z] = (float)Math.Clamp(shaped * 0.72 + plateau * 0.28, 0, 1);
            }
        }
        if (existing == null)
        {
            // Keep the outer ring low enough to stand on and to see the terrain silhouette.
            for (int x = 0; x < w; x++)
            {
                heights[x, 0] *= 0.45f;
                heights[x, d - 1] *= 0.45f;
            }
            for (int z = 0; z < d; z++)
            {
                heights[0, z] *= 0.45f;
                heights[w - 1, z] *= 0.45f;
            }
        }
        return heights;
    }

    public static TerrainResult Build(GrabLevel level, TerrainSettings settings, float[,]? heightmap = null)
        => Build(level, settings, heightmap, null, null);

    public static TerrainResult Build(GrabLevel level, TerrainSettings settings, float[,]? heightmap, int[,]? biomeMap)
        => Build(level, settings, heightmap, biomeMap, null);

    /// <summary>Builds terrain from a heightmap. A painted path mask can replace the old
    /// typed start/end coordinates, so roads are drawn directly on the heightmap.</summary>
    public static TerrainResult Build(GrabLevel level, TerrainSettings settings, float[,]? heightmap,
        int[,]? biomeMap, bool[,]? pathMaskOverride)
    {
        int w = Math.Clamp(settings.Width, 2, 128);
        int d = Math.Clamp(settings.Depth, 2, 128);
        float cell = Math.Clamp(settings.CellSize, 0.25f, 4f);
        settings.Width = w;
        settings.Depth = d;
        settings.CellSize = cell;
        var heights = heightmap != null && heightmap.GetLength(0) == w && heightmap.GetLength(1) == d
            ? (float[,])heightmap.Clone()
            : GenerateHeights(settings);

        // The path is carved into a copy so the editable heightmap keeps its sculpted
        // contour; rebuilding without the path restores the original terrain. A painted
        // mask is used when present, otherwise the legacy coordinate path is converted.
        var pathMask = new bool[w, d];
        int pathCells = 0;
        if (pathMaskOverride != null && pathMaskOverride.GetLength(0) == w && pathMaskOverride.GetLength(1) == d)
        {
            for (int x = 0; x < w; x++)
                for (int z = 0; z < d; z++)
                    if (pathMaskOverride[x, z]) { pathMask[x, z] = true; pathCells++; }
        }
        else
        {
            pathCells = BuildPathMask(settings, heights, pathMask, cell);
        }
        if (pathCells > 0) CarvePath(settings, heights, pathMask, cell);

        if (settings.ReplaceExisting)
        {
            foreach (var old in level.Nodes.Where(n => n.EditorLabel is "terrain" or "terrain-water").ToList())
                level.RemoveNode(old);
        }

        var groups = new Dictionary<(int X, int Z), LevelNode>();
        int blocks = 0, caves = 0, columns = 0;
        int chunk = Math.Clamp(settings.ChunkSize, 2, 32);
        int seed = settings.Seed;
        double snowHeight = settings.BaseHeight + settings.HeightScale * Math.Clamp(settings.SnowLine, 0, 1);
        int topBand = Math.Max(1, (int)Math.Round(1.1 / cell));
        var biomes = settings.Biomes.Count > 0 ? settings.Biomes : TerrainBiome.Defaults();
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < d; z++)
            {
                float h = Math.Clamp(heights[x, z], 0, 1);
                int height = (int)Math.Round((settings.BaseHeight + h * settings.HeightScale) / cell);
                if (height < 1) continue;
                columns++;
                int cx = x / chunk, cz = z / chunk;
                if (!groups.TryGetValue((cx, cz), out var group))
                {
                    group = new LevelNode
                    {
                        Kind = NodeKind.Group,
                        Name = "Terrain " + (cx * chunk) + "," + (cz * chunk),
                        EditorLabel = "terrain",
                        Position = Vec3.Zero,
                        Scale = Vec3.One,
                        LayerId = level.ActiveLayerId,
                        Generated = true
                    };
                    level.AddNode(group);
                    groups[(cx, cz)] = group;
                }

                float dx = x > 0 ? Math.Abs(h - heights[x - 1, z]) : 0;
                float dz = z > 0 ? Math.Abs(h - heights[x, z - 1]) : 0;
                double slope = Math.Max(dx, dz) * settings.HeightScale / Math.Max(cell, 0.01f);
                bool cliff = slope > 1.05;
                bool snow = settings.BaseHeight + height * cell >= snowHeight;
                int topMaterial = snow ? settings.SnowMaterial : settings.TopMaterial;
                Col topColor = snow ? settings.SnowColor : settings.TopColor;
                int bodyMaterial = cliff ? settings.CliffMaterial : settings.BodyMaterial;
                Col bodyColor = cliff ? settings.CliffColor : settings.BodyColor;

                int biomeIndex = biomeMap != null && biomeMap.GetLength(0) == w && biomeMap.GetLength(1) == d
                    ? biomeMap[x, z] : -1;
                if (settings.AutoMaterials || biomeIndex >= 0)
                {
                    var biome = BiomeFor(settings, biomes, biomeIndex, h, slope, snow);
                    if (biome != null)
                    {
                        topMaterial = biome.TopMaterial;
                        topColor = biome.TopColor;
                        bodyMaterial = cliff ? biome.CliffMaterial : biome.BodyMaterial;
                        bodyColor = cliff ? biome.CliffColor : biome.BodyColor;
                        snow = biome.IsSnow;
                    }
                }

                // Shoreline: the top block becomes sand within a small band above water.
                bool shore = settings.Water && settings.WaterLevel > 0 &&
                             Math.Abs(settings.BaseHeight + height * cell - settings.WaterLevel) < 0.75f;
                if (shore && !cliff)
                {
                    topMaterial = settings.ShoreMaterial;
                    topColor = settings.ShoreColor;
                    snow = false;
                }

                if (pathMask[x, z])
                {
                    topMaterial = settings.PathMaterial;
                    topColor = settings.PathColor;
                    bodyMaterial = settings.BodyMaterial;
                    bodyColor = settings.BodyColor;
                }

                var runs = new List<(int Start, int End)>();
                if (!settings.Caves)
                {
                    runs.Add((0, height));
                }
                else
                {
                    int runStart = -1;
                    for (int y = 0; y < height; y++)
                    {
                        double n = ProceduralNoise.Fbm3(
                            (x + seed * 0.19) * 0.11,
                            (y + seed * 0.07) * 0.13,
                            (z + seed * 0.29) * 0.11,
                            Math.Clamp(settings.CaveOctaves, 1, 5));
                        bool air = y > 0 && n > settings.CaveThreshold;
                        if (air)
                        {
                            if (runStart >= 0) { runs.Add((runStart, y - 1)); runStart = -1; }
                            if (y < height - 1) caves++;
                        }
                        else if (runStart < 0) runStart = y;
                    }
                    if (runStart >= 0) runs.Add((runStart, height - 1));
                    // Never erase the very top of a column entirely; otherwise the preview
                    // can look like missing terrain rather than a cave entrance.
                    if (runs.Count == 2 && runs[^1].End < height - 1)
                        runs[^1] = (runs[^1].Start, height - 1);
                }

                double worldX = (x - (w - 1) * 0.5 + 0.5) * cell;
                double worldZ = (z - (d - 1) * 0.5 + 0.5) * cell;
                foreach (var run in runs)
                {
                    int start = run.Start;
                    int end = run.End;
                    if (end < start) continue;
                    bool isTop = end >= height - 1;
                    if (isTop && end - start + 1 > topBand && !snow)
                    {
                        int split = end - topBand;
                        AddBlock(level, group, x, z, start, split, cell, bodyMaterial, bodyColor, worldX, worldZ);
                        AddBlock(level, group, x, z, split + 1, end, cell, topMaterial, topColor, worldX, worldZ);
                        blocks += 2;
                    }
                    else if (isTop && end - start + 1 > topBand && snow)
                    {
                        int split = Math.Max(start, end - topBand);
                        AddBlock(level, group, x, z, start, split, cell, bodyMaterial, bodyColor, worldX, worldZ);
                        AddBlock(level, group, x, z, split + (split < end ? 1 : 0), end, cell, topMaterial, topColor, worldX, worldZ);
                        blocks += 2;
                    }
                    else
                    {
                        AddBlock(level, group, x, z, start, end, cell, isTop ? topMaterial : bodyMaterial,
                            isTop ? topColor : bodyColor, worldX, worldZ);
                        blocks++;
                    }
                }
            }
        }

        double waterBlocks = 0;
        if (settings.Water && settings.WaterLevel > 0)
        {
            // One passable tinted slab keeps the water cheap and lets the player swim
            // through it; the retail format has no water material.
            float width = w * cell, depth = d * cell;
            var water = new LevelNode
            {
                Kind = NodeKind.Static,
                Name = "Water",
                Shape = ShapeInfo.Cube,
                Material = 8,
                Color1 = settings.WaterColor,
                IsTransparent = true,
                IsPassable = true,
                IsGrabbable = false,
                IsGrapplable = false,
                EditorLabel = "terrain-water",
                Generated = true,
                Position = new Vec3(0, settings.WaterLevel - 0.15f, 0),
                Scale = new Vec3(width, 0.3f, depth),
                LayerId = level.ActiveLayerId
            };
            level.AddNode(water);
            waterBlocks = settings.WaterLevel;
        }

        level.EnsureNames();
        level.IsDirty = true;
        return new TerrainResult
        {
            Blocks = blocks,
            Groups = groups.Count,
            Columns = columns,
            Caves = caves,
            WaterBlocks = (int)Math.Round(waterBlocks),
            PathBlocks = pathCells,
            Level = level,
            TerrainGroups = groups.Values.ToList()
        };
    }

    /// <summary>Number of cells covered by a painted road mask.</summary>
    public static int CountPathCells(bool[,]? mask)
    {
        if (mask == null) return 0;
        int count = 0;
        foreach (var cell in mask) if (cell) count++;
        return count;
    }

    private static TerrainBiome? BiomeFor(TerrainSettings settings, List<TerrainBiome> biomes, int index, float height, double slope, bool snow)
    {
        if (index >= 0 && index < biomes.Count) return biomes[index];
        if (snow) return biomes.FirstOrDefault(b => b.IsSnow) ?? biomes.LastOrDefault();
        // Explicit painting wins; otherwise first matching rule in list order.
        foreach (var b in biomes)
        {
            if (b.IsSnow) continue;
            if (height >= b.MinHeight && height <= b.MaxHeight && slope <= b.MaxSlope) return b;
        }
        return biomes.FirstOrDefault(b => !b.IsSnow);
    }

    /// <summary>Marks cells covered by the path polyline and returns the count.</summary>
    private static int BuildPathMask(TerrainSettings s, float[,] heights, bool[,] mask, float cell)
    {
        if (s.PathPoints.Count < 2) return 0;
        int w = heights.GetLength(0), d = heights.GetLength(1);
        float halfWidth = MathF.Max(0.25f, s.PathWidth * 0.5f);
        int count = 0;
        for (int i = 0; i < s.PathPoints.Count - 1; i++)
        {
            var a = s.PathPoints[i];
            var b = s.PathPoints[i + 1];
            float dist = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            int steps = Math.Clamp((int)MathF.Ceiling(dist / 0.25f), 1, 4000);
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                float px = a.X + (b.X - a.X) * t;
                float pz = a.Z + (b.Z - a.Z) * t;
                int cx = Math.Clamp((int)MathF.Round(px / cell + (w - 1) * 0.5f), 0, w - 1);
                int cz = Math.Clamp((int)MathF.Round(pz / cell + (d - 1) * 0.5f), 0, d - 1);
                int radius = Math.Max(1, (int)MathF.Ceiling(halfWidth / cell));
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(w - 1, cx + radius); x++)
                    for (int z = Math.Max(0, cz - radius); z <= Math.Min(d - 1, cz + radius); z++)
                    {
                        float dx = (x - cx) * cell, dz = (z - cz) * cell;
                        if (dx * dx + dz * dz > halfWidth * halfWidth) continue;
                        if (!mask[x, z]) { mask[x, z] = true; count++; }
                    }
            }
        }
        return count;
    }

    /// <summary>Flattens the path cross-section and blends its edges into the terrain.</summary>
    private static void CarvePath(TerrainSettings s, float[,] heights, bool[,] mask, float cell)
    {
        if (!s.PathFlatten) return;
        int w = heights.GetLength(0), d = heights.GetLength(1);
        var copy = (float[,])heights.Clone();
        // Pass 1: smooth along the path so a step does not become a wall.
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (float[,])copy.Clone();
            for (int x = 0; x < w; x++)
                for (int z = 0; z < d; z++)
                {
                    if (!mask[x, z]) continue;
                    float sum = 0;
                    int count = 0;
                    for (int ax = Math.Max(0, x - 1); ax <= Math.Min(w - 1, x + 1); ax++)
                        for (int az = Math.Max(0, z - 1); az <= Math.Min(d - 1, z + 1); az++)
                        {
                            sum += copy[ax, az];
                            count++;
                        }
                    next[x, z] = sum / Math.Max(1, count);
                }
            copy = next;
        }
        float strength = Math.Clamp(s.PathSmooth, 0.05f, 1f);
        for (int x = 0; x < w; x++)
            for (int z = 0; z < d; z++)
            {
                if (!mask[x, z]) continue;
                heights[x, z] = heights[x, z] + (copy[x, z] - heights[x, z]) * strength;
            }
    }

    /// <summary>Thermal erosion: material above the talus angle slides downhill.</summary>
    public static void Erode(float[,] heights, int iterations = 12, float strength = 0.18f)
    {
        int w = heights.GetLength(0), d = heights.GetLength(1);
        var delta = new float[w, d];
        for (int it = 0; it < Math.Clamp(iterations, 1, 200); it++)
        {
            Array.Clear(delta, 0, delta.Length);
            for (int x = 1; x < w - 1; x++)
                for (int z = 1; z < d - 1; z++)
                {
                    float h = heights[x, z];
                    int lowestX = x, lowestZ = z;
                    float lowest = h;
                    if (heights[x - 1, z] < lowest) { lowest = heights[x - 1, z]; lowestX = x - 1; lowestZ = z; }
                    if (heights[x + 1, z] < lowest) { lowest = heights[x + 1, z]; lowestX = x + 1; lowestZ = z; }
                    if (heights[x, z - 1] < lowest) { lowest = heights[x, z - 1]; lowestX = x; lowestZ = z - 1; }
                    if (heights[x, z + 1] < lowest) { lowest = heights[x, z + 1]; lowestX = x; lowestZ = z + 1; }
                    float diff = h - lowest;
                    if (diff <= 0.001f) continue;
                    float move = diff * Math.Clamp(strength, 0.01f, 0.5f) * 0.25f;
                    delta[x, z] -= move;
                    delta[lowestX, lowestZ] += move;
                }
            for (int x = 0; x < w; x++)
                for (int z = 0; z < d; z++)
                    heights[x, z] = Math.Clamp(heights[x, z] + delta[x, z], 0, 1);
        }
    }

    public static void Smooth(float[,] heights, int iterations = 4)
    {
        int w = heights.GetLength(0), d = heights.GetLength(1);
        for (int it = 0; it < Math.Clamp(iterations, 1, 50); it++)
        {
            var copy = (float[,])heights.Clone();
            for (int x = 0; x < w; x++)
                for (int z = 0; z < d; z++)
                {
                    float sum = 0;
                    int count = 0;
                    for (int ax = Math.Max(0, x - 1); ax <= Math.Min(w - 1, x + 1); ax++)
                        for (int az = Math.Max(0, z - 1); az <= Math.Min(d - 1, z + 1); az++)
                        {
                            sum += copy[ax, az];
                            count++;
                        }
                    heights[x, z] = sum / Math.Max(1, count);
                }
        }
    }

    /// <summary>Places prefab assets on the terrain surface, respecting slope and height limits.</summary>
    public static int ScatterPrefabs(GrabLevel level, TerrainSettings settings, float[,] heights,
        IReadOnlyList<string> prefabPaths, int count, float minSlope = 0f, float maxSlope = 0.9f,
        float minHeight = 0.05f, float maxHeight = 0.95f, int seed = 7)
    {
        if (prefabPaths.Count == 0 || heights == null) return 0;
        int w = heights.GetLength(0), d = heights.GetLength(1);
        float cell = Math.Max(0.1f, settings.CellSize);
        var rng = new Random(seed);
        int placed = 0;
        int attempts = 0;
        while (placed < Math.Clamp(count, 0, 500) && attempts++ < count * 30)
        {
            int x = rng.Next(1, Math.Max(2, w - 1));
            int z = rng.Next(1, Math.Max(2, d - 1));
            float h = heights[x, z];
            if (h < minHeight || h > maxHeight) continue;
            float hx = heights[Math.Min(w - 1, x + 1), z];
            float hz = heights[x, Math.Min(d - 1, z + 1)];
            float slope = Math.Max(Math.Abs(h - hx), Math.Abs(h - hz)) * settings.HeightScale / cell;
            if (slope < minSlope || slope > maxSlope) continue;
            var world = new Vec3((x - (w - 1) * 0.5f) * cell, settings.BaseHeight + h * settings.HeightScale, (z - (d - 1) * 0.5f) * cell);
            var path = prefabPaths[rng.Next(prefabPaths.Count)];
            try
            {
                PrefabLibrary.Instantiate(path, level, world, Quat.FromAxisAngle(Vec3.Up, rng.Next(0, 360)), null);
                placed++;
            }
            catch { }
        }
        if (placed > 0) level.IsDirty = true;
        return placed;
    }

    private static void AddBlock(GrabLevel level, LevelNode parent, int x, int z, int start, int end,
        float cell, int material, Col color, double worldX, double worldZ)
    {
        if (end < start) return;
        double height = (end - start + 1) * cell;
        var node = new LevelNode
        {
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = material,
            Color1 = color,
            Position = new Vec3((float)worldX, (float)(start * cell + height * 0.5), (float)worldZ),
            Scale = new Vec3(cell, (float)height, cell),
            LayerId = parent.LayerId
        };
        level.AddNode(node, parent);
    }
}
