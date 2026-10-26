using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using AvalonPlugin.Avalon;
using AvalonPlugin.Teamcraft;
using AvalonPlugin.Universalis;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
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
    private TeamcraftTeam? currentTeam;
    private bool onlyMyItems;
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

    public override void Draw()
    {
        if (!plugin.AvalonKey.IsUnlocked)
        {
            DrawLocked();
            return;
        }

        plugin.Teamcraft.SaveNamesIfChanged();
        DrawAccountBar();
        ImGui.Separator();
        DrawTeam();
        ImGui.Separator();
        DrawList();
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

    private void DrawAccountBar()
    {
        var auth = plugin.TeamcraftAuth;
        if (auth.IsSignedIn)
            ImGui.Text($"Teamcraft: {plugin.Teamcraft.GetDisplayName(auth.Uid!)}");
        else
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Not signed in to Teamcraft (lists are read-only).");

        ImGui.SameLine();
        if (ImGui.SmallButton(auth.IsSignedIn ? "Settings" : "Sign in…"))
            plugin.ToggleConfigUi();
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
        if (ImGui.SmallButton("Refresh##teams"))
            teams.Start(() => plugin.Teamcraft.GetMyTeamsAsync(CancellationToken.None));

        if (teams.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, teams.Error);
        else if (teams.Value is { Count: 0 })
            ImGui.TextDisabled("You are not in any Teamcraft team.");

        if (selected != null)
        {
            var members = selected.Members.Select(uid =>
                plugin.Teamcraft.GetDisplayName(uid) + (uid == selected.Leader ? " (leader)" : string.Empty));
            ImGui.TextWrapped($"Members: {string.Join(", ", members)}");
        }
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

        ImGui.Text(current.Name);
        var selectedTeamId = plugin.Configuration.SelectedTeamId;
        if (current.TeamId == null)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This list is not attached to a team in Teamcraft.");
        else if (selectedTeamId != null && current.TeamId != selectedTeamId)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This list belongs to a different team than the one selected.");

        DrawPriceStatus(current);
        DrawBulkAssign(current);
        DrawMyItemsBar(current);

        if (ImGui.CollapsingHeader($"Final items ({current.FinalItems.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            DrawRows("##final", current.FinalItems, true);
        if (ImGui.CollapsingHeader($"Materials ({current.Items.Count})"))
            DrawRows("##items", current.Items, false);
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
        if (ImGui.SmallButton("Refresh##lists"))
            LoadListChoices(teamId);

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
            ImGui.SetTooltip($"Cheapest North America listing, on {price.World}.\nTotal for {row.Amount}: about {price.PricePerUnit * row.Amount:N0} gil");
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
            if (ImGui.SmallButton($"Assign##{popupId}"))
                ImGui.OpenPopup(popupId);
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

        if (row.WorkingOnIt.Count == 0)
            ImGui.TextDisabled("—");
        else
            ImGui.Text(string.Join(", ", row.WorkingOnIt.Select(plugin.Teamcraft.GetDisplayName)));
    }

    /// <summary>Saves to Teamcraft; the list shown is replaced by the saved version, or an error is shown.</summary>
    private void SaveAssignments(TeamcraftList current, IReadOnlyList<RowAssignment> changes) =>
        list.Start(() => plugin.Teamcraft.SetAssigneesAsync(current.Id, changes, CancellationToken.None));

    private bool IsLeader => currentTeam != null && currentTeam.Leader == plugin.TeamcraftAuth.Uid;

    /// <summary>A crafter (not the leader) who chose to see only their own lists and items.</summary>
    private bool CrafterFilter => currentTeam != null && !IsLeader && plugin.Configuration.OnlyMyAssignments;

    private bool IsMine(TeamcraftListRow row) =>
        plugin.TeamcraftAuth.Uid is { } uid && row.WorkingOnIt.Contains(uid);

    /// <summary>People assigned to an item can update its progress; so can the team leader.</summary>
    private bool CanUpdateProgress(TeamcraftList current, TeamcraftListRow row) =>
        currentTeam != null && current.TeamId == currentTeam.Id && (IsMine(row) || currentTeam.Leader == plugin.TeamcraftAuth.Uid);

    private void DrawDone(TeamcraftList current, TeamcraftListRow row, bool finalItem)
    {
        var complete = row.Done >= row.Amount;
        if (complete)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"{row.Done}/{row.Amount}");
        else
            ImGui.Text($"{row.Done}/{row.Amount}");

        if (!CanUpdateProgress(current, row)) return;

        var id = $"{(finalItem ? "f" : "i")}{row.ItemId}";
        if (list.IsLoading) ImGui.BeginDisabled();
        ImGui.SameLine();
        if (row.Done <= 0) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"-##minus{id}"))
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, -1)]);
        if (row.Done <= 0) ImGui.EndDisabled();
        ImGui.SameLine();
        DrawSetAmount(current, row, finalItem, id);
        ImGui.SameLine();
        if (complete) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"+##plus{id}"))
            SaveProgress(current, [new DoneChange(finalItem, row.ItemId, row.RecipeId, 1)]);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Done##all{id}"))
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
        if (ImGui.SmallButton($"Set##set{id}"))
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
    }

    private void SaveProgress(TeamcraftList current, IReadOnlyList<DoneChange> changes) =>
        list.Start(() => plugin.Teamcraft.SetDoneAsync(current.Id, changes, CancellationToken.None));

    private void DrawRows(string tableId, IReadOnlyList<TeamcraftListRow> rows, bool finalItem)
    {
        if (!ImGui.BeginTable(tableId, 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Done", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("NA price (gil)", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Assigned to", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var current = list.Value!;
        foreach (var row in rows)
        {
            if ((CrafterFilter || (IsLeader && onlyMyItems)) && !IsMine(row)) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(Plugin.GetItemName(row));
            ImGui.TableNextColumn();
            DrawDone(current, row, finalItem);
            ImGui.TableNextColumn();
            DrawPrice(row);
            ImGui.TableNextColumn();
            DrawAssignees(row, finalItem);
        }

        ImGui.EndTable();
    }
}
