using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 窗口缩放适配助手（搬自旧项目）：提示文字自动换行、按钮行放不下自动换行，保证缩小窗口不裁字。 </summary>
internal static class Ui
{
    /// <summary> 灰色提示文字（取主题 TextDisabled 色），自动换行。 </summary>
    public static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary> 带色文字，自动换行。 </summary>
    public static void ColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary> 流式同行：剩余宽度够才 SameLine，否则自动落到下一行（防按钮被窗口边缘裁掉）。 </summary>
    public static void SameLineIfFits(float nextWidth)
    {
        if (ImGui.GetContentRegionAvail().X >= nextWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
    }

    /// <summary> 估算文字按钮宽度（含左右内边距），供 SameLineIfFits 使用。 </summary>
    public static float ButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
}
