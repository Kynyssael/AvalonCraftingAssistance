using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AvalonPlugin;

/// <summary>Remembered contents of a saddlebag or retainer, which the game only loads while it is open.</summary>
[Serializable]
public class StoredInventory
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<uint, int> Items { get; set; } = [];
    public DateTime Updated { get; set; }
}

/// <summary>Where a character keeps an item, and how many.</summary>
public sealed record ItemHoldings(int Bags, int Saddlebag, IReadOnlyList<(string Retainer, int Count)> Retainers)
{
    public int Total => Bags + Saddlebag + Retainers.Sum(r => r.Count);
}

/// <summary>
/// Counts items in the current character's bags (live), and in their saddlebag and retainers
/// (remembered from the last time each was opened in game, saved per character).
/// </summary>
public sealed class InventoryService(Configuration config)
{
    private static readonly InventoryType[] BagTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private static readonly InventoryType[] SaddlebagTypes =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RetainerTypes =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private Dictionary<uint, int> bags = [];
    private DateTime nextRefresh;
    private bool changed;

    /// <summary>Re-reads the inventories at most every couple of seconds. Call from the UI/framework thread.</summary>
    public void Refresh()
    {
        if (DateTime.UtcNow < nextRefresh) return;
        nextRefresh = DateTime.UtcNow + RefreshInterval;

        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0)
        {
            bags = [];
            return;
        }

        bags = Read(BagTypes) ?? [];
        var stored = config.Inventories.TryGetValue(contentId, out var existing) ? existing : config.Inventories[contentId] = new CharacterInventories();

        if (Read(SaddlebagTypes) is { } saddlebag)
            changed |= Store(stored.Saddlebag, "Saddlebag", saddlebag);

        if (ActiveRetainer() is { } retainer && Read(RetainerTypes) is { } retainerItems)
        {
            if (!stored.Retainers.TryGetValue(retainer.Id, out var entry))
                entry = stored.Retainers[retainer.Id] = new StoredInventory();
            changed |= Store(entry, retainer.Name, retainerItems);
        }

        if (changed)
        {
            changed = false;
            config.Save();
        }
    }

    public ItemHoldings Holdings(uint itemId)
    {
        var stored = config.Inventories.GetValueOrDefault(Plugin.PlayerState.ContentId);
        return new ItemHoldings(
            bags.GetValueOrDefault(itemId),
            stored?.Saddlebag.Items.GetValueOrDefault(itemId) ?? 0,
            stored?.Retainers.Values
                .Select(r => (r.Name, r.Items.GetValueOrDefault(itemId)))
                .Where(r => r.Item2 > 0)
                .ToList() ?? []);
    }

    /// <summary>Retainers whose contents are remembered for the current character.</summary>
    public IReadOnlyList<StoredInventory> KnownRetainers() =>
        config.Inventories.GetValueOrDefault(Plugin.PlayerState.ContentId)?.Retainers.Values.OrderBy(r => r.Name).ToList() ?? [];

    private static bool Store(StoredInventory target, string name, Dictionary<uint, int> items)
    {
        var same = target.Name == name && target.Items.Count == items.Count
                   && items.All(kv => target.Items.TryGetValue(kv.Key, out var n) && n == kv.Value);
        if (same) return false;
        target.Name = name;
        target.Items = items;
        target.Updated = DateTime.UtcNow;
        return true;
    }

    /// <summary>Item counts (NQ and HQ together) of the given containers, or null if they aren't loaded.</summary>
    private static unsafe Dictionary<uint, int>? Read(InventoryType[] types)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return null;

        var counts = new Dictionary<uint, int>();
        var anyLoaded = false;
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            anyLoaded = true;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;
                var itemId = slot->GetBaseItemId();
                if (itemId == 0) continue;
                counts[itemId] = counts.GetValueOrDefault(itemId) + (int)slot->GetQuantity();
            }
        }
        return anyLoaded ? counts : null;
    }

    private static unsafe (ulong Id, string Name)? ActiveRetainer()
    {
        var manager = RetainerManager.Instance();
        if (manager == null) return null;
        var retainer = manager->GetActiveRetainer();
        if (retainer == null || retainer->RetainerId == 0) return null;
        return (retainer->RetainerId, retainer->NameString);
    }
}

[Serializable]
public class CharacterInventories
{
    public StoredInventory Saddlebag { get; set; } = new() { Name = "Saddlebag" };

    /// <summary>By retainer id.</summary>
    public Dictionary<ulong, StoredInventory> Retainers { get; set; } = [];
}
