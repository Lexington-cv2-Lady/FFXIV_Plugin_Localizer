using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 主窗口：钩子状态、排障开关、功能入口；日志置底持续显示。
/// 文案获取 = 「文案扫描」（静态，主来源）+「安装器翻译」（对照表/机翻/手动），运行时采集已移除。 </summary>
public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;
    private int _lastLogCount = -1;

    public MainWindow(Plugin plugin) : base("插件界面汉化###PluginLocalizer")
    {
        _plugin = plugin;
        Size = new Vector2(560, 470);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var hook = _plugin.Hook;

        // ── 钩子状态 + 排障开关 ──
        if (hook.Hooked)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), "✔ " + hook.HookStatus);
        else
            Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), "✘ " + hook.HookStatus);

        var hooksOn = _plugin.Configuration.HooksEnabled;
        if (ImGui.Checkbox("全部钩子（排障开关）", ref hooksOn))
        {
            _plugin.Configuration.HooksEnabled = hooksOn;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关闭后本插件对游戏 UI 零干扰。\n安装器替换依赖钩子，关闭后不可用；静态扫描不受影响。\n改动后需重载插件生效。");

        ImGui.Separator();

        // ── 功能入口 ──
        if (ImGui.Button("安装器翻译"))
        {
            _plugin.ToggleTranslationUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件安装器里插件介绍的中文化：对照表 + 机翻 + 手动编辑，随插件更新自动补翻。");
        ImGui.SameLine();
        if (ImGui.Button("窗口文字翻译"))
        {
            _plugin.ToggleWindowReplaceUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按插件翻译窗口内的界面文字（候选来自文案扫描），替换层即时生效。");
        ImGui.SameLine();
        if (ImGui.Button("文案扫描"))
        {
            _plugin.ToggleScanUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("静态扫描已安装插件 DLL 的全部英文文案（不依赖游戏内窗口，双语汉化版自动跳过）。");
        ImGui.SameLine();
        if (ImGui.Button("日志窗口"))
        {
            _plugin.ToggleLogUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("操作日志 + 一键导出报错日志 zip。");

        ImGui.Separator();

        // ── 日志（置底持续显示） ──
        DrawLog(_plugin.AppLog, 170f);
    }

    /// <summary> 底部日志面板：旧→新显示（最多 80 条），新日志且原先贴底时自动滚到底。 </summary>
    private void DrawLog(AppLog log, float height)
    {
        var entries = log.Snapshot(); // 新→旧
        ImGui.TextDisabled("日志（报错置底持续显示）");
        ImGui.SameLine();
        if (ImGui.SmallButton("清空日志"))
        {
            log.Clear();
        }
        var innerH = height - ImGui.GetTextLineHeight() - ImGui.GetStyle().ItemSpacing.Y;
        using (var child = ImRaii.Child("##底部日志", new Vector2(-1f, Math.Max(40f, innerH)), false))
        {
            if (child.Success)
            {
                var wasAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
                var shown = entries.Count > 80 ? entries.Take(80).ToList() : entries;
                for (var i = shown.Count - 1; i >= 0; i--)
                {
                    var e = shown[i];
                    var color = e.Lv switch
                    {
                        AppLog.Level.Error => new Vector4(1f, 0.45f, 0.4f, 1f),
                        AppLog.Level.Warn => new Vector4(1f, 0.8f, 0.3f, 1f),
                        _ => new Vector4(0.75f, 0.8f, 0.85f, 1f),
                    };
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    ImGui.TextWrapped($"{e.Time:HH:mm:ss} {e.Text}");
                    ImGui.PopStyleColor();
                }
                if (_lastLogCount != entries.Count && wasAtBottom)
                    ImGui.SetScrollHereY(1f);
                _lastLogCount = entries.Count;
            }
        }
    }
}
