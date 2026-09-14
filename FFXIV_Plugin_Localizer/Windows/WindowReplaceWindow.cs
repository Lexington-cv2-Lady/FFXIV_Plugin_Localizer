using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 窗口文字翻译窗口（独立窗口）：按插件管理窗口内界面文字的对照表。
/// 候选来自「文案扫描」输出，翻译结果写 窗口翻译\&lt;插件名&gt;.json 并即时参与替换（需钩子 + 替换开关）。
/// 支持整插件机翻（智谱）与逐条内联编辑（失焦即存，仿旧项目详情区）。 </summary>
public sealed class WindowReplaceWindow : Window
{
    private const int MaxEditorRows = 150;

    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private string _summary = "";
    private bool _mtWasRunning;

    // 按插件缓存（展开时读取文件；机翻结束后自动失效）
    private readonly Dictionary<string, (List<(string En, string Zh)> Translated, List<string> Untranslated)> _cache = new();
    private readonly Dictionary<string, string> _editBufs = new();
    private string _openPlugin = ""; // 当前展开的插件（只允许一个展开，简化缓存管理）

    public WindowReplaceWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("窗口文字翻译###PluginLocalizerWindow")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("按插件管理窗口内界面文字的对照表。候选来自「文案扫描」，翻译写 窗口翻译\\<插件名>.json 并参与替换。\n" +
                "⚠ 当前替换通道只覆盖走 TextUnformatted 绘制的文字（随钩子覆盖面扩展而增强）；\n" +
                "按钮/滑条等控件标签暂不支持（相关桩会破坏游戏 UI，已移除）。");

        // 机翻结束的瞬间清缓存（后台任务合并了新翻译）
        if (_mtWasRunning && !_mt.Running)
        {
            _cache.Clear();
        }
        _mtWasRunning = _mt.Running;

        if (_mt.Running)
        {
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.3f, 1f), _mt.Status);
        }

        ImGui.Separator();

        var plugins = _replacement.GetWindowPlugins();
        if (plugins.Count == 0)
        {
            Ui.Hint("还没有候选文案。先到「文案扫描」窗口点「扫描全部插件」，再来这里逐插件翻译。");
            return;
        }

        using (var list = ImRaii.Child("##窗口插件列表", new Vector2(-1f), true))
        {
            if (list.Success)
            {
                for (var i = 0; i < plugins.Count; i++)
                {
                    var (name, total, translated) = plugins[i];
                    ImGui.PushID(i);
                    var label = translated >= total ? $"{name}（已翻译 {translated}/{total}）✔" : $"{name}（已翻 {translated}/共 {total}）";
                    if (ImGui.CollapsingHeader(label))
                    {
                        if (_openPlugin != name)
                        {
                            _openPlugin = name;
                            Invalidate(name);
                        }
                        DrawPluginEditor(name);
                    }
                    else if (_openPlugin == name)
                    {
                        _openPlugin = "";
                    }
                    ImGui.PopID();
                }
            }
        }
    }

    private void Invalidate(string plugin) => _cache.Remove(plugin);

    private (List<(string En, string Zh)> Translated, List<string> Untranslated) GetCached(string plugin)
    {
        if (!_cache.TryGetValue(plugin, out var data))
        {
            data = _replacement.GetWindowEntries(plugin);
            _cache[plugin] = data;
        }
        return data;
    }

    /// <summary> 用资源管理器打开目录（不存在则创建）。 </summary>
    private void OpenDir(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _summary = "打开目录失败：" + ex.Message;
            _plugin.AppLog.Error("[窗口] 打开目录失败：" + ex.Message);
        }
    }

    private void DrawPluginEditor(string plugin)
    {        var (translated, untranslated) = GetCached(plugin);

        if (_mt.Running)
        {
            ImGui.TextDisabled($"机翻进行中（{_mt.Status}）——完成后自动刷新。");
        }
        else
        {
            if (ImGui.Button($"翻译全部缺失（{untranslated.Count} 条）"))
            {
                if (string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(_plugin.Configuration)))
                {
                    _summary = "请先在「AI 设置」填写 API Key。";
                }
                else
                {
                    _summary = "";
                    _mt.StartWindowPlugin(plugin);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("打开翻译目录"))
            {
                OpenDir(_replacement.WindowTableDir);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("打开窗口对照表所在目录（窗口翻译\\<插件名>.json），可直接查看/备份/分享。");
            ImGui.SameLine();
            if (ImGui.Button("打开候选目录"))
            {
                OpenDir(System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), ReplacementService.CandidateDirName));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("打开候选文案目录（未翻译清单：源码提取 / 历史扫描产物）。");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把该插件全部未翻译条目分批送智谱翻译（免费模型，自动限速）。");
        }
        if (_summary.Length > 0)
        {
            ImGui.SameLine();
            Ui.ColoredWrapped(new Vector4(1f, 0.5f, 0.2f, 1f), _summary);
        }
        ImGui.Spacing();

        var shown = 0;
        using (var child = ImRaii.Child("##窗口条目列表", new Vector2(-1f, 300f), true))
        {
            if (child.Success)
            {
                // 已翻译（可改可删）
                foreach (var (en, zh) in translated)
                {
                    if (shown >= MaxEditorRows) break;
                    shown++;
                    DrawEntryRow(plugin, en, zh);
                }
                // 未翻译（空输入框，填了即存）
                foreach (var en in untranslated)
                {
                    if (shown >= MaxEditorRows) break;
                    shown++;
                    DrawEntryRow(plugin, en, "");
                }

                if (translated.Count + untranslated.Count > MaxEditorRows)
                {
                    ImGui.TextDisabled($"……条目过多，仅显示前 {MaxEditorRows} 条（机翻不受影响，可对整插件直接点翻译）");
                }
                if (translated.Count + untranslated.Count == 0)
                {
                    Ui.Hint("该插件没有候选文案（可能未扫描或已全中文）。");
                }
            }
        }
    }

    private void DrawEntryRow(string plugin, string en, string zh)
    {
        ImGui.PushID(en);
        // 英文原文：明确标注并保持灰色只读（本插件窗口已免疫替换，这里永远显示原样英文，供对照）
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("原文");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped(en);
        ImGui.SameLine();
        if (ImGui.SmallButton("删除"))
        {
            _replacement.RemoveWindowEntry(plugin, en);
            Invalidate(plugin);
            ImGui.PopID();
            return;
        }

        // 中文译文：可编辑（失焦即存）
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.85f, 1f, 1f));
        ImGui.TextUnformatted("译文");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var buf = zh;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##窗口译文", ref buf, 1024))
        {
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (buf.Trim().Length > 0)
                {
                    _replacement.SetWindowEntry(plugin, en, buf.Trim());
                    Invalidate(plugin);
                }
            }
        }
        ImGui.Spacing();
        ImGui.PopID();
    }
}
