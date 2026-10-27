using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace AvalonPlugin;

/// <summary>An NPC that sells an item for gil, and where to find them.</summary>
public sealed record Vendor(string Npc, string Place, float X, float Y, uint TerritoryId, uint MapId);

/// <summary>
/// Which NPCs sell an item for gil, from the game's own data: gil shops, the NPCs that open them (directly or
/// through a dialogue menu), and where each NPC stands. Built once in the background; ask <see cref="IsReady"/>.
/// </summary>
public sealed class VendorSources
{
    // Event handler ids start with their sheet's "type" in the top 16 bits.
    private const uint GilShopType = 0x04, CustomTalkType = 0x0B, TopicSelectType = 0x32, PreHandlerType = 0x36;
    private const byte LevelTypeNpc = 8;

    private readonly Task<Dictionary<uint, List<Vendor>>> building;

    public VendorSources() => building = Task.Run(Build);

    public bool IsReady => building.IsCompletedSuccessfully;

    /// <summary>NPCs selling the item for gil (empty until ready, or if none).</summary>
    public IReadOnlyList<Vendor> For(uint itemId) =>
        IsReady && building.Result.TryGetValue(itemId, out var vendors) ? vendors : [];

    /// <summary>The item's fixed price at gil vendors.</summary>
    public static uint GilPrice(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceMid ?? 0;

    private static Dictionary<uint, List<Vendor>> Build()
    {
        var data = Plugin.DataManager;
        var gilShops = data.GetExcelSheet<GilShop>();
        var topics = data.GetExcelSheet<TopicSelect>();
        var preHandlers = data.GetExcelSheet<PreHandler>();
        var customTalks = data.GetExcelSheet<CustomTalk>();

        // Gil shop -> items it sells.
        var shopItems = new Dictionary<uint, List<uint>>();
        foreach (var shop in data.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var entry in shop)
            {
                if (entry.Item.RowId == 0) continue;
                if (!shopItems.TryGetValue(shop.RowId, out var items)) shopItems[shop.RowId] = items = [];
                items.Add(entry.Item.RowId);
            }
        }

        // Gil shops an event handler leads to, following dialogue menus (TopicSelect), pre-handlers and custom talks.
        IEnumerable<uint> ShopsOf(uint handler, int depth)
        {
            if (handler == 0 || depth > 4) yield break;
            switch (handler >> 16)
            {
                case GilShopType when gilShops.HasRow(handler):
                    yield return handler;
                    break;
                case TopicSelectType when topics.GetRowOrDefault(handler) is { } topic:
                    foreach (var shop in topic.Shop)
                        foreach (var id in ShopsOf(shop.RowId, depth + 1)) yield return id;
                    break;
                case PreHandlerType when preHandlers.GetRowOrDefault(handler) is { } pre:
                    foreach (var id in ShopsOf(pre.Target.RowId, depth + 1)) yield return id;
                    break;
                case CustomTalkType when customTalks.GetRowOrDefault(handler) is { } talk:
                    foreach (var script in talk.Script)
                        if (script.ScriptArg >> 16 == GilShopType)
                            foreach (var id in ShopsOf(script.ScriptArg, depth + 1)) yield return id;
                    break;
            }
        }

        // Item -> NPCs selling it.
        var itemNpcs = new Dictionary<uint, HashSet<uint>>();
        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            foreach (var handler in npc.ENpcData)
            {
                foreach (var shop in ShopsOf(handler.RowId, 0))
                {
                    if (!shopItems.TryGetValue(shop, out var items)) continue;
                    foreach (var item in items)
                    {
                        if (!itemNpcs.TryGetValue(item, out var npcs)) itemNpcs[item] = npcs = [];
                        npcs.Add(npc.RowId);
                    }
                }
            }
        }

        // Where those NPCs stand (first placement of each).
        var vendorNpcs = itemNpcs.Values.SelectMany(n => n).ToHashSet();
        var places = new Dictionary<uint, (string Place, float X, float Y, uint Territory, uint Map)>();
        foreach (var level in data.GetExcelSheet<Level>())
        {
            var npcId = level.Object.RowId;
            if (level.Type != LevelTypeNpc || !vendorNpcs.Contains(npcId) || places.ContainsKey(npcId)) continue;
            if (level.Map.ValueNullable is not { } map || level.Territory.RowId == 0) continue;
            var place = map.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(place)) continue;
            places[npcId] = (place, ToMapCoordinate(level.X, map.OffsetX, map.SizeFactor), ToMapCoordinate(level.Z, map.OffsetY, map.SizeFactor),
                             level.Territory.RowId, map.RowId);
        }

        var residents = data.GetExcelSheet<ENpcResident>();
        var result = new Dictionary<uint, List<Vendor>>();
        foreach (var (item, npcs) in itemNpcs)
        {
            var vendors = npcs
                .Where(places.ContainsKey)
                .Select(id => (Name: NpcName(residents.GetRowOrDefault(id)), Place: places[id]))
                .Where(v => !string.IsNullOrEmpty(v.Name))
                .Select(v => new Vendor(v.Name, v.Place.Place, v.Place.X, v.Place.Y, v.Place.Territory, v.Place.Map))
                .DistinctBy(v => (v.Npc, v.Place))
                .OrderBy(v => v.Place, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (vendors.Count > 0) result[item] = vendors;
        }
        Plugin.Log.Debug($"Vendor index built: {result.Count} items sold by NPCs.");
        return result;
    }

    // Game world position to the X/Y shown on the in-game map.
    private static float ToMapCoordinate(float position, short offset, ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        return 41f / scale * (((position + offset) * scale + 1024f) / 2048f) + 1f;
    }

    // Generic NPC names are stored in lower case ("material supplier"); capitalize them like the game shows.
    private static string NpcName(ENpcResident? resident)
    {
        var name = resident?.Singular.ExtractText() ?? string.Empty;
        return string.IsNullOrEmpty(name) ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }
}
