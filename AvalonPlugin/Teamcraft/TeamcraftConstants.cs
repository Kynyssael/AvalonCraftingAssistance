namespace AvalonPlugin.Teamcraft;

internal static class TeamcraftConstants
{
    // Public Firebase web config, from Teamcraft's apps/client/src/environments/environment.prod.ts.
    public const string ApiKey = "AIzaSyDZPSJj-nXHLIATOz3IBESUTFk8zvFaUc0";
    public const string ProjectId = "ffxivteamcraft";
    public const string FirestoreBase = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents";

    // Teamcraft's Lodestone service, used by Teamcraft itself to show character names (lodestone.service.ts).
    public const string LodestoneBase = "https://lodestone.ffxivteamcraft.com";

    // Google login used by the Teamcraft desktop app (apps/client/src/app/core/auth/oauth.service.ts).
    // Google only redirects to the URI registered for this client, so the port is fixed.
    public const string GoogleClientId = "1082504004791-qjnubk6kj80kfvn3mg86lmu6eba16c6l.apps.googleusercontent.com";
    public const string GoogleAuthorizeUrl = "https://accounts.google.com/o/oauth2/auth";
    public const string GoogleScope = "https://www.googleapis.com/auth/userinfo.profile";
    public const string GoogleCodeExchangeUrl = "https://us-central1-ffxivteamcraft.cloudfunctions.net/google-oauth";
    public const int OAuthPort = 14500;
    public const string OAuthRedirectUri = "http://localhost:14500/oauth";
}
