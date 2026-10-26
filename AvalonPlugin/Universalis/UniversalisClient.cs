using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvalonPlugin.Universalis;

/// <summary>One market board listing: a stack sold as a whole.</summary>
public sealed record MarketListing(long PricePerUnit, int Quantity, string World, bool Hq);

/// <summary>Current NA listings for an item, cheapest first.</summary>
public sealed record MarketPrice(uint ItemId, IReadOnlyList<MarketListing> Listings)
{
    public MarketListing Cheapest => Listings[0];
    public long PricePerUnit => Cheapest.PricePerUnit;
    public string World => Cheapest.World;
    public bool Hq => Cheapest.Hq;

    /// <summary>
    /// The cheapest way to buy at least <paramref name="needed"/> units. Listings are bought whole, so this picks
    /// the cheapest combination of stacks (one small stack can beat a cheap-per-unit stack of 999).
    /// <c>Bought</c> can be more than needed; when the listings we know of don't add up to the amount,
    /// <c>Enough</c> is false and the cost is buying all of them.
    /// </summary>
    public (long Cost, int Bought, bool Enough) CostToBuy(int needed)
    {
        if (needed <= 0) return (0, 0, true);

        var listed = Listings.Sum(l => l.Quantity);
        if (listed < needed)
            return (Listings.Sum(l => l.PricePerUnit * l.Quantity), listed, false);

        // Minimum-cost cover: best[q] = cheapest way to get at least q units (q capped at needed).
        var best = new long[needed + 1];
        var units = new int[needed + 1];
        Array.Fill(best, long.MaxValue);
        best[0] = 0;
        foreach (var listing in Listings)
        {
            var stackCost = listing.PricePerUnit * listing.Quantity;
            for (var have = needed; have >= 0; have--)
            {
                if (best[have] == long.MaxValue) continue;
                var next = Math.Min(needed, have + listing.Quantity);
                if (best[have] + stackCost < best[next])
                {
                    best[next] = best[have] + stackCost;
                    units[next] = units[have] + listing.Quantity;
                }
            }
        }
        return (best[needed], units[needed], true);
    }
}

/// <summary>Reads market board prices from Universalis, always for the North America region.</summary>
public sealed class UniversalisClient(HttpClient http)
{
    // The team only trades on NA, so the region is fixed on purpose.
    public const string Region = "North-America";
    private const string BaseUrl = "https://universalis.app/api/v2";
    private const int MaxItemsPerRequest = 100; // Universalis limit
    private const int ListingsPerItem = 50;     // enough to cost buying a few hundred of something
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly string[] ListingFields = ["pricePerUnit", "quantity", "worldName", "hq"];

    private readonly ConcurrentDictionary<uint, (MarketPrice? Price, DateTimeOffset Fetched)> cache = new();

    /// <summary>Prices for the given items; items with no listings are left out.</summary>
    public async Task<IReadOnlyDictionary<uint, MarketPrice>> GetPricesAsync(IEnumerable<uint> itemIds, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = itemIds.Distinct().ToList();
        var missing = wanted.Where(id => !cache.TryGetValue(id, out var c) || now - c.Fetched > CacheDuration).ToList();

        foreach (var chunk in missing.Chunk(MaxItemsPerRequest))
        {
            // Ask only for the listing fields we use. Several ids nest results under "items.".
            var prefix = chunk.Length > 1 ? "items." : string.Empty;
            var fields = string.Join(',', ListingFields.Select(f => $"{prefix}listings.{f}").Append($"{prefix}itemID"));
            var url = $"{BaseUrl}/{Region}/{string.Join(',', chunk)}?listings={ListingsPerItem}&entries=0&fields={fields}";
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Universalis request failed ({(int)response.StatusCode}).");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            // Several ids return { "items": { "<id>": {...} } }; a single id returns the item itself.
            var items = root.TryGetProperty("items", out var many) ? many.EnumerateObject().Select(p => p.Value) : [root];

            var found = new HashSet<uint>();
            foreach (var item in items)
            {
                var id = item.GetProperty("itemID").GetUInt32();
                found.Add(id);
                cache[id] = (ReadPrice(id, item), now);
            }
            foreach (var id in chunk.Where(id => !found.Contains(id)))
                cache[id] = (null, now);
        }

        return wanted
            .Select(id => cache.TryGetValue(id, out var c) ? c.Price : null)
            .OfType<MarketPrice>()
            .ToDictionary(p => p.ItemId);
    }

    private static MarketPrice? ReadPrice(uint id, JsonElement item)
    {
        if (!item.TryGetProperty("listings", out var listings) || listings.GetArrayLength() == 0)
            return null;

        var parsed = listings.EnumerateArray()
            .Select(l => new MarketListing(
                l.GetProperty("pricePerUnit").GetInt64(),
                l.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1,
                l.TryGetProperty("worldName", out var world) ? world.GetString() ?? "?" : "?",
                l.TryGetProperty("hq", out var hq) && hq.GetBoolean()))
            .OrderBy(l => l.PricePerUnit)
            .ToList();
        return new MarketPrice(id, parsed);
    }
}
