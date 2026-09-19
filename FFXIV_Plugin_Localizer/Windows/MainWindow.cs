using System;
using System.IO;
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
    private long _restoreArmMs;          // 彻底还原卫月的二次确认计时（0 = 未武装）
    private string _restoreMsg = "";     // 彻底还原的结果提示

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
        if (ImGui.Checkbox("启用汉化", ref enabled))
        {
            _plugin.Configuration.ReplacementEnabled = enabled;
            _plugin.Replacement.Enabled = enabled;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("汉化总开关：关掉后所有界面文字立即还原英文（翻译文件保留，随时可再开）。");
        // 常驻排障说明（独占一行，不依赖 hover）
        Ui.ColoredWrapped(new Vector4(0.9f, 0.85f, 0.55f, 1f),
            "排障：若哪个插件界面错乱/显示异常，把上面的「启用汉化」取消勾选，所有界面立刻还原英文；翻译文件不删，重新勾选即恢复。");

        // ── 彻底清空所有译文（真删，有备份；与"取消启用汉化"不重复：那个只关开关，这个连磁盘文件一起清） ──
        if (_restoreArmMs > 0 && Environment.TickCount64 - _restoreArmMs < 5000)
        {
            if (ImGui.Button("确认彻底清空"))
            {
                _restoreArmMs = 0;
                try
                {
                    var (aff, rem, bak) = _plugin.Replacement.PurgeAllWindowTranslations();
                    _plugin.Configuration.ReplacementEnabled = false;
                    _plugin.Replacement.Enabled = false;
                    _plugin.Configuration.Save();
                    _restoreMsg = $"已彻底清空：删除 {aff} 个插件共 {rem} 条译文，替换开关已关闭。" +
                                  (string.IsNullOrEmpty(bak) ? "" : $"（译文已备份到 {ReplacementService.WindowBackupDirName}）") +
                                  "\n如需完全移除插件本体，请到卫月插件列表卸载「FFXIV_Plugin_Localizer」——钩子会自动释放，卫月 100% 恢复原样。";
                }
                catch (Exception ex)
                {
                    _restoreMsg = "彻底还原失败：" + ex.Message;
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("再次点击确认：备份并删除全部译文文件 + 关闭替换（有备份，可找回）。");
        }
        else
        {
            if (ImGui.Button("彻底清空所有译文"))
            {
                _restoreArmMs = Environment.TickCount64;
                _restoreMsg = "再点一次「确认彻底清空」执行（3 秒内）。";
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("**真删**：备份并删除全部译文文件 + 关闭替换，界面立即回到英文。\n" +
                                 "与「临时切回英文」的区别：这个连磁盘文件一起清（有备份）；那个只关开关、不动文件。\n" +
                                 "只清译文，不动你的 API Key/代理/词典等设置。\n" +
                                 "完全移除插件本体请到卫月插件列表卸载（钩子自动释放，界面 100% 恢复原样）。");
        }
        if (_restoreMsg.Length > 0)
        {
            ImGui.Spacing();
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.4f, 1f), _restoreMsg);
        }

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

        // ── 仓库地址：按钮打开二级窗口（只读展示主库 + 第三方仓库） ──
        if (ImGui.Button("仓库地址"))
        {
            _plugin.ToggleRepoUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开只读窗口：看你在卫月设置里配的主库 + 第三方仓库。\n" +
                             "「后台自动翻译」勾了「预翻全部」时按这些仓库送翻介绍。\n" +
                             "本插件不增删、不改卫月任何设置。");

        ImGui.Separator();

        // ── 功能入口（第一行：流程 ① 配置 → ② 提取 → ③ 翻译；第二行：非流程工具） ──
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
        // 2026-09-17：流程（第 1/2/3 步）一行；非流程工具（后台自动翻译/日志/词典/黑名单）强制换行到下一行
        ImGui.NewLine();

        Ui.SameLineIfFits(Ui.ButtonWidth("后台自动翻译"));
        if (ImGui.Button("后台自动翻译"))
        {
            _plugin.ToggleTranslationUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件安装器里各插件介绍（简介/描述）的自动汉化，基本不用管：\n" +
                             "启动约 10 秒自动扫缺口，有 Key 就后台静默补翻，插件更新后下次启动自动覆盖。\n点进来可看进度、手动补译、导出/导入翻译包。");
        Ui.SameLineIfFits(Ui.ButtonWidth("日志窗口"));
        if (ImGui.Button("日志窗口"))
        {
            _plugin.ToggleLogUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("随时可看：操作日志 + 一键导出报错日志 zip。");

        // 2026-09-17：词典入口（独立二级窗口，含词典目录/重载/黑名单）
        Ui.SameLineIfFits(Ui.ButtonWidth("词典目录"));
        if (ImGui.Button("词典目录"))
        {
            _plugin.ToggleDictUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开词典窗口（预翻译词源目录、重载词典、单词黑名单）。\n译文在此沉淀、跨插件复用；黑名单词永远保持英文。");

        // 2026-09-19：体检——逐个打开已装插件配置窗，自动找出"还是英文"的漏网文字
        Ui.SameLineIfFits(Ui.ButtonWidth("体检"));
        if (_plugin.HealthRunning)
        {
            ImGui.BeginDisabled();
            ImGui.Button("体检中…");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("体检"))
        {
            _plugin.StartHealthCheck();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自动逐个打开已装插件的配置窗（约 1.5 秒/个），收集"
                           + "「对照表里没有、界面仍显示英文」的漏网文字——像你肉眼逐个窗口看一遍。\n"
                           + "跑完在下方和日志出报告。会短暂自动开关窗口，属正常。");
        if (_plugin.HealthReport.Length > 0)
        {
            Ui.ColoredWrapped(new Vector4(0.7f, 0.9f, 0.75f, 1f), _plugin.HealthReport);
        }

        ImGui.Separator();

        // ── 日志（置底持续显示） ──
        DrawLog(_plugin.AppLog, 170f);
    }

    /// <summary> 底部日志面板：旧→新显示（最多 80 条），新日志且原先贴底时自动滚到底。 </summary>
    // (2026-09-17 黑名单增补 Ko-fi/KonaeAkira/WorkingRobot/ResizableHUD——触发重载)
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
