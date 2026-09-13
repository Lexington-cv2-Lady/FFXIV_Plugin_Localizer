using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 翻译文件的统一读写：**原文/译文成对的对象数组**格式（抄自旧项目「我的翻译.json」的条目风格）。
/// 结构：<c>{ "entries": [ { "原文": "Retry Count", "译文": "重试次数" }, ... ] }</c>
///
/// 为什么用这个格式：原文是 JSON 的**值**而非**键**——窗口文案常含 <c>* [ ] : ' " ,</c> 等字符，
/// 用「英文原文当键」的字典格式时（AI 往返尤其明显）极易转义失败；成对数组则天然安全。
/// 读取时兼容三种历史形态：①本格式 ②纯字典 <c>{en: zh}</c> ③裸数组 <c>[{原文,译文}]</c>。
/// </summary>
public static class TranslationFile
{
    /// <summary> 单条对照。属性名用中文，与旧项目「我的翻译.json」保持一致。 </summary>
    public sealed class Pair
    {
        public string 原文 { get; set; } = "";
        public string 译文 { get; set; } = "";
    }

    private sealed class Doc
    {
        public List<Pair> entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = JsonFile.Indented;

    /// <summary> 写出为成对数组格式（自动去空白、去空译文、去同文，键去重保序）。 </summary>
    public static void Save(string path, IEnumerable<KeyValuePair<string, string>> table)
    {
        var doc = new Doc();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (en, zh) in table)
        {
            var k = (en ?? "").Trim();
            var v = (zh ?? "").Trim();
            if (k.Length < 1 || v.Length == 0 || k == v) continue;
            if (!seen.Add(k)) continue;
            doc.entries.Add(new Pair { 原文 = k, 译文 = v });
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Options), Encoding.UTF8);
    }

    /// <summary> 读取（兼容成对数组 / 纯字典 / 裸数组三种格式）；文件不存在或损坏返回空表。 </summary>
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        try
        {
            ReadInto(result, File.ReadAllText(path));
        }
        catch
        {
            /* 损坏则返回已解析部分 */
        }
        return result;
    }

    /// <summary> 把一段 JSON 文本解析进字典（供导入包、内置包、迁移等复用）。 </summary>
    public static void ReadInto(Dictionary<string, string> into, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ① 对象：可能是 {entries:[...]} 或 直接 {en: zh} 字典
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                ReadPairArray(into, entries);
                return;
            }
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var k = prop.Name.Trim();
                var v = prop.Value.GetString()?.Trim() ?? "";
                if (k.Length >= 1 && v.Length > 0 && k != v) into[k] = v;
            }
            return;
        }

        // ② 裸数组：[{原文,译文}, ...]
        if (root.ValueKind == JsonValueKind.Array)
        {
            ReadPairArray(into, root);
        }
    }

    private static void ReadPairArray(Dictionary<string, string> into, JsonElement array)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var en = el.TryGetProperty("原文", out var o) ? o.GetString()?.Trim() : null;
            if (en == null && el.TryGetProperty("Original", out var o2)) en = o2.GetString()?.Trim();
            var zh = el.TryGetProperty("译文", out var t) ? t.GetString()?.Trim() : null;
            if (zh == null && el.TryGetProperty("Translated", out var t2)) zh = t2.GetString()?.Trim();
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh) || en == zh) continue;
            into[en!] = zh!;
        }
    }

    /// <summary>
    /// AI 交换专用条目：字段名用无语义歧义的 <c>en</c>/<c>zh</c>。
    /// ⚠ 实测教训：给 AI 用「原文/译文」字段名时，它会把译文填进「原文」字段（中文语义词界不明）；
    /// 换成 en/zh 后行为正确。磁盘文件仍用「原文/译文」（与旧项目一致、人读友好），只在 AI 往返时用这套。
    /// </summary>
    private sealed class AiPair
    {
        public string en { get; set; } = "";
        public string zh { get; set; } = "";
    }

    /// <summary> 序列化成对数组的 JSON 文本供 AI 翻译（en/zh 字段，值里放原文，特殊字符安全）。 </summary>
    public static string ToPairJson(IEnumerable<string> originals)
        => JsonSerializer.Serialize(
            originals.Select(o => new AiPair { en = o, zh = "" }).ToList(), Options);

    /// <summary> 从 AI 返回的成对数组 JSON 里取译文，按**下标**对应回原文。 </summary>
    public static Dictionary<string, string> FromPairJson(string json, List<string> originals)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = StripFences(json);
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return result;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var i = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (i >= originals.Count) break;
                if (el.ValueKind != JsonValueKind.Object) { i++; continue; }
                // 优先取 zh；兼容 AI 可能回 "译文"/"Translated"；再不行看 en 是否被 AI 填成了译文
                var zh = el.TryGetProperty("zh", out var z) ? z.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(zh) && el.TryGetProperty("译文", out var z2)) zh = z2.GetString()?.Trim();
                if (string.IsNullOrEmpty(zh) && el.TryGetProperty("Translated", out var z3)) zh = z3.GetString()?.Trim();
                if (string.IsNullOrEmpty(zh) && el.TryGetProperty("en", out var e2))
                {
                    // AI 偶尔把译文填进 en 字段（原文被覆盖）；若该值≠原英文且含非 ASCII，视为译文
                    var enVal = e2.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(enVal) && enVal != originals[i] && enVal.Any(ch => ch > 0x7F))
                        zh = enVal;
                }
                if (!string.IsNullOrEmpty(zh)) result[originals[i]] = zh!;
                i++;
            }
        }
        catch
        {
            /* 解析失败返回已得部分 */
        }
        return result;
    }

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
        }
        return s.Trim();
    }
}
