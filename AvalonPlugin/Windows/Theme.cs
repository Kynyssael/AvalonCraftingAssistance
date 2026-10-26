using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AvalonPlugin.Windows;

/// <summary>ADC colors (lavender, sky blue and gold from the logo) applied on top of the Dalamud style.</summary>
internal static class Theme
{
    public static readonly Vector4 Lavender = new(0.62f, 0.50f, 0.92f, 1f);
    public static readonly Vector4 SkyBlue = new(0.40f, 0.68f, 0.95f, 1f);
    public static readonly Vector4 Gold = new(0.96f, 0.78f, 0.36f, 1f);
    public static readonly Vector4 Done = new(0.45f, 0.80f, 0.50f, 1f);
    public static readonly Vector4 Muted = new(0.65f, 0.62f, 0.75f, 1f);

    private static readonly (ImGuiCol Col, Vector4 Color)[] Colors =
    [
        (ImGuiCol.TitleBgActive, new Vector4(0.30f, 0.22f, 0.52f, 1f)),
        (ImGuiCol.Header, new Vector4(0.42f, 0.32f, 0.72f, 0.45f)),
        (ImGuiCol.HeaderHovered, new Vector4(0.50f, 0.40f, 0.82f, 0.70f)),
        (ImGuiCol.HeaderActive, new Vector4(0.55f, 0.44f, 0.88f, 0.85f)),
        (ImGuiCol.Button, new Vector4(0.32f, 0.42f, 0.72f, 0.55f)),
        (ImGuiCol.ButtonHovered, new Vector4(0.40f, 0.55f, 0.88f, 0.80f)),
        (ImGuiCol.ButtonActive, new Vector4(0.46f, 0.62f, 0.95f, 1f)),
        (ImGuiCol.FrameBg, new Vector4(0.22f, 0.18f, 0.36f, 0.60f)),
        (ImGuiCol.FrameBgHovered, new Vector4(0.30f, 0.25f, 0.48f, 0.70f)),
        (ImGuiCol.CheckMark, Gold),
        (ImGuiCol.PlotHistogram, new Vector4(0.50f, 0.58f, 0.95f, 1f)),
        (ImGuiCol.TableHeaderBg, new Vector4(0.26f, 0.21f, 0.42f, 1f)),
        (ImGuiCol.Separator, new Vector4(0.45f, 0.38f, 0.70f, 0.50f)),
    ];

    public static void Push()
    {
        foreach (var (col, color) in Colors)
            ImGui.PushStyleColor(col, color);
    }

    public static void Pop() => ImGui.PopStyleColor(Colors.Length);

    /// <summary>A progress bar with "done/amount" written on it; green once complete.</summary>
    public static void Progress(int done, int amount, float width, float height = 0, string suffix = "")
    {
        var fraction = amount > 0 ? (float)done / amount : 0f;
        var complete = amount > 0 && done >= amount;
        if (complete) ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Done);
        ImGui.ProgressBar(fraction, new Vector2(width, height > 0 ? height : ImGui.GetFrameHeight()), $"{done:N0}/{amount:N0}{suffix}");
        if (complete) ImGui.PopStyleColor();
    }
}
