using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AvalonPlugin.Teamcraft;

/// <summary>Reads Teamcraft lists, teams and users through the Firestore REST API.</summary>
public sealed partial class TeamcraftClient(HttpClient http, TeamcraftAuth auth, Configuration config)
{
    private readonly ConcurrentDictionary<string, string> displayNames = new();
    private volatile bool namesChanged;

    /// <summary>Accepts a list link (https://ffxivteamcraft.com/list/ID) or a bare list ID.</summary>
    public static string? ParseListId(string linkOrId)
    {
        var match = ListIdRegex().Match(linkOrId.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    public async Task<TeamcraftList> GetListAsync(string listId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"lists/{Uri.EscapeDataString(listId)}", null, ct)
                        ?? throw new TeamcraftException("List not found. Check the link.");
        return ParseList(doc.RootElement);
    }

    /// <summary>
    /// When the list was last saved, without downloading its items (only the tiny "etag" field is fetched).
    /// Used to poll for changes made in Teamcraft.
    /// </summary>
    public async Task<string?> GetListUpdateTimeAsync(string listId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"lists/{Uri.EscapeDataString(listId)}", null, ct, "&mask.fieldPaths=etag");
        return doc?.RootElement.GetProperty("updateTime").GetString();
    }

    /// <summary>
    /// Changes who is working on some rows of a list, the way Teamcraft's own "assign" does: it only touches
    /// each row's workingOnIt and bumps the list's etag so open Teamcraft windows pick the change up.
    /// </summary>
    public Task<TeamcraftList> SetAssigneesAsync(string listId, IReadOnlyList<RowAssignment> changes, CancellationToken ct) =>
        UpdateListAsync(listId, doc => ListAssignments.BuildUpdate(doc, changes, DateTimeOffset.UtcNow), ct);

    /// <summary>
    /// Marks items as (partly) done the way Teamcraft does, including marking ingredients of finished crafts as used.
    /// Like Teamcraft, it is applied to a fresh copy of the list so concurrent progress isn't lost.
    /// </summary>
    public Task<TeamcraftList> SetDoneAsync(string listId, IReadOnlyList<DoneChange> changes, CancellationToken ct) =>
        UpdateListAsync(listId, doc => ListProgress.BuildUpdate(doc, changes, DateTimeOffset.UtcNow), ct);

    /// <summary>Reads the list, builds an update from it, and saves only if the list didn't change meanwhile (else retries).</summary>
    private async Task<TeamcraftList> UpdateListAsync(
        string listId, Func<JsonElement, (JsonObject Body, IReadOnlyList<string> FieldPaths)> buildUpdate, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var path = $"lists/{Uri.EscapeDataString(listId)}";
            using var current = await SendAsync(HttpMethod.Get, path, null, ct)
                                ?? throw new TeamcraftException("This list no longer exists.");
            var (body, fieldPaths) = buildUpdate(current.RootElement);
            var updateTime = current.RootElement.GetProperty("updateTime").GetString()!;

            var query = string.Concat(fieldPaths.Select(f => $"&updateMask.fieldPaths={f}")) +
                        $"&currentDocument.updateTime={Uri.EscapeDataString(updateTime)}";
            try
            {
                using var saved = await SendAsync(HttpMethod.Patch, path, JsonContent.Create(body), ct, query);
                return ParseList(saved!.RootElement);
            }
            catch (TeamcraftConflictException) when (attempt < attempts)
            {
                // Someone edited the list in the meantime: re-read it and apply our change on top.
            }
        }
    }

    private static TeamcraftList ParseList(JsonElement root)
    {
        var fields = FirestoreJson.DecodeFields(root);
        return new TeamcraftList(
            FirestoreJson.DocumentId(root),
            fields["name"]?.ToString() ?? "(unnamed list)",
            fields["teamId"]?.ToString(),
            ReadRows(fields["finalItems"]),
            ReadRows(fields["items"]),
            root.GetProperty("updateTime").GetString()!);
    }

    /// <summary>Teams the signed-in user is a member of.</summary>
    public async Task<IReadOnlyList<TeamcraftTeam>> GetMyTeamsAsync(CancellationToken ct)
    {
        var uid = auth.Uid ?? throw new TeamcraftException("Sign in to Teamcraft to see your teams.");
        var docs = await RunQueryAsync("teams", "members", "ARRAY_CONTAINS", uid, null, ct);
        return docs.Select(d => new TeamcraftTeam(
                d.Id,
                d.Fields["name"]?.ToString() ?? "(unnamed team)",
                d.Fields["leader"]?.ToString() ?? string.Empty,
                ReadStrings(d.Fields["members"]),
                ReadStrings(d.Fields["officers"])))
            .ToList();
    }

    /// <summary>Non-archived lists attached to a team, sorted by name.</summary>
    /// <remarks>Also counts the items assigned to the signed-in user, so crafters can see only their work.</remarks>
    public Task<IReadOnlyList<TeamcraftListSummary>> GetTeamListsAsync(string teamId, CancellationToken ct) =>
        GetListSummariesAsync("teamId", teamId, true, ct);

    /// <summary>Non-archived lists the signed-in user created, sorted by name.</summary>
    public Task<IReadOnlyList<TeamcraftListSummary>> GetMyListsAsync(CancellationToken ct) =>
        GetListSummariesAsync("authorId", auth.Uid ?? throw new TeamcraftException("Sign in to Teamcraft to see your lists."), false, ct);

    private async Task<IReadOnlyList<TeamcraftListSummary>> GetListSummariesAsync(string field, string value, bool countMyItems, CancellationToken ct)
    {
        // Only fetch the fields the drop-down needs; the item rows only when counting assignments.
        string[] select = countMyItems ? ["name", "teamId", "archived", "finalItems", "items"] : ["name", "teamId", "archived"];
        var docs = await RunQueryAsync("lists", field, "EQUAL", value, select, ct);
        var uid = auth.Uid;
        return docs
            .Where(d => !(d.Fields["archived"] is JsonValue archived && archived.TryGetValue<bool>(out var isArchived) && isArchived))
            .Select(d =>
            {
                var mine = ReadRows(d.Fields["finalItems"]).Concat(ReadRows(d.Fields["items"]))
                    .Where(r => uid != null && r.WorkingOnIt.Contains(uid))
                    .ToList();
                return new TeamcraftListSummary(
                    d.Id, d.Fields["name"]?.ToString() ?? "(unnamed list)", d.Fields["teamId"]?.ToString(),
                    mine.Count, mine.Count(r => r.Done < r.Amount));
            })
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Runs a single-filter Firestore query and returns each document's id and decoded fields.</summary>
    private async Task<List<(string Id, JsonObject Fields)>> RunQueryAsync(
        string collection, string field, string op, string value, string[]? selectFields, CancellationToken ct)
    {
        var structuredQuery = new Dictionary<string, object>
        {
            ["from"] = new[] { new { collectionId = collection } },
            ["where"] = new
            {
                fieldFilter = new
                {
                    field = new { fieldPath = field },
                    op,
                    value = new { stringValue = value },
                },
            },
        };
        if (selectFields != null)
            structuredQuery["select"] = new { fields = selectFields.Select(f => new { fieldPath = f }).ToArray() };

        using var doc = await SendAsync(HttpMethod.Post, ":runQuery", JsonContent.Create(new { structuredQuery }), ct);
        var results = new List<(string, JsonObject)>();
        foreach (var result in doc!.RootElement.EnumerateArray())
        {
            if (result.TryGetProperty("document", out var found))
                results.Add((FirestoreJson.DocumentId(found), FirestoreJson.DecodeFields(found)));
        }
        return results;
    }

    /// <summary>
    /// Returns the character name for a Teamcraft user id. Shows the name remembered from last time
    /// (or a short id) right away and refreshes it in the background once per session.
    /// </summary>
    public string GetDisplayName(string uid)
    {
        if (displayNames.TryGetValue(uid, out var name)) return name;

        var saved = config.TeamcraftNames.TryGetValue(uid, out var remembered) ? remembered : null;
        var shown = saved ?? (uid.Length > 6 ? uid[..6] + "…" : uid);
        if (displayNames.TryAdd(uid, shown))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var fetched = await FetchDisplayNameAsync(uid, CancellationToken.None);
                    if (fetched == null || fetched == saved) return;
                    displayNames[uid] = fetched;
                    config.TeamcraftNames[uid] = fetched;
                    namesChanged = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.Warning(e, $"Could not load Teamcraft user {uid}");
                }
            });
        }
        return shown;
    }

    /// <summary>Saves newly learned names. Called from the UI thread so config saves never overlap.</summary>
    public void SaveNamesIfChanged()
    {
        if (!namesChanged) return;
        namesChanged = false;
        config.Save();
    }

    private async Task<string?> FetchDisplayNameAsync(string uid, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"users/{Uri.EscapeDataString(uid)}", null, ct);
        if (doc == null) return null;
        var fields = FirestoreJson.DecodeFields(doc.RootElement);

        // Same order as Teamcraft's LodestoneService.getUserCharacter: a negative default id is a character
        // added by hand (customCharacters); otherwise the default Lodestone id, else the first linked one.
        var defaultId = FirestoreJson.ToLong(fields["defaultLodestoneId"]);
        string? characterName = null;
        string? world = null;
        if (defaultId < 0)
        {
            var custom = (fields["customCharacters"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(c => FirestoreJson.ToLong(c["ID"]) == defaultId);
            characterName = custom?["Name"]?.ToString();
            world = custom?["Server"]?.ToString();
        }
        else
        {
            var linked = (fields["lodestoneIds"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var lodestoneId = defaultId > 0 ? defaultId : linked.Select(c => FirestoreJson.ToLong(c["id"])).FirstOrDefault(id => id > 0);
            // Teamcraft doesn't store the name, only the Lodestone id, so look it up like Teamcraft does.
            if (lodestoneId > 0)
                (characterName, world) = await FetchLodestoneCharacterAsync(lodestoneId, ct);
        }

        if (!string.IsNullOrEmpty(characterName))
            return string.IsNullOrEmpty(world) ? characterName : $"{characterName} @ {world}";

        var nickname = fields["nickname"]?.ToString();
        return string.IsNullOrWhiteSpace(nickname) ? null : nickname;
    }

    /// <summary>Character name and world from Teamcraft's Lodestone service; nulls if private or unknown.</summary>
    private async Task<(string? Name, string? World)> FetchLodestoneCharacterAsync(long lodestoneId, CancellationToken ct)
    {
        // No "columns" filter: the service then returns only the ID.
        var url = $"{TeamcraftConstants.LodestoneBase}/Character/{lodestoneId}";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("Character", out var character)) return (null, null);
        var name = character.TryGetProperty("Name", out var n) ? n.GetString() : null;
        // Usually just "Gilgamesh"; strip a " [Aether]" suffix if one is ever added.
        var world = character.TryGetProperty("World", out var w) ? w.GetString()?.Split('[')[0].Trim() : null;
        return (name, world);
    }

    /// <summary>Sends a Firestore request; returns null on 404.</summary>
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct, string extraQuery = "")
    {
        var separator = path.StartsWith(':') ? string.Empty : "/";
        using var request = new HttpRequestMessage(method, $"{TeamcraftConstants.FirestoreBase}{separator}{path}?key={TeamcraftConstants.ApiKey}{extraQuery}")
        {
            Content = content,
        };
        var token = await auth.GetIdTokenAsync(ct);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TeamcraftException("Teamcraft refused access (are you signed in with the right account?).");
            // A failed currentDocument.updateTime check: the document changed since we read it.
            if (response.StatusCode == HttpStatusCode.Conflict || json.Contains("FAILED_PRECONDITION", StringComparison.Ordinal))
                throw new TeamcraftConflictException();
            throw new TeamcraftException($"Teamcraft request failed ({(int)response.StatusCode}): {ReadFirestoreError(json)}");
        }
        return JsonDocument.Parse(json);
    }

    private static List<TeamcraftListRow> ReadRows(JsonNode? node) =>
        (node as JsonArray)?.OfType<JsonObject>()
            .Select(row => new TeamcraftListRow(
                row["id"]?.ToString() ?? string.Empty,
                row["recipeId"]?.ToString(),
                (int)FirestoreJson.ToLong(row["amount"]),
                (int)FirestoreJson.ToLong(row["done"]),
                ReadStrings(row["workingOnIt"]),
                row["requires"] is JsonArray { Count: > 0 }))
            .ToList() ?? [];

    private static List<string> ReadStrings(JsonNode? node) =>
        (node as JsonArray)?.Select(n => n?.ToString()).OfType<string>().ToList() ?? [];

    private static string ReadFirestoreError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            return root.GetProperty("error").GetProperty("message").GetString() ?? json;
        }
        catch (Exception)
        {
            return json.Length > 200 ? json[..200] : json;
        }
    }

    [GeneratedRegex(@"^(?:.*/list/)?(?<id>[A-Za-z0-9_-]{10,})(?:[/?#].*)?$")]
    private static partial Regex ListIdRegex();
}
