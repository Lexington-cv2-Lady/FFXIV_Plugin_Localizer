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

    /// <summary>
    /// 给刚用 <c>BeginGroup()/EndGroup()</c> 画好的内容**外围加一个圆角边框**（自适应内容尺寸）。
    /// 用途：把一组相关控件在视觉上框在一起，减少"一屏按钮看不出归属"的杂乱感。
    /// 搬自旧项目 `MainWindow.FrameLastGroup`（那边长期实战验证过的画法）。
    /// </summary>
    public static void FrameLastGroup(float alpha = 0.5f)
    {
        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRect(mn - new Vector2(7f, 6f), mx + new Vector2(7f, 6f),
            ImGui.GetColorU32(new Vector4(0.42f, 0.72f, 1f, alpha)), 8f);
    }

    /// <summary> 主操作高亮配色（橙金色，用于"一键翻译"这类主要动作），配合 <see cref="PopAccent"/> 成对使用。 </summary>
    public static void PushAccent()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.52f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.98f, 0.62f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.72f, 0.42f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }

    /// <summary> 弹出主操作高亮配色（与 PushAccent 成对）。 </summary>
    public static void PopAccent() => ImGui.PopStyleColor(4);

    /// <summary> 危险操作按钮配色（红，用于"还原英文"这类破坏性动作），配合 <see cref="PopDanger"/>。 </summary>
    public static void PushDanger()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.66f, 0.26f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.32f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.55f, 0.2f, 0.17f, 1f));
    }

    /// <summary> 弹出危险操作配色（与 PushDanger 成对）。 </summary>
    public static void PopDanger() => ImGui.PopStyleColor(3);
}
