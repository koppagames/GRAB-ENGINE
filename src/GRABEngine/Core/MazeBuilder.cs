using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum MazeStyle
{
    /// <summary>Classic perfect maze: exactly one route between any two cells.</summary>
    Perfect,
    /// <summary>Dead ends are opened up, so the maze has loops and short cuts.</summary>
    Braided,
    /// <summary>A perfect maze with carved out chambers and halls.</summary>
    Rooms,
    /// <summary>Lots of loops: reads as an open plaza of pillars rather than corridors.</summary>
    Open,
    /// <summary>
    /// One unbroken corridor that winds all the way to the centre. No branches and no
    /// shortcuts, so the only way to the finish is the whole route — the hardest layout.
    /// </summary>
    Spiral
}

public enum MazeShape
{
    /// <summary>Square grid of cells, the classic corridor maze.</summary>
    Rectangle,
    /// <summary>Concentric rings joined by radial corridors. Finish sits in the hub.</summary>
    Circle
}

public enum MazeFinishPlacement
{
    /// <summary>Finish in the far cell from the start.</summary>
    Opposite,
    /// <summary>Finish in the middle of the maze, boxed in by the route.</summary>
    Centre
}

public sealed class MazeSettings
{
    public int Columns { get; set; } = 14;
    public int Rows { get; set; } = 14;
    /// <summary>Number of concentric rings for a circle maze.</summary>
    public int Rings { get; set; } = 5;
    /// <summary>Cells around each ring for a circle maze.</summary>
    public int Sectors { get; set; } = 20;
    public float Cell { get; set; } = 3f;
    public float WallHeight { get; set; } = 3f;
    public float WallThickness { get; set; } = 0.4f;
    /// <summary>0 = very twisty, 0.9 = long straight corridors.</summary>
    public float StraightBias { get; set; } = 0.35f;
    public MazeStyle Style { get; set; } = MazeStyle.Perfect;
    public MazeShape Shape { get; set; } = MazeShape.Rectangle;
    public MazeFinishPlacement FinishPlacement { get; set; } = MazeFinishPlacement.Opposite;
    /// <summary>Percentage of dead ends / walls opened up by the Braided and Open styles.</summary>
    public int LoopPercent { get; set; } = 15;
    public int Rooms { get; set; } = 3;
    public int RoomSize { get; set; } = 3;
    public int Seed { get; set; } = 1;
    /// <summary>Cell the Start block goes in. -1 picks the generator's own cell.</summary>
    public int StartCell { get; set; } = -1;
    /// <summary>Cell the Finish block goes in. -1 picks the generator's own cell.</summary>
    public int FinishCell { get; set; } = -1;
    public int WallMaterial { get; set; } = 8;
    public Col WallColor { get; set; } = new(0.72f, 0.72f, 0.72f, 1f);
    public bool Floor { get; set; } = true;
    public float FloorThickness { get; set; } = 0.4f;
    public int FloorMaterial { get; set; } = 4;
    public Col FloorColor { get; set; } = new(0.58f, 0.42f, 0.28f, 1f);
    public bool Ceiling { get; set; }
    public int CeilingMaterial { get; set; } = 8;
    public Col CeilingColor { get; set; } = new(0.72f, 0.72f, 0.72f, 1f);
    public bool Lights { get; set; }
    public int LightSpacing { get; set; } = 3;
    public bool Openings { get; set; } = true;
    public bool PlaceStartFinish { get; set; } = true;
    public Vec3 Origin { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;

    public MazeSettings Clone() => (MazeSettings)MemberwiseClone();
}

/// <summary>
/// A generated maze. The wall grids (rectangles) and the cell graph (both layouts) are
/// kept so the editor can draw a top-down preview, drag the endpoints and show the
/// solution without re-running the generator.
/// </summary>
public sealed class MazePlan
{
    public MazeShape Shape { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }
    public float Cell { get; init; }
    public float WallHeight { get; init; }
    /// <summary>Radius of the centre chamber of a circle maze.</summary>
    public float HubRadius { get; init; }
    public int CellCount { get; init; }
    /// <summary>[x, z] — wall running along X at z boundary z, covering column x.</summary>
    public bool[,] AlongX { get; init; } = new bool[0, 0];
    /// <summary>[x, z] — wall running along Z at x boundary x, covering row z.</summary>
    public bool[,] AlongZ { get; init; } = new bool[0, 0];
    /// <summary>Cell adjacency used to solve the route. Index = cell id.</summary>
    public List<int>[] Adjacency { get; init; } = Array.Empty<List<int>>();
    public List<(float X, float Z)> Centres { get; } = new();
    /// <summary>Straight wall segments in maze-local space, for the 2D preview.</summary>
    public List<(float X0, float Z0, float X1, float Z1)> PreviewWalls { get; } = new();
    /// <summary>Cell ids of the shortest route from start to finish.</summary>
    public List<int> Solution { get; } = new();
    public List<LevelNode> Blocks { get; } = new();
    public List<LevelNode> Lights { get; } = new();
    public LevelNode? Start { get; init; }
    public LevelNode? Finish { get; init; }
    public int StartCell { get; init; }
    public int FinishCell { get; init; }
    public int WallRuns { get; init; }

    public float Width => Shape == MazeShape.Circle ? (HubRadius + Columns * Cell) * 2 : Columns * Cell;
    public float Depth => Shape == MazeShape.Circle ? Width : Rows * Cell;
    public float MinX => Shape == MazeShape.Circle ? -(HubRadius + Columns * Cell) : 0;
    public float MaxX => Shape == MazeShape.Circle ? HubRadius + Columns * Cell : Width;
    public float MinZ => Shape == MazeShape.Circle ? -(HubRadius + Columns * Cell) : 0;
    public float MaxZ => Shape == MazeShape.Circle ? HubRadius + Columns * Cell : Depth;

    /// <summary>Local-space centre of a cell.</summary>
    public (float X, float Z) Centre(int cell) =>
        cell >= 0 && cell < Centres.Count ? Centres[cell] : (0f, 0f);

    /// <summary>
    /// Cell under a local-space point, or -1 when the point is outside the maze. Used by
    /// the preview so the start/finish markers can be dragged straight onto a cell.
    /// </summary>
    public int CellAt(float x, float z)
    {
        if (Shape == MazeShape.Circle)
        {
            float radius = MathF.Sqrt(x * x + z * z);
            if (radius < HubRadius) return Columns * Rows;
            float outer = HubRadius + Columns * Cell;
            if (radius > outer) return -1;
            int ring = Math.Clamp((int)((radius - HubRadius) / MathF.Max(0.001f, Cell)), 0, Columns - 1);
            float angle = MathF.Atan2(z, x);
            if (angle < 0) angle += MathF.PI * 2;
            int sector = Math.Clamp((int)(angle / (MathF.PI * 2) * Rows), 0, Rows - 1);
            return ring * Rows + sector;
        }

        if (x < 0 || z < 0 || x >= Width || z >= Depth) return -1;
        int cx = Math.Clamp((int)(x / MathF.Max(0.001f, Cell)), 0, Columns - 1);
        int cz = Math.Clamp((int)(z / MathF.Max(0.001f, Cell)), 0, Rows - 1);
        return cz * Columns + cx;
    }

    public int ObjectCount => Blocks.Count + Lights.Count + (Start != null ? 1 : 0) + (Finish != null ? 1 : 0);

    public string Summary =>
        $"{(Shape == MazeShape.Circle ? "Circle" : "Rectangle")} · " +
        (Shape == MazeShape.Circle
            ? $"{Columns} ring(s) × {Rows} sector(s)"
            : $"{Columns}×{Rows} cells") +
        $" · {Width:0.#}×{Depth:0.#} m · {Blocks.Count} block(s)" +
        (Solution.Count > 1 ? $" · route {Solution.Count} cells" : "") +
        (Lights.Count > 0 ? $" · {Lights.Count} light(s)" : "") +
        (Start != null || Finish != null ? " · start & finish" : "");
}

/// <summary>
/// Builds a maze out of ordinary GRAB blocks. Rectangle walls are emitted as merged
/// runs rather than one block per cell boundary, so a 14×14 maze is a few hundred
/// objects instead of thousands. Circle mazes use straight chords along the ring
/// walls, which is how a block-built round maze has to be made.
/// </summary>
public static class MazeBuilder
{
    public const int MaxCells = 96;

    public static MazePlan Build(MazeSettings settings) =>
        settings.Shape == MazeShape.Circle ? BuildCircle(settings) : BuildRectangle(settings);

    // ------------------------------------------------------------------ rectangle

    private static MazePlan BuildRectangle(MazeSettings settings)
    {
        int cols = Math.Clamp(settings.Columns, 2, MaxCells);
        int rows = Math.Clamp(settings.Rows, 2, MaxCells);
        float cell = MathF.Max(1f, settings.Cell);
        float height = MathF.Max(0.5f, settings.WallHeight);
        float thickness = Math.Clamp(settings.WallThickness, 0.1f, MathF.Min(cell * 0.9f, 3f));
        var rng = new Random(settings.Seed == 0 ? 1 : settings.Seed);
        int cells = cols * rows;

        // Start with every possible wall solid, then carve passages out of it.
        var alongX = new bool[cols + 1, rows + 1];
        var alongZ = new bool[cols + 1, rows + 1];
        for (int x = 0; x <= cols; x++)
            for (int z = 0; z <= rows; z++)
            {
                alongX[x, z] = true;
                alongZ[x, z] = true;
            }

        int centreCell = (rows / 2) * cols + cols / 2;
        int startCell = settings.StartCell >= 0 && settings.StartCell < cells ? settings.StartCell : 0;
        int finishCell;
        if (settings.FinishCell >= 0 && settings.FinishCell < cells) finishCell = settings.FinishCell;
        else if (settings.FinishPlacement == MazeFinishPlacement.Centre) finishCell = centreCell;
        else finishCell = cells - 1;
        if (finishCell == startCell) finishCell = (finishCell + 1) % cells;

        if (settings.Style == MazeStyle.Spiral)
        {
            // A spiral has two ends: the outer corner and the centre. The corridor is
            // always carved from the corner so every cell stays on it; the markers can
            // then be dragged anywhere along that one route.
            int spiralEnd = CarveSpiral(cols, rows, alongX, alongZ);
            if (settings.StartCell < 0) startCell = 0;
            if (settings.FinishCell < 0) finishCell = spiralEnd;
            if (finishCell == startCell) finishCell = spiralEnd;
        }
        else
        {
            Carve(cols, rows, settings, rng, alongX, alongZ);
            if (settings.Style == MazeStyle.Rooms)
                CarveRooms(cols, rows, settings, rng, alongX, alongZ);
            else if (settings.Style == MazeStyle.Braided)
                Braid(cols, rows, settings.LoopPercent, rng, alongX, alongZ);
            else if (settings.Style == MazeStyle.Open)
                OpenUp(cols, rows, settings.LoopPercent, rng, alongX, alongZ);
        }

        // An entrance on the edge next to Start and an exit next to Finish, so the maze
        // can be entered from outside wherever the endpoints were dragged.
        if (settings.Openings)
        {
            OpenEdge(cols, rows, startCell % cols, startCell / cols, alongX, alongZ);
            OpenEdge(cols, rows, finishCell % cols, finishCell / cols, alongX, alongZ);
        }

        Vec3 World(float x, float y, float z) => settings.Origin + settings.Rotation.Rotate(new Vec3(x, y, z));

        int runs = 0;
        var blocks = new List<LevelNode>();
        var preview = new List<(float, float, float, float)>();

        // Walls that run along X (one row of the maze at a time, merged into runs).
        for (int bz = 0; bz <= rows; bz++)
        {
            int ix = 0;
            while (ix < cols)
            {
                if (!alongX[ix, bz]) { ix++; continue; }
                int run = 1;
                while (ix + run < cols && alongX[ix + run, bz]) run++;
                float length = run * cell + thickness;
                var position = World((ix + run * 0.5f) * cell, height * 0.5f, bz * cell);
                blocks.Add(Wall(position, new Vec3(length, height, thickness), settings, settings.Rotation));
                preview.Add((ix * cell, bz * cell, (ix + run) * cell, bz * cell));
                runs++;
                ix += run;
            }
        }

        // Walls that run along Z.
        for (int bx = 0; bx <= cols; bx++)
        {
            int iz = 0;
            while (iz < rows)
            {
                if (!alongZ[bx, iz]) { iz++; continue; }
                int run = 1;
                while (iz + run < rows && alongZ[bx, iz + run]) run++;
                float length = run * cell + thickness;
                var position = World(bx * cell, height * 0.5f, (iz + run * 0.5f) * cell);
                blocks.Add(Wall(position, new Vec3(thickness, height, length), settings, settings.Rotation));
                preview.Add((bx * cell, iz * cell, bx * cell, (iz + run) * cell));
                runs++;
                iz += run;
            }
        }

        float slabX = cols * cell + thickness;
        float slabZ = rows * cell + thickness;

        if (settings.Floor)
        {
            float ft = Math.Clamp(settings.FloorThickness, 0.1f, 4f);
            blocks.Add(Block(World(cols * cell * 0.5f, -ft * 0.5f, rows * cell * 0.5f),
                new Vec3(slabX, ft, slabZ), settings.Rotation, settings.FloorMaterial, settings.FloorColor));
        }

        var adjacency = RectangleAdjacency(cols, rows, alongX, alongZ);
        var plan = new MazePlan
        {
            Shape = MazeShape.Rectangle,
            Columns = cols,
            Rows = rows,
            Cell = cell,
            WallHeight = height,
            CellCount = cells,
            AlongX = alongX,
            AlongZ = alongZ,
            Adjacency = adjacency,
            WallRuns = runs,
            StartCell = startCell,
            FinishCell = finishCell,
            Start = settings.PlaceStartFinish ? StartPoint(World, CellCentreRect, startCell, cols, cell) : null,
            Finish = settings.PlaceStartFinish ? FinishPoint(World, CellCentreRect, finishCell, cols, cell) : null
        };
        foreach (var segment in preview) plan.PreviewWalls.Add(segment);
        for (int i = 0; i < cells; i++) plan.Centres.Add(((i % cols + 0.5f) * cell, (i / cols + 0.5f) * cell));
        plan.Solution.AddRange(Solve(adjacency, startCell, finishCell));
        plan.Blocks.AddRange(blocks);

        if (settings.Ceiling)
        {
            float ct = Math.Clamp(settings.FloorThickness, 0.1f, 4f);
            plan.Blocks.Add(Block(World(cols * cell * 0.5f, height + ct * 0.5f, rows * cell * 0.5f),
                new Vec3(slabX, ct, slabZ), settings.Rotation, settings.CeilingMaterial, settings.CeilingColor));
        }

        if (settings.Lights)
        {
            int spacing = Math.Clamp(settings.LightSpacing, 1, 24);
            for (int ix = 0; ix < cols; ix += spacing)
                for (int iz = 0; iz < rows; iz += spacing)
                {
                    var node = new LevelNode
                    {
                        Kind = NodeKind.Light,
                        Shape = ShapeInfo.Light,
                        Position = World((ix + 0.5f) * cell, MathF.Max(0.6f, height - 0.35f), (iz + 0.5f) * cell),
                        LightColor = new Col(1f, 0.95f, 0.86f, 1f),
                        LightIntensity = 2.3f,
                        LightRange = MathF.Max(6f, cell * 2.1f),
                        EditorLabel = "maze light"
                    };
                    plan.Lights.Add(node);
                }
        }

        return plan;
    }

    // ------------------------------------------------------------------ circle

    private static MazePlan BuildCircle(MazeSettings settings)
    {
        int rings = Math.Clamp(settings.Rings, 2, 40);
        int sectors = Math.Clamp(settings.Sectors, 6, 72);
        float cell = MathF.Max(1f, settings.Cell);
        float height = MathF.Max(0.5f, settings.WallHeight);
        float thickness = Math.Clamp(settings.WallThickness, 0.1f, MathF.Min(cell * 0.9f, 3f));
        float hub = cell * 0.55f;
        float outer = hub + rings * cell;
        var rng = new Random(settings.Seed == 0 ? 1 : settings.Seed);
        int hubCell = rings * sectors;
        int cells = hubCell + 1;

        // Open flags: true = passage through that boundary.
        var hubOpen = new bool[sectors];
        var ringOpen = new bool[Math.Max(0, rings - 1), sectors];
        var radialOpen = new bool[rings, sectors];

        int startCell = settings.StartCell >= 0 && settings.StartCell < hubCell
            ? settings.StartCell
            : (rings - 1) * sectors;
        int finishCell = settings.FinishCell >= 0 && settings.FinishCell < cells
            ? settings.FinishCell
            : hubCell;
        if (finishCell == startCell) finishCell = hubCell == startCell ? 0 : hubCell;

        if (settings.Style == MazeStyle.Spiral)
        {
            CarveCircleSpiral(rings, sectors, startCell, hubOpen, ringOpen, radialOpen);
        }
        else
        {
            CarveCircle(rings, sectors, settings, rng, startCell, hubOpen, ringOpen, radialOpen);
            if (settings.Style == MazeStyle.Rooms)
                CarveCircleRooms(rings, sectors, settings, rng, ringOpen, radialOpen);
            else if (settings.Style is MazeStyle.Braided or MazeStyle.Open)
                LoopCircle(rings, sectors, settings.LoopPercent, rng, hubOpen, ringOpen, radialOpen);
        }

        Vec3 World(float x, float y, float z) => settings.Origin + settings.Rotation.Rotate(new Vec3(x, y, z));

        var blocks = new List<LevelNode>();
        var preview = new List<(float, float, float, float)>();
        int runs = 0;

        void Segment(float x0, float z0, float x1, float z1, int material, Col color)
        {
            var a = World(x0, height * 0.5f, z0);
            var b = World(x1, height * 0.5f, z1);
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (length < 1e-3f) return;
            float yaw = MathF.Atan2(-dz, dx) * 180f / MathF.PI;
            blocks.Add(Block(new Vec3((a.X + b.X) * 0.5f, settings.Origin.Y + height * 0.5f, (a.Z + b.Z) * 0.5f),
                new Vec3(length + thickness * 0.5f, height, thickness),
                Quat.FromEulerDegrees(new Vec3(0, yaw, 0)), material, color));
            preview.Add((x0, z0, x1, z1));
            runs++;
        }

        void Arc(float radius, int sector, int material, Col color)
        {
            float step = MathF.PI * 2 / sectors;
            float a0 = sector * step;
            float arcLength = radius * step;
            int pieces = Math.Max(1, (int)MathF.Ceiling(arcLength / MathF.Max(0.8f, cell * 0.6f)));
            float piece = step / pieces;
            for (int i = 0; i < pieces; i++)
            {
                float t0 = a0 + i * piece;
                float t1 = t0 + piece;
                Segment(MathF.Cos(t0) * radius, MathF.Sin(t0) * radius,
                    MathF.Cos(t1) * radius, MathF.Sin(t1) * radius, material, color);
            }
        }

        void Radial(float radius0, float radius1, int sector, int material, Col color)
        {
            float angle = sector * MathF.PI * 2 / sectors;
            float ux = MathF.Cos(angle), uz = MathF.Sin(angle);
            Segment(ux * radius0, uz * radius0, ux * radius1, uz * radius1, material, color);
        }

        // Radial walls between neighbouring sectors.
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sectors; s++)
                if (!radialOpen[r, s])
                    Radial(hub + r * cell, hub + (r + 1) * cell, s, settings.WallMaterial, settings.WallColor);

        // Ring walls between rings.
        for (int r = 0; r < rings - 1; r++)
            for (int s = 0; s < sectors; s++)
                if (!ringOpen[r, s])
                    Arc(hub + (r + 1) * cell, s, settings.WallMaterial, settings.WallColor);

        // Hub wall, except where a ring cell opens into the centre.
        for (int s = 0; s < sectors; s++)
            if (!hubOpen[s])
                Arc(hub, s, settings.WallMaterial, settings.WallColor);

        // Outer boundary, with one entrance gap next to the start cell when it is on the
        // outside ring; otherwise a gap at sector 0 keeps the maze enterable.
        int entranceSector = startCell >= (rings - 1) * sectors && startCell < hubCell
            ? startCell - (rings - 1) * sectors
            : 0;
        for (int s = 0; s < sectors; s++)
            if (s != entranceSector || !settings.Openings)
                Arc(outer, s, settings.WallMaterial, settings.WallColor);

        float ft = Math.Clamp(settings.FloorThickness, 0.1f, 4f);
        if (settings.Floor)
            blocks.Add(Block(World(0, -ft * 0.5f, 0), new Vec3(outer * 2f, ft, outer * 2f),
                settings.Rotation, settings.FloorMaterial, settings.FloorColor, ShapeInfo.Cylinder));
        if (settings.Ceiling)
            blocks.Add(Block(World(0, height + ft * 0.5f, 0), new Vec3(outer * 2f, ft, outer * 2f),
                settings.Rotation, settings.CeilingMaterial, settings.CeilingColor, ShapeInfo.Cylinder));

        var adjacency = CircleAdjacency(rings, sectors, hubOpen, ringOpen, radialOpen);
        var plan = new MazePlan
        {
            Shape = MazeShape.Circle,
            Columns = rings,
            Rows = sectors,
            Cell = cell,
            WallHeight = height,
            HubRadius = hub,
            CellCount = cells,
            Adjacency = adjacency,
            WallRuns = runs,
            StartCell = startCell,
            FinishCell = finishCell,
            Start = settings.PlaceStartFinish ? CirclePoint(World, startCell, rings, sectors, hub, cell, isFinish: false) : null,
            Finish = settings.PlaceStartFinish ? CirclePoint(World, finishCell, rings, sectors, hub, cell, isFinish: true) : null
        };
        foreach (var segment in preview) plan.PreviewWalls.Add(segment);

        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sectors; s++)
            {
                float radius = hub + (r + 0.5f) * cell;
                float angle = (s + 0.5f) * MathF.PI * 2 / sectors;
                plan.Centres.Add((MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
            }
        plan.Centres.Add((0f, 0f));

        plan.Solution.AddRange(Solve(adjacency, startCell, finishCell));
        plan.Blocks.AddRange(blocks);

        if (settings.Lights)
        {
            int spacing = Math.Clamp(settings.LightSpacing, 1, 24);
            for (int r = 0; r < rings; r += spacing)
                for (int s = 0; s < sectors; s += spacing)
                {
                    float radius = hub + (r + 0.5f) * cell;
                    float angle = (s + 0.5f) * MathF.PI * 2 / sectors;
                    plan.Lights.Add(new LevelNode
                    {
                        Kind = NodeKind.Light,
                        Shape = ShapeInfo.Light,
                        Position = World(MathF.Cos(angle) * radius, MathF.Max(0.6f, height - 0.35f), MathF.Sin(angle) * radius),
                        LightColor = new Col(1f, 0.95f, 0.86f, 1f),
                        LightIntensity = 2.3f,
                        LightRange = MathF.Max(6f, cell * 2.1f),
                        EditorLabel = "maze light"
                    });
                }
        }

        return plan;
    }

    // ------------------------------------------------------------------ carving

    private static void Carve(int cols, int rows, MazeSettings settings, Random rng, bool[,] alongX, bool[,] alongZ)
    {
        var visited = new bool[cols, rows];
        var stack = new Stack<(int X, int Z, int Dir)>();
        visited[0, 0] = true;
        stack.Push((0, 0, -1));

        while (stack.Count > 0)
        {
            var (x, z, dir) = stack.Peek();
            var options = new List<(int X, int Z, int Dir)>(4);
            if (x > 0 && !visited[x - 1, z]) options.Add((x - 1, z, 0));
            if (x < cols - 1 && !visited[x + 1, z]) options.Add((x + 1, z, 1));
            if (z > 0 && !visited[x, z - 1]) options.Add((x, z - 1, 2));
            if (z < rows - 1 && !visited[x, z + 1]) options.Add((x, z + 1, 3));
            if (options.Count == 0)
            {
                stack.Pop();
                continue;
            }

            int pick = rng.Next(options.Count);
            // Corridor bias: when the direction we just travelled is still open,
            // keep going that way so the maze reads as corridors with branches
            // instead of a uniform noise of one-cell turns.
            float bias = Math.Clamp(settings.StraightBias, 0f, 0.95f);
            if (dir >= 0 && rng.NextDouble() < bias)
            {
                int straight = options.FindIndex(o => o.Dir == dir);
                if (straight >= 0) pick = straight;
            }

            var next = options[pick];
            OpenWall(alongX, alongZ, x, z, next.X, next.Z);
            visited[next.X, next.Z] = true;
            stack.Push((next.X, next.Z, next.Dir));
        }
    }

    /// <summary>
    /// Draws one unbroken right-hand spiral corridor across the grid. Every cell is
    /// visited once and only consecutive cells are joined, so the result is a single
    /// winding path: the hardest possible layout for a given size.
    /// </summary>
    private static int CarveSpiral(int cols, int rows, bool[,] alongX, bool[,] alongZ)
    {
        int total = cols * rows;
        var visited = new bool[cols, rows];
        int x = 0;
        int z = 0;
        int dx = 1, dz = 0;
        int last = z * cols + x;
        visited[x, z] = true;

        for (int step = 1; step < total; step++)
        {
            int nx = x + dx, nz = z + dz;
            if (nx < 0 || nx >= cols || nz < 0 || nz >= rows || visited[nx, nz])
            {
                (dx, dz) = (-dz, dx);
                nx = x + dx;
                nz = z + dz;
                if (nx < 0 || nx >= cols || nz < 0 || nz >= rows || visited[nx, nz]) break;
            }
            OpenWall(alongX, alongZ, x, z, nx, nz);
            x = nx;
            z = nz;
            visited[x, z] = true;
            last = z * cols + x;
        }
        return last;
    }

    private static void CarveCircle(int rings, int sectors, MazeSettings settings, Random rng, int startCell,
        bool[] hubOpen, bool[,] ringOpen, bool[,] radialOpen)
    {
        var visited = new bool[rings * sectors + 1];
        var stack = new Stack<(int Cell, int Dir)>();
        visited[startCell] = true;
        stack.Push((startCell, -1));
        float bias = Math.Clamp(settings.StraightBias, 0f, 0.95f);

        while (stack.Count > 0)
        {
            var (cell, dir) = stack.Peek();
            var options = CircleNeighbours(rings, sectors, cell);
            options.RemoveAll(o => visited[o.Cell]);
            if (options.Count == 0)
            {
                stack.Pop();
                continue;
            }

            int pick = rng.Next(options.Count);
            if (dir >= 0 && rng.NextDouble() < bias)
            {
                int straight = options.FindIndex(o => o.Dir == dir);
                if (straight >= 0) pick = straight;
            }

            var next = options[pick];
            OpenCircleBoundary(rings, sectors, cell, next.Cell, hubOpen, ringOpen, radialOpen);
            visited[next.Cell] = true;
            stack.Push((next.Cell, next.Dir));
        }
    }

    /// <summary>
    /// A spiral for the ring layout: walk all the way around each ring, step inward at
    /// the point reached, and repeat. Each ring leaves exactly one closed boundary so the
    /// loop cannot become a shortcut, which makes the route the whole maze.
    /// </summary>
    private static void CarveCircleSpiral(int rings, int sectors, int startCell,
        bool[] hubOpen, bool[,] ringOpen, bool[,] radialOpen)
    {
        // Always start the winding corridor at the outside, so dragging the markers
        // inward can never leave outer rings walled off from the route.
        int entry = 0;
        for (int r = rings - 1; r >= 0; r--)
        {
            for (int k = 0; k < sectors - 1; k++)
                radialOpen[r, (entry + k + 1) % sectors] = true;
            int leave = (entry + sectors - 1) % sectors;
            if (r > 0) ringOpen[r - 1, leave] = true;
            else hubOpen[leave] = true;
            entry = leave;
        }
    }

    /// <summary>Opens a few blocks of cells into chambers, the ring-layout version of Rooms.</summary>
    private static void CarveCircleRooms(int rings, int sectors, MazeSettings settings, Random rng,
        bool[,] ringOpen, bool[,] radialOpen)
    {
        int count = Math.Clamp(settings.Rooms, 0, 20);
        int size = Math.Clamp(settings.RoomSize, 2, Math.Min(6, sectors));
        for (int i = 0; i < count; i++)
        {
            int r0 = rng.Next(0, rings);
            int s0 = rng.Next(0, sectors);
            int height = Math.Min(size, rings - r0);
            int width = Math.Min(size, sectors);
            for (int dr = 0; dr < height; dr++)
                for (int ds = 0; ds < width; ds++)
                {
                    if (ds > 0) radialOpen[r0 + dr, (s0 + ds) % sectors] = true;
                    if (dr > 0) ringOpen[r0 + dr - 1, (s0 + ds) % sectors] = true;
                }
        }
    }

    private static void LoopCircle(int rings, int sectors, int loopPercent, Random rng,
        bool[] hubOpen, bool[,] ringOpen, bool[,] radialOpen)
    {
        double chance = Math.Clamp(loopPercent, 0, 100) / 100.0;
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sectors; s++)
            {
                if (rng.NextDouble() >= chance) continue;
                int dir = rng.Next(3);
                if (dir == 0) radialOpen[r, s] = true;
                else if (dir == 1 && r + 1 < rings) ringOpen[r, s] = true;
                else if (dir == 2 && r == 0) hubOpen[s] = true;
            }
    }

    private static void CarveRooms(int cols, int rows, MazeSettings settings, Random rng, bool[,] alongX, bool[,] alongZ)
    {
        int roomCount = Math.Clamp(settings.Rooms, 0, 40);
        int maxSize = Math.Clamp(settings.RoomSize, 2, Math.Min(12, Math.Min(cols, rows)));
        for (int i = 0; i < roomCount; i++)
        {
            int w = rng.Next(2, maxSize + 1);
            int d = rng.Next(2, maxSize + 1);
            if (w > cols || d > rows) continue;
            int x0 = rng.Next(0, cols - w + 1);
            int z0 = rng.Next(0, rows - d + 1);
            for (int x = x0; x < x0 + w; x++)
                for (int z = z0; z < z0 + d; z++)
                {
                    if (x + 1 < x0 + w) alongZ[x + 1, z] = false;
                    if (z + 1 < z0 + d) alongX[x, z + 1] = false;
                }
        }
    }

    private static void Braid(int cols, int rows, int loopPercent, Random rng, bool[,] alongX, bool[,] alongZ)
    {
        double chance = Math.Clamp(loopPercent, 0, 100) / 100.0;
        for (int x = 0; x < cols; x++)
            for (int z = 0; z < rows; z++)
            {
                var open = new List<int>(4);
                var closed = new List<int>(4);
                for (int dir = 0; dir < 4; dir++)
                    (MazeWalls(alongX, alongZ, cols, rows, x, z, dir) ? closed : open).Add(dir);
                if (open.Count != 1 || closed.Count != 3) continue; // only true dead ends
                if (rng.NextDouble() >= chance) continue;
                var candidates = closed.FindAll(d => CanOpen(cols, rows, x, z, d));
                if (candidates.Count == 0) continue;
                Knock(alongX, alongZ, x, z, candidates[rng.Next(candidates.Count)]);
            }
    }

    private static void OpenUp(int cols, int rows, int loopPercent, Random rng, bool[,] alongX, bool[,] alongZ)
    {
        double chance = Math.Clamp(loopPercent, 0, 100) / 100.0;
        for (int x = 0; x < cols; x++)
            for (int z = 0; z < rows; z++)
                for (int dir = 0; dir < 4; dir++)
                {
                    if (!CanOpen(cols, rows, x, z, dir)) continue;
                    if (!MazeWalls(alongX, alongZ, cols, rows, x, z, dir)) continue;
                    if (rng.NextDouble() < chance) Knock(alongX, alongZ, x, z, dir);
                }
    }

    /// <summary>True when the wall in that direction is not on the outer boundary.</summary>
    private static bool CanOpen(int cols, int rows, int x, int z, int dir) => dir switch
    {
        0 => x > 0,
        1 => x < cols - 1,
        2 => z > 0,
        _ => z < rows - 1
    };

    private static bool MazeWalls(bool[,] alongX, bool[,] alongZ, int cols, int rows, int x, int z, int dir) => dir switch
    {
        0 => x > 0 && alongZ[x, z],
        1 => x < cols - 1 && alongZ[x + 1, z],
        2 => z > 0 && alongX[x, z],
        _ => z < rows - 1 && alongX[x, z + 1]
    };

    private static void Knock(bool[,] alongX, bool[,] alongZ, int x, int z, int dir)
    {
        switch (dir)
        {
            case 0: alongZ[x, z] = false; break;
            case 1: alongZ[x + 1, z] = false; break;
            case 2: alongX[x, z] = false; break;
            default: alongX[x, z + 1] = false; break;
        }
    }

    private static void OpenWall(bool[,] alongX, bool[,] alongZ, int x, int z, int nx, int nz)
    {
        if (nx > x) alongZ[x + 1, z] = false;
        else if (nx < x) alongZ[x, z] = false;
        if (nz > z) alongX[x, z + 1] = false;
        else if (nz < z) alongX[x, z] = false;
    }

    /// <summary>Opens the outer edge nearest a cell so the maze has a way in there.</summary>
    private static void OpenEdge(int cols, int rows, int x, int z, bool[,] alongX, bool[,] alongZ)
    {
        if (x <= 0) alongZ[0, z] = false;
        else if (x >= cols - 1) alongZ[cols, z] = false;
        else if (z <= 0) alongX[x, 0] = false;
        else if (z >= rows - 1) alongX[x, rows] = false;
        else alongZ[0, z] = false;
    }

    // ------------------------------------------------------------------ cell graph

    private static List<int>[] RectangleAdjacency(int cols, int rows, bool[,] alongX, bool[,] alongZ)
    {
        var adjacency = new List<int>[cols * rows];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>(4);
        for (int z = 0; z < rows; z++)
            for (int x = 0; x < cols; x++)
            {
                int cell = z * cols + x;
                if (x + 1 < cols && !alongZ[x + 1, z]) Link(adjacency, cell, cell + 1);
                if (z + 1 < rows && !alongX[x, z + 1]) Link(adjacency, cell, cell + cols);
            }
        return adjacency;
    }

    private static List<int>[] CircleAdjacency(int rings, int sectors, bool[] hubOpen, bool[,] ringOpen, bool[,] radialOpen)
    {
        int hub = rings * sectors;
        var adjacency = new List<int>[hub + 1];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>(4);
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sectors; s++)
            {
                int cell = r * sectors + s;
                int next = r * sectors + (s + 1) % sectors;
                if (radialOpen[r, (s + 1) % sectors]) Link(adjacency, cell, next);
                if (r + 1 < rings && ringOpen[r, s]) Link(adjacency, cell, (r + 1) * sectors + s);
                if (r == 0 && hubOpen[s]) Link(adjacency, cell, hub);
            }
        return adjacency;
    }

    private static void Link(List<int>[] adjacency, int a, int b)
    {
        if (!adjacency[a].Contains(b)) adjacency[a].Add(b);
        if (!adjacency[b].Contains(a)) adjacency[b].Add(a);
    }

    private static List<(int Cell, int Dir)> CircleNeighbours(int rings, int sectors, int cell)
    {
        int hub = rings * sectors;
        if (cell == hub)
        {
            var all = new List<(int, int)>(sectors);
            for (int s = 0; s < sectors; s++) all.Add((s, 2));
            return all;
        }
        int r = cell / sectors, sec = cell % sectors;
        var list = new List<(int, int)>(4)
        {
            (r * sectors + (sec + 1) % sectors, 0),          // around
            (r * sectors + (sec + sectors - 1) % sectors, 0) // around the other way
        };
        if (r + 1 < rings) list.Add(((r + 1) * sectors + sec, 1)); // outward
        if (r > 0) list.Add(((r - 1) * sectors + sec, 2));          // inward
        else list.Add((hub, 2));                                     // into the centre
        return list;
    }

    private static void OpenCircleBoundary(int rings, int sectors, int a, int b,
        bool[] hubOpen, bool[,] ringOpen, bool[,] radialOpen)
    {
        int hub = rings * sectors;
        if (a == hub || b == hub)
        {
            int sector = a == hub ? b % sectors : a % sectors;
            hubOpen[sector] = true;
            return;
        }
        int ra = a / sectors, sa = a % sectors;
        int rb = b / sectors, sb = b % sectors;
        if (ra == rb)
        {
            // The boundary is owned by the higher sector id (wrapping back to 0).
            int boundary = (sa + 1) % sectors == sb ? sb : sa;
            radialOpen[ra, boundary] = true;
        }
        else
        {
            int inner = Math.Min(ra, rb);
            ringOpen[inner, sa] = true;
        }
    }

    private static List<int> Solve(List<int>[] adjacency, int from, int to)
    {
        var path = new List<int>();
        if (from < 0 || to < 0 || from >= adjacency.Length || to >= adjacency.Length) return path;
        var previous = new int[adjacency.Length];
        Array.Fill(previous, -1);
        previous[from] = from;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            if (cell == to) break;
            foreach (int next in adjacency[cell])
            {
                if (previous[next] != -1) continue;
                previous[next] = cell;
                queue.Enqueue(next);
            }
        }
        if (previous[to] == -1) return path;
        for (int cell = to; cell != from; cell = previous[cell])
            path.Add(cell);
        path.Add(from);
        path.Reverse();
        return path;
    }

    // ------------------------------------------------------------------ nodes

    private static LevelNode Wall(Vec3 position, Vec3 scale, MazeSettings settings, Quat rotation)
        => Block(position, scale, rotation, settings.WallMaterial, settings.WallColor);

    private static LevelNode Block(Vec3 position, Vec3 scale, Quat rotation, int material, Col color, int shape = ShapeInfo.Cube) => new()
    {
        Kind = NodeKind.Static,
        Shape = shape,
        Position = position,
        Scale = new Vec3(MathF.Max(0.02f, MathF.Abs(scale.X)), MathF.Max(0.02f, MathF.Abs(scale.Y)), MathF.Max(0.02f, MathF.Abs(scale.Z))),
        Rotation = rotation,
        Material = material,
        Color1 = color,
        EditorLabel = "maze"
    };

    private static (float X, float Z) CellCentreRect(int cell, int cols, float cellSize) =>
        ((cell % cols + 0.5f) * cellSize, (cell / cols + 0.5f) * cellSize);

    private static LevelNode StartPoint(Func<float, float, float, Vec3> world, Func<int, int, float, (float X, float Z)> centre,
        int cell, int cols, float size) => new()
    {
        Kind = NodeKind.Start,
        Shape = ShapeInfo.Start,
        Name = "Start",
        Position = world(centre(cell, cols, size).X, 0f, centre(cell, cols, size).Z),
        // Face +X, into the maze for the default corner placement.
        Rotation = Quat.FromEulerDegrees(new Vec3(0, -90, 0)),
        Radius = 0.5f
    };

    private static LevelNode FinishPoint(Func<float, float, float, Vec3> world, Func<int, int, float, (float X, float Z)> centre,
        int cell, int cols, float size) => new()
    {
        Kind = NodeKind.Finish,
        Shape = ShapeInfo.Finish,
        Name = "Finish",
        Position = world(centre(cell, cols, size).X, 0f, centre(cell, cols, size).Z),
        Radius = 1f
    };

    private static LevelNode CirclePoint(Func<float, float, float, Vec3> world, int cell, int rings, int sectors,
        float hub, float size, bool isFinish)
    {
        var (x, z) = CircleCentre(cell, rings, sectors, hub, size);
        return new LevelNode
        {
            Kind = isFinish ? NodeKind.Finish : NodeKind.Start,
            Shape = isFinish ? ShapeInfo.Finish : ShapeInfo.Start,
            Name = isFinish ? "Finish" : "Start",
            Position = world(x, 0f, z),
            Rotation = Quat.FromEulerDegrees(new Vec3(0, isFinish ? 0 : 180, 0)),
            Radius = isFinish ? 1f : 0.5f
        };
    }

    private static (float X, float Z) CircleCentre(int cell, int rings, int sectors, float hub, float size)
    {
        int hubCell = rings * sectors;
        if (cell >= hubCell) return (0f, 0f);
        int ring = Math.Clamp(cell / sectors, 0, rings - 1);
        int sector = ((cell % sectors) + sectors) % sectors;
        float radius = hub + (ring + 0.5f) * size;
        float angle = (sector + 0.5f) * MathF.PI * 2 / sectors;
        return (MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// Flood fills the maze from cell 0 and returns how many cells are reachable. A
    /// perfect maze reaches every cell; used by the engine self-test.
    /// </summary>
    public static int ReachableCells(MazePlan plan)
    {
        if (plan.Adjacency.Length == 0) return 0;
        var seen = new bool[plan.Adjacency.Length];
        var queue = new Queue<int>();
        seen[0] = true;
        queue.Enqueue(0);
        int count = 0;
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            count++;
            foreach (int next in plan.Adjacency[cell])
            {
                if (seen[next]) continue;
                seen[next] = true;
                queue.Enqueue(next);
            }
        }
        return count;
    }

    /// <summary>
    /// Number of open cell boundaries. A perfect maze has exactly one fewer passage
    /// than cells (a spanning tree); more means loops.
    /// </summary>
    public static int PassageCount(MazePlan plan)
    {
        int ends = plan.Adjacency.Sum(list => list.Count);
        return ends / 2;
    }
}
