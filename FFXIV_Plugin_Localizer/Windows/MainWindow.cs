using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 主窗口：钩子状态、排障开关、功能入口；日志置底持续显示。
/// 文案获取 = 「源码提取」（静态，主来源）+ 各插件的译文表/对照表，运行时采集已移除。 </summary>
public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;
    private int _lastLogCount = -1;

    public MainWindow(Plugin plugin) : base("翻译插件的插件###PluginLocalizer")
    {
        _plugin = plugin;
        Size = new Vector2(560, 470);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var hook = _plugin.Hook;

        var enabled = _plugin.Configuration.ReplacementEnabled;
        if (ImGui.Checkbox("启用替换（即时生效，无需重载）", ref enabled))
        {
            _plugin.Configuration.ReplacementEnabled = enabled;
            _plugin.Replacement.Enabled = enabled;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("总开关：关掉后所有已翻译的界面文字立即还原为英文（对照表保留，随时可再开）。");
        ImGui.SameLine();
        // 一键还原英文：关替换 + 清空内存表，让所有窗口立刻回到原样
        if (ImGui.Button("还原英文"))
        {
            _plugin.Configuration.ReplacementEnabled = false;
            _plugin.Replacement.Enabled = false;
            _plugin.Configuration.Save();
            _plugin.RestoreEnglish();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立即把界面还原成英文（关闭替换开关 + 清空当前生效的对照表）。\n对照表文件不会删除，重新勾选「启用替换」并重载即可恢复中文。");

        ImGui.Separator();

        // ── 钩子状态 + 排障开关 ──
        if (hook.Hooked)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), "【正常】 " + hook.HookStatus);
        else
            Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), "【异常】 " + hook.HookStatus);

        var hooksOn = _plugin.Configuration.HooksEnabled;
        if (ImGui.Checkbox("全部钩子（排障开关）", ref hooksOn))
        {
            _plugin.Configuration.HooksEnabled = hooksOn;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关闭后本插件对游戏 UI 零干扰。\n安装器替换依赖钩子，关闭后不可用；静态扫描不受影响。\n改动后需重载插件生效。");
        ImGui.SameLine();
        var widgetOn = _plugin.Configuration.WidgetHooks;
        if (ImGui.Checkbox("控件标签桩（排障开关）", ref widgetOn))
        {
            _plugin.Configuration.WidgetHooks = widgetOn;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("滑条/复选框/下拉框等控件标签的替换（文本之外的另一条通道）。\n若界面异常，先关它重载试试；仍异常再关「全部钩子」。");
        Ui.SameLineIfFits(Ui.ButtonWidth("钩子调试日志"));
        var dbgLog = _plugin.Configuration.DebugHookLog;
        if (ImGui.Checkbox("钩子调试日志", ref dbgLog))
        {
            _plugin.Configuration.DebugHookLog = dbgLog;
            _plugin.Configuration.Save();
            _plugin.Hook.DebugStats = dbgLog; // 即时生效，无需重载
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("排查「对照表里有译文、但界面没变成中文」用。\n开启后每 5 秒在日志输出各钩子（文字/控件）的调用次数、命中次数与未命中样本——\n能区分是「钩子没被触发」还是「触发了但没查中」。");

        ImGui.Separator();

        // ── 功能入口（按使用流程排序：① 配置 → ② 提取 → ③ 翻译 → ④ 排障；放不下自动换行） ──
        Ui.Hint("流程：第 1 步 AI 设置填 Key，第 2 步 源码提取获取英文（需代理），第 3 步 插件翻译翻成中文；后台自动翻译为并行支线（基本不用管），日志随时可看。");
        if (ImGui.Button("AI 设置（第 1 步）"))
        {
            _plugin.ToggleAiSettingsUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("第一步·前置配置：AI 供应商 / API Key / 模型 / 温度 / 批量 / 测试连接。\n填好免费智谱 Key 后，后面两步的机翻才能用。");
        Ui.SameLineIfFits(Ui.ButtonWidth("源码提取（第 2 步）"));
        if (ImGui.Button("源码提取（第 2 步）"))
        {
            _plugin.ToggleSourceExtractUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("从插件公开源码提取界面文案（最准：只拿界面文字 + 标出绘制函数）。\n需勾选并填写代理才能访问 GitHub；闭源插件无法翻译。");
        Ui.SameLineIfFits(Ui.ButtonWidth("插件翻译（第 3 步）"));
        if (ImGui.Button("插件翻译（第 3 步）"))
        {
            _plugin.ToggleWindowReplaceUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("第三步·翻译：按插件把窗口内文字翻成中文（机翻/手动，候选来自源码提取），替换层即时生效。");
        Ui.SameLineIfFits(Ui.ButtonWidth("后台自动翻译"));
        if (ImGui.Button("后台自动翻译"))
        {
            _plugin.ToggleTranslationUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件安装器里各插件介绍（简介/描述）的自动汉化——**基本不用管**：\n" +
                             "启动约 10 秒自动扫缺口，有 Key 就后台静默补翻，插件更新后下次启动自动覆盖。\n点进来可看进度、手动补译、导出/导入翻译包。");
        Ui.SameLineIfFits(Ui.ButtonWidth("日志窗口"));
        if (ImGui.Button("日志窗口"))
        {
            _plugin.ToggleLogUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("随时可看：操作日志 + 一键导出报错日志 zip。");

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
