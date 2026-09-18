using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 仓库地址二级窗口（2026-09-18）：
/// 只读展示卫月设置里配的主库 + 第三方仓库链接。本插件不增删、不改卫月任何设置。
/// 布局抄卫月官方「第三方插件仓库」设置页：# | 链接（只读输入框） | 状态。 </summary>
public sealed class RepoWindow : Window
{
    private readonly Plugin _plugin;
    private static readonly Vector4 Green = new(0.55f, 0.9f, 0.55f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.8f, 0.3f, 1f);

    public RepoWindow(Plugin plugin) : base("仓库地址###PluginLocalizerRepo")
    {
        _plugin = plugin;
        Size = new Vector2(680, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // ── 主库 ──
        ImGui.TextColored(Green, "主库");
        DrawUrlRow(_plugin.DalamudMainRepo.Length > 0 ? _plugin.DalamudMainRepo : "（未读到）", true);

        ImGui.Spacing();

        // ── 第三方插件仓库（抄卫月官方布局） ──
        ImGui.TextColored(Green, "第三方插件仓库");
        Ui.Hint("添加第三方插件仓库，可能导致数据丢失、游戏崩溃等，请自行承担使用风险。\n" +
                "（本窗口只读展示；要增删请到卫月自带设置操作。）");

        if (ImGui.BeginTable("##repos", 3,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("链接", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableHeadersRow();

            if (_plugin.DalamudThirdRepos.Count > 0)
            {
                var idx = 1;
                foreach (var (url, en) in _plugin.DalamudThirdRepos)
                {
                    ImGui.TableNextRow();
                    // 序号
                    ImGui.TableNextColumn();
                    ImGui.Text(idx.ToString());
                    // 链接（只读输入框样式，抄卫月）
                    ImGui.TableNextColumn();
                    var buf = url;
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.InputText($"##url{idx}", ref buf, 9999, ImGuiInputTextFlags.ReadOnly);
                    // 状态
                    ImGui.TableNextColumn();
                    ImGui.TextColored(en ? Green : Amber, en ? "启用" : "[停用]");
                    idx++;
                }
            }
            else
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("—");
                ImGui.TableNextColumn();
                ImGui.TextDisabled("（无第三方仓库）");
                ImGui.TableNextColumn();
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        Ui.Hint("「后台自动翻译」勾了「预翻仓库全部插件」时，会按上面这些仓库的插件清单送翻介绍——这就是费用会涨的原因。");
    }

    /// <summary> 单条 URL 用只读输入框样式显示（与表里链接列同风格）。 </summary>
    private static void DrawUrlRow(string url, bool ok)
    {
        var buf = url;
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##url_main", ref buf, 9999, ImGuiInputTextFlags.ReadOnly);
    }
}
