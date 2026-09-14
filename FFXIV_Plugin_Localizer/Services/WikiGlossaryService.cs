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
    /// <summary> 默认目录名（相对于插件数据目录）。 </summary>
    public const string DirName = "wiki_术语对照";
    /// <summary> 单条术语的最大长度（过长的不是术语，是描述）。 </summary>
    private const int MaxTermLen = 120;

    private readonly AppLog _appLog;

    /// <summary> 英文 → 中文（去重后的全量术语表）。 </summary>
    private Dictionary<string, string> _terms = new(StringComparer.Ordinal);

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

    /// <summary> 从指定目录加载全部术语 json（返回加载条数；目录不存在返回 0）。 </summary>
    public int Load(string dir)
    {
        _terms = new Dictionary<string, string>(StringComparer.Ordinal);
        _categoryCounts.Clear();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
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
                        if (_terms.ContainsKey(k)) continue; // 先到先得（同名术语以先加载的为准）
                        _terms[k] = v;
                        added++;
                    }
                    _categoryCounts[name] = added;
                }
                catch (Exception ex)
                {
                    _appLog.Warn($"[wiki] 读取 {name} 失败：{ex.Message}");
                }
            }
            _appLog.Info($"[wiki] 已加载术语表 {_terms.Count} 条（{_categoryCounts.Count} 个分类）");
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
        // 按首字母分桶（英文术语首字母），只为缩小比较范围
        var buckets = new Dictionary<char, List<string>>(64);
        foreach (var en in _terms.Keys)
        {
            if (en.Length < 3) continue;
            var c = char.ToUpperInvariant(en[0]);
            if (!buckets.TryGetValue(c, out var list)) buckets[c] = list = new List<string>();
            list.Add(en);
        }
        // 长术语优先（更具体）
        foreach (var list in buckets.Values) list.Sort((a, b) => b.Length.CompareTo(a.Length));

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
                    if (text.Contains(en, StringComparison.OrdinalIgnoreCase)) hits[en] = _terms[en];
                }
            }
        }
        return hits.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary> 直接暴露全部术语（替换层合并用）。 </summary>
    public IReadOnlyDictionary<string, string> All => _terms;
}
