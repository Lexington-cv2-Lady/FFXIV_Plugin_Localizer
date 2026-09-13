using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 静态文案提取：直接扫本机已安装插件文件夹（installedPlugins + devPlugins），把插件主 DLL 的
/// #US 字符串堆（程序集内全部字符串字面量，界面文案都在其中）倒出来，按插件写 &lt;插件名&gt;_未翻译.json。
/// 不依赖游戏内窗口和运行时钩子——作为采集的第二来源（P1 架构设计②「DLL 字符串堆直接倒出」）。
/// </summary>
public sealed class PluginScanService
{
    /// <summary> 扫描结果子目录（位于插件数据目录下）。 </summary>
    public const string OutputDirName = "文案扫描";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppLog _appLog;
    private readonly Func<string> _configDir;

    public PluginScanService(AppLog appLog, Func<string> configDir)
    {
        _appLog = appLog;
        _configDir = configDir;
    }

    public sealed record InstalledPlugin(string Name, List<string> DllPaths, List<string> Versions);

    /// <summary> 单插件扫描结果。已内置中文/自带语言文件的插件视为已汉化，跳过并清理旧结果。 </summary>
    public sealed record ScanOutcome(bool SkipBilingualZh, bool SkipLangFiles, int ChineseCount, int CandidateCount, List<string> Strings)
    {
        /// <summary> 中文字符串相对英文候选量的占比（英文候选为 0 时记 100%）。 </summary>
        public int ChineseRatioPercent => CandidateCount > 0 ? (int)Math.Round(ChineseCount * 100.0 / CandidateCount) : 100;

        public bool Skipped => SkipBilingualZh || SkipLangFiles;
    }

    /// <summary>
    /// 「近乎完整中英双语」判定（如 Artisan：1455 中文 / 2569 英文候选 ≈ 57%）：
    /// 中文条数 ≥ BilingualZhFloor 直接判双语；否则需 ≥ BilingualZhMin 条且占比 ≥ 1/BilingualZhRatioDiv（防少量中文误判）。
    /// </summary>
    private const int BilingualZhFloor = 100;
    private const int BilingualZhMin = 20;
    private const int BilingualZhRatioDiv = 4;

    /// <summary> 输出目录：数据目录\文案扫描\。 </summary>
    public string OutputDir => Path.Combine(_configDir(), OutputDirName);

    /// <summary> 枚举本机已安装插件（含 devPlugins；每个插件把所有版本的主 DLL 都列上，扫描时合并去重）。 </summary>
    public List<InstalledPlugin> ListInstalled()
    {
        var list = new List<InstalledPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // launcher 根目录由 pluginConfigs 上一级推算（国服 XIVLauncherCN / 国际服 XIVLauncher 通吃）
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        if (!string.IsNullOrEmpty(launcherDir))
        {
            AddFrom(list, seen, Path.Combine(launcherDir, "installedPlugins"));
            AddFrom(list, seen, Path.Combine(launcherDir, "devPlugins"));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private void AddFrom(List<InstalledPlugin> list, HashSet<string> seen, string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var pluginDir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(pluginDir);
                if (!seen.Add(name)) continue; // installedPlugins 与 devPlugins 同名只取先到
                var dlls = new List<string>();
                var versions = new List<string>();
                foreach (var vDir in Directory.GetDirectories(pluginDir))
                {
                    var main = Path.Combine(vDir, name + ".dll");
                    if (File.Exists(main))
                    {
                        dlls.Add(main);
                        versions.Add(Path.GetFileName(vDir));
                    }
                }
                if (dlls.Count > 0) list.Add(new InstalledPlugin(name, dlls, versions));
            }
        }
        catch (Exception ex)
        {
            _appLog.Error($"[扫描] 枚举 {root} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 扫描单个插件（全部版本合并去重）。已内置中文（DLL 里中文字符串多）或自带语言文件目录的插件
    /// 视为已汉化：不写英文清单，并删除早前生成的旧结果；否则写 &lt;插件名&gt;_未翻译.json。
    /// </summary>
    public ScanOutcome ScanAndSave(InstalledPlugin plugin)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        var zhCount = 0;
        foreach (var dll in plugin.DllPaths)
        {
            try
            {
                var (candidates, zh) = AnalyzeDll(dll);
                foreach (var s in candidates) strings.Add(s);
                zhCount += zh;
            }
            catch (Exception ex)
            {
                _appLog.Warn($"[扫描] {plugin.Name} 读 {Path.GetFileName(dll)} 失败：{ex.Message}");
            }
        }

        var skipZh = zhCount >= BilingualZhFloor
                     || (zhCount >= BilingualZhMin && (long)zhCount * BilingualZhRatioDiv >= strings.Count);
        var skipLang = HasLangFiles(plugin);
        var path = Path.Combine(OutputDir, $"{plugin.Name}_未翻译.json");
        if (skipZh || skipLang)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _appLog.Info($"[扫描] {plugin.Name} 已汉化（{(skipZh ? $"内置中文 {zhCount} 条 / 英文候选 {strings.Count} 条" : "自带语言文件")}），删除旧英文清单");
                }
            }
            catch { /* 删不掉旧文件不影响结论 */ }
            return new ScanOutcome(skipZh, skipLang, zhCount, strings.Count, new List<string>());
        }

        try
        {
            Directory.CreateDirectory(OutputDir);
            var dict = new Dictionary<string, string>();
            foreach (var s in strings) dict[s] = "";
            File.WriteAllText(path, JsonSerializer.Serialize(dict, Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _appLog.Error($"[扫描] {plugin.Name} 写结果失败：{ex.Message}");
        }
        return new ScanOutcome(false, false, zhCount, strings.Count, new List<string>(strings));
    }

    /// <summary> 版本目录（含其一级子目录，如 Assets\Langs）里是否存在语言文件目录。 </summary>
    private static bool HasLangFiles(InstalledPlugin plugin)
    {
        foreach (var dll in plugin.DllPaths)
        {
            var dir = Path.GetDirectoryName(dll);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Contains("lang", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* 读不了就当没有 */ }
        }
        return false;
    }

    /// <summary> 解析一个 DLL：返回（候选英文文案，中文字符串条数）。 </summary>
    private static (List<string> Candidates, int ChineseCount) AnalyzeDll(string dllPath)
    {
        var candidates = new List<string>();
        var chinese = 0;
        foreach (var raw in DecodeDllUserStrings(dllPath))
        {
            var s = raw.Trim();
            if (s.Length < 2) continue;
            var hasChinese = false;
            foreach (var c in s)
            {
                if (c is >= (char)0x4E00 and <= (char)0x9FFF) { hasChinese = true; break; }
            }
            if (hasChinese)
            {
                chinese++;
                continue;
            }
            if (IsCandidate(s)) candidates.Add(s);
        }
        return (candidates, chinese);
    }

    /// <summary>
    /// 解码一个 DLL 的 #US（用户字符串）堆原始内容：手工解析 CLI 元数据根，
    /// 按 ECMA-335 II.24.2.4 解码（压缩 uint 长度 + UTF-16，末位奇数字节是标志位）。
    /// System.Reflection.Metadata 没有公开的 #US 枚举 API，只能这样读。
    /// </summary>
    private static IEnumerable<string> DecodeDllUserStrings(string dllPath)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var cor = pe.PEHeaders.CorHeader;
        if (cor == null) yield break;
        var md = pe.GetSectionData(cor.MetadataDirectory.RelativeVirtualAddress).GetContent().ToArray();
        var pos = 0;
        if (md.Length < 20 || ReadU32(md, ref pos) != 0x424A5342) yield break; // BSJB
        pos += 6; // Major/Minor 版本
        pos += 2; // Reserved
        var versionLen = (int)ReadU32(md, ref pos);
        pos += versionLen; // 版本字符串（含对齐填充）
        pos += 2; // Flags
        var streams = ReadU16(md, ref pos);
        for (var i = 0; i < streams; i++)
        {
            var offset = (int)ReadU32(md, ref pos);
            var size = (int)ReadU32(md, ref pos);
            var name = ReadASCIIZ(md, ref pos);
            pos += (4 - (pos - name.Start) % 4) % 4; // 名字按 4 字节对齐
            if (name.Value != "#US") continue;
            foreach (var s in DecodeUserStringHeap(md, offset, size)) yield return s;
            break;
        }
    }

    private static uint ReadU32(byte[] b, ref int pos)
    {
        var v = (uint)(b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24));
        pos += 4;
        return v;
    }

    private static ushort ReadU16(byte[] b, ref int pos)
    {
        var v = (ushort)(b[pos] | (b[pos + 1] << 8));
        pos += 2;
        return v;
    }

    /// <summary> 读以 \0 结尾的 ASCII 名字，返回名字和消费后的新位置起点（用于对齐计算）。 </summary>
    private static (string Value, int Start) ReadASCIIZ(byte[] b, ref int pos)
    {
        var start = pos;
        while (pos < b.Length && b[pos] != 0) pos++;
        var s = Encoding.ASCII.GetString(b, start, pos - start);
        pos++; // 跳过 \0
        return (s, start);
    }

    private static IEnumerable<string> DecodeUserStringHeap(byte[] b, int start, int size)
    {
        var p = start;
        var end = start + size;
        if (p < end && b[p] == 0) p++; // 堆首字节固定为 0
        while (p < end)
        {
            // 压缩无符号整数（ECMA-335 II.23.2）：1/2/4 字节
            uint len;
            var b0 = b[p++];
            if ((b0 & 0x80) == 0)
            {
                len = b0;
            }
            else if ((b0 & 0xC0) == 0x80)
            {
                if (p >= end) break;
                len = (uint)(((b0 & 0x3F) << 8) | b[p]);
                p += 1;
            }
            else if ((b0 & 0xE0) == 0xC0)
            {
                if (p + 2 >= end) break;
                len = (uint)(((b0 & 0x1F) << 24) | (b[p] << 16) | (b[p + 1] << 8) | b[p + 2]);
                p += 3;
            }
            else
            {
                break;
            }
            if (len == 0) continue;
            var dataBytes = (int)(len & ~1u); // 奇数长度的末字节是标志位，不是字符
            if (p + dataBytes > end || dataBytes == 0)
            {
                p += dataBytes;
                continue;
            }
            var s = Encoding.Unicode.GetString(b, p, dataBytes).Trim();
            p += dataBytes;
            yield return s;
        }
    }

    /// <summary> 候选口径：纯可打印 ASCII、含字母、长度合适、排除 URL 和十六进制串。
    /// 另排除：ImGui 内部 ID（<c>##</c> 开头，无显示文字）、纯键位名（Ctrl/F1/Tab 等，无需翻译）。 </summary>
    private static bool IsCandidate(string s)
    {
        if (s.Length < 2 || s.Length > 300) return false;
        if (s.StartsWith("##")) return false;        // ImGui 内部 ID（无显示部分）
        if (TextHeuristics.IsKeyName(s)) return false; // 纯键位名（Ctrl / F5 / Tab 等）
        var letter = false;
        var hexish = s.Length >= 8;
        foreach (var c in s)
        {
            if (c < 0x20 && c != '\n' && c != '\t') return false;
            if (c >= 0x80) return false; // 中文/全角字符串不要（本插件目标是英文文案）
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letter = true;
            var isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHexDigit) hexish = false;
        }
        if (!letter || hexish) return false;
        if (s.Contains("://")) return false; // URL
        return true;
    }
}
