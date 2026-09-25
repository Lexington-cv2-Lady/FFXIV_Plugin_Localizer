using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// wiki 术语对照：读取旧项目（FFXIV_MOD选项汉化工具）维护的官方译名词表，
/// 覆盖物品/技能/任务/成就/种族/情感动作共 6 类（约 6.2 万条）。
///
/// 用途：
///   ① **替换优先词源**——命中 wiki 的英文一律用官方译名（机翻物品名几乎必错，如 "Crystal" 应为"水晶"而非"晶体"）；
///   ② AI 翻译参考——送翻时把相关术语作为对照提示，提高一致性。
///
/// 文件格式（旧项目）：<c>{ "terms": { "英文": "中文", ... } }</c>，放在 <c>wiki_术语对照\</c> 目录下。
/// </summary>
public sealed class WikiGlossaryService
{
    /// <summary> 默认目录名（相对于插件数据目录，用于用户手动放置术语文件）。 </summary>
    public const string DirName = "wiki_术语对照";
    /// <summary> 单条术语的最大长度（过长的不是术语，是描述）。 </summary>
    private const int MaxTermLen = 120;

    /// <summary>
    /// **自动探测旧项目（FFXIV 模组汉化工具）的 wiki 术语目录**——用于与其联动：
    /// 用户若装了旧项目插件，就直接读它词典目录下的 wiki 文件（**不复制**），
    /// 这样老项目更新术语后，本插件下次启动即用最新版本，无需重新内置。
    ///
    /// 探测方式：扫描 pluginConfigs 下所有插件配置，找含 <c>DictionaryPath</c> 的，
    /// 检查其下 <c>wiki_术语对照</c> 目录是否存在且含术语 json。返回可用的 wiki 目录（找不到返回 null）。
    /// 不依赖旧插件的具体 ID（兼容中英文 ID 的不同版本）。
    /// </summary>
    public static string? DetectOldProjectWikiDir(string pluginConfigRoot)
    {
        try
        {
            if (!Directory.Exists(pluginConfigRoot)) return null;
            foreach (var cfgFile in Directory.EnumerateFiles(pluginConfigRoot, "*.json"))
            {
                // 跳过本插件自己的配置
                if (Path.GetFileName(cfgFile).StartsWith("FFXIV_Plugin_Localizer", StringComparison.OrdinalIgnoreCase)) continue;
                if (cfgFile.Contains(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                string? dictPath = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfgFile));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("DictionaryPath", out var dp) &&
                        dp.ValueKind == JsonValueKind.String)
                    {
                        dictPath = dp.GetString();
                    }
                }
                catch { continue; } // 单个配置坏了跳过
                if (string.IsNullOrWhiteSpace(dictPath)) continue;

                var wikiDir = Path.Combine(dictPath!, DirName);
                if (Directory.Exists(wikiDir) && Directory.EnumerateFiles(wikiDir, "*.json").Any())
                {
                    return wikiDir;
                }
            }
        }
        catch { /* 探测失败返回 null */ }
        return null;
    }

    /// <summary>
    /// **自动探测旧项目（FFXIV 模组汉化工具）的「词典目录」根**（即其配置里的 <c>DictionaryPath</c> 本身，
    /// **不含** wiki_术语对照 等子目录）。与 <see cref="DetectOldProjectWikiDir"/> 同一套扫描逻辑，
    /// 但返回的是目录根——供「单词黑名单联动」定位 <c>&lt;词典目录&gt;\单词黑名单.json</c>。
    /// 扫描 pluginConfigs 下所有插件配置，找含有效 <c>DictionaryPath</c> 且目录真实存在者。找不到返回 null。
    /// 不依赖旧插件的具体 ID（兼容不同版本）；只读其配置，不修改任何文件。
    /// </summary>
    public static string? DetectOldProjectDictionaryPath(string pluginConfigRoot)
    {
        try
        {
            if (!Directory.Exists(pluginConfigRoot)) return null;
            foreach (var cfgFile in Directory.EnumerateFiles(pluginConfigRoot, "*.json"))
            {
                // 跳过本插件自己的配置
                if (Path.GetFileName(cfgFile).StartsWith("FFXIV_Plugin_Localizer", StringComparison.OrdinalIgnoreCase)) continue;
                if (cfgFile.Contains(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                string? dictPath = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfgFile));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("DictionaryPath", out var dp) &&
                        dp.ValueKind == JsonValueKind.String)
                    {
                        dictPath = dp.GetString();
                    }
                }
                catch { continue; } // 单个配置坏了跳过
                if (string.IsNullOrWhiteSpace(dictPath)) continue;
                if (Directory.Exists(dictPath)) return dictPath;
            }
        }
        catch { /* 探测失败返回 null */ }
        return null;
    }

    private readonly AppLog _appLog;

    /// <summary> 英文 → 中文（去重后的全量术语表）。 </summary>
    private Dictionary<string, string> _terms = new(StringComparer.Ordinal);


    /// <summary>
    /// `_terms` + `_buckets` 的**一致性快照**：后台线程一次取到"配套的"两者。
    ///
    /// ⚠ 2026-09-18 全面审查④（**中**）：原实现 `Load()` 在主线程把 `_terms` 整个换新、`_buckets` 置 null，
    ///   而**后台机翻线程**正在 `FindRelevant` 里拿**旧 `_buckets` 的键**去查 `_terms[en]` —— 两者不配套时
    ///   抛 **KeyNotFoundException**，被 `TranslateBatchWithSplit` 的通用 catch 吞成"批次失败"
    ///   → **静默丢翻译**（日志只留一条 warn）。触发场景：「翻译进行中点重载 wiki 术语」。
    ///   现在：`Load()` 先在**局部**构建，完成后用**单个 volatile 引用**一次发布 →
    ///   读方取一次快照，`Terms`/`Buckets` 必定配套，不存在中间态。
    /// </summary>
    private sealed record Snapshot(Dictionary<string, string> Terms, Dictionary<char, List<string>> Buckets);
    private volatile Snapshot? _snap;

    /// <summary> 各类术语条数（文件名 → 条数），用于 UI 展示。 </summary>
    public IReadOnlyDictionary<string, int> CategoryCounts => _categoryCounts;
    private readonly Dictionary<string, int> _categoryCounts = new(StringComparer.Ordinal);

    /// <summary> 是否已加载到术语。 </summary>
    public bool Loaded => _terms.Count > 0;

    /// <summary> 术语总数。 </summary>
    public int Count => _terms.Count;

    public WikiGlossaryService(AppLog appLog)
    {
        _appLog = appLog;
    }

    /// <summary> 从指定目录加载全部术语 json（返回加载条数；目录不存在返回 0）。
    /// ⚠ 审查④：**在局部构建**，完成后才把 terms+分桶作为一个快照**原子发布**——
    /// 这样后台 `FindRelevant` 永远取到配套的两者，不会出现"旧桶 + 新表"的 KeyNotFound。 </summary>
    public int Load(string dir)
    {
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        _categoryCounts.Clear();
        _snap = null;      // 先撤下旧快照（期间读方会自行重建，见 FindRelevant 的 ??=）

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _terms = terms;
            return 0;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("黑名单") || name.Contains("bak")) continue; // 黑名单非术语表
                try
                {
                    var raw = File.ReadAllText(file);
                    var map = ReadTerms(raw);
                    if (map.Count == 0) continue;
                    var added = 0;
                    foreach (var (en, zh) in map)
                    {
                        var k = en.Trim();
                        var v = zh.Trim();
                        if (k.Length < 2 || k.Length > MaxTermLen || v.Length == 0 || k == v) continue;
                        if (terms.ContainsKey(k)) continue; // 先到先得（同名术语以先加载的为准）
                        terms[k] = v;
                        added++;
                    }
                    _categoryCounts[name] = added;
                }
                catch (Exception ex)
                {
                    _appLog.Warn($"[wiki] 读取 {name} 失败：{ex.Message}");
                }
            }
            // ── 原子发布：先建好配套快照，再一次性挂上 ──
            _terms = terms;
            _snap = BuildSnapshot(terms);
            _appLog.Info($"[wiki] 已加载术语表 {terms.Count} 条（{_categoryCounts.Count} 个分类）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[wiki] 术语表加载失败：" + ex.Message);
        }
        return _terms.Count;
    }

    /// <summary> 解析一个术语文件：兼容 <c>{terms:{...}}</c> 与裸字典两种格式。 </summary>
    private static Dictionary<string, string> ReadTerms(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement target = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("terms", out var terms) &&
            terms.ValueKind == JsonValueKind.Object)
        {
            target = terms;
        }
        if (target.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in target.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var v = p.Value.GetString() ?? "";
            if (v.Length > 0) result[p.Name] = v;
        }
        return result;
    }

    /// <summary> 查术语（供替换层/AI 参考）。 </summary>
    public bool TryGet(string en, out string zh) => _terms.TryGetValue(en, out zh!);

    /// <summary>
    /// 为一批待翻文案挑出**相关术语**（作为 AI 提示的对照，保证译名一致）。
    /// 性能：不做「6 万条 × 每句」的暴力匹配——先按**首字母**分桶，只比较候选桶内的术语。
    /// </summary>
    public List<(string En, string Zh)> FindRelevant(IEnumerable<string> texts, int max = 120)
    {
        var hits = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_terms.Count == 0) return new();
        // ⚠ 一致性快照（审查④）：一次取到**配套的** terms+buckets，避免后台线程拿旧桶查新表 → KeyNotFound。
        //    快照缺失（尚未构建）时在此构建一次并发布；竞态最坏只是重复构建，无副作用。
        var snap = _snap ??= BuildSnapshot(_terms);
        var terms = snap.Terms;
        var buckets = snap.Buckets;

        foreach (var text in texts)
        {
            if (hits.Count >= max) break;
            var first = char.ToUpperInvariant(text.Length > 0 ? text[0] : ' ');
            // 只查正文里出现过的首字母对应桶
            foreach (var c in text.Where(char.IsLetter).Select(char.ToUpperInvariant).Distinct())
            {
                if (hits.Count >= max) break;
                if (!buckets.TryGetValue(c, out var list)) continue;
                foreach (var en in list)
                {
                    if (hits.Count >= max) break;
                    if (en.Length > text.Length) continue;
                    // 用快照里的 terms（与 buckets 配套）取值，不用可能已被换新的 _terms
                    if (text.Contains(en, StringComparison.OrdinalIgnoreCase) && terms.TryGetValue(en, out var zh))
                        hits[en] = zh;
                }
            }
        }
        return hits.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary> 由给定词表构建「术语表 + 首字母分桶」的一致性快照（分桶内按长度降序，长术语更具体优先）。 </summary>
    private static Snapshot BuildSnapshot(Dictionary<string, string> terms)
    {
        var b = new Dictionary<char, List<string>>(64);
        foreach (var en in terms.Keys)
        {
            if (en.Length < 3) continue;
            var c = char.ToUpperInvariant(en[0]);
            if (!b.TryGetValue(c, out var list)) b[c] = list = new List<string>();
            list.Add(en);
        }
        foreach (var list in b.Values) list.Sort((x, y) => y.Length.CompareTo(x.Length));
        return new Snapshot(terms, b);
    }

    /// <summary> 直接暴露全部术语（替换层合并用）。 </summary>
    public IReadOnlyDictionary<string, string> All => _terms;
}
