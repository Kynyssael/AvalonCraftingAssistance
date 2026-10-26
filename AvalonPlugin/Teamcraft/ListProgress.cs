using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvalonPlugin.Teamcraft;

/// <summary>
/// Changes how many of a list's items are done, following Teamcraft's own rules
/// (ListController.setDone in apps/client/src/app/modules/list/list-controller.ts):
/// finishing a craft also marks its ingredients as done and used, recursively, taking recipe yields into account.
/// </summary>
internal static class ListProgress
{
    private sealed class Row
    {
        public required JsonObject Fields;
        public required bool InFinalItems;
        public required bool FinalItemFlag;
        public required string Id;
        public string? RecipeId;
        public double Amount, Done, Used, AmountNeeded, Yield;
        public List<(string Id, double Amount)>? Requires; // null when Teamcraft stored none (not a craft)
        public bool Changed;
    }

    /// <summary>
    /// Takes the list document exactly as Firestore returned it, applies the changes and returns the PATCH body
    /// (only done/used of touched rows change) and the field paths for its update mask.
    /// </summary>
    public static (JsonObject Body, IReadOnlyList<string> FieldPaths) BuildUpdate(
        JsonElement document, IReadOnlyList<DoneChange> changes, DateTimeOffset now)
    {
        var fields = JsonNode.Parse(document.GetProperty("fields").GetRawText())!.AsObject();
        var finalItems = ReadRows(fields, "finalItems", true);
        var items = ReadRows(fields, "items", false);

        foreach (var change in changes)
        {
            var row = GetItemById(finalItems, items, change.ItemId, !change.FinalItem, change.FinalItem, change.RecipeId);
            if (row == null) continue;
            var delta = change.SetTo is { } target
                ? Math.Clamp(target, 0, row.Amount) - row.Done
                : change.Delta ?? row.Amount - row.Done;
            if (delta == 0 || (change.OnlyIncrease && delta < 0)) continue;
            SetDone(finalItems, items, change.ItemId, delta, !change.FinalItem, change.FinalItem, false, change.RecipeId, delta);
        }

        var bodyFields = new JsonObject();
        var paths = new List<string>();
        foreach (var (name, rows) in new[] { ("finalItems", finalItems), ("items", items) })
        {
            if (!rows.Any(r => r.Changed)) continue;
            foreach (var row in rows.Where(r => r.Changed))
            {
                row.Fields["done"] = EncodeNumber(row.Done);
                row.Fields["used"] = EncodeNumber(row.Used);
            }
            bodyFields[name] = fields[name]!.DeepClone();
            paths.Add(name);
        }

        if (paths.Count == 0)
            throw new TeamcraftException("Nothing to change: those items are already at that amount.");

        // Teamcraft only accepts an incoming list if its etag is higher than the one it has (it uses Date.now()).
        var currentEtag = document.GetProperty("fields").TryGetProperty("etag", out var etag)
            ? FirestoreJson.ToLong(FirestoreJson.DecodeValue(etag))
            : 0;
        bodyFields["etag"] = EncodeNumber(Math.Max(now.ToUnixTimeMilliseconds(), currentEtag + 1));
        paths.Add("etag");

        return (new JsonObject { ["fields"] = bodyFields }, paths);
    }

    // Port of ListController.setDone (with external = false).
    private static void SetDone(List<Row> finalItems, List<Row> items, string itemId, double amount, bool excludeFinalItems,
                                bool onlyFinalItems, bool setUsed, string? recipeId, double initialAddition)
    {
        var item = GetItemById(finalItems, items, itemId, excludeFinalItems, onlyFinalItems, recipeId);
        if (item == null) return;
        item.Changed = true;

        var previousDone = item.AmountNeeded - AbsoluteCeil((item.Amount - item.Done) / item.Yield);
        if (setUsed)
        {
            var previousUsed = item.Used;
            item.Used += amount;
            if (item.Used > previousUsed && item.Used != Math.Min(item.Done + amount, item.Amount))
                amount = Math.Max(0, amount - (item.Done - previousUsed));
            item.Done += amount;
            if (item.Used > item.Amount) item.Used = item.Amount;
            if (item.Used < 0) item.Used = 0;
        }
        else
        {
            item.Done += amount;
        }
        if (item.Done > item.Amount) item.Done = item.Amount;
        if (item.Done < 0) item.Done = 0;

        var newDone = item.AmountNeeded - AbsoluteCeil((item.Amount - item.Done) / item.Yield);
        amount = newDone - previousDone;
        if (item.Requires == null || newDone == previousDone) return;

        foreach (var requirement in item.Requires)
        {
            var requirementItem = GetItemById(finalItems, items, requirement.Id, excludeFinalItems, false, null);
            if (requirementItem == null) continue;

            var nextAmount = requirement.Amount * amount;
            // Not a precraft: take its yield into account.
            if (requirementItem.Requires == null)
                nextAmount = AbsoluteCeil(nextAmount / requirementItem.Yield);

            // Only propagate when everything goes the same direction (all additions or all removals).
            if ((nextAmount <= 0) == (newDone - previousDone <= 0)
                && (nextAmount <= 0) == (initialAddition <= 0)
                && (newDone - previousDone <= 0) == (initialAddition <= 0))
            {
                SetDone(finalItems, items, requirement.Id, nextAmount, true, false, true, null, initialAddition);
            }
        }
    }

    // Port of ListController.getItemById.
    private static Row? GetItemById(List<Row> finalItems, List<Row> items, string id, bool excludeFinalItems, bool onlyFinalItems, string? recipeId)
    {
        IEnumerable<Row> candidates = items.Where(i => !i.FinalItemFlag);
        if (!excludeFinalItems && !onlyFinalItems) candidates = candidates.Concat(finalItems);
        if (onlyFinalItems) candidates = finalItems;
        return candidates.FirstOrDefault(r => SameId(r.Id, id) && (string.IsNullOrEmpty(recipeId) || r.RecipeId == recipeId));
    }

    // JavaScript's "row.id === id || row.id === +id".
    private static bool SameId(string a, string b) =>
        a == b || (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                   && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y);

    // Teamcraft's MathTools.absoluteCeil: rounds away from zero.
    private static double AbsoluteCeil(double n) => n >= 0 ? Math.Ceiling(n) : Math.Floor(n);

    private static List<Row> ReadRows(JsonObject fields, string name, bool inFinalItems)
    {
        if (fields[name]?["arrayValue"]?["values"] is not JsonArray values) return [];
        var rows = new List<Row>();
        foreach (var value in values.OfType<JsonObject>())
        {
            if (value["mapValue"]?["fields"] is not JsonObject f) continue;
            var decoded = DecodeMap(f);
            var amount = Number(decoded["amount"]);
            var yield = Number(decoded["yield"], 1);
            rows.Add(new Row
            {
                Fields = f,
                InFinalItems = inFinalItems,
                FinalItemFlag = decoded["finalItem"] is JsonValue flag && flag.TryGetValue<bool>(out var isFinal) && isFinal,
                Id = decoded["id"]?.ToString() ?? string.Empty,
                RecipeId = decoded["recipeId"]?.ToString(),
                Amount = amount,
                Done = Number(decoded["done"]),
                Used = Number(decoded["used"]),
                Yield = yield,
                AmountNeeded = decoded["amount_needed"] != null ? Number(decoded["amount_needed"]) : Math.Ceiling(amount / yield),
                Requires = (decoded["requires"] as JsonArray)?.OfType<JsonObject>()
                    .Select(r => (r["id"]?.ToString() ?? string.Empty, Number(r["amount"])))
                    .ToList(),
            });
        }
        return rows;
    }

    private static JsonObject DecodeMap(JsonObject firestoreFields) =>
        FirestoreJson.DecodeFields(JsonDocument.Parse(new JsonObject { ["fields"] = firestoreFields.DeepClone() }.ToJsonString()).RootElement);

    private static double Number(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        return fallback;
    }

    private static JsonObject EncodeNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? new JsonObject { ["integerValue"] = ((long)value).ToString(CultureInfo.InvariantCulture) }
            : new JsonObject { ["doubleValue"] = value };
}
