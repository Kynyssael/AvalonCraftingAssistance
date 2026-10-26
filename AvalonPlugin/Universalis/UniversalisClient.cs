using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvalonPlugin.Universalis;

/// <summary>Cheapest current listing for an item across North America.</summary>
public sealed record MarketPrice(uint ItemId, long PricePerUnit, string World, bool Hq);

/// <summary>Reads market board prices from Universalis, always for the North America region.</summary>
public sealed class UniversalisClient(HttpClient http)
{
    // The team only trades on NA, so the region is fixed on purpose.
    public const string Region = "North-America";
    private const string BaseUrl = "https://universalis.app/api/v2";
    private const int MaxItemsPerRequest = 100; // Universalis limit
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<uint, (MarketPrice? Price, DateTimeOffset Fetched)> cache = new();

    /// <summary>Prices for the given items; items with no listings are left out.</summary>
    public async Task<IReadOnlyDictionary<uint, MarketPrice>> GetPricesAsync(IEnumerable<uint> itemIds, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = itemIds.Distinct().ToList();
        var missing = wanted.Where(id => !cache.TryGetValue(id, out var c) || now - c.Fetched > CacheDuration).ToList();

        foreach (var chunk in missing.Chunk(MaxItemsPerRequest))
        {
            var url = $"{BaseUrl}/{Region}/{string.Join(',', chunk)}?listings=1&entries=0";
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

        var cheapest = listings[0];
        return new MarketPrice(
            id,
            cheapest.GetProperty("pricePerUnit").GetInt64(),
            cheapest.TryGetProperty("worldName", out var world) ? world.GetString() ?? "?" : "?",
            cheapest.TryGetProperty("hq", out var hq) && hq.GetBoolean());
    }
}
