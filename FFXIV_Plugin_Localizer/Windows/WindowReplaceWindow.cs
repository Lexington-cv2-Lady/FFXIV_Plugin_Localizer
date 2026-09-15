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
        : base("插件翻译###PluginLocalizerWindow")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary> 目录指纹（文件名+修改时间+大小），用于检测外部增删改，自动刷新。</summary>
    private string _dirStamp = "";
    private int _stampTick;                     // 目录指纹检查的帧计数（降频用）
    private readonly Dictionary<string, DateTime> _restoreArmedUntil = new(); // 破坏性操作（清空译文）的二次确认（键=restore::插件名）

    public override void Draw()
    {
        Ui.Hint("按插件翻译界面文字。候选来自「源码提取」；译文写入 插件翻译\\<插件名>.json 后即时参与替换。\n" +
                "已是中文版的插件会自动跳过。");

        // ── 自动检测译文目录变化（外部删/改 json 后无需手动点刷新）──
        // ⚠ 检测逻辑已上移到 ReplacementService.CheckExternalChanges（框架回调周期调用），
        //    原因：只在本窗口 Draw 里检测会导致「窗口没开时改 json 不生效」。这里只需处理本窗口的显示缓存。
        if (++_stampTick >= 30)
        {
            _stampTick = 0;
            var stamp = ComputeDirStamp();
            if (stamp != _dirStamp)
            {
                _dirStamp = stamp;
                _cache.Clear();      // 磁盘变了，本窗口的候选缓存也要跟着失效
                _editBufs.Clear();
            }
        }

        // 机翻结束的瞬间清缓存（后台任务合并了新翻译）
        if (_mtWasRunning && !_mt.Running)
        {
            _cache.Clear();
        }
        _mtWasRunning = _mt.Running;

        // ── ① 主操作（醒目）：翻译与预翻译 ──
        //    刻意只放两个最重要按钮，其余全部收进下方带边框的分组，避免"一屏按钮看不出重点"。
        if (_mt.Running)
        {
            Ui.PushDanger();
            if (ImGui.Button("停止翻译"))
            {
                _mt.Stop();
                _summary = "已请求停止：不再发起新批次，已翻完的部分会保留。";
            }
            Ui.PopDanger();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("停止翻译：不再发送新的批次请求。\n" +
                                 "⚠ 已翻完的批次会保留并写入译文表（那些已经花掉额度了，不浪费）；\n" +
                                 "正在请求中的那一批也会被立即中断。");
        }
        else
        {
            Ui.PushAccent();   // 主操作用橙金高亮，一眼看到
            if (ImGui.Button("一键翻译"))
            {
                if (string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(_plugin.Configuration)))
                {
                    _summary = "请先在「AI 设置」填写 API Key。";
                }
                else
                {
                    _summary = "";
                    _mt.StartAllWindowPlugins();
                }
            }
            Ui.PopAccent();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把所有插件的**窗口内文字**缺口一次翻完（调 AI，需先在「AI 设置」填 Key）。\n" +
                                 "同一句原文只送翻一次，结果分别落回各插件自己的译文表。\n" +
                                 "建议先点「全部预翻译」用现成词典白嫖一批，剩下的再交给它。");
        }

        Ui.SameLineIfFits(Ui.ButtonWidth("全部预翻译"));
        if (ImGui.Button("全部预翻译"))
        {
            // 用本项目词典给**所有插件**的候选套用现成译文（不调 AI、零成本）
            PrefillAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用本项目词典（我的翻译.json 等）给所有插件的候选套用已有译文——**不调 AI、零成本**。\n已翻译过的不动；命中才填，剩下的再交给「一键翻译」。\n建议每次翻新插件都先点它。");

        Ui.SameLineIfFits(Ui.ButtonWidth("确认用词典刷新"));
        {
            // ⚠ 二次确认：这个操作**会覆盖已翻译的条目**，包括你手动改过的那些
            //   （无法区分"AI 翻的"与"手改的"——两者在同一个译文表里）。
            //   "词典优先"正是本功能的目的，但必须让用户明确知道代价。
            var armedDict = _restoreArmedUntil.TryGetValue("refresh::__DICT__", out var untilDict) && DateTime.Now < untilDict;
            if (armedDict) Ui.PushAccent();
            if (ImGui.Button(armedDict ? "确认用词典刷新" : "用词典刷新译文"))
            {
                if (armedDict)
                {
                    _restoreArmedUntil.Remove("refresh::__DICT__");
                    RefreshAllWithDict();
                }
                else
                {
                    _restoreArmedUntil["refresh::__DICT__"] = DateTime.Now.AddSeconds(3);
                    _summary = "⚠ 此操作会把词典译文**覆盖到已翻条目上**（含你手改过的）。3 秒内再点一次确认。";
                }
            }
            if (armedDict) Ui.PopAccent();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把词典里的译文覆盖到**已经翻过的**条目上——改词典后一键生效，不必重新机翻。\n" +
                             "补的是这个死角：某条被 AI 翻错后，光改词典修不好它（预翻译只补缺口）。\n" +
                             "⚠ **会覆盖已翻条目，包括你手动改过的**（无法区分二者——它们在同一张表里）。\n" +
                             "词典里没有的条目一律不动；想只重翻某插件，可先对该插件点「清空本插件译文」。");

        ImGui.Spacing();

        // ── ② 目录与维护（带边框分组，收起视觉噪音）──
        using (var g = ImRaii.Group())
        {
            ImGui.TextDisabled("目录与维护");
            if (ImGui.Button("打开翻译目录"))
                OpenDir(_replacement.WindowTableDir);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("打开各插件的译文对照表目录（插件翻译\\<插件名>.json）。");

            Ui.SameLineIfFits(Ui.ButtonWidth("打开候选目录"));
            if (ImGui.Button("打开候选目录"))
                OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), ReplacementService.CandidateDirName));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("打开候选文案目录（源码提取产出的未翻译清单）。");

            Ui.SameLineIfFits(Ui.ButtonWidth("打开还原备份"));
            if (ImGui.Button("打开还原备份"))
                OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), ReplacementService.WindowBackupDirName));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("打开「清空译文」的自动备份目录（每次还原前都会先存一份，按时间戳分文件夹，最多保留最近 10 份）。\n" +
                                 "误点还原后，把对应时间戳文件夹里的 json 复制回 窗口翻译\\ 即可找回译文。\n" +
                                 "另外删除本身也走**系统回收站**，双保险。");

            Ui.SameLineIfFits(Ui.ButtonWidth("重新扫描"));
            if (ImGui.Button("重新扫描"))
            {
                // 清缓存 + 重读目录与表，解决"删了 json 仍显示已翻译"的问题
                _cache.Clear();
                _editBufs.Clear();
                _openPlugin = "";
                _replacement.Reload();
                _summary = "已重新读取对照表与候选目录（删除的译文会立刻消失）。";
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("重新读取磁盘上的译文与候选文件。\n在外部删除/修改 json 后点它即可同步（无需重载插件）。");
        }
        Ui.FrameLastGroup(0.35f);

        ImGui.Spacing();

        // ── ③ 导出与沉淀（带边框分组）──
        using (var g2 = ImRaii.Group())
        {
            ImGui.TextDisabled("导出与沉淀");
            if (ImGui.Button("复制待翻原文"))
            {
                // 汇总所有插件的未翻译原文，一行一条，便于丢给外部 AI / 在线翻译
                var lines = new List<string>();
                foreach (var (pname, _, _) in _replacement.GetWindowPlugins())
                {
                    var (_, untranslated) = _replacement.GetWindowEntries(pname);
                    foreach (var en in untranslated) lines.Add(en);
                }
                if (lines.Count == 0)
                {
                    _summary = "没有待翻译的原文（各插件均已翻完或未提取候选）。";
                }
                else
                {
                    ImGui.SetClipboardText(string.Join("\n", lines));
                    _summary = $"已复制 {lines.Count} 条待翻原文到剪贴板（一行一条）。";
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把所有插件尚未翻译的英文原文汇总复制（一行一条）。\n可直接粘到外部 AI / 在线翻译，再逐条填回。");

            Ui.SameLineIfFits(Ui.ButtonWidth("复制为翻译提示词"));
            if (ImGui.Button("复制为翻译提示词"))
            {
                CopyPromptToClipboard();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("生成一段完整的翻译提示词（含格式要求与全部英文原文）复制到剪贴板，\n直接粘给任意 AI 对话框即可，无需自己写要求。");

            Ui.SameLineIfFits(Ui.ButtonWidth("翻译结果写入词典"));
            if (ImGui.Button("翻译结果写入词典"))
                MergeAllIntoDict();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把**所有插件**已经翻好的「原文/译文」汇总写入 词典目录\\我的翻译.json。\n" +
                                 "写进去之后：①这些译文会参与「全部预翻译」，将来别的插件遇到同一句直接命中，不用再调 AI；\n" +
                                 "②词典只增不改——已存在的键不会被覆盖。");
        }
        Ui.FrameLastGroup(0.35f);

        ImGui.Spacing();

        // ── ④ 危险操作：**按"动作"命名**（清空/删除），不按"结果"命名（还原英文）──
        // ⚠ 命名教训（2026-09-15，用户实测撞坑）：原来叫「一键还原英文」，与上方的
        //   「一键翻译」只差两个字、还共用「一键」前缀，结果被误点 → 删掉整表译文。
        //   破坏性按钮要**直接说明它做什么**（清空/删除），而不是描述它的副作用（变回英文）。
        //   同时去掉「一键」前缀，避免与「一键翻译」在视觉上成对。
        Ui.Hint("危险操作（会删除译文文件）");
        {
            var armedAll = _restoreArmedUntil.TryGetValue("restore::__ALL__", out var untilAll) && DateTime.Now < untilAll;
            if (armedAll) Ui.PushDanger();
            if (ImGui.Button(armedAll ? "确认清空全部译文" : "清空全部译文（变回英文）"))
            {
                if (armedAll)
                {
                    _restoreArmedUntil.Remove("restore::__ALL__");
                    RestoreAll();
                }
                else
                {
                    _restoreArmedUntil["restore::__ALL__"] = DateTime.Now.AddSeconds(3);
                    _summary = "再次点击「确认清空全部译文」将删除所有插件的译文（3 秒内有效）。";
                }
            }
            if (armedAll) Ui.PopDanger();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("删除**所有插件**的译文文件，界面即变回英文（=把这个工具翻的东西清掉）。\n" +
                             "⚠ 删除走**系统回收站**，且清空前会先备份到 窗口翻译_还原备份\\<时间戳>\\，两种方式都能找回。\n" +
                             "候选清单保留，之后可重新翻译（但要重新花 AI 额度）。\n" +
                             "⚠ 只想临时看英文、不丢译文 → 请用**主窗口**的「还原英文」（只关替换开关、不动文件）。");

        if (_mt.Running)
        {
            // ⚠ 文字在前、进度条在其**右侧同一行**（用户要求"保留文字"，进度条只是补充）。
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.3f, 1f), _mt.Status);
            DrawTranslateProgressBar();
        }
        if (_summary.Length > 0)
        {
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _summary);
        }

        ImGui.Separator();

        var plugins = _replacement.GetWindowPlugins();
        if (plugins.Count == 0)
        {
            Ui.Hint("还没有候选文案。请先到「源码提取」窗口提取某个插件的文案，再回来翻译。");
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
                    var label = translated >= total ? $"{name}（已翻译 {translated}/{total}）全部已翻译" : $"{name}（已翻 {translated}/共 {total}）";
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

    /// <summary>
    /// 翻译进度条（配合上方文字，**文字保留不动**）。
    /// 只在有进度可算（总数 &gt; 0）时画；用 `ProgressBar` 自带覆盖文字显示 "已完成/总数 (百分比)"。
    /// ⚠ 数值来自 `MtTranslateService.ProgressDone/Total`，是**已落盘**的条数（每批即写，见 TranslateAll）。
    /// </summary>
    private void DrawTranslateProgressBar()
    {
        var total = _mt.ProgressTotal;
        if (total <= 0) return;
        var done = Math.Clamp(_mt.ProgressDone, 0, total);
        var frac = (float)done / total;
        // 宽度自适应窗口（窄窗口不会溢出）；高度用默认，跟输入框等高
        var w = Math.Max(120f, ImGui.GetContentRegionAvail().X);
        ImGui.ProgressBar(frac, new Vector2(w, 0f), $"{done}/{total}（{frac * 100f:F0}%）");
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

    /// <summary>
    /// 计算译文目录指纹（每个 json 的文件名 + 修改时间 + 大小 + 总数）。
    /// 用于检测外部增删改：用户在资源管理器里删了 json，指纹变化 → 自动重读，界面不再显示已翻译。
    /// </summary>
    private string ComputeDirStamp()
    {
        try
        {
            var dir = _replacement.WindowTableDir;
            if (!Directory.Exists(dir)) return "<none>";
            var entries = Directory.EnumerateFiles(dir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name}:{f.LastWriteTimeUtc.Ticks}:{f.Length}");
            return string.Join("|", entries);
        }
        catch
        {
            return "<err>";
        }
    }

    /// <summary> 对单个插件执行预翻译（用旧词典套用现成译文，不调 AI）。 </summary>
    private void PrefillOne(string plugin)
    {
        var (_, untranslated) = _replacement.GetWindowEntries(plugin);
        var (hit, pairs) = _plugin.OldDict.Prefill(untranslated);
        if (hit == 0)
        {
            _summary = _plugin.OldDict.Loaded
                ? $"{plugin}：旧词典未命中任何待翻条目（词典 {_plugin.OldDict.Count} 条）。"
                : "旧词典未加载（检查「AI 设置」里的词典目录）。";
            return;
        }
        var dict = pairs.ToDictionary(p => p.En, p => p.Zh, StringComparer.Ordinal);
        _replacement.MergeWindowEntries(plugin, dict);
        Invalidate(plugin);
        var left = untranslated.Count - hit;
        _summary = $"{plugin}：预翻译命中 {hit} 条（剩余 {left} 条可机翻）。";
        _plugin.AppLog.Info($"[预翻译] {plugin} 命中 {hit} 条，剩余 {left} 条");
    }

    /// <summary> 对所有插件执行预翻译。 </summary>
    private void PrefillAll()
    {
        if (!_plugin.OldDict.Loaded)
        {
            _summary = "旧词典未加载（词典目录可能未探测到，可在「AI 设置」手动指定）。";
            return;
        }
        var totalHit = 0;
        var totalLeft = 0;
        var plugins = _replacement.GetWindowPlugins();
        foreach (var (name, _, _) in plugins)
        {
            var (_, untranslated) = _replacement.GetWindowEntries(name);
            var (hit, pairs) = _plugin.OldDict.Prefill(untranslated);
            if (hit > 0)
            {
                _replacement.MergeWindowEntries(name, pairs.ToDictionary(p => p.En, p => p.Zh, StringComparer.Ordinal));
                Invalidate(name);
            }
            totalHit += hit;
            totalLeft += untranslated.Count - hit;
        }
        _summary = $"全部预翻译完成：{plugins.Count} 个插件共命中 {totalHit} 条（剩余 {totalLeft} 条待机翻）。";
        _plugin.AppLog.Info($"[预翻译] {_summary}");
    }

    /// <summary>
    /// **用词典刷新译文**：把词典里的译文**覆盖到已翻译的条目**上。
    ///
    /// 补的死角（2026-09-15 发现）：某条被 AI 翻错后，即使改进词典也修不好它——
    /// 「全部预翻译」只作用于缺口、合并又跳过已有键 → 只能「还原英文」全删重来。
    /// 本操作让**词典（人工维护、权威）覆盖 AI 产出**，改词典后一键生效，不必重翻。
    /// 只覆盖"词典里有且值不同"的条目，其余（含你手动编辑过的）一律不动。
    /// </summary>
    private void RefreshAllWithDict()
    {
        try
        {
            if (!_plugin.OldDict.Loaded)
            {
                _summary = $"本项目词典为空（{_plugin.Configuration.DictDir}）——先在「我的翻译.json」里写好译文再刷新。";
                return;
            }
            var totalUpdated = 0;
            var totalAdded = 0;
            var affected = 0;
            foreach (var (name, _, _) in _replacement.GetWindowPlugins())
            {
                var (translated, _) = _replacement.GetWindowEntries(name);
                var updates = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (en, cur) in translated)
                {
                    if (!_plugin.OldDict.TryGet(en, out var zh)) continue;   // 词典没这条 → 不动
                    if (string.Equals(zh, cur, StringComparison.Ordinal)) continue; // 值一样 → 不动
                    updates[en] = zh;
                }
                if (updates.Count == 0) continue;
                var (u, a) = _replacement.OverwriteWindowEntries(name, updates);
                if (u + a > 0) affected++;
                totalUpdated += u;
                totalAdded += a;
                Invalidate(name);
            }
            _summary = totalUpdated + totalAdded > 0
                ? $"已用词典刷新 {affected} 个插件：更新 {totalUpdated} 条、新增 {totalAdded} 条（其余条目未改动）。"
                : "没有需要刷新的条目（词典与现有译文一致，或词典里没有这些文案）。";
            _plugin.AppLog.Info($"[预翻译] 用词典刷新：{affected} 个插件，更新 {totalUpdated}，新增 {totalAdded}");
        }
        catch (Exception ex)
        {
            _summary = "刷新失败：" + ex.Message;
            _plugin.AppLog.Error("[预翻译] 用词典刷新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 「复制为翻译提示词」：汇总所有待翻原文，套上一段含格式要求的完整提示词，整段进剪贴板。
    /// 给"不想用内置 AI、要手工丢给外部对话框"的用户用（旧项目也有这套"外部 AI 路线"的思路）。
    /// </summary>
    private void CopyPromptToClipboard()
    {
        var pairs = new List<string>();
        foreach (var (pname, _, _) in _replacement.GetWindowPlugins())
        {
            var (_, untranslated) = _replacement.GetWindowEntries(pname);
            foreach (var en in untranslated) pairs.Add($"- {en}");
        }
        if (pairs.Count == 0)
        {
            _summary = "没有待翻译内容。";
            return;
        }
        var prompt = "请把下列游戏插件界面英文翻译成简洁自然的简体中文，保持原顺序，逐条输出「英文 => 中文」：\n\n"
                     + string.Join("\n", pairs);
        ImGui.SetClipboardText(prompt);
        _summary = $"已复制翻译提示词（含 {pairs.Count} 条原文）到剪贴板。";
    }

    /// <summary>
    /// **翻译结果写入词典**：把所有插件已翻好的「原文/译文」汇总进 词典目录\我的翻译.json。
    ///
    /// 为什么有用：译文表是**按插件**存的，同一个英文串在别的插件里遇到还得重翻一次；
    /// 沉淀到词典后，「全部预翻译」就能免费命中，且译名前后一致（词典是跨插件的通用词源）。
    /// ⚠ 词典是用户资产：**只新增、不覆盖已有键**（见 OldDictionaryService.MergeIntoDict）。
    /// </summary>
    private void MergeAllIntoDict()
    {
        try
        {
            var dir = _plugin.Configuration.DictDir;
            if (string.IsNullOrWhiteSpace(dir))
            {
                _summary = "词典目录未设置（可在「AI 设置」里指定）。";
                return;
            }
            var pairs = new List<(string En, string Zh)>();
            var plugins = _replacement.GetWindowPlugins();
            foreach (var (name, _, _) in plugins)
            {
                var (translated, _) = _replacement.GetWindowEntries(name);
                foreach (var (en, zh) in translated) pairs.Add((en, zh));
            }
            if (pairs.Count == 0)
            {
                _summary = "还没有任何已完成的译文可写入。";
                return;
            }
            var (added, skipped) = _plugin.OldDict.MergeIntoDict(dir, pairs);
            _plugin.OldDict.Load(dir);                                   // 立即重载，新词条马上可用于预翻译
            _summary = added > 0
                ? $"已把 {added} 条译文写入 我的翻译.json（跳过 {skipped} 条：已存在/同文/不合格）。"
                : $"没有新增（{skipped} 条都已存在或不合格）。";
            _plugin.AppLog.Info($"[预翻译] 写入词典：+{added}，跳过 {skipped}");
        }
        catch (Exception ex)
        {
            _summary = "写入词典失败：" + ex.Message;
            _plugin.AppLog.Error("[预翻译] 写入词典失败：" + ex.Message);
        }
    }

    /// <summary>
    /// **清空全部译文**（原「一键还原英文」）：删掉每个插件的译文文件，界面立即回到英文。
    /// 候选清单保留（题目仍在），之后还能「一键翻译」重来。
    /// 与主窗口「还原英文」的区别：那个只关替换、**不动文件**（可无损恢复）；这个**真删译文**。
    /// </summary>
    private void RestoreAll()
    {
        try
        {
            // ⚠ 先备份再删：译文的代价是 AI 额度，误点不可接受（2026-09-15 已真实丢过 416 条）
            var backup = _replacement.BackupWindowTables();
            var plugins = _replacement.GetWindowPlugins();
            var totalRemoved = 0;
            var affected = 0;
            foreach (var (name, _, _) in plugins)
            {
                var n = _replacement.ClearPluginTranslations(name);
                if (n > 0) { affected++; totalRemoved += n; }
                Invalidate(name);
            }
            _openPlugin = "";
            _replacement.Reload();
            var bak = string.IsNullOrEmpty(backup) ? "" : $"\n（已自动备份到 {ReplacementService.WindowBackupDirName}\\{Path.GetFileName(backup)}，可从「打开翻译目录」的上级找回）";
            _summary = affected > 0
                ? $"已把 {affected} 个插件还原为英文（共删除 {totalRemoved} 条译文；候选保留，可重新翻译）。{bak}"
                : "没有可还原的译文（各插件当前都没有译文）。";
            _plugin.AppLog.Info($"[还原] 全部还原英文：{affected} 个插件、{totalRemoved} 条");
        }
        catch (Exception ex)
        {
            _summary = "还原失败：" + ex.Message;
            _plugin.AppLog.Error("[还原] 全部还原失败：" + ex.Message);
        }
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

    /// <summary>
    /// 在游戏里打开目标插件**自己的**配置界面（精确到该插件，等同插件安装器里每行的 ⚙ 按钮），
    /// 方便翻完立刻看效果。
    /// 官方正路（反射 Dalamud 15.0.3.4 核实）：`IDalamudPluginInterface.InstalledPlugins`
    /// → `IExposedPlugin.OpenConfigUi()`；无配置界面则退 `OpenMainUi()`。
    /// </summary>
    private void OpenPluginConfigUi(string plugin)
    {
        try
        {
            var target = Plugin.PluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, plugin, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                _summary = $"未找到已安装的 {plugin}（可能只提取过源码、本机未装或未启用）。";
                return;
            }
            if (!target.IsLoaded)
            {
                _summary = $"{target.Name} 当前未加载（被禁用了？），无法打开配置界面。";
                return;
            }
            if (target.HasConfigUi)
            {
                target.OpenConfigUi();
                _summary = $"已打开 {target.Name} 的配置界面。";
            }
            else if (target.HasMainUi)
            {
                target.OpenMainUi();
                _summary = $"{target.Name} 没有配置界面，已打开其主界面。";
            }
            else
            {
                _summary = $"{target.Name} 未提供配置/主界面入口，无法打开。";
            }
        }
        catch (Exception ex)
        {
            _summary = "打开插件配置失败：" + ex.Message;
            _plugin.AppLog.Error($"[窗口] 打开插件配置失败（{plugin}）：{ex.Message}");
        }
    }

    private void DrawPluginEditor(string plugin)
    {
        var (translated, untranslated) = GetCached(plugin);

        if (_mt.Running)
        {
            ImGui.TextDisabled($"机翻进行中（{_mt.Status}）——完成后自动刷新。");
            DrawTranslateProgressBar();
        }
        else
        {
            // ── 翻译动作（主操作高亮）──
            Ui.PushAccent();
            if (ImGui.Button($"翻译本插件缺失（{untranslated.Count} 条）"))
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
            Ui.PopAccent();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("只翻**这一个插件**的缺口（调 AI）。\n已翻译的不覆盖；缺口为 0 时按钮上会显示 0 条。");

            Ui.SameLineIfFits(Ui.ButtonWidth("预翻译本插件"));
            if (ImGui.Button("预翻译本插件"))
            {
                PrefillOne(plugin);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("用本项目词典给该插件的候选文案套用已有译文（**不调 AI、零成本**）。\n已翻译过的不覆盖；剩下的可再点「翻译本插件缺失」机翻。");

            Ui.SameLineIfFits(Ui.ButtonWidth("重读本插件"));
            if (ImGui.Button("重读本插件"))
            {
                // 外部改了/删了该插件的 json 后，用它立刻同步（不解缓存就要靠这个）
                Invalidate(plugin);
                _replacement.Reload();
                _summary = $"已重新读取 {plugin} 的译文与候选。";
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("重新读取该插件的译文文件与候选清单。\n在外部删除/编辑 json 后点它同步，无需重载插件。");

            Ui.SameLineIfFits(Ui.ButtonWidth("打开插件配置"));
            if (ImGui.Button("打开插件配置"))
            {
                OpenPluginConfigUi(plugin);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("在游戏里打开该插件自己的配置界面（等同插件安装器里的 ⚙ 按钮），方便即时查看翻译效果。\n插件没有配置界面时改开其主界面；两者都没有或未加载则提示。");

            // 单插件清空译文（候选保留），二次确认防误点
            // ⚠ 命名同全局那个：**说动作（清空）而非结果（还原英文）**，避免与「翻译本插件缺失」看混。
            Ui.SameLineIfFits(Ui.ButtonWidth("确认清空本插件译文"));
            var restoreKey = "restore::" + plugin;
            var armed = _restoreArmedUntil.TryGetValue(restoreKey, out var until) && DateTime.Now < until;
            if (armed) Ui.PushDanger();
            if (ImGui.Button(armed ? "确认清空本插件译文" : "清空本插件译文"))
            {
                if (armed)
                {
                    _replacement.BackupWindowTables();   // 清空前先备份（误点可捞回）
                    var n = _replacement.ClearPluginTranslations(plugin);
                    _restoreArmedUntil.Remove(restoreKey);
                    Invalidate(plugin);
                    _summary = $"已清空 {plugin} 的 {n} 条译文（界面变回英文；候选保留，可重新翻译）。";
                }
                else
                {
                    _restoreArmedUntil[restoreKey] = DateTime.Now.AddSeconds(3);
                    _summary = $"再次点击「确认清空本插件译文」将删除 {plugin} 的全部译文（3 秒内有效）。";
                }
            }
            if (armed) Ui.PopDanger();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("删除这个插件的译文文件，它的界面即变回英文。\n" +
                                 "⚠ 删除走**系统回收站**，且清空前会先备份到 窗口翻译_还原备份\\，两种方式都能找回。\n" +
                                 "候选清单保留，之后可重新翻译；二次确认防误点。");
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
        if (ImGui.SmallButton("复制原文"))
        {
            ImGui.SetClipboardText(en);
            _summary = $"已复制原文到剪贴板：{(en.Length > 40 ? en[..40] + "…" : en)}";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把这句英文原文复制到剪贴板（免手动选中，便于外部检索或翻译）。");
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
