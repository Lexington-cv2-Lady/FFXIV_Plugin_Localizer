using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 安装器对照表手动编辑窗口（2026-09-18 从后台自动翻译主界面拆出的二级窗口）：
/// 编辑插件安装器里各插件介绍（Punchline/Description）的译文——偶尔修个别翻错的介绍用。
/// 从主界面「手动翻译」按钮打开，避免主界面表格挤成一团。 </summary>
public sealed class ManualEditWindow : Window
{
    private const int MaxManualRows = 200;

    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly Dictionary<string, string> _editBufs = new();
    private string _search = "";
    private string _newEn = "";
    private string _newZh = "";

    public ManualEditWindow(Plugin plugin, ReplacementService replacement)
        : base("手动翻译（安装器对照表）###PluginLocalizerManualEdit")
    {
        _plugin = plugin;
        _replacement = replacement;
        Size = new Vector2(640, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("这里编辑的是**插件安装器列表里**每个插件的介绍（简介/描述）译文——" +
                "不是插件配置窗口里的文字（那在「插件翻译」窗口管）。\n" +
                "FDCN 机翻表/AI 把某条介绍翻错时，在这里手动修这一条；失焦即存。");

        ImGui.InputTextWithHint("##手动筛选", "筛选：英文或中文包含…", ref _search, 128);

        // 新增条目行
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##新增英", "新增英文原文", ref _newEn, 1024);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##新增中", "中文译文", ref _newZh, 1024);
        ImGui.SameLine();
        if (ImGui.Button("添加") && _newEn.Trim().Length >= 2 && _newZh.Trim().Length > 0)
        {
            _replacement.SetTranslation(_newEn.Trim(), _newZh.Trim());
            _replacement.Save();
            _plugin.AppLog.Info($"[替换] 手动添加对照：{_newEn.Trim()}");
            _newEn = "";
            _newZh = "";
        }

        var search = _search.Trim();
        var entries = _replacement.GetEntries();
        var shown = 0;
        var filtered = 0;
        var avail = ImGui.GetContentRegionAvail();
        using (var list = ImRaii.Child("##手动翻译列表", new Vector2(-1f, Math.Max(160f, avail.Y)), true))
        {
            if (list.Success)
            {
                foreach (var (en, zh) in entries)
                {
                    if (search.Length > 0 &&
                        !en.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                        !zh.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    filtered++;
                    if (shown >= MaxManualRows) continue;
                    shown++;

                    if (!_editBufs.TryGetValue(en, out var buf))
                    {
                        buf = zh;
                        _editBufs[en] = buf;
                    }

                    ImGui.PushID(en);
                    // 英文原文（灰色只读；本插件窗口已免疫替换，永远显示原样英文供对照）
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextUnformatted("原文");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.TextWrapped(en);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("删除"))
                    {
                        _replacement.RemoveTranslation(en);
                        _replacement.Save();
                        _editBufs.Remove(en);
                        ImGui.PopID();
                        break; // 删除后本帧列表已失效，下帧重画
                    }

                    // 中文译文（内联编辑，失焦即存）
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.85f, 1f, 1f));
                    ImGui.TextUnformatted("译文");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##中文", ref buf, 1024))
                    {
                        _editBufs[en] = buf;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            _replacement.SetTranslation(en, buf.Trim());
                            _replacement.Save();
                        }
                    }
                    ImGui.Spacing();
                    ImGui.PopID();
                }

                if (filtered > MaxManualRows)
                {
                    ImGui.TextDisabled($"……共 {filtered} 条，仅显示前 {MaxManualRows} 条，请用筛选缩小范围");
                }
                if (filtered == 0)
                {
                    Ui.Hint(search.Length > 0 ? "没有匹配的条目。" : "对照表是空的。可导入机翻表、自动翻译或手动添加。");
                }
            }
        }
    }
}
