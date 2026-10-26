using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AvalonPlugin.Teamcraft;
using Lumina.Excel.Sheets;

namespace AvalonPlugin;

/// <summary>
/// Turns a Teamcraft list into an Artisan crafting list, for Artisan's "Import List From Clipboard (Artisan Export)".
/// Artisan has no IPC to create lists, and its importer rejects unknown fields, so only fields of its
/// NewCraftingList / ListItem classes are written (Artisan/CraftingList/CraftingList.cs).
/// </summary>
public static class ArtisanExport
{
    public sealed record Result(string Json, int Recipes, int Crafts, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Builds the list from the rows still to craft: pre-crafts first (ingredients before what uses them),
    /// then final items. Quantities are crafts, so recipes that make several per craft are divided.
    /// </summary>
    public static Result Build(TeamcraftList list, Func<TeamcraftListRow, bool> include)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        var skipped = new List<string>();
        var entries = new List<(uint RecipeId, int Crafts, int Depth, bool Final, uint ItemId)>();

        // Pre-crafts are the list's crafted materials; final items may also be gathered or bought (then there's no recipe).
        var rows = list.Items.Where(r => r.IsCraft).Select(r => (Row: r, Final: false))
            .Concat(list.FinalItems.Select(r => (Row: r, Final: true)));
        var crafted = new HashSet<uint>(list.Items.Where(r => r.IsCraft).Select(r => r.GameItemId ?? 0));

        foreach (var (row, final) in rows)
        {
            if (!include(row) || row.Done >= row.Amount || row.GameItemId is not { } itemId) continue;

            Recipe? recipe = uint.TryParse(row.RecipeId, out var recipeId) ? recipes.GetRowOrDefault(recipeId) : null;
            recipe ??= recipes.FirstOrDefault(r => r.ItemResult.RowId == itemId);
            if (recipe is not { } found || found.RowId == 0)
            {
                if (final) skipped.Add($"{Plugin.GetItemName(row)} (no recipe)");
                continue;
            }

            var perCraft = Math.Max(1, (int)found.AmountResult);
            var crafts = (int)Math.Ceiling((row.Amount - row.Done) / (double)perCraft);
            entries.Add((found.RowId, crafts, Depth(found, crafted, recipes, 0), final, itemId));
        }

        var ordered = entries
            .OrderBy(e => e.Final)
            .ThenBy(e => e.Depth)
            .ThenBy(e => e.RecipeId)
            .Select(e => (e.RecipeId, e.Crafts))
            .ToList();
        return new Result(Serialize(list.Name, ordered), ordered.Count, ordered.Sum(e => e.Crafts), skipped);
    }

    /// <summary>Artisan's list JSON. Separate from <see cref="Build"/> so the format can be checked without game data.</summary>
    public static string Serialize(string name, IReadOnlyList<(uint RecipeId, int Crafts)> recipes)
    {
        var list = new Dictionary<string, object>
        {
            ["ID"] = 0, // Artisan assigns a fresh id on import
            ["Name"] = name,
            ["Recipes"] = recipes.Select(r => new Dictionary<string, object>
            {
                ["ID"] = r.RecipeId,
                ["Quantity"] = r.Crafts,
                ["ListItemOptions"] = new Dictionary<string, object> { ["NQOnly"] = false, ["Skipping"] = false },
            }).ToList(),
            // Artisan rebuilds this from Recipes when the list starts; filled the same way for completeness.
            ["ExpandedList"] = recipes.SelectMany(r => Enumerable.Repeat(r.RecipeId, r.Crafts)).ToList(),
            ["SkipIfEnough"] = false,
            ["Materia"] = false,
            ["Repair"] = false,
            ["RepairPercent"] = 50,
            ["AddAsQuickSynth"] = false,
            ["TidyAfter"] = true,
            ["IsPremade"] = false,
        };
        return JsonSerializer.Serialize(list);
    }

    /// <summary>How many levels of this list's pre-crafts sit under a recipe (0 = only raw materials).</summary>
    private static int Depth(Recipe recipe, HashSet<uint> crafted, Lumina.Excel.ExcelSheet<Recipe> recipes, int guard)
    {
        if (guard > 10) return 0;
        var depth = 0;
        foreach (var ingredient in recipe.Ingredient)
        {
            var id = ingredient.RowId;
            if (id == 0 || !crafted.Contains(id)) continue;
            if (recipes.FirstOrDefault(r => r.ItemResult.RowId == id) is { RowId: > 0 } sub)
                depth = Math.Max(depth, 1 + Depth(sub, crafted, recipes, guard + 1));
        }
        return depth;
    }
}
