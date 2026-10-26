using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Dalamud.Utility;

namespace AvalonPlugin.Teamcraft;

/// <summary>
/// Signs in to Teamcraft's Firebase project, either with email/password or with Google
/// (the same flow as the Teamcraft desktop app), and keeps the ID token fresh.
/// </summary>
public sealed class TeamcraftAuth
{
    private const string IdentityUrl = "https://identitytoolkit.googleapis.com/v1/accounts";
    private const string RefreshUrl = "https://securetoken.googleapis.com/v1/token";

    private readonly HttpClient http;
    private readonly Configuration config;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    private string? idToken;
    private DateTimeOffset idTokenExpiry;
    private string? refreshToken;

    public TeamcraftAuth(HttpClient http, Configuration config)
    {
        this.http = http;
        this.config = config;
        Uid = config.TeamcraftUid;
        refreshToken = SecretStore.Unprotect(config.TeamcraftRefreshToken);
    }

    /// <summary>Teamcraft user id of the signed-in account.</summary>
    public string? Uid { get; private set; }

    public bool IsSignedIn => Uid != null && refreshToken != null;

    public async Task SignInWithPasswordAsync(string email, string password, CancellationToken ct)
    {
        var body = new { email, password, returnSecureToken = true };
        using var response = await http.PostAsJsonAsync($"{IdentityUrl}:signInWithPassword?key={TeamcraftConstants.ApiKey}", body, ct);
        await ApplySignInResponseAsync(response, ct);
    }

    public async Task SignInWithGoogleAsync(CancellationToken ct)
    {
        var code = await WaitForGoogleCodeAsync(ct);

        // Teamcraft's cloud function holds the client secret and swaps the code for a Google access token.
        var exchangeUrl = $"{TeamcraftConstants.GoogleCodeExchangeUrl}?code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(TeamcraftConstants.OAuthRedirectUri)}";
        using var exchange = await http.GetAsync(exchangeUrl, ct);
        if (!exchange.IsSuccessStatusCode)
            throw new TeamcraftException($"Google sign-in failed at the token exchange ({(int)exchange.StatusCode}).");
        using var exchangeJson = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync(ct));
        var accessToken = exchangeJson.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new TeamcraftException("Google sign-in failed: no access token returned.");

        var body = new
        {
            postBody = $"access_token={accessToken}&providerId=google.com",
            requestUri = "http://localhost",
            returnSecureToken = true,
            returnIdpCredential = true,
        };
        using var response = await http.PostAsJsonAsync($"{IdentityUrl}:signInWithIdp?key={TeamcraftConstants.ApiKey}", body, ct);
        await ApplySignInResponseAsync(response, ct);
    }

    public void SignOut()
    {
        Uid = null;
        idToken = null;
        refreshToken = null;
        config.TeamcraftUid = null;
        config.TeamcraftRefreshToken = null;
        config.Save();
    }

    /// <summary>Returns a valid ID token, refreshing it if needed, or null when signed out.</summary>
    public async Task<string?> GetIdTokenAsync(CancellationToken ct)
    {
        if (!IsSignedIn) return null;
        if (idToken != null && DateTimeOffset.UtcNow < idTokenExpiry) return idToken;

        await refreshLock.WaitAsync(ct);
        try
        {
            if (idToken != null && DateTimeOffset.UtcNow < idTokenExpiry) return idToken;

            using var content = new FormUrlEncodedContent([
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken!),
            ]);
            using var response = await http.PostAsync($"{RefreshUrl}?key={TeamcraftConstants.ApiKey}", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = ReadFirebaseError(json);
                if (message is "TOKEN_EXPIRED" or "USER_DISABLED" or "USER_NOT_FOUND" or "INVALID_REFRESH_TOKEN")
                    SignOut();
                throw new TeamcraftException($"Teamcraft session expired, please sign in again ({message}).");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            SetTokens(
                root.GetProperty("user_id").GetString()!,
                root.GetProperty("id_token").GetString()!,
                root.GetProperty("refresh_token").GetString()!,
                root.GetProperty("expires_in").GetString()!);
            return idToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task ApplySignInResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new TeamcraftException(FriendlyError(ReadFirebaseError(json)));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        SetTokens(
            root.GetProperty("localId").GetString()!,
            root.GetProperty("idToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("expiresIn").GetString()!);
    }

    private void SetTokens(string uid, string newIdToken, string newRefreshToken, string expiresInSeconds)
    {
        Uid = uid;
        idToken = newIdToken;
        // Refresh a minute early so requests never go out with an expiring token.
        idTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(int.Parse(expiresInSeconds) - 60);

        if (newRefreshToken != refreshToken || config.TeamcraftUid != uid)
        {
            refreshToken = newRefreshToken;
            config.TeamcraftUid = uid;
            config.TeamcraftRefreshToken = SecretStore.Protect(newRefreshToken);
            config.Save();
        }
    }

    /// <summary>Opens Google's consent page and waits for the redirect to localhost:14500.</summary>
    private static async Task<string> WaitForGoogleCodeAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, TeamcraftConstants.OAuthPort);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new TeamcraftException($"Port {TeamcraftConstants.OAuthPort} is in use. Close the Teamcraft desktop app and try again.");
        }

        try
        {
            var authUrl = $"{TeamcraftConstants.GoogleAuthorizeUrl}?response_type=code" +
                          $"&redirect_uri={Uri.EscapeDataString(TeamcraftConstants.OAuthRedirectUri)}" +
                          $"&client_id={Uri.EscapeDataString(TeamcraftConstants.GoogleClientId)}" +
                          $"&scope={Uri.EscapeDataString(TeamcraftConstants.GoogleScope)}";
            Util.OpenLink(authUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                // "GET /oauth?code=... HTTP/1.1", then headers until an empty line.
                var requestLine = await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/oauth", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, "404 Not Found", "Not found.", timeout.Token);
                    continue;
                }

                var query = HttpUtility.ParseQueryString(new Uri("http://localhost" + parts[1]).Query);
                var code = query["code"];
                if (string.IsNullOrEmpty(code))
                {
                    await RespondAsync(stream, "400 Bad Request", "Sign-in was cancelled. You can close this tab.", timeout.Token);
                    throw new TeamcraftException($"Google sign-in was cancelled ({query["error"] ?? "no code"}).");
                }

                await RespondAsync(stream, "200 OK", "Signed in to Avalon. You can close this tab and return to the game.", timeout.Token);
                return code;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TeamcraftException("Google sign-in timed out.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondAsync(Stream stream, string status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><meta charset=\"utf-8\"><title>Avalon</title><p style=\"font-family:sans-serif\">{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static string ReadFirebaseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "UNKNOWN";
        }
        catch (Exception)
        {
            return "UNKNOWN";
        }
    }

    private static string FriendlyError(string firebaseMessage) => firebaseMessage switch
    {
        "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" or "EMAIL_NOT_FOUND" => "Wrong email or password.",
        "USER_DISABLED" => "This Teamcraft account is disabled.",
        var m when m.StartsWith("TOO_MANY_ATTEMPTS", StringComparison.Ordinal) => "Too many attempts, try again later.",
        var m => $"Sign-in failed ({m}).",
    };
}
