using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 词典管理窗口（2026-09-17 从 AI 设置拆出独立二级窗口）：
/// 本项目词典（预翻译词源）的查看/重载 + 单词黑名单的查看与打开编辑。 </summary>
public sealed class DictWindow : Window
{
    private readonly Plugin _plugin;
    private string _wikiResult = "";   // wiki「自动探测并加载」的操作反馈
    private string _missedResult = ""; // 「运行时发现」补译反馈
    private readonly Dictionary<string, string> _missedBufs = new(StringComparer.Ordinal); // 每条未翻译英文的输入框缓冲

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

        // ── wiki 官方术语表（官方译名；2026-09-19 从 AI 设置挪到这里——本质是词典/术语，跟 API 配置不一类） ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("wiki 官方术语表（物品/技能名的官方译名，优先级高于机翻）：");
        Ui.Hint("与旧项目「FFXIV 模组汉化工具」联动：装了它就直接读取它词典目录下的 wiki 术语，每次启动重新读取，\n" +
                "故旧项目更新术语后本插件立即用上最新版（不复制文件）。未装旧项目则留空此目录，正常走机翻。");
        var wikiOn = cfg.WikiEnabled;
        if (ImGui.Checkbox("启用 wiki 术语表", ref wikiOn))
        {
            cfg.WikiEnabled = wikiOn;
            cfg.Save();
            _plugin.ReloadWiki(); // 立即生效
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("启用后，命中术语的英文一律用官方译名（如物品名、技能名），\n优先于机翻和普通对照表。中文化界面时更准确统一。");

        var wikiDir = cfg.WikiDir;
        ImGui.SetNextItemWidth(Math.Max(200f, ImGui.GetContentRegionAvail().X - 140f));
        if (ImGui.InputTextWithHint("##WikiDir", "术语目录（留空 = 自动探测旧项目的词典目录）", ref wikiDir, 512))
        {
            cfg.WikiDir = wikiDir.Trim();
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("自动探测并加载"))
        {
            var n = _plugin.ReloadWiki();
            _wikiResult = n > 0
                ? $"wiki 术语已加载 {n} 条并生效（来源：{cfg.WikiDir}）"
                : "未找到术语表：请确认已装旧项目插件（会读其词典目录），或在上方手动填写术语目录。";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新探测旧项目词典目录并加载其 wiki 术语（不复制，直接读取）。");

        if (_plugin.Wiki.Loaded)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f),
                $"【已启用】 已加载 {_plugin.Wiki.Count} 条官方术语（{string.Join("、", _plugin.Wiki.CategoryCounts.Select(kv => $"{kv.Key} {kv.Value}"))}）");
        else
            Ui.Hint("未加载术语表。装了旧项目插件会自动读取其术语；也可在上方手动指定目录后点「自动探测并加载」。");

        if (_wikiResult.Length > 0)
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _wikiResult);

        // ── 运行时发现的未翻译英文（钩子替换表查不到、但插件在游戏里实际显示过的英文）──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var missed = _plugin.Replacement.GetMissed();
        ImGui.TextUnformatted($"运行时发现的未翻译英文（{missed.Count} 条）");
        Ui.Hint("这些是插件在游戏界面里**实际显示过**、但翻译表没命中的英文。\n" +
                "源码提取会漏扫（有些插件文字从游戏数据读、或不在源码字符串里），但只要它显示过就会被这里抓到。\n" +
                "在右边填中文 → 点「补」写进本项目词典，立即全局生效（别的插件遇到同一句也直接命中）。");

        if (missed.Count > 0)
        {
            using (var list = ImRaii.Child("##missedList", new Vector2(-1f, 220f), true))
            {
                if (list.Success)
                {
                    foreach (var en in missed)
                    {
                        ImGui.PushID(en);
                        if (!_missedBufs.TryGetValue(en, out var buf)) buf = "";
                        var shown = en.Length > 46 ? en[..46] + "…" : en;
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                        ImGui.TextWrapped(shown);
                        ImGui.PopStyleColor();
                        ImGui.SetNextItemWidth(220f);
                        ImGui.InputTextWithHint("##zh", "填中文译文…", ref buf, 256);
                        ImGui.SameLine();
                        if (ImGui.Button("补"))
                        {
                            var zh = buf.Trim();
                            if (zh.Length > 0)
                            {
                                var (added, _) = _plugin.OldDict.MergeIntoDict(cfg.DictDir, new List<(string, string)> { (en, zh) });
                                _plugin.OldDict.Load(cfg.DictDir);
                                _plugin.Replacement.RemoveMissed(en);
                                _missedBufs.Remove(en);
                                _missedResult = added > 0 ? $"已补：{shown} → {zh}" : $"词典已有此条（值保留）：{shown}";
                            }
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("忽略"))
                        {
                            _plugin.Replacement.RemoveMissed(en);
                            _missedBufs.Remove(en);
                        }
                        ImGui.PopID();
                    }
                }
            }
        }
        if (_missedResult.Length > 0)
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _missedResult);
    }
}
