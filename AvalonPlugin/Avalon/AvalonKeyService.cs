using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvalonPlugin.Avalon;

public enum AvalonKeyState
{
    NoKey,
    Checking,
    Valid,
    Invalid,
    Unreachable,
}

/// <summary>
/// Checks the crafter's personal key against the Avalon website (see docs/avalon-key-api.md).
/// The plugin stays locked until the website accepts the key, and re-checks it every hour so a
/// revoked key stops working.
/// </summary>
public sealed class AvalonKeyService : IDisposable
{
    public const string BaseUrl = "https://avalondesignsco.com";
    private const string VerifyPath = "/api/plugin/key/verify";
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    private readonly HttpClient http;
    private readonly Configuration config;
    private readonly Timer recheckTimer;
    private int checkVersion;

    public AvalonKeyService(HttpClient http, Configuration config)
    {
        this.http = http;
        this.config = config;
        recheckTimer = new Timer(_ => StartCheck(), null, RecheckInterval, RecheckInterval);
        StartCheck();
    }

    public AvalonKeyState State { get; private set; } = AvalonKeyState.NoKey;

    /// <summary>Crafter name returned by the website for a valid key.</summary>
    public string? CrafterName { get; private set; }

    public string? Message { get; private set; }

    public bool IsUnlocked => State == AvalonKeyState.Valid;

    /// <summary>Whether the website lets this key send quotes to tickets.</summary>
    public bool CanQuote { get; private set; }

    /// <summary>The saved key, for calling the website's plugin endpoints.</summary>
    internal string? Key => SecretStore.Unprotect(config.AvalonKey);

    public bool HasKey => SecretStore.Unprotect(config.AvalonKey) != null;

    public void SetKey(string key)
    {
        config.AvalonKey = SecretStore.Protect(key.Trim());
        config.Save();
        StartCheck();
    }

    public void RemoveKey()
    {
        config.AvalonKey = null;
        config.Save();
        StartCheck();
    }

    /// <summary>Checks the saved key in the background; only the newest check updates the state.</summary>
    public void StartCheck()
    {
        var version = Interlocked.Increment(ref checkVersion);
        var key = SecretStore.Unprotect(config.AvalonKey);
        if (key == null)
        {
            SetState(AvalonKeyState.NoKey, null, null);
            return;
        }

        // Keep the plugin usable while an hourly re-check of a valid key is running.
        if (State != AvalonKeyState.Valid)
            SetState(AvalonKeyState.Checking, null, null);

        _ = Task.Run(async () =>
        {
            var (state, name, message) = await VerifyAsync(key);
            if (version == Volatile.Read(ref checkVersion))
                SetState(state, name, message);
        });
    }

    private async Task<(AvalonKeyState State, string? Name, string? Message)> VerifyAsync(string key)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + VerifyPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, CancellationToken.None);
            var json = await response.Content.ReadAsStringAsync();

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (AvalonKeyState.Invalid, null, ReasonMessage(ReadString(json, "reason")));

            if (!response.IsSuccessStatusCode)
                return (AvalonKeyState.Unreachable, null, $"The Avalon website returned an error ({(int)response.StatusCode}).");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("valid", out var valid) || !valid.GetBoolean())
                return (AvalonKeyState.Invalid, null, ReasonMessage(ReadString(json, "reason")));

            // The website says whether this key's owner may send quotes (admins, managers, Staff role).
            CanQuote = root.TryGetProperty("canQuote", out var canQuote) && canQuote.ValueKind == JsonValueKind.True;
            return (AvalonKeyState.Valid, ReadString(json, "crafterName"), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Plugin.Log.Warning(e, "Avalon key check failed");
            return (AvalonKeyState.Unreachable, null, "Can't reach the Avalon website. Check your connection and retry.");
        }
    }

    private void SetState(AvalonKeyState state, string? name, string? message)
    {
        State = state;
        CrafterName = name;
        Message = message;
    }

    private static string ReasonMessage(string? reason) => reason switch
    {
        "revoked" => "This key was revoked. Generate a new one on the Avalon website.",
        "expired" => "This key has expired. Generate a new one on the Avalon website.",
        "not_in_team" => "Your Avalon account is no longer a team crafter.",
        _ => "The Avalon website did not accept this key.",
    };

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => recheckTimer.Dispose();
}
