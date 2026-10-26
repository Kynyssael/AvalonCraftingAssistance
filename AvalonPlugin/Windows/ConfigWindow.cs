using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AvalonPlugin.Avalon;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

namespace AvalonPlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Loadable<string> signIn = new();
    private CancellationTokenSource? signInCancel;
    private string email = string.Empty;
    private string password = string.Empty;
    private string avalonKeyInput = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Avalon Crafting Assistance Settings###AvalonConfig")
    {
        Size = new Vector2(420, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() => signInCancel?.Cancel();

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##configTabs")) return;

        if (ImGui.BeginTabItem("Avalon key"))
        {
            DrawAvalonKey();
            ImGui.EndTabItem();
        }

        // Everything else stays hidden until the Avalon website accepts the key.
        if (!plugin.AvalonKey.IsUnlocked)
        {
            ImGui.EndTabBar();
            return;
        }

        if (ImGui.BeginTabItem("Teamcraft account"))
        {
            DrawAccount();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawAvalonKey()
    {
        var key = plugin.AvalonKey;

        switch (key.State)
        {
            case AvalonKeyState.Valid:
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Key accepted{(key.CrafterName != null ? $" for {key.CrafterName}" : string.Empty)}.");
                if (key.CanQuote)
                    ImGui.TextColored(ImGuiColors.HealerGreen, "You can send quotes to tickets.");
                else
                    ImGui.TextDisabled("Sending quotes: not allowed (website admins, managers and the Discord Staff role only).");
                break;
            case AvalonKeyState.Checking:
                ImGui.Text("Checking key…");
                break;
            case AvalonKeyState.NoKey:
                ImGui.TextWrapped("Sign in on the Avalon website, generate your personal key, and paste it here.");
                break;
            default:
                ImGui.TextColored(ImGuiColors.DalamudRed, key.Message ?? "Key not accepted.");
                break;
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(280);
        var submitted = ImGui.InputTextWithHint("##avalonKey", key.HasKey ? "Paste a new key to replace yours" : "Avalon key",
                                                ref avalonKeyInput, 256, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Save key") || submitted) && !string.IsNullOrWhiteSpace(avalonKeyInput))
        {
            key.SetKey(avalonKeyInput);
            avalonKeyInput = string.Empty;
        }

        if (key.HasKey)
        {
            if (ImGui.Button("Check again"))
                key.StartCheck();
            ImGui.SameLine();
            if (ImGui.Button("Remove key"))
                key.RemoveKey();
        }
    }

    private void DrawAccount()
    {
        var auth = plugin.TeamcraftAuth;

        if (auth.IsSignedIn)
        {
            ImGui.Text($"Signed in as {plugin.Teamcraft.GetDisplayName(auth.Uid!)}");
            ImGui.TextDisabled($"Teamcraft user id: {auth.Uid}");
            if (ImGui.Button("Sign out"))
            {
                auth.SignOut();
                signIn.Clear();
            }
            return;
        }

        ImGui.TextWrapped("Sign in with the account you use on Teamcraft. Your password is not stored.");
        ImGui.Spacing();

        var busy = signIn.IsLoading;
        if (busy) ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(240);
        ImGui.InputText("Email", ref email, 128);
        ImGui.SetNextItemWidth(240);
        ImGui.InputText("Password", ref password, 128, ImGuiInputTextFlags.Password);

        var canSubmit = email.Contains('@') && password.Length > 0;
        if (!canSubmit) ImGui.BeginDisabled();
        if (ImGui.Button("Sign in"))
        {
            var e = email.Trim();
            var p = password;
            password = string.Empty;
            StartSignIn(ct => auth.SignInWithPasswordAsync(e, p, ct));
        }
        if (!canSubmit) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Sign in with Google"))
            StartSignIn(auth.SignInWithGoogleAsync);
        ImGui.TextDisabled("Opens your browser. Close the Teamcraft desktop app first.");

        if (busy) ImGui.EndDisabled();

        if (busy)
        {
            ImGui.Text("Signing in… finish in your browser if using Google.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                signInCancel?.Cancel();
        }

        if (signIn.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, signIn.Error);
    }

    private void StartSignIn(Func<CancellationToken, Task> signInAction)
    {
        signInCancel?.Cancel();
        var cancel = signInCancel = new CancellationTokenSource();
        signIn.Start(async () =>
        {
            await signInAction(cancel.Token);
            return plugin.TeamcraftAuth.Uid!;
        });
    }
}
