using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

/// <summary>
/// A small visibility graph over the walkable ground, plus the all-pairs shortest
/// distances and next-hop table between its nodes.
///
/// The exported Code Block cannot run A*: GRAB's GASM has no loops, so every search has
/// to be unrolled. What it *can* do is a table lookup. Baking the graph here means the
/// shipped block only has to answer "which node do I walk to next?" - and it can answer
/// it again at any time, which is what lets the agent re-plan when the target moves.
///
/// Nodes are spread over the surface with farthest-point sampling (plus the finder and
/// target feet), edges are straight lines that pass the navigation grid's line-of-sight
/// test, and the shortest paths are Floyd-Warshall over that tiny graph.
/// </summary>
public sealed class NavRouteGraph
{
    public List<Vec3> Nodes { get; } = new();
    public List<List<int>> Links { get; } = new();

    /// <summary>All-pairs path length in metres. <c>Distance[i, j]</c> is +inf when unreachable.</summary>
    public float[,] Distance { get; private set; } = new float[0, 0];

    /// <summary>Next node to walk to when going from i to j, or -1.</summary>
    public int[,] Next { get; private set; } = new int[0, 0];

    public int StartNode { get; private set; }
    public int GoalNode { get; private set; }
    public float LinkRange { get; private set; }
    public int ForcedLinks { get; private set; }

    public int Count => Nodes.Count;

    public static NavRouteGraph? Build(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode target, IReadOnlyList<Vec3>? surface, int maxNodes = 18)
    {
        settings.Clamp();
        var finderFoot = Pathfinder.FootPoint(finder);
        var targetFoot = Pathfinder.FootPoint(target);
        var grid = NavigationGrid.Build(level, settings, finderFoot, targetFoot, finder);

        var candidates = new List<Vec3>();
        if (surface != null) candidates.AddRange(surface.Where(p => grid.Contains(p, 1.5f)));
        if (candidates.Count < 6)
            candidates = PathSensorRig.WalkableSurface(grid, finderFoot, settings.NavMeshArea);
        if (candidates.Count == 0) return null;

        Vec3 Snap(Vec3 p)
        {
            int index = grid.FindStandableNear(p, 4);
            return index >= 0 ? grid.Center(index) : p;
        }

        var graph = new NavRouteGraph
        {
            LinkRange = MathF.Max(4f, settings.NavMeshLinkRange)
        };
        var nodes = graph.Nodes;

        void Add(Vec3 p)
        {
            foreach (var n in nodes)
                if (Flat(n, p) < 0.09f) return;
            nodes.Add(p);
        }

        var startPoint = Snap(finderFoot);
        var goalPoint = Snap(targetFoot);
        Add(startPoint);
        Add(goalPoint);
        int startNode = 0;
        int goalNode = nodes.Count > 1 ? 1 : 0;

        // Farthest-point sampling: every new node is the point furthest from everything
        // already chosen, so the graph spreads over the ground instead of clustering on
        // the route the editor happened to preview.
        float spacing = MathF.Max(0.5f, MathF.Max(settings.NavMeshDetail, settings.CellSize));
        int limit = Math.Clamp(maxNodes, 4, 48);
        while (nodes.Count < limit)
        {
            Vec3 pick = default;
            float pickDistance = -1f;
            foreach (var candidate in candidates)
            {
                float nearest = float.MaxValue;
                foreach (var node in nodes)
                {
                    float d = Flat(candidate, node);
                    if (d < nearest) nearest = d;
                }
                if (nearest > pickDistance)
                {
                    pickDistance = nearest;
                    pick = candidate;
                }
            }
            if (pickDistance < spacing * spacing * 0.6f) break;
            Add(pick);
        }

        int n = nodes.Count;
        for (int i = 0; i < n; i++) graph.Links.Add(new List<int>());

        float[,] weight = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                weight[i, j] = float.PositiveInfinity;

        void Link(int a, int b, float w)
        {
            if (a == b) return;
            if (w < weight[a, b]) weight[a, b] = weight[b, a] = w;
            if (!graph.Links[a].Contains(b)) graph.Links[a].Add(b);
            if (!graph.Links[b].Contains(a)) graph.Links[b].Add(a);
        }

        var lift = new Vec3(0, 0.06f, 0);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float d = MathF.Sqrt(Flat(nodes[i], nodes[j]));
                if (d > graph.LinkRange) continue;
                if (!grid.LineClear(nodes[i] + lift, nodes[j] + lift, settings)) continue;
                Link(i, j, d + MathF.Abs(nodes[j].Y - nodes[i].Y) * 0.5f);
            }
        }

        // A level split into rooms can leave a node stranded if the sampling missed the
        // doorway. Join the closest pair between components so the agent still has a
        // route to follow (the whiskers handle whatever is actually in the way).
        for (int guard = 0; guard < n; guard++)
        {
            var component = Components(graph);
            if (component.Max() == 0) break;
            int bestA = -1, bestB = -1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (component[i] == component[j]) continue;
                    float d = Flat(nodes[i], nodes[j]);
                    if (d >= best) continue;
                    best = d;
                    bestA = i;
                    bestB = j;
                }
            }
            if (bestA < 0) break;
            Link(bestA, bestB, MathF.Sqrt(best) * 1.35f);
            graph.ForcedLinks++;
        }

        graph.Distance = new float[n, n];
        graph.Next = new int[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                graph.Distance[i, j] = i == j ? 0f : weight[i, j];
                graph.Next[i, j] = i == j ? -1 : (weight[i, j] < float.PositiveInfinity ? j : -1);
            }
        }
        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < n; i++)
            {
                if (float.IsPositiveInfinity(graph.Distance[i, k])) continue;
                for (int j = 0; j < n; j++)
                {
                    float through = graph.Distance[i, k] + graph.Distance[k, j];
                    if (through >= graph.Distance[i, j]) continue;
                    graph.Distance[i, j] = through;
                    graph.Next[i, j] = graph.Next[i, k];
                }
            }
        }

        graph.StartNode = startNode;
        graph.GoalNode = goalNode;
        return graph;
    }

    /// <summary>Index of the node closest to a world point, used by the tests and previews.</summary>
    public int Nearest(Vec3 point)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < Nodes.Count; i++)
        {
            float d = Flat(Nodes[i], point);
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = i;
        }
        return best;
    }

    /// <summary>Node chain from a start position to a goal position, for previews.</summary>
    public List<Vec3> Route(Vec3 from, Vec3 to)
    {
        var route = new List<Vec3>();
        int a = Nearest(from);
        int b = Nearest(to);
        if (a < 0 || b < 0) return route;
        route.Add(from);
        for (int guard = 0; guard <= Nodes.Count && a != b; guard++)
        {
            int next = Next[a, b];
            if (next < 0) break;
            a = next;
            route.Add(Nodes[a]);
        }
        route.Add(to);
        return route;
    }

    private static float Flat(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static int[] Components(NavRouteGraph graph)
    {
        int n = graph.Count;
        var component = Enumerable.Repeat(-1, n).ToArray();
        int id = 0;
        for (int i = 0; i < n; i++)
        {
            if (component[i] >= 0) continue;
            var queue = new Queue<int>();
            queue.Enqueue(i);
            component[i] = id;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in graph.Links[current])
                {
                    if (component[next] >= 0) continue;
                    component[next] = id;
                    queue.Enqueue(next);
                }
            }
            id++;
        }
        return id <= 1 ? Enumerable.Repeat(0, n).ToArray() : component;
    }
}
