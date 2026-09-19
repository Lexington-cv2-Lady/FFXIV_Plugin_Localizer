using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FFXIVPluginLocalizer.Windows;

/// <summary>
/// 体检报告窗口：跑完「体检」后自动弹出，逐个列出"仍显示英文"的插件及其漏网文字。
/// 数据来自 <see cref="Plugin.HealthResults"/>（体检时实时填充）。
/// </summary>
public sealed class HealthReportWindow : Window
{
    private readonly Plugin _plugin;

    public HealthReportWindow(Plugin plugin) : base("体检报告###PluginLocalizerHealthReport")
    {
        _plugin = plugin;
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (_plugin.HealthResults.Count == 0)
        {
            ImGui.TextWrapped(_plugin.HealthReport.Length > 0
                ? _plugin.HealthReport
                : "尚未体检。点主窗口「体检」开始。");
            return;
        }

        var total = _plugin.HealthResults.Sum(r => r.Items.Count);
        ImGui.TextWrapped($"共 {_plugin.HealthResults.Count} 个插件、{total} 条漏网英文：");
        ImGui.TextDisabled("（下面每项展开看具体哪句还是英文；明细也写在日志里）");
        ImGui.Separator();

        ImGui.BeginChild("##healthlist");
        foreach (var (name, items) in _plugin.HealthResults)
        {
            if (ImGui.CollapsingHeader($"{name}（{items.Count} 条）"))
            {
                ImGui.Indent();
                foreach (var it in items)
                    ImGui.TextWrapped("• " + it);
                ImGui.Unindent();
            }
        }
        ImGui.EndChild();
    }
}
