using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GrabEngine.Core;

/// <summary>One reusable block-brush setup. The palette stores full setups rather than
/// only a material id, so switching back to a recently used block restores its colour,
/// shape and cell size too.</summary>
public sealed class BlockPaletteEntry
{
    public int Material { get; set; } = 8;
    public string Color { get; set; } = "#B8B8B8";
    public int Shape { get; set; } = ShapeInfo.Cube;
    public double CellSize { get; set; } = 1.0;
    public bool Hollow { get; set; }
    public int Layers { get; set; } = 1;

    public string Label => MaterialInfo.Display(Material) +
        (Material == 8 ? "  " + Color : "") +
        (Shape != ShapeInfo.Cube ? "  " + ShapeInfo.Display(Shape) : "");

    public string Tooltip =>
        $"{MaterialInfo.Display(Material)}{(Material == 8 ? "  " + Color : "")}" +
        $"  ·  {ShapeInfo.Display(Shape)}  ·  {CellSize:0.##} m" +
        (Layers > 1 ? "  ·  " + Layers + " layers" : "") +
        (Hollow ? "  ·  hollow" : "");

    public bool SameAs(BlockPaletteEntry other) =>
        other != null &&
        Material == other.Material &&
        string.Equals(Color, other.Color, StringComparison.OrdinalIgnoreCase) &&
        Shape == other.Shape &&
        Math.Abs(CellSize - other.CellSize) < 0.0001 &&
        Hollow == other.Hollow &&
        Layers == other.Layers;
}

public sealed class BlockPaletteState
{
    public List<BlockPaletteEntry> Favorites { get; set; } = new();
    public List<BlockPaletteEntry> Recent { get; set; } = new();
}

/// <summary>
/// Persists the smart block palette under the editor workspace. Recents are capped and
/// favourites are kept separately, so a long build session never pushes a deliberately
/// saved setup out of the palette.
/// </summary>
public static class BlockPalette
{
    private const int MaxRecent = 8;
    private const int MaxFavorites = 12;
    private static string PathFile => Path.Combine(WorkspaceStore.Root, "block-palette.json");

    public static BlockPaletteState Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new BlockPaletteState();
            var state = JsonSerializer.Deserialize<BlockPaletteState>(File.ReadAllText(PathFile)) ?? new BlockPaletteState();
            state.Favorites ??= new List<BlockPaletteEntry>();
            state.Recent ??= new List<BlockPaletteEntry>();
            state.Favorites = state.Favorites.Where(e => e != null).Take(MaxFavorites).ToList();
            state.Recent = state.Recent.Where(e => e != null).Take(MaxRecent).ToList();
            return state;
        }
        catch
        {
            return new BlockPaletteState();
        }
    }

    public static void Save(BlockPaletteState state)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceStore.Root);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A palette write must never interrupt editing.
        }
    }

    public static void AddRecent(BlockPaletteState state, BlockPaletteEntry entry)
    {
        if (entry == null) return;
        state.Recent.RemoveAll(e => e.SameAs(entry));
        state.Recent.Insert(0, entry);
        if (state.Recent.Count > MaxRecent) state.Recent.RemoveRange(MaxRecent, state.Recent.Count - MaxRecent);
        Save(state);
    }

    public static bool ToggleFavorite(BlockPaletteState state, BlockPaletteEntry entry)
    {
        if (entry == null) return false;
        int index = state.Favorites.FindIndex(e => e.SameAs(entry));
        if (index >= 0)
        {
            state.Favorites.RemoveAt(index);
            Save(state);
            return false;
        }
        state.Favorites.Insert(0, entry);
        if (state.Favorites.Count > MaxFavorites) state.Favorites.RemoveRange(MaxFavorites, state.Favorites.Count - MaxFavorites);
        Save(state);
        return true;
    }

    public static bool IsFavorite(BlockPaletteState state, BlockPaletteEntry entry)
        => state.Favorites.Any(e => e.SameAs(entry));
}
