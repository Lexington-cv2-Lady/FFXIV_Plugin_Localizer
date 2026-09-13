using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 替换层（精确匹配）：对照表 英文 → 中文，钩子在绘制转发前查表换字符串指针。
/// 热路径（每帧每条文字都调）用 FNV 哈希集合预筛，零分配；只有哈希命中才解码字符串做精确比对。
/// 中文指针按条目缓存（HGlobal、NUL 结尾），重载表时统一释放重建。
/// </summary>
public sealed unsafe class ReplacementService
{
    /// <summary> 安装器翻译对照表文件名（英文 → 中文）。 </summary>
    public const string TableFileName = "安装器对照表.json";
    /// <summary> 安装器已装插件还缺翻译的文案清单（机翻 API 的输入）。 </summary>
    public const string MissingFileName = "安装器未翻译.json";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppLog _appLog;
    private readonly Func<string> _configDir;
    private readonly object _lock = new();

    private Dictionary<string, byte[]> _table = new();   // 英文 → 中文 UTF-8
    private HashSet<ulong> _hashes = new();              // 英文键 FNV（热路径预筛）
    private readonly Dictionary<string, nint> _ptrs = new(); // 英文 → 中文指针

    /// <summary> 替换开关（只影响绘制替换；表的管理不受影响）。 </summary>
    public bool Enabled { get; set; }

    public int Count { get { lock (_lock) return _table.Count; } }

    public ReplacementService(AppLog appLog, Func<string> configDir)
    {
        _appLog = appLog;
        _configDir = configDir;
        Load();
    }

    /// <summary> 对照表文件路径。 </summary>
    public string TablePath => Path.Combine(_configDir(), TableFileName);

    /// <summary> 启动载入对照表（文件不存在则空表）。 </summary>
    public void Load()
    {
        try
        {
            var path = TablePath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data == null) return;
            SetTable(data);
            _appLog.Info($"[替换] 已载入对照表：{_table.Count} 条（{TableFileName}）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 对照表载入失败：" + ex.Message);
        }
    }

    /// <summary> 保存对照表。 </summary>
    public void Save()
    {
        Dictionary<string, string> copy;
        lock (_lock)
        {
            copy = _table.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.Ordinal);
        }
        try
        {
            Directory.CreateDirectory(_configDir());
            File.WriteAllText(TablePath, JsonSerializer.Serialize(copy, Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 对照表保存失败：" + ex.Message);
        }
    }

    /// <summary> 重建表（含哈希集合与中文指针；旧指针统一释放）。 </summary>
    public void SetTable(Dictionary<string, string> table)
    {
        lock (_lock)
        {
            foreach (var ptr in _ptrs.Values) Marshal.FreeCoTaskMem(ptr);
            _ptrs.Clear();
            _table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _hashes = new HashSet<ulong>();
            foreach (var (en, zh) in table)
            {
                var key = en.Trim();
                var val = (zh ?? "").Trim();
                if (key.Length < 2 || val.Length == 0 || key == val) continue;
                if (_table.ContainsKey(key)) continue;
                var bytes = Encoding.UTF8.GetBytes(val);
                var ptr = Marshal.StringToCoTaskMemUTF8(val);
                _table[key] = bytes;
                _ptrs[key] = ptr;
                _hashes.Add(FnvUtf8(key));
            }
        }
    }

    /// <summary>
    /// 热路径查表：命中返回 NUL 结尾的中文指针（转发时 text_end 传 0），未命中返回 0。
    /// </summary>
    public nint TryReplace(byte* p, int n)
    {
        if (!Enabled || _table.Count == 0 || n < 2 || n > 1024) return 0;
        ulong h;
        unsafe
        {
            h = FnvBytes(p, n);
        }
        lock (_lock)
        {
            if (!_hashes.Contains(h)) return 0;
            var s = Encoding.UTF8.GetString(p, n);
            return _ptrs.TryGetValue(s, out var ptr) ? ptr : 0;
        }
    }

    /// <summary>
    /// 导入 FuckDalamudCN 的机翻表（installedPlugins\FuckDalamudCN\&lt;版本&gt;\Assets\translations.json，
    /// 结构：{ 插件ID: { Punchline/Description: { Original, Translated } } }）。返回新增条数；找不到文件返回 -1。
    /// </summary>
    public int ImportFdcn()
    {
        var path = FindFdcnFile();
        if (path == null)
        {
            _appLog.Warn("[替换] 未找到 FuckDalamudCN 的 translations.json（未安装或版本目录变化）");
            return -1;
        }
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(path));
            if (raw == null) return -1;
            lock (_lock)
            {
                var added = 0;
                foreach (var fields in raw.Values)
                {
                    foreach (var f in fields.Values)
                    {
                        var en = f.TryGetProperty("Original", out var o) ? o.GetString()?.Trim() : null;
                        var zh = f.TryGetProperty("Translated", out var t) ? t.GetString()?.Trim() : null;
                        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh) || en == zh) continue;
                        if (_table.ContainsKey(en!)) continue;
                        var bytes = Encoding.UTF8.GetBytes(zh!);
                        var ptr = Marshal.StringToCoTaskMemUTF8(zh!);
                        _table[en!] = bytes;
                        _ptrs[en!] = ptr;
                        _hashes.Add(FnvUtf8(en!));
                        added++;
                    }
                }
                _appLog.Info($"[替换] 已从 FuckDalamudCN 机翻表导入 {added} 条（来源 {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}）");
                return added;
            }
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 机翻表导入失败：" + ex.Message);
            return -1;
        }
    }

    private string? FindFdcnFile()
    {
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        if (string.IsNullOrEmpty(launcherDir)) return null;
        var root = Path.Combine(launcherDir, "installedPlugins", "FuckDalamudCN");
        if (!Directory.Exists(root)) return null;
        try
        {
            var file = Directory.EnumerateFiles(root, "translations.json", SearchOption.AllDirectories)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 扫描已安装插件的清单（Name/Description/Punchline），列出对照表还没有翻译的条目，
    /// 写入 安装器未翻译.json（机翻 API 的输入）。返回缺失条数。
    /// </summary>
    public int ScanInstallerMissing()
    {
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var root = Path.Combine(launcherDir!, "installedPlugins");
            if (Directory.Exists(root))
            {
                foreach (var manifestPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    // 清单形如 installedPlugins\<ID>\<版本>\<ID>.json；跳过明显不是清单的文件
                    var name = Path.GetFileNameWithoutExtension(manifestPath);
                    var dir = Path.GetFileName(Path.GetDirectoryName(manifestPath));
                    if (name != dir) continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(manifestPath));
                        if (m == null) continue;
                        foreach (var field in new[] { "Description", "Punchline" })
                        {
                            if (!m.TryGetValue(field, out var el) || el.ValueKind != JsonValueKind.String) continue;
                            var text = el.GetString()?.Trim();
                            if (string.IsNullOrEmpty(text) || text!.Length < 2) continue;
                            bool hasLetter = false;
                            foreach (var c in text)
                            {
                                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { hasLetter = true; break; }
                            }
                            if (!hasLetter) continue;
                            lock (_lock)
                            {
                                if (!_table.ContainsKey(text!)) missing[$"{name}｜{field}"] = text!;
                            }
                        }
                    }
                    catch { /* 单个清单坏了跳过 */ }
                }
            }
            Directory.CreateDirectory(_configDir());
            var outPath = Path.Combine(_configDir(), MissingFileName);
            File.WriteAllText(outPath,
                JsonSerializer.Serialize(missing, Indented), Encoding.UTF8);
            _appLog.Info($"[替换] 安装器缺失翻译 {missing.Count} 条，已写入 {MissingFileName}");
            return missing.Count;
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 安装器缺失扫描失败：" + ex.Message);
            return -1;
        }
    }

    private static ulong FnvUtf8(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    private static ulong FnvBytes(byte* p, int n)
    {
        ulong h = 14695981039346656037UL;
        for (var i = 0; i < n; i++)
        {
            h ^= p[i];
            h *= 1099511628211UL;
        }
        return h;
    }
}
