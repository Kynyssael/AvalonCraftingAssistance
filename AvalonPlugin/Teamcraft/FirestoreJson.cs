using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvalonPlugin.Teamcraft;

/// <summary>
/// Converts Firestore REST documents ({"stringValue": "x"}, {"integerValue": "1"}, ...) into plain JSON.
/// </summary>
internal static class FirestoreJson
{
    public static string DocumentId(JsonElement document)
    {
        var name = document.GetProperty("name").GetString() ?? string.Empty;
        return name[(name.LastIndexOf('/') + 1)..];
    }

    /// <summary>Decodes the "fields" of a document or of a mapValue.</summary>
    public static JsonObject DecodeFields(JsonElement documentOrMap)
    {
        var result = new JsonObject();
        if (documentOrMap.TryGetProperty("fields", out var fields))
        {
            foreach (var field in fields.EnumerateObject())
                result[field.Name] = DecodeValue(field.Value);
        }
        return result;
    }

    public static JsonNode? DecodeValue(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            var inner = property.Value;
            switch (property.Name)
            {
                case "nullValue":
                    return null;
                case "booleanValue":
                    return JsonValue.Create(inner.GetBoolean());
                case "integerValue":
                    return JsonValue.Create(long.Parse(inner.GetString()!, CultureInfo.InvariantCulture));
                case "doubleValue":
                    return inner.ValueKind == JsonValueKind.Number ? JsonValue.Create(inner.GetDouble()) : null;
                case "mapValue":
                    return DecodeFields(inner);
                case "arrayValue":
                    var array = new JsonArray();
                    if (inner.TryGetProperty("values", out var values))
                    {
                        foreach (var item in values.EnumerateArray())
                            array.Add(DecodeValue(item));
                    }
                    return array;
                default:
                    // stringValue, timestampValue, referenceValue, bytesValue
                    return inner.ValueKind == JsonValueKind.String ? JsonValue.Create(inner.GetString()) : null;
            }
        }
        return null;
    }

    public static long ToLong(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }
}
