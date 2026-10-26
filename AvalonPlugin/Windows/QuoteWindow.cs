using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using AvalonPlugin.Avalon;
using AvalonPlugin.Teamcraft;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AvalonPlugin.Windows;

/// <summary>
/// Sends a quote for the open Teamcraft list to a ticket on the Avalon website. The website posts it in the
/// Discord ticket exactly like the "Send quote" button, and links the list to the request.
/// </summary>
public class QuoteWindow : Window
{
    private readonly Plugin plugin;
    private readonly Loadable<IReadOnlyList<AvalonRequest>> requests = new();
    private readonly Loadable<QuoteSent> sending = new();

    private TeamcraftList? list;
    private string? requestId;
    private string service = string.Empty;
    private string price = string.Empty;
    private string details = string.Empty;
    private string dueDate = string.Empty;
    private bool preselected;

    public QuoteWindow(Plugin plugin) : base("Send quote###AvalonQuote")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 470);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Opens the window for a list, with the price and description filled in from it.</summary>
    public void Open(TeamcraftList forList, long? materialsEstimate)
    {
        list = forList;
        price = materialsEstimate is { } gil ? $"{gil:N0} gil" : string.Empty;
        details = DescribeList(forList, materialsEstimate);
        dueDate = string.Empty;
        requestId = null;
        preselected = false;
        sending.Clear();
        requests.Start(() => plugin.AvalonApi.GetQuotableRequestsAsync(CancellationToken.None));
        IsOpen = true;
    }

    public override void PreDraw() => Theme.Push();

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        if (list == null)
        {
            ImGui.TextDisabled("Open a list in Avalon Crafting Assistance first.");
            return;
        }

        ImGui.TextColored(Theme.Gold, list.Name);
        ImGui.TextColored(Theme.Muted, "The client gets this quote in their Discord ticket and on the website.");
        ImGui.Spacing();

        if (sending.Value is { } sent)
        {
            DrawSent(sent);
            return;
        }

        DrawRequestPicker();
        var selected = requests.Value?.FirstOrDefault(r => r.Id == requestId);
        if (selected == null) return;

        var width = -ImGuiHelpers.GlobalScale * 110;
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("Service", ref service, 120);
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("Price", "e.g. 120,000 gil", ref price, 60);
        ImGui.InputTextMultiline("##details", ref details, 1000, new Vector2(width, ImGui.GetTextLineHeight() * 8));
        ImGui.SameLine();
        ImGui.Text("What the price\nincludes");
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("Due date", "e.g. 15 November 2026", ref dueDate, 60);

        if (selected.Quote is { Status: "pending" })
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"This replaces the quote waiting for the client ({selected.Quote.Price}).");
        else if (selected.Quote is { Status: "accepted" })
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"The client already accepted {selected.Quote.Price}; this sends an updated quote they must accept.");
        if (!selected.HasTicket)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This request has no Discord ticket; the client will only see it on the website.");

        ImGui.Spacing();
        var ready = new[] { service, price, details, dueDate }.All(s => !string.IsNullOrWhiteSpace(s));
        if (!ready || sending.IsLoading) ImGui.BeginDisabled();
        if (ImGui.Button(sending.IsLoading ? "Sending…" : $"Send quote to #{selected.Number:0000}", new Vector2(-1, 0)))
        {
            var draft = new QuoteDraft(service.Trim(), price.Trim(), details.Trim(), dueDate.Trim(), list.Id, list.Name);
            var id = selected.Id;
            sending.Start(() => plugin.AvalonApi.SendQuoteAsync(id, draft, CancellationToken.None));
        }
        if (!ready || sending.IsLoading) ImGui.EndDisabled();
        if (sending.Error != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, sending.Error);
    }

    private void DrawRequestPicker()
    {
        if (requests.IsLoading)
        {
            ImGui.TextDisabled("Loading tickets…");
            return;
        }
        if (requests.Error != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, requests.Error);
            return;
        }

        var all = requests.Value ?? [];
        if (all.Count == 0)
        {
            ImGui.TextWrapped("No ticket can take a quote right now. A request must be accepted (or in progress) on the website first.");
            return;
        }

        // Pre-select the ticket this list was quoted for before, if any.
        if (!preselected)
        {
            preselected = true;
            if (all.FirstOrDefault(r => r.Teamcraft?.ListId == list!.Id) is { } linked)
                Select(linked);
        }

        var current = all.FirstOrDefault(r => r.Id == requestId);
        ImGui.SetNextItemWidth(-ImGuiHelpers.GlobalScale * 110);
        if (ImGui.BeginCombo("Ticket", current != null ? Label(current) : "Choose a ticket"))
        {
            foreach (var request in all)
            {
                if (ImGui.Selectable($"{Label(request)}##{request.Id}", request.Id == requestId))
                    Select(request);
                if (request.Teamcraft?.ListId == list!.Id && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Already linked to this list");
            }
            ImGui.EndCombo();
        }
    }

    private void Select(AvalonRequest request)
    {
        requestId = request.Id;
        if (string.IsNullOrWhiteSpace(service) || requests.Value!.Any(r => r.Service == service))
            service = request.Service;
    }

    private static string Label(AvalonRequest r)
    {
        var quote = r.Quote == null ? "no quote yet" : $"quote {r.Quote.Status}: {r.Quote.Price}";
        return $"#{r.Number:0000} · {r.Client} · {(string.IsNullOrEmpty(r.Service) ? "General request" : r.Service)} ({quote})";
    }

    private void DrawSent(QuoteSent sent)
    {
        ImGui.TextColored(Theme.Done, $"Quote sent to request #{sent.Number:0000}.");
        ImGui.TextWrapped(sent.PostedToTicket
            ? "It was posted in the Discord ticket; the client can accept it there or on the website."
            : "Saved on the website. It couldn't be posted in Discord (no ticket, or the bot is offline); the client can still accept it under My requests.");
        ImGui.Spacing();
        if (ImGui.Button("Close"))
            IsOpen = false;
    }

    /// <summary>Starting text for "what the price includes": the final items, material estimate and list link.</summary>
    private static string DescribeList(TeamcraftList list, long? materialsEstimate)
    {
        var items = string.Join(", ", list.FinalItems.Select(r => $"{Plugin.GetItemName(r)} ×{r.Amount:N0}"));
        var lines = new List<string> { $"Crafting: {items}" };
        if (materialsEstimate is { } gil)
            lines.Add($"Materials (NA market estimate): ~{gil:N0} gil");
        lines.Add($"Teamcraft list: https://ffxivteamcraft.com/list/{list.Id}");
        var text = string.Join("\n", lines);
        return text.Length <= 1000 ? text : text[..1000];
    }
}
