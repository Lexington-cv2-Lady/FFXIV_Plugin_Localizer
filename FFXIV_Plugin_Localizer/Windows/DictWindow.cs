using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 词典管理窗口（2026-09-17 从 AI 设置拆出独立二级窗口）：
/// 本项目词典（预翻译词源）的查看/重载 + 单词黑名单的查看与打开编辑。 </summary>
public sealed class DictWindow : Window
{
    private readonly Plugin _plugin;

    public DictWindow(Plugin plugin) : base("词典###PluginLocalizerDict")
    {
        _plugin = plugin;
        Size = new Vector2(600, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        // ── 本项目词典（预翻译词源；独立于旧项目） ──
        ImGui.TextUnformatted("本项目词典（预翻译词源，独立于旧项目）：");
        Ui.Hint("「插件翻译」的「全部预翻译」会读这里的「我的翻译.json」等文件，把现成译文直接套到候选文案上（不调 AI、不花额度）。\n" +
                "目录默认在本插件数据目录下，与旧项目互不影响；放入/修改译文后点「重载词典」即可生效。");

        if (ImGui.Button("打开词典目录"))
        {
            try
            {
                Directory.CreateDirectory(cfg.DictDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{cfg.DictDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _plugin.AppLog.Error("打开词典目录失败：" + ex.Message); }
        }
        ImGui.SameLine();
        if (ImGui.Button("重载词典"))
        {
            _plugin.ReloadOldDict();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新读取词典目录下的译文文件。放入/修改「我的翻译.json」后点它即可。");

        if (_plugin.OldDict.Loaded)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f),
                $"【已启用】 本项目词典 {_plugin.OldDict.Count} 条（{string.Join("、", _plugin.OldDict.SourceCounts.Select(kv => $"{kv.Key} {kv.Value}"))}）");
        else
            Ui.Hint($"本项目词典为空（{cfg.DictDir}）。把「我的翻译.json」放进去再点「重载词典」即可用于预翻译。");

        // 单词黑名单（命中词永远保持英文）——搬自旧项目同名字段
        if (_plugin.OldDict.BlacklistCount > 0)
            Ui.ColoredWrapped(new Vector4(0.7f, 0.85f, 1f, 1f),
                $"单词黑名单 {_plugin.OldDict.BlacklistCount} 条（这些词永远保持英文，不翻译也不替换）");
        else
            Ui.Hint($"没有单词黑名单（{OldDictionaryService.BlacklistFileName}）。想让某些词永远保持英文（如 URL、DPS），\n" +
                    "点「打开黑名单文件」写入即可（每行一个词）。");
        if (ImGui.Button("打开黑名单文件"))
        {
            try
            {
                Directory.CreateDirectory(cfg.DictDir);
                var path = OldDictionaryService.EnsureBlacklistFile(cfg.DictDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { _plugin.AppLog.Error("打开黑名单文件失败：" + ex.Message); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开/创建 单词黑名单.json（用默认程序编辑）。\n黑名单里的词永远保持英文、不参与翻译（如 URL、DPS、人名/品牌名）。\n保存后点「重载词典」生效。");
    }
}
