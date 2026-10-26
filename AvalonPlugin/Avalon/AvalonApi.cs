using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvalonPlugin.Avalon;

public sealed record AvalonQuoteInfo(string Status, string Price);

public sealed record AvalonTeamcraftLink(string ListId, string? ListName);

/// <summary>A website request (ticket) that can be quoted: accepted or in progress, ticket still open.</summary>
public sealed record AvalonRequest(
    string Id, int Number, string Client, string Service, string? Artist, string Status,
    AvalonQuoteInfo? Quote, AvalonTeamcraftLink? Teamcraft, bool HasTicket);

public sealed record QuoteDraft(string Service, string Price, string Details, string DueDate, string? TeamcraftListId, string? TeamcraftListName);

public sealed record QuoteSent(int Number, bool PostedToTicket);

public sealed class AvalonException(string message) : Exception(message);

/// <summary>The Avalon website's plugin endpoints for quotes (see Avalon/server/index.js), signed with the Avalon key.</summary>
public sealed class AvalonApi(HttpClient http, AvalonKeyService key)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AvalonRequest>> GetQuotableRequestsAsync(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, "/api/plugin/requests");
        using var response = await http.SendAsync(request, ct);
        await ThrowIfFailed(response, ct);
        return await response.Content.ReadFromJsonAsync<List<AvalonRequest>>(Json, ct) ?? [];
    }

    public async Task<QuoteSent> SendQuoteAsync(string requestId, QuoteDraft draft, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"/api/plugin/requests/{Uri.EscapeDataString(requestId)}/quote");
        request.Content = JsonContent.Create(draft, options: Json);
        using var response = await http.SendAsync(request, ct);
        await ThrowIfFailed(response, ct);
        return await response.Content.ReadFromJsonAsync<QuoteSent>(Json, ct) ?? new QuoteSent(0, false);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, AvalonKeyService.BaseUrl + path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key ?? throw new AvalonException("Enter your Avalon key first."));
        return message;
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
        }
        throw new AvalonException(error ?? $"The Avalon website returned an error ({(int)response.StatusCode}).");
    }
}
