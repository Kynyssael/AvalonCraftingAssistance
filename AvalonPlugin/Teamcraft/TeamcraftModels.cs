using System;
using System.Collections.Generic;

namespace AvalonPlugin.Teamcraft;

public class TeamcraftException(string message) : Exception(message);

/// <summary>The list was changed by someone else between reading and saving it.</summary>
public sealed class TeamcraftConflictException()
    : TeamcraftException("Someone else changed this list at the same time. Please try again.");

/// <summary>New "working on it" users for one row, identified by item id and which part of the list it's in.</summary>
public sealed record RowAssignment(bool FinalItem, string ItemId, IReadOnlyList<string> WorkingOnIt);

/// <summary>
/// Adds <paramref name="Delta"/> to a row's done count, or sets it to <paramref name="SetTo"/> when given
/// (worked out against the latest saved count). With neither, the row is completed.
/// </summary>
public sealed record DoneChange(bool FinalItem, string ItemId, string? RecipeId, double? Delta, double? SetTo = null);

/// <summary>One row of a Teamcraft list (see Teamcraft's list-row.ts).</summary>
public sealed record TeamcraftListRow(string ItemId, string? RecipeId, int Amount, int Done, IReadOnlyList<string> WorkingOnIt)
{
    /// <summary>Game item id, or null for custom items.</summary>
    public uint? GameItemId => uint.TryParse(ItemId, out var id) ? id : null;
}

/// <param name="UpdateTime">Firestore update time, used later to avoid overwriting concurrent edits.</param>
public sealed record TeamcraftList(
    string Id,
    string Name,
    string? TeamId,
    IReadOnlyList<TeamcraftListRow> FinalItems,
    IReadOnlyList<TeamcraftListRow> Items,
    string UpdateTime);

/// <summary>Just enough of a list to show it in a drop-down, with how many items are assigned to the signed-in user.</summary>
public sealed record TeamcraftListSummary(string Id, string Name, string? TeamId, int MyItems = 0, int MyRemaining = 0);

public sealed record TeamcraftTeam(string Id, string Name, string Leader, IReadOnlyList<string> Members);
