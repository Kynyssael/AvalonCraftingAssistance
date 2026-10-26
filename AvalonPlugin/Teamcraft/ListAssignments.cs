using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvalonPlugin.Teamcraft;

/// <summary>Builds the Firestore update that changes who is working on rows of a Teamcraft list.</summary>
internal static class ListAssignments
{
    /// <summary>
    /// Takes the list document exactly as Firestore returned it and changes only workingOnIt on the given rows,
    /// leaving every other row field untouched. Returns the PATCH body and the field paths for its update mask.
    /// </summary>
    public static (JsonObject Body, IReadOnlyList<string> FieldPaths) BuildUpdate(
        JsonElement document, IReadOnlyList<RowAssignment> changes, DateTimeOffset now)
    {
        var fields = JsonNode.Parse(document.GetProperty("fields").GetRawText())!.AsObject();
        var bodyFields = new JsonObject();
        var paths = new List<string>();

        foreach (var (finalItem, name) in new[] { (true, "finalItems"), (false, "items") })
        {
            var rowChanges = changes.Where(c => c.FinalItem == finalItem).ToList();
            if (rowChanges.Count == 0 || fields[name]?["arrayValue"]?["values"] is not JsonArray rows)
                continue;

            var changed = false;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row["mapValue"]?["fields"] is not JsonObject rowFields) continue;
                var change = rowChanges.LastOrDefault(c => c.ItemId == RowId(rowFields));
                if (change == null) continue;
                rowFields["workingOnIt"] = EncodeStrings(change.WorkingOnIt);
                changed = true;
            }

            if (changed)
            {
                bodyFields[name] = fields[name]!.DeepClone();
                paths.Add(name);
            }
        }

        if (paths.Count == 0)
            throw new TeamcraftException("Those items are no longer in the list.");

        // Teamcraft only accepts an incoming list if its etag is higher than the one it has (it uses Date.now()).
        var currentEtag = document.GetProperty("fields").TryGetProperty("etag", out var etag)
            ? FirestoreJson.ToLong(FirestoreJson.DecodeValue(etag))
            : 0;
        var newEtag = Math.Max(now.ToUnixTimeMilliseconds(), currentEtag + 1);
        bodyFields["etag"] = new JsonObject { ["integerValue"] = newEtag.ToString(CultureInfo.InvariantCulture) };
        paths.Add("etag");

        return (new JsonObject { ["fields"] = bodyFields }, paths);
    }

    /// <summary>The row's item id as text, whether Firestore stored it as a number or a string.</summary>
    private static string? RowId(JsonObject rowFields) =>
        (rowFields["id"] as JsonObject)?.FirstOrDefault().Value?.ToString();

    private static JsonObject EncodeStrings(IReadOnlyList<string> values)
    {
        var arrayValue = new JsonObject();
        if (values.Count > 0)
            arrayValue["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["stringValue"] = v }).ToArray());
        return new JsonObject { ["arrayValue"] = arrayValue };
    }
}
