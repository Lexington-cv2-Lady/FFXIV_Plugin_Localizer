using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 旧项目词典读取（用于「预翻译」）：从旧项目「FFXIV 模组汉化工具」的词典文件里取现成译文，
/// 直接填入本插件的译文表——**省去这些条目的机翻开销，且译名与人已确认的版本一致**。
///
/// 兼容旧项目两种结构（均为「原文/译文」成对数组）：
///   · <c>terms</c>：通用术语（不分模组）；
///   · <c>mods</c>：按模组分组（<c>mods[模组名][文件名][组名] = [{原文,译文}]</c>）。
/// 本插件按**插件**（而非模组）组织，故预翻译时把两者都当"通用词源"用（按英文精确匹配）。
/// </summary>
public sealed class OldDictionaryService
{
    /// <summary> 可识别的旧项目词典文件名（词典目录下）。 </summary>
    private static readonly string[] KnownFiles = { "我的翻译.json", "个性翻译.json" };

    private readonly AppLog _appLog;

    /// <summary> 英文 → 中文（合并全部来源，后加载的不覆盖先加载的）。 </summary>
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary> 各来源条数（文件名 → 条数）。 </summary>
    public IReadOnlyDictionary<string, int> SourceCounts => _sourceCounts;
    private readonly Dictionary<string, int> _sourceCounts = new(StringComparer.Ordinal);

    public int Count => _entries.Count;
    public bool Loaded => _entries.Count > 0;

    public OldDictionaryService(AppLog appLog)
    {
        _appLog = appLog;
    }

    /// <summary>
    /// 从目录加载旧项目词典（读取 <c>我的翻译.json</c> / <c>个性翻译.json</c>）。返回总条数。
    /// </summary>
    public int Load(string dir)
    {
        _entries.Clear();
        _sourceCounts.Clear();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;

        foreach (var file in KnownFiles)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            try
            {
                var n = LoadFile(path);
                if (n > 0) _sourceCounts[file] = n;
            }
            catch (Exception ex)
            {
                _appLog.Warn($"[预翻译] 读取 {file} 失败：{ex.Message}");
            }
        }
        _appLog.Info($"[预翻译] 已加载旧项目词典：{_entries.Count} 条（{_sourceCounts.Count} 个文件）");
        return _entries.Count;
    }

    private int LoadFile(string path)
    {
        var added = 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        // ① terms（成对数组 或 字典）
        if (doc.RootElement.TryGetProperty("terms", out var terms))
        {
            added += ReadPairs(terms);
        }
        // ② mods（按模组分组，逐层深入到成对数组）
        if (doc.RootElement.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object)
        {
            foreach (var mod in mods.EnumerateObject())
            {
                if (mod.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var file in mod.Value.EnumerateObject())
                {
                    if (file.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var group in file.Value.EnumerateObject())
                    {
                        added += ReadPairs(group.Value);
                    }
                }
            }
        }
        // ③ 兜底：顶层直接是成对数组或字典
        if (added == 0) added += ReadPairs(doc.RootElement);
        return added;
    }

    /// <summary> 读「原文/译文」成对数组（也兼容纯字典与 Original/Translated 英文键）。 </summary>
    private int ReadPairs(JsonElement el)
    {
        var added = 0;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var en = GetStr(item, "原文", "Original");
                var zh = GetStr(item, "译文", "Translated");
                if (Add(en, zh)) added++;
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                if (Add(p.Name, p.Value.GetString())) added++;
            }
        }
        return added;
    }

    private static string? GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private bool Add(string? en, string? zh)
    {
        var k = (en ?? "").Trim();
        var v = (zh ?? "").Trim();
        if (k.Length < 2 || v.Length == 0 || k == v) return false;
        if (TextHeuristics.HasCjk(k)) return false;      // 原文不该含中文（旧数据偶有脏值）
        if (_entries.ContainsKey(k)) return false;        // 先到先得
        _entries[k] = v;
        return true;
    }

    /// <summary> 查词（供预翻译）。 </summary>
    public bool TryGet(string en, out string zh) => _entries.TryGetValue(en, out zh!);

    /// <summary>
    /// **预翻译**：把该插件候选文案里能在旧词典命中的条目直接填成译文。
    /// 返回（命中条数, 命中明细），由调用方写入译文表。
    /// </summary>
    public (int Hit, List<(string En, string Zh)> Pairs) Prefill(IEnumerable<string> candidates)
    {
        var pairs = new List<(string, string)>();
        foreach (var raw in candidates)
        {
            var key = raw.Trim();
            if (key.Length < 2 || _entries.ContainsKey(key) == false) continue;
            var zh = _entries[key];
            // 原文是命令/技术串之类的跳过（与扫描口径一致）
            if (!TextHeuristics.IsTranslatable(key)) continue;
            pairs.Add((key, zh));
        }
        return (pairs.Count, pairs);
    }
}
