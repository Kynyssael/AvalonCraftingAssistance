using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AvalonPlugin.Avalon;
using AvalonPlugin.Teamcraft;
using AvalonPlugin.Universalis;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AvalonPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Loadable<IReadOnlyList<TeamcraftTeam>> teams = new();
    private readonly Loadable<TeamcraftList> list = new();
    private readonly Loadable<ListChoices> listChoices = new();
    private readonly Loadable<IReadOnlyDictionary<uint, MarketPrice>> prices = new();
    private TeamcraftList? pricesFor;
    private (TeamcraftList? List, IReadOnlyDictionary<uint, MarketPrice>? Prices, bool Filtered) estimateFor;
    private BuyEstimate? estimate;
    private TeamcraftTeam? currentTeam;
    private bool onlyMyItems;
    private (string Text, DateTime At)? artisanMessage;
    private TeamcraftList? sectionsFor;
    private List<(ListSection Section, List<(bool Final, TeamcraftListRow Row)> Rows)> sections = [];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private Task<string?>? pollTask;
    private string? pollListId;
    private DateTime nextPoll;
    private DateTime? lastPolled;
    private int typedAmount;
    private bool focusAmountBox;
    private string listLink = string.Empty;
    private bool teamsRequested;
    private bool lastListRequested;
    // Team the drop-down's lists were loaded for ("" = no team); null = not loaded yet.
    private string? listChoicesTeamId;

    private sealed record ListChoices(IReadOnlyList<TeamcraftListSummary> TeamLists, IReadOnlyList<TeamcraftListSummary> MyLists);

    public MainWindow(Plugin plugin) : base("Avalon Crafting Assistance###AvalonMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.plugin = plugin;
    }

    public void Dispose() { }

    // Reload the list drop-down each time the window opens, so new assignments show up.
    public override void OnOpen() => listChoicesTeamId = null;

    public override void PreDraw() => Theme.Push();

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();
        ImGui.Spacing();

        if (!plugin.AvalonKey.IsUnlocked)
        {
            DrawLocked();
            return;
        }

        plugin.Teamcraft.SaveNamesIfChanged();
        DrawTeam();
        DrawList();
    }

    /// <summary>ADC logo, plugin name, Teamcraft account and a settings button.</summary>
    private void DrawHeader()
    {
        const float logoSize = 44;
        var logo = Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, "AvalonPlugin.logo.png").GetWrapOrEmpty();
        ImGui.Image(logo.Handle, new Vector2(logoSize, logoSize) * ImGuiHelpers.GlobalScale);
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextColored(Theme.Lavender, "Avalon Crafting Assistance");
        ImGui.SetWindowFontScale(1f);
        var auth = plugin.TeamcraftAuth;
        if (!plugin.AvalonKey.IsUnlocked)
            ImGui.TextColored(Theme.Muted, "Team crafters only");
        else if (auth.IsSignedIn)
            ImGui.TextColored(Theme.Muted, $"Teamcraft: {plugin.Teamcraft.GetDisplayName(auth.Uid!)}");
        else
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Not signed in to Teamcraft");
        ImGui.EndGroup();

        // Settings button, right-aligned.
        var buttonWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttonWidth);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            plugin.ToggleConfigUi();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(auth.IsSignedIn ? "Settings" : "Settings — sign in to Teamcraft here");
    }

    private void DrawLocked()
    {
        var key = plugin.AvalonKey;
        if (key.State == AvalonKeyState.Checking)
        {
            ImGui.Text("Checking your Avalon key…");
            return;
        }

        ImGui.TextWrapped("Avalon Crafting Assistance is only available to the team's crafters. Sign in on the Avalon website, generate your personal key, and enter it in the settings.");
        if (key.Message != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, key.Message);

        if (ImGui.Button("Enter key…"))
            plugin.ToggleConfigUi();
        if (key.State == AvalonKeyState.Unreachable)
        {
            ImGui.SameLine();
            if (ImGui.Button("Retry"))
                key.StartCheck();
        }
    }

    private void DrawTeam()
    {
        var config = plugin.Configuration;
        if (!plugin.TeamcraftAuth.IsSignedIn)
        {
            teamsRequested = false;
            teams.Clear();
            currentTeam = null;
            ImGui.TextDisabled("Sign in to load your Teamcraft teams.");
            return;
        }

        if (!teamsRequested)
        {
            teamsRequested = true;
            teams.Start(() => plugin.Teamcraft.GetMyTeamsAsync(CancellationToken.None));
        }

        var all = teams.Value ?? [];
        var selected = currentTeam = all.FirstOrDefault(t => t.Id == config.SelectedTeamId);

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Team", selected?.Name ?? (teams.IsLoading ? "Loading…" : "Choose a team")))
        {
            foreach (var team in all)
            {
                if (ImGui.Selectable($"{team.Name}##{team.Id}", team.Id == config.SelectedTeamId))
                {
                    config.SelectedTeamId = team.Id;
                    config.Save();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##refreshTeams", FontAwesomeIcon.SyncAlt))
            teams.Start(() => plugin.Teamcraft.GetMyTeamsAsync(CancellationToken.None));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reload teams");

        if (selected != null)
        {
            // Members on hover, to keep the toolbar compact.
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{selected.Members.Count} members");
            if (ImGui.IsItemHovered())
            {
                var members = selected.Members.Select(uid =>
                    plugin.Teamcraft.GetDisplayName(uid) +
                    (uid == selected.Leader ? "  (leader)" : selected.Officers.Contains(uid) ? "  (officer)" : string.Empty));
                ImGui.SetTooltip(string.Join("\n", members));
            }
            if (IsLeader || IsOfficer)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Gold, IsLeader ? "Leader" : "Officer");
            }
        }

        if (teams.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, teams.Error);
        else if (teams.Value is { Count: 0 })
            ImGui.TextDisabled("You are not in any Teamcraft team.");
    }

    private void DrawList()
    {
        if (!lastListRequested)
        {
            lastListRequested = true;
            if (plugin.Configuration.LastListId is { } lastId)
                OpenList(lastId);
        }

        DrawListPicker();

        if (ImGui.TreeNode("Open a list by link"))
        {
            ImGui.SetNextItemWidth(320);
            var submitted = ImGui.InputTextWithHint("##listLink", "Teamcraft list link", ref listLink, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Open") || submitted) && !list.IsLoading)
            {
                if (TeamcraftClient.ParseListId(listLink) is { } id)
                    OpenList(id);
                else
                    list.Start(() => throw new TeamcraftException("That doesn't look like a Teamcraft list link."));
            }
            ImGui.TreePop();
        }

        if (list.IsLoading) ImGui.TextDisabled("Loading…");
        if (list.Error != null) ImGui.TextColored(ImGuiColors.DalamudRed, list.Error);

        var current = list.Value;
        if (current == null) return;

        PollForChanges(current);

        // List title card: name, sync time, and overall progress.
        ImGui.Spacing();
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextColored(Theme.Gold, current.Name);
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, lastPolled is { } polled ? $"synced {polled:HH:mm:ss}" : "syncing…");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Changes made in Teamcraft show up here within about 10 seconds.");
        var allRows = current.FinalItems.Concat(current.Items).ToList();
        Theme.Progress(allRows.Count(r => r.Done >= r.Amount), allRows.Count, -1, suffix: " items done");
        var selectedTeamId = plugin.Configuration.SelectedTeamId;
        if (current.TeamId == null)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This list is not attached to a team in Teamcraft.");
        else if (selectedTeamId != null && current.TeamId != selectedTeamId)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This list belongs to a different team than the one selected.");

        DrawPriceStatus(current);
        DrawBulkAssign(current);
        DrawMyItemsBar(current);

        plugin.Inventory.Refresh();
        DrawSections(current);
    }

    /// <summary>The list divided like Teamcraft's default layout: final items, crystals, vendors, gathering, pre-crafts…</summary>
    private void DrawSections(TeamcraftList current)
    {
        if (!ReferenceEquals(sectionsFor, current))
        {
            sectionsFor = current;
            sections = plugin.ItemSources.Group(current);
        }

        foreach (var (section, rows) in sections)
        {
            var visible = rows.Where(x => !HidesRow(x.Row)).ToList();
            if (visible.Count == 0) continue;

            var done = visible.Count(x => x.Row.Done >= x.Row.Amount);
            var flags = section is ListSection.Crystals ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
            if (ImGui.CollapsingHeader($"{ItemSources.Title(section)}  ({done}/{visible.Count} done)###section{section}", flags))
                DrawRows(visible, section);
        }
    }

    private bool HidesRow(TeamcraftListRow row) => (CrafterFilter || (IsLeader && onlyMyItems)) && !IsMine(row);

    /// <summary>
    /// Keeps the open list in sync with changes made in Teamcraft. Every few seconds it asks Teamcraft when the
    /// list was last saved (a tiny request) and reloads it only if that changed. Runs only while the window is drawn.
    /// </summary>
    private void PollForChanges(TeamcraftList current)
    {
        if (pollTask is { IsCompleted: true } finished)
        {
            pollTask = null;
            if (finished.IsCompletedSuccessfully)
            {
                lastPolled = DateTime.Now;
                var changed = finished.Result is { } updateTime && updateTime != current.UpdateTime;
                if (changed && pollListId == current.Id && !list.IsLoading)
                    list.Start(() => plugin.Teamcraft.GetListAsync(current.Id, CancellationToken.None));
            }
            else
            {
                Plugin.Log.Debug(finished.Exception?.GetBaseException(), "Checking the list for changes failed");
            }
        }

        // Don't check while a save or reload is running; their result already has the latest version.
        if (pollTask != null || list.IsLoading || DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        var listId = pollListId = current.Id;
        pollTask = Task.Run(() => plugin.Teamcraft.GetListUpdateTimeAsync(listId, CancellationToken.None));
    }

    /// <summary>Drop-down of the selected team's lists, then the user's own lists.</summary>
    private void DrawListPicker()
    {
        if (!plugin.TeamcraftAuth.IsSignedIn)
        {
            listChoicesTeamId = null;
            listChoices.Clear();
            ImGui.TextDisabled("Sign in to pick from your lists, or open one by link below.");
            return;
        }

        var teamId = plugin.Configuration.SelectedTeamId ?? string.Empty;
        if (listChoicesTeamId != teamId)
            LoadListChoices(teamId);

        var choices = listChoices.Value;
        var preview = list.Value?.Name ?? (listChoices.IsLoading ? "Loading…" : "Choose a list");
        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("List", preview, ImGuiComboFlags.HeightLarge))
        {
            if (choices != null)
            {
                if (CrafterFilter)
                {
                    DrawListChoiceGroup("Lists with your assignments", choices.TeamLists.Where(l => l.MyItems > 0).ToList(),
                                        "Nothing is assigned to you yet.");
                }
                else
                {
                    if (teamId.Length > 0)
                        DrawListChoiceGroup("Team lists", choices.TeamLists, "No lists attached to this team.");
                    var teamIds = choices.TeamLists.Select(l => l.Id).ToHashSet();
                    DrawListChoiceGroup("My lists", choices.MyLists.Where(l => !teamIds.Contains(l.Id)).ToList(), "No other lists.");
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##refreshLists", FontAwesomeIcon.SyncAlt))
            LoadListChoices(teamId);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reload lists");

        // Crafters can narrow everything down to their own work; the leader always sees all of it.
        if (currentTeam != null && !IsLeader)
        {
            ImGui.SameLine();
            var onlyMine = plugin.Configuration.OnlyMyAssignments;
            if (ImGui.Checkbox("Only my assignments", ref onlyMine))
            {
                plugin.Configuration.OnlyMyAssignments = onlyMine;
                plugin.Configuration.Save();
            }
        }

        if (listChoices.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, listChoices.Error);
    }

    private void DrawListChoiceGroup(string title, IReadOnlyList<TeamcraftListSummary> lists, string emptyText)
    {
        ImGui.TextDisabled(title);
        if (lists.Count == 0)
            ImGui.TextDisabled($"  {emptyText}");
        foreach (var summary in lists)
        {
            var mine = summary.MyItems == 0 ? string.Empty
                : summary.MyRemaining == 0 ? "  (your items done)"
                : $"  ({summary.MyRemaining} of yours left)";
            if (ImGui.Selectable($"  {summary.Name}{mine}##{summary.Id}", summary.Id == list.Value?.Id))
                OpenList(summary.Id);
        }
        ImGui.Spacing();
    }

    private void LoadListChoices(string teamId)
    {
        listChoicesTeamId = teamId;
        listChoices.Start(async () =>
        {
            var teamLists = teamId.Length > 0
                ? await plugin.Teamcraft.GetTeamListsAsync(teamId, CancellationToken.None)
                : [];
            var myLists = await plugin.Teamcraft.GetMyListsAsync(CancellationToken.None);
            return new ListChoices(teamLists, myLists);
        });
    }

    private void OpenList(string id)
    {
        plugin.Configuration.LastListId = id;
        plugin.Configuration.Save();
        list.Start(() => plugin.Teamcraft.GetListAsync(id, CancellationToken.None));
    }

    /// <summary>Fetches NA prices whenever a different list (or a reload of it) is shown.</summary>
    private void DrawPriceStatus(TeamcraftList current)
    {
        if (!ReferenceEquals(pricesFor, current))
        {
            pricesFor = current;
            var ids = current.FinalItems.Concat(current.Items)
                .Select(r => r.GameItemId).OfType<uint>()
                .Where(Plugin.IsMarketable)
                .Distinct().ToList();
            prices.Start(() => plugin.Universalis.GetPricesAsync(ids, CancellationToken.None));
        }

        ImGui.TextDisabled(prices.IsLoading
            ? "Loading North America prices from Universalis…"
            : "Prices: cheapest listing on North America (Universalis).");
        if (prices.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, prices.Error);

        DrawBuyEstimate(current);
        DrawSendQuote(current);
        DrawCopyForArtisan(current);
    }

    /// <summary>Copies the crafts still to do as an Artisan list, for Artisan's "Import List From Clipboard".</summary>
    private void DrawCopyForArtisan(TeamcraftList current)
    {
        if (IsLeader || IsOfficer) ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Clipboard, "Copy for Artisan"))
        {
            var result = ArtisanExport.Build(current, row => !HidesRow(row));
            if (result.Recipes == 0)
            {
                artisanMessage = ("Nothing left to craft here.", DateTime.UtcNow);
            }
            else
            {
                ImGui.SetClipboardText(result.Json);
                var skipped = result.Skipped.Count > 0 ? $" Skipped: {string.Join(", ", result.Skipped)}." : string.Empty;
                artisanMessage = ($"Copied {result.Recipes} recipes ({result.Crafts:N0} crafts). In Artisan: Crafting Lists > " +
                                  $"\"Import List From Clipboard (Artisan Export)\".{skipped}", DateTime.UtcNow);
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copies the crafts still to do as an Artisan crafting list: pre-crafts first, then final items.\n" +
                             (CrafterFilter || (IsLeader && onlyMyItems) ? "Only your assigned items are included.\n" : string.Empty) +
                             "Then in Artisan, open Crafting Lists and press \"Import List From Clipboard (Artisan Export)\".");
        }

        // Confirmation for a few seconds after copying.
        if (artisanMessage is { } message && DateTime.UtcNow - message.At < TimeSpan.FromSeconds(12))
            ImGui.TextColored(Theme.Done, message.Text);
    }

    /// <summary>
    /// What buying the rest of the raw materials would cost on NA. Pre-crafts and final items are left out,
    /// since they're made from those materials. Follows the "only my items/assignments" filter.
    /// </summary>
    private void DrawBuyEstimate(TeamcraftList current)
    {
        if (prices.Value is not { } known) return;

        // Working out the cheapest stacks is too slow to redo every frame; only redo it when something changed.
        var filtered = CrafterFilter || (IsLeader && onlyMyItems);
        if (!ReferenceEquals(estimateFor.List, current) || !ReferenceEquals(estimateFor.Prices, known) || estimateFor.Filtered != filtered)
        {
            estimateFor = (current, known, filtered);
            estimate = BuildBuyEstimate(current, known);
        }
        if (estimate is not { } e) return;

        // Grouped so hovering anywhere on the line shows the breakdown.
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Buy {(filtered ? "your remaining materials" : "remaining materials")} on NA:");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Gold, $"about {e.Total:N0} gil");
        if (e.Missing.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"({e.Missing.Count} not fully available)");
        }
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(e.Tooltip);
    }

    private sealed record BuyEstimate(long Total, IReadOnlyList<string> Missing, string Tooltip);

    /// <summary>"Send quote" for website staff: opens the quote window with the whole list's material estimate.</summary>
    private void DrawSendQuote(TeamcraftList current)
    {
        // Only the Teamcraft team's leader and officers send quotes, and the website must also allow their key.
        // Everyone else never sees the button; a leader/officer whose key isn't allowed sees it greyed out with the reason.
        if (!IsLeader && !IsOfficer) return;
        var canQuote = plugin.AvalonKey.CanQuote;

        if (!canQuote) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileInvoiceDollar, "Send quote to a ticket"))
        {
            // Quotes are for the whole order, so ignore the "only my items" filter here.
            var whole = prices.Value is { } known ? BuildBuyEstimate(current, known, applyFilter: false) : null;
            plugin.QuoteWindow.Open(current, whole?.Total);
        }
        if (!canQuote) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canQuote
                ? "Send a quote for this list to a ticket on the Avalon website (posted in its Discord ticket)."
                : "You're a team leader/officer, but the Avalon website hasn't allowed your key to send quotes.\n" +
                  "Quotes are for website admins, managers (Admin > Team) and the Discord Staff role.\n" +
                  "If that's you, open Settings > Avalon key and press \"Check again\".");
        }
    }

    private BuyEstimate? BuildBuyEstimate(TeamcraftList current, IReadOnlyDictionary<uint, MarketPrice> known, bool applyFilter = true)
    {
        var toBuy = current.Items
            .Where(r => !r.IsCraft && r.GameItemId is not null && r.Done < r.Amount && !(applyFilter && HidesRow(r)))
            .Select(r => (Row: r, Needed: r.Amount - r.Done))
            .ToList();
        if (toBuy.Count == 0) return null;

        long total = 0;
        var breakdown = new List<(string Line, long Cost)>();
        var missing = new List<string>();
        foreach (var (row, needed) in toBuy)
        {
            var name = Plugin.GetItemName(row);
            if (!known.TryGetValue(row.GameItemId!.Value, out var price))
            {
                missing.Add(Plugin.IsMarketable(row.GameItemId.Value) ? $"{name} (no listings)" : $"{name} (can't be sold)");
                continue;
            }

            var (cost, bought, enough) = price.CostToBuy(needed);
            total += cost;
            var note = !enough ? $"  — only {bought:N0} listed" : bought > needed ? $"  (buys {bought:N0})" : string.Empty;
            breakdown.Add(($"{name} ×{needed:N0}: {cost:N0} gil{note}", cost));
            if (!enough) missing.Add($"{name} (only {bought:N0} of {needed:N0} listed)");
        }

        var lines = breakdown.OrderByDescending(b => b.Cost).Select(b => b.Line).ToList();
        if (missing.Count > 0)
            lines.AddRange(["", "Not included / short:", .. missing]);
        lines.AddRange(["", "Raw materials only (pre-crafts are made from them). Uses the cheapest", "combination of whole stacks on any NA world. Prices refresh every 5 minutes."]);
        return new BuyEstimate(total, missing, string.Join("\n", lines));
    }

    private void DrawPrice(TeamcraftListRow row)
    {
        if (row.GameItemId is not { } id || !Plugin.IsMarketable(id))
        {
            ImGui.TextDisabled("Not sold");
            return;
        }
        if (prices.Value == null)
        {
            ImGui.TextDisabled(prices.IsLoading ? "…" : "—");
            return;
        }
        if (!prices.Value.TryGetValue(id, out var price))
        {
            ImGui.TextDisabled("No listings");
            return;
        }

        ImGui.Text($"{price.PricePerUnit:N0}{(price.Hq ? " HQ" : string.Empty)}");
        if (ImGui.IsItemHovered())
        {
            var needed = row.Amount - row.Done;
            var tip = $"Cheapest North America price per unit, on {price.World}.";
            if (needed > 0)
            {
                var (cost, bought, enough) = price.CostToBuy(needed);
                tip += enough
                    ? $"\nBuying the {needed:N0} still needed: about {cost:N0} gil{(bought > needed ? $" ({bought:N0} in whole stacks)" : string.Empty)}"
                    : $"\nOnly {bought:N0} of the {needed:N0} still needed are listed ({cost:N0} gil).";
            }
            ImGui.SetTooltip(tip);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(price.World);
    }

    /// <summary>Only the leader of the selected team may assign, and only on that team's lists.</summary>
    private bool CanAssign(TeamcraftList current) =>
        currentTeam != null && current.TeamId == currentTeam.Id && currentTeam.Leader == plugin.TeamcraftAuth.Uid;

    private void DrawBulkAssign(TeamcraftList current)
    {
        var isLeader = currentTeam != null && currentTeam.Leader == plugin.TeamcraftAuth.Uid;
        if (!isLeader) return;
        if (!CanAssign(current))
        {
            ImGui.TextDisabled("To assign items, attach this list to your team in Teamcraft.");
            return;
        }

        var allRows = current.FinalItems.Select(r => (Final: true, Row: r)).Concat(current.Items.Select(r => (Final: false, Row: r))).ToList();
        if (list.IsLoading) ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##assignAll", "Assign whole list to…"))
        {
            foreach (var member in currentTeam!.Members)
            {
                if (ImGui.Selectable($"{plugin.Teamcraft.GetDisplayName(member)}##all{member}"))
                    SaveAssignments(current, allRows.Select(x => new RowAssignment(x.Final, x.Row.ItemId, [member])).ToList());
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear all assignments") && allRows.Any(x => x.Row.WorkingOnIt.Count > 0))
            SaveAssignments(current, allRows.Select(x => new RowAssignment(x.Final, x.Row.ItemId, [])).ToList());

        if (list.IsLoading) ImGui.EndDisabled();
    }

    /// <summary>Assigned names, plus a member picker for the leader.</summary>
    private void DrawAssignees(TeamcraftListRow row, bool finalItem)
    {
        var current = list.Value!;
        if (CanAssign(current))
        {
            var popupId = $"assign##{(finalItem ? "f" : "i")}{row.ItemId}";
            if (list.IsLoading) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButton($"##btn{popupId}", FontAwesomeIcon.UserPlus))
                ImGui.OpenPopup(popupId);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Assign team members");
            if (list.IsLoading) ImGui.EndDisabled();
            ImGui.SameLine();

            if (ImGui.BeginPopup(popupId))
            {
                ImGui.TextDisabled(Plugin.GetItemName(row));
                foreach (var member in currentTeam!.Members)
                {
                    var assigned = row.WorkingOnIt.Contains(member);
                    if (list.IsLoading) ImGui.BeginDisabled();
                    if (ImGui.Checkbox($"{plugin.Teamcraft.GetDisplayName(member)}##{popupId}{member}", ref assigned))
                    {
                        var updated = assigned ? row.WorkingOnIt.Append(member).ToList() : row.WorkingOnIt.Where(u => u != member).ToList();
                        SaveAssignments(current, [new RowAssignment(finalItem, row.ItemId, updated)]);
                    }
                    if (list.IsLoading) ImGui.EndDisabled();
                }
                if (list.IsLoading) ImGui.TextDisabled("Saving…");
                ImGui.EndPopup();
            }
        }

        ImGui.AlignTextToFramePadding();
        if (row.WorkingOnIt.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "—");
            return;
        }

        // "You" for yourself and character names without the world; full names on hover.
        var me = plugin.TeamcraftAuth.Uid;
        var shortNames = row.WorkingOnIt
            .OrderBy(uid => uid == me ? 0 : 1)
            .Select(uid => uid == me ? "You" : plugin.Teamcraft.GetDisplayName(uid).Split(" @ ")[0]);
        var text = string.Join(", ", shortNames);
        if (IsMine(row))
            ImGui.TextColored(Theme.SkyBlue, text);
        else
            ImGui.Text(text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", row.WorkingOnIt.Select(plugin.Teamcraft.GetDisplayName)));
    }

    /// <summary>Saves to Teamcraft; the list shown is replaced by the saved version, or an error is shown.</summary>
    private void SaveAssignments(TeamcraftList current, IReadOnlyList<RowAssignment> changes) =>
        list.Start(() => plugin.Teamcraft.SetAssigneesAsync(current.Id, changes, CancellationToken.None));

    private bool IsLeader => currentTeam != null && currentTeam.Leader == plugin.TeamcraftAuth.Uid;

    private bool IsOfficer => currentTeam != null && plugin.TeamcraftAuth.Uid is { } uid && currentTeam.Officers.Contains(uid);

    /// <summary>A crafter (not the leader) who chose to see only their own lists and items.</summary>
    private bool CrafterFilter => currentTeam != null && !IsLeader && plugin.Configuration.OnlyMyAssignments;

    private bool IsMine(TeamcraftListRow row) =>
        plugin.TeamcraftAuth.Uid is { } uid && row.WorkingOnIt.Contains(uid);

    /// <summary>People assigned to an item can update its progress; so can the team leader.</summary>
    private bool CanUpdateProgress(TeamcraftList current, TeamcraftListRow row) =>
        currentTeam != null && current.TeamId == currentTeam.Id && (IsMine(row) || currentTeam.Leader == plugin.TeamcraftAuth.Uid);

    private static float ProgressBarWidth => 90 * ImGuiHelpers.GlobalScale;

    /// <summary>Room for the progress bar and the four buttons (−, ✎, +, ✓) at the current UI scale.</summary>
    private static float DoneColumnWidth
    {
        get
        {
            var style = ImGui.GetStyle();
            // An icon button is about a frame high and as wide as its icon plus padding; leave a little slack.
            var button = ImGui.GetFrameHeight() + style.FramePadding.X * 2;
            return ProgressBarWidth + 4 * (button + style.ItemSpacing.X) + style.CellPadding.X * 2;
        }
    }

    private void DrawDone(TeamcraftList current, TeamcraftListRow row, bool finalItem)
    {
        var complete = row.Done >= row.Amount;
        var canUpdate = CanUpdateProgress(current, row);
        Theme.Progress(row.Done, row.Amount, canUpdate ? ProgressBarWidth : -1);
        if (!canUpdate) return;

        var id = $"{(finalItem ? "f" : "i")}{row.ItemId}";
        if (list.IsLoading) ImGui.BeginDisabled();
        ImGui.SameLine();
        if (row.Done <= 0) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton($"##minus{id}", FontAwesomeIcon.Minus))
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, -1)]);
        if (row.Done <= 0) ImGui.EndDisabled();
        ImGui.SameLine();
        DrawSetAmount(current, row, finalItem, id);
        ImGui.SameLine();
        if (complete) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton($"##plus{id}", FontAwesomeIcon.Plus))
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, 1)]);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton($"##all{id}", FontAwesomeIcon.Check))
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, null)]);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Mark the full amount as done (its ingredients are marked as used, like in Teamcraft).");
        if (complete) ImGui.EndDisabled();
        if (list.IsLoading) ImGui.EndDisabled();
    }

    /// <summary>"Set" button with a box to type the exact amount done; Enter saves.</summary>
    private void DrawSetAmount(TeamcraftList current, TeamcraftListRow row, bool finalItem, string id)
    {
        var popupId = $"setAmount##{id}";
        var clicked = ImGuiComponents.IconButton($"##set{id}", FontAwesomeIcon.Pen);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Type the amount done");
        if (clicked)
        {
            typedAmount = row.Done;
            focusAmountBox = true;
            ImGui.OpenPopup(popupId);
        }

        if (!ImGui.BeginPopup(popupId)) return;

        ImGui.TextDisabled(Plugin.GetItemName(row));
        if (focusAmountBox)
        {
            ImGui.SetKeyboardFocusHere();
            focusAmountBox = false;
        }
        ImGui.SetNextItemWidth(90);
        var entered = ImGui.InputInt($"##amount{id}", ref typedAmount, 0, 0, flags: ImGuiInputTextFlags.EnterReturnsTrue);
        typedAmount = Math.Clamp(typedAmount, 0, row.Amount);
        ImGui.SameLine();
        ImGui.Text($"/ {row.Amount}");
        ImGui.SameLine();
        if ((ImGui.Button($"Save##save{id}") || entered) && typedAmount != row.Done)
        {
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, null, typedAmount)]);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>"Only my items" filter and "Complete all my items" for anyone with assignments on this list.</summary>
    private void DrawMyItemsBar(TeamcraftList current)
    {
        var mine = current.FinalItems.Select(r => (Final: true, Row: r))
            .Concat(current.Items.Select(r => (Final: false, Row: r)))
            .Where(x => IsMine(x.Row))
            .ToList();
        if (currentTeam == null || current.TeamId != currentTeam.Id) return;
        if (mine.Count == 0)
        {
            if (CrafterFilter)
                ImGui.TextDisabled("Nothing on this list is assigned to you. Untick \"Only my assignments\" to see everything.");
            return;
        }

        var remaining = mine.Count(x => x.Row.Done < x.Row.Amount);
        ImGui.Text($"Your items: {mine.Count - remaining}/{mine.Count} done");
        if (IsLeader)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Only my items", ref onlyMyItems);
        }
        ImGui.SameLine();
        if (list.IsLoading || remaining == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Complete all my items"))
        {
            // Final items first, so their ingredients are marked as used before materials are topped up.
            SaveProgress(current, mine.Where(x => x.Row.Done < x.Row.Amount)
                .OrderByDescending(x => x.Final)
                .Select(x => new DoneChange(x.Final, x.Row.ItemId, x.Row.RecipeId, null))
                .ToList());
        }
        if (list.IsLoading || remaining == 0) ImGui.EndDisabled();

        // Raise done counts to what this character owns (bags, saddlebag, remembered retainers); never lowers them.
        var fills = mine
            .Where(x => x.Row.GameItemId is not null)
            .Select(x => (x.Final, x.Row, Target: Math.Min(x.Row.Amount, plugin.Inventory.Holdings(x.Row.GameItemId!.Value).Total)))
            .Where(x => x.Target > x.Row.Done)
            .ToList();
        ImGui.SameLine();
        if (list.IsLoading || fills.Count == 0) ImGui.BeginDisabled();
        if (ImGui.Button($"Fill from inventory ({fills.Count})"))
        {
            // Final items first, like "Complete all", so ingredients they used aren't counted twice.
            SaveProgress(current, fills
                .OrderByDescending(x => x.Final)
                .Select(x => new DoneChange(x.Final, x.Row.ItemId, x.Row.RecipeId, null, x.Target, OnlyIncrease: true))
                .ToList());
        }
        if (list.IsLoading || fills.Count == 0) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Sets your items' done count to how many you own (bags, saddlebag and retainers).\nIt never lowers a count.");
    }

    private void SaveProgress(TeamcraftList current, IReadOnlyList<DoneChange> changes) =>
        list.Start(() => plugin.Teamcraft.SetDoneAsync(current.Id, changes, CancellationToken.None));

    private void DrawRows(List<(bool Final, TeamcraftListRow Row)> rows, ListSection section)
    {
        // Every section uses the same table id, so ImGui keeps their columns in sync: resizing one resizes them all.
        // (The "v2" leaves behind widths the game saved for the old, unsynced tables.)
        if (!ImGui.BeginTable("##listItems.v2", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Done", ImGuiTableColumnFlags.WidthFixed, DoneColumnWidth);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("NA price (gil)", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Assigned to", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var current = list.Value!;
        foreach (var (final, row) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var iconSize = new Vector2(ImGui.GetFrameHeight());
            if (Plugin.GetItemIcon(row) is { } icon)
                ImGui.Image(icon.Handle, iconSize);
            else
                ImGui.Dummy(iconSize);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (section == ListSection.PreCrafts && plugin.ItemSources.CraftJobOf(row) is { } job)
            {
                ImGui.TextColored(Theme.Muted, job);
                ImGui.SameLine();
            }
            if (row.Done >= row.Amount)
                ImGui.TextColored(Theme.Muted, Plugin.GetItemName(row));
            else
                ImGui.Text(Plugin.GetItemName(row));
            if (section == ListSection.Vendors)
                DrawVendor(row);
            ImGui.TableNextColumn();
            DrawDone(current, row, final);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            DrawHave(row);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            DrawPrice(row);
            ImGui.TableNextColumn();
            DrawAssignees(row, final);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The NPC selling the item (nearest-alphabetical place first), its gil price, and a map button that flags the
    /// vendor on the in-game map. With several vendors, the button opens a list to pick from.
    /// </summary>
    private void DrawVendor(TeamcraftListRow row)
    {
        if (row.GameItemId is not { } itemId) return;
        var vendorIndex = plugin.Vendors;
        ImGui.SameLine();
        if (!vendorIndex.IsReady)
        {
            ImGui.TextColored(Theme.Muted, "· finding vendors…");
            return;
        }

        var vendors = vendorIndex.For(itemId);
        if (vendors.Count == 0) return;
        var price = VendorSources.GilPrice(itemId);
        var first = vendors[0];
        var popupId = $"vendors##{row.ItemId}";

        if (ImGuiComponents.IconButton($"##map{row.ItemId}", FontAwesomeIcon.MapMarkerAlt))
        {
            if (vendors.Count == 1) Plugin.ShowOnMap(first);
            else ImGui.OpenPopup(popupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(vendors.Count == 1 ? "Show this vendor on the map" : $"{vendors.Count} vendors — pick one to show on the map");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var more = vendors.Count > 1 ? $" +{vendors.Count - 1} more" : string.Empty;
        ImGui.TextColored(Theme.Muted, $"{price:N0} gil · {first.Npc}, {first.Place}{more}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", vendors.Take(12).Select(v => $"{v.Npc} — {v.Place} ({v.X:0.0}, {v.Y:0.0})")) +
                             (vendors.Count > 12 ? $"\n…and {vendors.Count - 12} more" : string.Empty) +
                             $"\n\n{price:N0} gil each from NPC vendors.");

        if (ImGui.BeginPopup(popupId))
        {
            ImGui.TextColored(Theme.Gold, $"Sold by ({price:N0} gil each)");
            foreach (var vendor in vendors)
            {
                if (ImGui.Selectable($"{vendor.Npc} — {vendor.Place} ({vendor.X:0.0}, {vendor.Y:0.0})##{vendor.Npc}{vendor.Place}"))
                    Plugin.ShowOnMap(vendor);
            }
            ImGui.EndPopup();
        }
    }

    /// <summary>How many you own; hover for bags / saddlebag / each retainer.</summary>
    private void DrawHave(TeamcraftListRow row)
    {
        if (row.GameItemId is not { } id)
        {
            ImGui.TextDisabled("—");
            return;
        }

        var have = plugin.Inventory.Holdings(id);
        var stillNeeded = row.Amount - row.Done;
        if (have.Total == 0)
            ImGui.TextDisabled("0");
        else if (stillNeeded > 0 && have.Total >= stillNeeded)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"{have.Total:N0}");
        else
            ImGui.Text($"{have.Total:N0}");

        // Dotted underline so it reads as "hover me" when there's something to break down.
        if (have.Total > 0)
            DottedUnderline(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

        if (!ImGui.IsItemHovered()) return;
        DrawHaveTooltip(row, have, stillNeeded);
    }

    /// <summary>Where the items are: on you, saddlebag, each retainer (largest first), plus what's still needed.</summary>
    private void DrawHaveTooltip(TeamcraftListRow row, ItemHoldings have, int stillNeeded)
    {
        ImGui.BeginTooltip();
        ImGui.TextColored(Theme.Gold, Plugin.GetItemName(row));

        var places = new List<(string Where, int Count)> { ("On you", have.Bags), ("Saddlebag", have.Saddlebag) };
        places.AddRange(have.Retainers.Select(r => ($"Retainer {r.Retainer}", r.Count)));
        var owned = places.Where(p => p.Count > 0).OrderByDescending(p => p.Count).ToList();

        if (owned.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "You don't have any.");
        }
        else if (ImGui.BeginTable("##haveTip", 2))
        {
            foreach (var (where, count) in owned)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(where);
                ImGui.TableNextColumn();
                ImGui.Text($"{count:N0}");
            }
            if (owned.Count > 1)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Lavender, "Total");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Lavender, $"{have.Total:N0}");
            }
            ImGui.EndTable();
        }

        if (stillNeeded > 0)
            ImGui.TextColored(have.Total >= stillNeeded ? Theme.Done : Theme.Muted, $"Still needed: {stillNeeded:N0}");

        ImGui.Spacing();
        var known = plugin.Inventory.KnownRetainers().Count;
        ImGui.TextColored(Theme.Muted, known == 0
            ? "Open your saddlebag and retainers in game once so they're counted."
            : $"Counts {known} retainer{(known == 1 ? string.Empty : "s")} and the saddlebag as of the last time you opened them.");
        ImGui.EndTooltip();
    }

    private static void DottedUnderline(Vector2 min, Vector2 max)
    {
        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(Theme.Muted with { W = 0.7f });
        for (var x = min.X; x < max.X; x += 4)
            drawList.AddLine(new Vector2(x, max.Y), new Vector2(Math.Min(x + 2, max.X), max.Y), color);
    }
}
