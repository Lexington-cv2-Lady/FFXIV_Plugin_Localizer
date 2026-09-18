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

    /// <summary> 写出为成对数组格式（自动去空白、去空译文、键去重保序）。 </summary>
    public static void Save(string path, IEnumerable<KeyValuePair<string, string>> table)
    {
        var doc = new Doc();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (en, zh) in table)
        {
            var k = (en ?? "").Trim();
            var v = (zh ?? "").Trim();
            // ⚠ **允许「译文 == 原文」**（表示"保持原样/无需翻译"，如 URL / DPS / HP 等专有名词与缩写）：
            //    此类条目若被丢弃，就会永远显示"待翻译"、每次机翻都重送一遍（AI 仍返回原文）→ 死循环。
            //    见 MergeWindowEntries 处同款说明。空译文仍跳过（那是真失败，要留着重试）。
            if (k.Length < 1 || v.Length == 0) continue;
            if (!seen.Add(k)) continue;
            doc.entries.Add(new Pair { 原文 = k, 译文 = v });
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteAtomic(path, JsonSerializer.Serialize(doc, Options));
    }

    /// <summary>
    /// **原子落盘**：先写同目录临时文件，再 `File.Move` 覆盖（同卷 rename 是原子操作）。
    /// ⚠ 2026-09-18 全面审查（**中高**）：原直接 `File.WriteAllText` 覆盖——
    ///   这类文件是用户的**心血**（「我的翻译.json」与每个插件的窗口表，可能上万条人工/机翻结果）。
    ///   若正好在写入中途游戏崩溃/断电，文件会被截断成**半截 JSON**；
    ///   而读取端 `Load` 解析失败时**返回空表**，等于用户全部译文瞬间归零（本项目已发生过一次真实数据事故）。
    ///   改为原子替换后，任何时刻磁盘上的文件要么是**完整的旧版本**、要么是**完整的新版本**，不存在半截态。
    /// 本方法**公开**供其它服务复用（项目里所有「承载用户心血」的 json 都应走它）。
    /// ⚠ 临时文件后缀是 `.tmp`（**不是 `.json`**）——这样它不会被 `EnumerateFiles(dir, "*.json")`
    ///   之类的表目录扫描误当成一个插件表读进去。
    /// </summary>
    /// <param name="encoding">默认 UTF-8 **无 BOM**；旧词典文件要求带 BOM（中文记事本判编码用），传 <c>new UTF8Encoding(true)</c>。</param>
    public static void WriteAtomic(string path, string content, Encoding? encoding = null)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, encoding ?? Encoding.UTF8);
            // 同卷 `File.Move(overwrite)` 走 MoveFileEx / rename，对读者来说是**原子替换**
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // 若 Move 失败（如被杀软短暂占用），清理临时文件，避免目录里堆 .tmp 垃圾
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary> 读取（兼容成对数组 / 纯字典 / 裸数组三种格式）；文件不存在返回空表。 </summary>
    /// <param name="onError">
    /// 解析失败时回调（传入异常信息）。⚠ 2026-09-18 全面审查：原来**静默吞掉**解析异常并返回空表，
    /// 用户看到的现象是「译文全没了」，却查不到任何线索。译文文件是用户心血，损坏必须留下日志。
    /// </param>
    public static Dictionary<string, string> Load(string path, Action<string>? onError = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        try
        {
            ReadInto(result, File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // 唯一能保留的线索就是异常本身；返回已解析部分（JSON 数组可部分解析成功）
            onError?.Invoke($"译文文件解析失败（{Path.GetFileName(path)}）：{ex.Message}");
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
                if (k.Length >= 1 && v.Length > 0) into[k] = v;   // 允许「译文==原文」（保持原样），见 Save 说明
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
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh)) continue;
            into[en!] = zh!;   // 允许「译文==原文」（保持原样），见 Save 说明
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
