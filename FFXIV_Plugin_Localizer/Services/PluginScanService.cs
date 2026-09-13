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
    /// 扫描单个插件（全部版本合并去重），写入 &lt;插件名&gt;_未翻译.json，返回提取到的文案列表。
    /// </summary>
    public List<string> ScanAndSave(InstalledPlugin plugin)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dll in plugin.DllPaths)
        {
            try
            {
                foreach (var s in ExtractStrings(dll)) strings.Add(s);
            }
            catch (Exception ex)
            {
                _appLog.Warn($"[扫描] {plugin.Name} 读 {Path.GetFileName(dll)} 失败：{ex.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(OutputDir);
            var dict = new Dictionary<string, string>();
            foreach (var s in strings) dict[s] = "";
            var path = Path.Combine(OutputDir, $"{plugin.Name}_未翻译.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dict, Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _appLog.Error($"[扫描] {plugin.Name} 写结果失败：{ex.Message}");
        }
        return new List<string>(strings);
    }

    /// <summary>
    /// 提取一个 DLL 的全部字符串字面量：手工解析 CLI 元数据根、定位 #US（用户字符串）堆，
    /// 按 ECMA-335 II.24.2.4 解码（压缩 uint 长度 + UTF-16，末位奇数字节是标志位）。
    /// System.Reflection.Metadata 没有公开的 #US 枚举 API，只能这样读。
    /// </summary>
    private static IEnumerable<string> ExtractStrings(string dllPath)
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
            if (IsCandidate(s)) yield return s;
        }
    }

    /// <summary> 候选口径：纯可打印 ASCII（与运行时采集同口径）、含字母、长度合适、排除 URL 和十六进制串。 </summary>
    private static bool IsCandidate(string s)
    {
        if (s.Length < 2 || s.Length > 300) return false;
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
