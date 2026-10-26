using System;
using System.Net.Http;
using AvalonPlugin.Avalon;
using AvalonPlugin.Teamcraft;
using AvalonPlugin.Universalis;
using AvalonPlugin.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AvalonPlugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/avalon";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public AvalonKeyService AvalonKey { get; }
    public TeamcraftAuth TeamcraftAuth { get; }
    public TeamcraftClient Teamcraft { get; }
    public UniversalisClient Universalis { get; }

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public readonly WindowSystem WindowSystem = new("Avalon");
    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AvalonPlugin/0.1");
        AvalonKey = new AvalonKeyService(http, Configuration);
        TeamcraftAuth = new TeamcraftAuth(http, Configuration);
        Teamcraft = new TeamcraftClient(http, TeamcraftAuth, Configuration);
        Universalis = new UniversalisClient(http);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Avalon Crafting Assistance. '/avalon config' opens settings.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("Avalon Crafting Assistance loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        AvalonKey.Dispose();
        http.Dispose();
    }

    public static string GetItemName(TeamcraftListRow row)
    {
        if (row.GameItemId is not { } id) return $"Custom item ({row.ItemId})";
        var item = DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
        var name = item?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"Item #{id}" : name;
    }

    /// <summary>True for items that can be sold on the market board.</summary>
    public static bool IsMarketable(uint itemId) =>
        DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.ItemSearchCategory.RowId > 0;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            ToggleConfigUi();
        else
            ToggleMainUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
