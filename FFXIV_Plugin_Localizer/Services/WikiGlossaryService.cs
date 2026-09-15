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

    private readonly AppLog _appLog;

    /// <summary> 英文 → 中文（去重后的全量术语表）。 </summary>
    private Dictionary<string, string> _terms = new(StringComparer.Ordinal);

    /// <summary>
    /// **首字母分桶缓存**（2026-09-15 代码审查 M3 修正）：`FindRelevant` 每次调用都要遍历
    /// **6.2 万条**术语重建分桶并逐桶排序，而机翻每批（10~200 条）就调它一次 →
    /// **每批白付数百 ms**。分桶只依赖 `_terms`，加载后即固定，故缓存起来（带锁，允许后台读）。
    /// </summary>
    private volatile Dictionary<char, List<string>>? _buckets;

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
        _buckets = null;   // 词表重建 → 分桶缓存失效（M3）
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
        // ⚠ 走缓存（M3）：分桶只依赖 _terms，加载后不变；原实现每次调用重建 6.2 万条 + 排序，
        //   而机翻**每批**都会调它 → 每批浪费数百毫秒。首次构建后在 Load 时失效。
        var buckets = _buckets;
        if (buckets == null)
        {
            var b = new Dictionary<char, List<string>>(64);
            foreach (var en in _terms.Keys)
            {
                if (en.Length < 3) continue;
                var c = char.ToUpperInvariant(en[0]);
                if (!b.TryGetValue(c, out var list)) b[c] = list = new List<string>();
                list.Add(en);
            }
            // 长术语优先（更具体）
            foreach (var list in b.Values) list.Sort((a, b2) => b2.Length.CompareTo(a.Length));
            buckets = _buckets = b;   // 发布（volatile 写；竞态最坏只是重复构建一次，无副作用）
        }

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
