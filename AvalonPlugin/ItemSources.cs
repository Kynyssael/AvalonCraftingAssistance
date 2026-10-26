using System;
using System.Collections.Generic;
using System.Linq;
using AvalonPlugin.Teamcraft;
using Lumina.Excel.Sheets;

namespace AvalonPlugin;

/// <summary>The sections a list is divided into, in the order they are shown (Teamcraft's default layout).</summary>
public enum ListSection
{
    FinalItems,
    Crystals,
    Vendors,
    Fishing,
    Gathering,
    Other,
    PreCrafts,
}

/// <summary>
/// Sorts list materials into Teamcraft-style sections using the game's own data. Teamcraft's "Timed nodes",
/// "Reducible", "Tomes/Tokens/Scrips" and "Drops or GC" sections need data that isn't in the game files,
/// so those items land in Gathering or Other.
/// </summary>
public sealed class ItemSources
{
    // ClassJob abbreviations by CraftType row (0 = Carpenter ... 7 = Culinarian).
    private static readonly string[] CraftJobs = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    private readonly HashSet<uint> soldForGil = [];
    private readonly HashSet<uint> fished = [];
    private readonly HashSet<uint> gathered = [];
    private readonly Dictionary<uint, int> craftJob = []; // item -> lowest CraftType that makes it

    public ItemSources()
    {
        var data = Plugin.DataManager;

        foreach (var shop in data.GetSubrowExcelSheet<GilShopItem>())
            foreach (var entry in shop)
                soldForGil.Add(entry.Item.RowId);

        foreach (var fish in data.GetExcelSheet<FishParameter>())
            fished.Add(fish.Item.RowId);
        foreach (var fish in data.GetExcelSheet<SpearfishingItem>())
            fished.Add(fish.Item.RowId);

        foreach (var item in data.GetExcelSheet<GatheringItem>())
            gathered.Add(item.Item.RowId);

        foreach (var recipe in data.GetExcelSheet<Recipe>())
        {
            var result = recipe.ItemResult.RowId;
            var job = (int)recipe.CraftType.RowId;
            if (result != 0 && job < CraftJobs.Length && (!craftJob.TryGetValue(result, out var known) || job < known))
                craftJob[result] = job;
        }

        soldForGil.Remove(0);
        fished.Remove(0);
        gathered.Remove(0);
    }

    /// <summary>Section for a material row (final items are always <see cref="ListSection.FinalItems"/>).</summary>
    public ListSection SectionOf(TeamcraftListRow row)
    {
        if (row.GameItemId is not { } id) return ListSection.Other;
        // Same priority as Teamcraft's default layout: the first matching section wins.
        if (id is > 1 and < 20) return ListSection.Crystals;
        if (soldForGil.Contains(id)) return ListSection.Vendors;
        if (fished.Contains(id)) return ListSection.Fishing;
        if (gathered.Contains(id)) return ListSection.Gathering;
        if (craftJob.ContainsKey(id)) return ListSection.PreCrafts;
        return ListSection.Other;
    }

    /// <summary>Crafting job abbreviation (e.g. "BSM") for a craftable item, or null.</summary>
    public string? CraftJobOf(TeamcraftListRow row) =>
        row.GameItemId is { } id && craftJob.TryGetValue(id, out var job) ? CraftJobs[job] : null;

    public static string Title(ListSection section) => section switch
    {
        ListSection.FinalItems => "Final items",
        ListSection.Crystals => "Crystals",
        ListSection.Vendors => "Vendors",
        ListSection.Fishing => "Fishing",
        ListSection.Gathering => "Gathering",
        ListSection.PreCrafts => "Pre-crafts",
        _ => "Other",
    };

    /// <summary>Groups a list's rows into sections, in display order, skipping empty ones.</summary>
    public List<(ListSection Section, List<(bool Final, TeamcraftListRow Row)> Rows)> Group(TeamcraftList list)
    {
        var sections = new List<(ListSection, List<(bool, TeamcraftListRow)>)>
        {
            (ListSection.FinalItems, list.FinalItems.Select(r => (true, r)).ToList()),
        };
        foreach (var group in list.Items.GroupBy(SectionOf).OrderBy(g => g.Key))
        {
            var rows = group.Key == ListSection.PreCrafts
                ? group.OrderBy(r => craftJob.GetValueOrDefault(r.GameItemId ?? 0)).ThenBy(Plugin.GetItemName, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(Plugin.GetItemName, StringComparer.CurrentCultureIgnoreCase);
            sections.Add((group.Key, rows.Select(r => (false, r)).ToList()));
        }
        return sections.Where(s => s.Item2.Count > 0).ToList();
    }
}
