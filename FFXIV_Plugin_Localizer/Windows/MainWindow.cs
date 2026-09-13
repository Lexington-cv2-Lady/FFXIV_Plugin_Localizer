using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 主窗口（MVP·采集模式）：钩子状态、采集开关、按窗口分组的英文清单、保存/清空；日志置底持续显示。 </summary>
public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;
    private DateTime _confirmClearUntil = DateTime.MinValue;
    private int _lastLogCount = -1;

    public MainWindow(Plugin plugin)
        : base("插件界面汉化###PluginLocalizer")
    {
        _plugin = plugin;
        Size = new Vector2(560, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var hook = _plugin.Hook;

        // ── 钩子状态 ──
        if (hook.Hooked)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), "✔ " + hook.HookStatus);
        else
            Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), "✘ " + hook.HookStatus);

        ImGui.Separator();

        // ── 采集开关 ──
        if (hook.Hooked)
        {
            if (hook.Collecting)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.24f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.86f, 0.32f, 0.26f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.19f, 0.16f, 1f));
                var stop = ImGui.Button("■ 停止采集（自动保存清单）", new Vector2(-1f, 0f));
                ImGui.PopStyleColor(3);
                if (stop) hook.Collecting = false;
                Ui.Hint("采集中：打开任意插件窗口，它渲染的英文会自动进入下方清单（只读模式，不改任何文字）。\n本插件窗口自身的内容不采集。");
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.5f, 0.23f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.62f, 0.29f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.13f, 0.42f, 0.19f, 1f));
                var start = ImGui.Button("● 开始采集（只读模式）", new Vector2(-1f, 0f));
                ImGui.PopStyleColor(3);
                if (start) hook.Collecting = true;
                Ui.Hint("点击后打开需要汉化的插件窗口（如 Penumbra 设置页），再回到本窗口查看采集结果。");
            }
        }
        else
        {
            Ui.Hint("钩子未挂接成功，无法采集。请查看下方日志中的失败原因。");
        }

        // ── 统计 + 操作行 ──
        ImGui.Text($"已采集：{hook.TotalCount} 条（本会话新增 {hook.SessionNewCount} 条，来自 {hook.WindowCount} 个窗口）");
        if (ImGui.Button("保存清单"))
        {
            hook.Save();
        }
        Ui.SameLineIfFits(120f);
        var clearLabel = DateTime.Now < _confirmClearUntil ? "再点一次确认清空" : "清空采集";
        if (ImGui.Button(clearLabel))
        {
            if (DateTime.Now < _confirmClearUntil)
            {
                hook.ClearAll();
                _confirmClearUntil = DateTime.MinValue;
            }
            else
            {
                _confirmClearUntil = DateTime.Now.AddSeconds(3);
            }
        }
        Ui.SameLineIfFits(110f);
        if (ImGui.Button("日志窗口"))
        {
            _plugin.ToggleLogUi(); // 独立日志窗口（环形日志 + 导出报错日志）
        }
        Ui.SameLineIfFits(110f);
        if (ImGui.Button("文案扫描"))
        {
            _plugin.ToggleScanUi(); // 静态扫描已安装插件 DLL 字符串堆（不依赖游戏内窗口）
        }

        // ── 清单（按窗口分组，占主区可滚动） ──
        var avail = ImGui.GetContentRegionAvail();
        var logH = 128f;
        var listH = Math.Max(60f, avail.Y - logH - ImGui.GetStyle().ItemSpacing.Y * 2f);
        using (var list = ImRaii.Child("##清单列表", new Vector2(-1f, listH), true))
        {
            if (list.Success)
            {
                var wins = hook.WindowSummaries();
                if (wins.Count == 0)
                {
                    Ui.Hint("还没有采集到文字。点「开始采集」后打开其他插件的窗口（如 Penumbra 设置页），" +
                            "它们的英文界面文字会按窗口分组出现在这里。");
                }
                else
                {
                    for (var i = 0; i < wins.Count; i++)
                    {
                        ImGui.PushID(i);
                        var open = ImGui.CollapsingHeader($"{wins[i].Window}（{wins[i].Count}）");
                        if (open)
                        {
                            foreach (var s in hook.StringsOfWindow(wins[i].Window))
                                ImGui.TextWrapped(s);
                            ImGui.Spacing();
                        }
                        ImGui.PopID();
                    }
                }
            }
        }

        // ── 日志（置底持续显示） ──
        ImGui.Separator();
        DrawLog(_plugin.AppLog, logH);
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
