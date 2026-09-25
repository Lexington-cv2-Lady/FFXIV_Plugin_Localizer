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
        // ── 可点击插件清单（体检仅清单模式产出）：点名字→打开该插件窗口做「窗内漏网英文」检测 ──
        // 这就是把「体检清单」与「检查这个插件」合并：不用再手敲插件名，直接点清单里的名字即可。
        if (_plugin.HealthPluginList.Count > 0)
        {
            ImGui.TextWrapped("点插件名可打开它的窗口，做「窗内漏网英文」检测：");
            ImGui.BeginChild("##pluginlist");
            foreach (var (name, internalName, hasConfig, hasMain, isSelf) in _plugin.HealthPluginList)
            {
                if (isSelf)
                {
                    ImGui.TextDisabled($"• {name}（{internalName}）〔自身，已跳过〕");
                }
                else
                {
                    var label = hasConfig ? $"{name}  〔配置窗〕"
                                : hasMain ? $"{name}  〔主窗〕"
                                : name;
                    if (ImGui.Button(label))
                        _plugin.CheckSinglePlugin(internalName);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"打开 {name}（{internalName}）的配置窗，采样漏网英文后保留窗口供你查看；\n关窗请手动关，或 /ptp close 一次性收起其它插件窗口。");
                }
            }
            ImGui.EndChild();
            ImGui.Separator();
        }

        // ── 漏网英文结果（安装器简介/描述覆盖率 + 单插件检查） ──
        if (_plugin.HealthResults.Count == 0)
        {
            ImGui.TextWrapped(_plugin.HealthReport.Length > 0
                ? _plugin.HealthReport
                : "尚未体检。点主窗口「体检」开始（仅清单，不开窗）。");
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
