using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// **单词黑名单**（`词典目录\单词黑名单.json`）：命中词**一律保持英文**，不参与翻译。
    ///
    /// 搬自旧项目 `DictionaryService` 的"单词黑名单 > 个性翻译 > 我的翻译 > wiki"优先级链首层。
    /// 用途（2026-09-15 用户实际需要）：
    ///   · 插件名/专有缩写（URL、DPS、GCD、Craftimizer…）机翻会乱译或译得别扭，拉黑即保持原文；
    ///   · 某些单词被插件**当标识符用**（改了会影响逻辑/显示），需要"永远不要动它"的硬保证。
    /// ⚠ 与「译文 == 原文」的区别：那是"这一条**这次**不用翻"，这是"这个名字**永远**不要翻"，
    ///   且**优先级最高**——即使词典/机翻给出了译文，也会被黑名单拦下。
    /// </summary>
    private readonly HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// ⚠ 2026-09-18 全面审查⑪（**高**）：`_blacklist` 的**写方是主线程**（`Load()` 的 `Clear`/`UnionWith`，
    ///   由「重载词典」触发），**读方是后台机翻线程**（`NeedsTranslation` → `IsBlacklisted`）。
    ///   `HashSet` 无并发保护时读写属**未定义行为**（可能抛异常、返回错值，极端情况内部桶损坏 → 挂死）。
    ///   触发场景很日常：「翻译进行中点重载词典」。用一把私有锁保护全部访问（黑名单仅几十条，开销可忽略）。
    /// </summary>
    private readonly object _blLock = new();

    /// <summary> 黑名单条数（供界面显示）。 </summary>
    public int BlacklistCount { get { lock (_blLock) return _blacklist.Count; } }

    /// <summary> 该词是否在黑名单里（应保持英文）。 </summary>
    public bool IsBlacklisted(string word)
    {
        var w = (word ?? "").Trim();
        lock (_blLock) return _blacklist.Contains(w);
    }

    /// <summary> 黑名单全部词条的**快照**（拷贝返回，避免调用方在锁外遍历被并发修改的集合）。 </summary>
    public IReadOnlyCollection<string> BlacklistWords
    {
        get { lock (_blLock) return _blacklist.ToArray(); }
    }

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
        lock (_blLock) _blacklist.Clear();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;

        // 0) 单词黑名单**先加载**：后续所有词表都要用它过滤（防止黑名单词被译成中文）
        //    ⚠ 先读盘到局部变量、再进锁（LoadWordList 会读文件，不该持锁做 IO）
        var blWords = LoadWordList(Path.Combine(dir, BlacklistFileName));
        lock (_blLock) _blacklist.UnionWith(blWords);

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
        _appLog.Info($"[预翻译] 已加载本项目词典：{_entries.Count} 条（{_sourceCounts.Count} 个文件），" +
                     $"单词黑名单 {BlacklistCount} 条");
        return _entries.Count;
    }

    /// <summary>
    /// 读取**词单**文件（每行一个词，支持 `#` 注释、逗号分隔、BOM）。
    /// 格式与旧项目 `TextListFile` 一致，便于用户直接从旧项目拷贝该文件过来用。
    /// </summary>
    private static List<string> LoadWordList(string path)
    {
        var words = new List<string>();
        if (!File.Exists(path)) return words;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length >= 1 && text[0] == '\uFEFF') text = text[1..];
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var trimmed = line.TrimStart(' ', '\t');
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                foreach (var rawToken in line.Split(','))
                {
                    var token = rawToken;
                    var hash = token.IndexOf('#');
                    if (hash >= 0) token = token[..hash];
                    token = token.Trim();
                    if (token.Length > 0) words.Add(token);
                }
            }
        }
        catch { /* 读不动就当空表 */ }
        return words;
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
        if (IsBlacklisted(k)) return false;               // ⚠ 黑名单词一律不进词表（永远保持英文）
        if (_entries.ContainsKey(k)) return false;        // 先到先得
        _entries[k] = v;
        return true;
    }

    /// <summary>
    /// **把成对译文合并进「我的翻译.json」**（插件翻译窗口的「写入词典」用）。
    ///
    /// 写入位置：<c>&lt;词典目录&gt;\我的翻译.json</c> 的 <c>terms</c> 数组（本项目的通用词源）。
    /// 规则：
    ///   · **已存在的键不覆盖**（先到先得：词典是用户资产，不应被一次汇总悄悄改掉）；
    ///   · 值为空、键含中文、键值相同、键长 &lt;2 的条目跳过（与读取口径一致）；
    ///   · 同一次调用内去重；
    ///   · **保留原有文件的 _说明 / mods 等字段**（用 JsonNode 局部改，不整个重写用户的文件结构）。
    /// 返回（新增条数, 跳过条数）。
    /// </summary>
    public (int Added, int Skipped) MergeIntoDict(string dir, IEnumerable<(string En, string Zh)> pairs)
    {
        var path = Path.Combine(dir, FileName);
        var added = 0;
        var skipped = 0;
        try
        {
            Directory.CreateDirectory(dir);

            // 读现有文件（不存在则从空对象开始；损坏则备份后重建，避免直接丢用户数据）
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(path))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                }
                catch (Exception ex)
                {
                    var bak = path + $".损坏备份{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Copy(path, bak, overwrite: true);
                    _appLog.Warn($"[预翻译] 我的翻译.json 解析失败（已备份到 {Path.GetFileName(bak)}）：{ex.Message}");
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (root["terms"] is not JsonArray terms)
            {
                terms = new JsonArray();
                root["terms"] = terms;
            }

            // 现有键（含 terms + mods 全部，避免与任何已有译文重复）
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in _entries.Keys) existing.Add(k);
            foreach (var item in terms)
            {
                var k = item?["原文"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(k)) existing.Add(k.Trim());
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rawEn, rawZh) in pairs)
            {
                var k = (rawEn ?? "").Trim();
                var v = (rawZh ?? "").Trim();
                if (k.Length < 2 || v.Length == 0 || k == v) { skipped++; continue; }
                if (TextHeuristics.HasCjk(k)) { skipped++; continue; }
                if (!seen.Add(k)) { skipped++; continue; }
                if (existing.Contains(k)) { skipped++; continue; }   // 已有 → 不覆盖
                terms.Add(new JsonObject { ["原文"] = k, ["译文"] = v });
                existing.Add(k);
                added++;
            }

            if (added > 0)
            {
                // ⚠ 2026-09-18 全面审查：改为**原子落盘**。「我的翻译.json」是用户词典（可能上万条人工成果），
                //   直写若在崩溃/断电时中断会留下半截 JSON → 读回时解析失败 → 词典等于丢失。
                TranslationFile.WriteAtomic(path,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    new UTF8Encoding(true));   // 带 BOM：中文 Windows 的记事本才不会把 UTF-8 误判成 GBK
                _appLog.Info($"[预翻译] 已写入词典：新增 {added} 条（跳过 {skipped} 条）→ {path}");
            }
        }
        catch (Exception ex)
        {
            _appLog.Error("[预翻译] 写入词典失败：" + ex.Message);
            throw;   // 由调用方转成界面提示
        }
        return (added, skipped);
    }

    /// <summary> 词典主文件名（本项目的通用词源）。 </summary>
    public const string FileName = "我的翻译.json";

    /// <summary> 单词黑名单文件名（命中词保持英文）。 </summary>
    public const string BlacklistFileName = "单词黑名单.json";

    /// <summary> 确保单词黑名单文件存在（缺失时创建带说明的模板，格式与旧项目一致），返回完整路径。 </summary>
    public static string EnsureBlacklistFile(string dir)
    {
        var path = Path.Combine(dir, BlacklistFileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "# 单词黑名单.json —— 使用方法\n" +
                "# 每行写一个英文单词/词组；同一行也可用英文逗号分隔多个词，例如：rue,bibo\n" +
                "# 以 # 开头的行（或行内 # 及之后的内容）是注释，不会被当作黑名单词\n" +
                "# 命中黑名单的词在翻译时保留英文原文，不进行翻译、不替换\n" +
                "# 编辑保存后点「重载词典」即可生效\n", Encoding.UTF8);
        }
        return path;
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
