using System;
using System.Collections.Concurrent;
using Dalamud.Configuration;

namespace AvalonPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Personal key from the Avalon website, encrypted with Windows DPAPI for the current user.</summary>
    public string? AvalonKey { get; set; }

    /// <summary>Teamcraft user id of the signed-in account.</summary>
    public string? TeamcraftUid { get; set; }

    /// <summary>Firebase refresh token, encrypted with Windows DPAPI for the current user.</summary>
    public string? TeamcraftRefreshToken { get; set; }

    public string? SelectedTeamId { get; set; }

    /// <summary>For crafters (not the team leader): show only lists and items assigned to them.</summary>
    public bool OnlyMyAssignments { get; set; } = true;

    /// <summary>Character names of Teamcraft users, by user id, so names show up instantly.</summary>
    public ConcurrentDictionary<string, string> TeamcraftNames { get; set; } = new();

    /// <summary>Teamcraft list shown last time; reopened automatically.</summary>
    public string? LastListId { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
