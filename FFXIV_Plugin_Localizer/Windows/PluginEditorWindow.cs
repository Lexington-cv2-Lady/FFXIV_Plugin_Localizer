using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility.Raii;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary>
/// 单个插件的译文编辑二级窗口（2026-09-19 替代原来主窗口里的 CollapsingHeader 展开）。
/// 点插件翻译列表里的「编辑」按钮弹出；同一时刻只编辑一个插件，切换即换内容。
/// </summary>
public sealed class PluginEditorWindow : Window
{
    private const int MaxEditorRows = 150;

    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private string _summary = "";
    private string _entryFilter = "";   // 条目过滤（搜原文/译文）

    /// <summary> 当前正在编辑的插件（空 = 还没点过编辑）。 </summary>
    public string CurrentPlugin { get; private set; } = "";

    // 按插件缓存（编辑时读取；机翻结束/外部变化后失效）
    private readonly Dictionary<string, (List<(string En, string Zh)> Translated, List<string> Untranslated)> _cache = new();
    // 「打开/关闭插件配置」toggle：记录我们帮开过的窗口
    private readonly Dictionary<string, bool> _openedByUs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _restoreArmedUntil = new(); // 单项还原的二次确认

    public PluginEditorWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("插件编辑###PluginLocalizerPluginEditor")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary> 打开并切换到指定插件的编辑页。 </summary>
    public void Open(string plugin)
    {
        CurrentPlugin = plugin;
        _summary = "";
        IsOpen = true;
    }

    public override void Draw()
    {
        if (string.IsNullOrEmpty(CurrentPlugin))
        {
            ImGui.TextWrapped("未选择插件。从「插件翻译」列表点某个插件的「编辑」打开这里。");
            return;
        }
        var plugin = CurrentPlugin;
        ImGui.TextUnformatted(plugin);
        ImGui.SameLine();
        if (ImGui.SmallButton("关闭"))
            IsOpen = false;
        ImGui.Separator();

        var (translated, untranslated) = GetCached(plugin);

        // 状态提示（机翻进行中）
        if (_mt.Running)
        {
            ImGui.TextDisabled($"机翻进行中（{_mt.Status}）——完成后自动刷新。");
        }

        // ── 动作区（与机翻冲突的变灰）──
        ImGui.BeginDisabled(_mt.Running);
        Ui.PushAccent();
        if (ImGui.Button($"翻译本插件缺失（{untranslated.Count} 条）"))
        {
            if (string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(_plugin.Configuration)))
                _summary = "请先在「AI 设置」填写 API Key。";
            else { _summary = ""; _mt.StartWindowPlugin(plugin); }
        }
        Ui.PopAccent();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只翻这一个插件的缺口。已翻译的不覆盖。");

        Ui.SameLineIfFits(Ui.ButtonWidth("重读本插件"));
        if (ImGui.Button("重读本插件"))
        {
            Invalidate(plugin);
            _replacement.Reload();
            _summary = $"已重新读取 {plugin} 的译文与候选。";
        }
        ImGui.EndDisabled();

        // 打开/关闭配置窗（不冲突，一直可用）
        var cfgOpen = _openedByUs.TryGetValue(plugin, out var opened) && opened;
        var cfgLabel = cfgOpen ? "关闭插件配置" : "打开插件配置";
        Ui.SameLineIfFits(Ui.ButtonWidth(cfgLabel));
        if (ImGui.Button(cfgLabel))
        {
            if (cfgOpen)
            {
                if (_plugin.AtkHook.TryClosePluginWindow(plugin))
                    _summary = $"已关闭 {plugin} 的配置界面。";
                else
                    _summary = $"没找到 {plugin} 的窗口（可能已被手动关闭）。";
                _openedByUs[plugin] = false;
            }
            else
            {
                OpenPluginConfigUi(plugin);
                _openedByUs[plugin] = true;
            }
        }

        // 复制插件名称（不冲突）
        Ui.SameLineIfFits(Ui.ButtonWidth("复制插件名称"));
        if (ImGui.Button("复制插件名称"))
        {
            ImGui.SetClipboardText(plugin);
            _summary = $"已复制插件名称到剪贴板：{plugin}";
        }

        // 单项还原英文（冲突 → 变灰）
        ImGui.BeginDisabled(_mt.Running);
        Ui.SameLineIfFits(Ui.ButtonWidth("还原本插件英文"));
        if (ImGui.Button("还原本插件英文"))
        {
            var bak = _replacement.BackupWindowTable(plugin);
            var n = _replacement.ClearPluginTranslations(plugin);
            Invalidate(plugin);
            _replacement.Reload();
            _summary = n > 0
                ? $"已把 {plugin} 还原为英文（删除 {n} 条译文" + (string.IsNullOrEmpty(bak) ? "" : $"，备份：{ReplacementService.WindowBackupDirName}") + "）。"
                : $"{plugin} 当前没有译文。";
        }
        ImGui.EndDisabled();

        if (_summary.Length > 0)
        {
            ImGui.Spacing();
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _summary);
        }
        ImGui.Spacing();

        // ── 条目搜索框（置顶在滚动区上方，按原文/译文过滤）──
        var ef = _entryFilter;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.InputTextWithHint("##entryFilter", "搜原文/译文…", ref ef, 256))
            _entryFilter = ef;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按英文原文或中文译文过滤下方条目。留空显示全部。");

        // ── 条目编辑区（滚动）──
        var shown = 0;
        using (var child = ImRaii.Child("##pluginEditorList", new Vector2(-1f, -1f), true))
        {
            if (child.Success)
            {
                foreach (var (en, zh) in translated)
                {
                    if (shown >= MaxEditorRows) break;
                    if (!MatchesEntry(en, zh, _entryFilter)) continue;
                    shown++;
                    DrawEntryRow(plugin, en, zh);
                }
                foreach (var en in untranslated)
                {
                    if (shown >= MaxEditorRows) break;
                    if (!MatchesEntry(en, "", _entryFilter)) continue;
                    shown++;
                    DrawEntryRow(plugin, en, "");
                }
                if (shown == 0 && translated.Count + untranslated.Count > 0)
                    Ui.Hint("没有匹配的条目。");
                else if (translated.Count + untranslated.Count == 0)
                    Ui.Hint("该插件没有候选文案（可能未扫描或已全中文）。");
            }
        }
    }

    /// <summary> 条目过滤：原文或译文是否含 needle（空 needle = 全过）。 </summary>
    private static bool MatchesEntry(string en, string zh, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        var n = needle.Trim();
        return en.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
            || zh.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0;
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

    private void OpenPluginConfigUi(string plugin)
    {
        try
        {
            var target = Plugin.PluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, plugin, StringComparison.OrdinalIgnoreCase));
            if (target == null) { _summary = $"未找到已安装的 {plugin}（可能只提取过源码、本机未装或未启用）。"; return; }
            if (!target.IsLoaded) { _summary = $"{target.Name} 当前未加载（被禁用了？），无法打开配置界面。"; return; }
            if (target.HasConfigUi) { target.OpenConfigUi(); _summary = $"已打开 {target.Name} 的配置界面。"; }
            else if (target.HasMainUi) { target.OpenMainUi(); _summary = $"{target.Name} 没有配置界面，已打开其主界面。"; }
            else _summary = $"{target.Name} 未提供配置/主界面入口，无法打开。";
        }
        catch (Exception ex)
        {
            _summary = "打开插件配置失败：" + ex.Message;
            _plugin.AppLog.Error($"[窗口] 打开插件配置失败（{plugin}）：{ex.Message}");
        }
    }

    private void DrawEntryRow(string plugin, string en, string zh)
    {
        ImGui.PushID(en);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("原文");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped(en);
        ImGui.SameLine();
        if (ImGui.SmallButton("复制原文"))
        {
            ImGui.SetClipboardText(en);
            _summary = $"已复制原文到剪贴板：{(en.Length > 40 ? en[..40] + "…" : en)}";
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("删除"))
        {
            _replacement.RemoveWindowEntry(plugin, en);
            Invalidate(plugin);
            ImGui.PopID();
            return;
        }

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
