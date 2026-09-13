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
/// 表分两路合并成一张查找表：①安装器介绍表（安装器对照表.json）②窗口文字表（窗口翻译\&lt;插件名&gt;.json，每插件一份）。
/// 热路径（每帧每条文字都调）用 FNV 哈希集合预筛，零分配；只有哈希命中才解码字符串做精确比对。
/// 中文指针按条目缓存（CoTaskMem、NUL 结尾），表重建时统一释放。
/// </summary>
public sealed unsafe class ReplacementService
{
    /// <summary> 安装器翻译对照表文件名（英文 → 中文）。 </summary>
    public const string TableFileName = "安装器对照表.json";
    /// <summary> 安装器已装插件还缺翻译的文案清单（机翻 API 的输入）。 </summary>
    public const string MissingFileName = "安装器未翻译.json";
    /// <summary> 窗口文字表目录（每插件一份 &lt;插件名&gt;.json）。 </summary>
    public const string WindowTableDirName = "窗口翻译";
    /// <summary> 窗口文字的未翻译候选来源目录（文案扫描输出）。 </summary>
    public const string ScanDirName = "文案扫描";
    /// <summary> 随插件分发的内置翻译包（构建时从 FDCN 表生成，未装 FDCN 的用户也有底子）。 </summary>
    public const string BundleFileName = "内置翻译包.json";
    /// <summary> FDCN 表指纹记录文件（变了才自动重导）。 </summary>
    private const string FdcnStampFileName = "FDCN同步状态.json";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppLog _appLog;
    private readonly Func<string> _configDir;
    private readonly object _lock = new();

    private Dictionary<string, string> _installerSource = new(StringComparer.Ordinal);              // 安装器表
    private readonly Dictionary<string, Dictionary<string, string>> _windowSources = new(StringComparer.Ordinal); // 插件名 → 窗口表
    private Dictionary<string, byte[]> _table = new(StringComparer.Ordinal);   // 合并查找：英文 → 中文 UTF-8
    private HashSet<ulong> _hashes = new();                                    // 英文键 FNV（热路径预筛）
    private readonly Dictionary<string, nint> _ptrs = new(StringComparer.Ordinal); // 英文 → 中文指针
    private readonly List<nint> _graveyard = new();                            // 已弃用的中文指针（仅 Dispose 释放，避免渲染线程 use-after-free）

    /// <summary> 替换开关（只影响绘制替换；表的管理不受影响）。 </summary>
    public bool Enabled { get; set; }

    /// <summary> 导出包的文件扩展名（JSON 内容，含安装器表 + 窗口表）。 </summary>
    public const string PackExtension = "json";

    /// <summary> 导出的翻译包结构：安装器表 + 每插件窗口表。 </summary>
    public sealed class TranslationPack
    {
        public string Format { get; set; } = "FFXIVPluginLocalizer.Translations";
        public int Version { get; set; } = 1;
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        /// <summary> 安装器介绍对照：英文 → 中文。 </summary>
        public Dictionary<string, string> Installer { get; set; } = new(StringComparer.Ordinal);
        /// <summary> 窗口文字对照：插件名 →（英文 → 中文）。 </summary>
        public Dictionary<string, Dictionary<string, string>> Windows { get; set; } = new(StringComparer.Ordinal);
    }

    public int Count { get { lock (_lock) return _table.Count; } }

    public ReplacementService(AppLog appLog, Func<string> configDir)
    {
        _appLog = appLog;
        _configDir = configDir;
        Load();
        LoadWindowTables();
    }

    /// <summary> 对照表文件路径。 </summary>
    public string TablePath => Path.Combine(_configDir(), TableFileName);

    /// <summary> 窗口文字表目录。 </summary>
    public string WindowTableDir => Path.Combine(_configDir(), WindowTableDirName);

    /// <summary> 启动载入安装器对照表。 </summary>
    public void Load()
    {
        try
        {
            var path = TablePath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data == null) return;
            _installerSource = Normalize(data);
            RebuildMerged();
            _appLog.Info($"[替换] 已载入安装器对照表：{_installerSource.Count} 条（{TableFileName}）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 对照表载入失败：" + ex.Message);
        }
    }

    /// <summary> 保存安装器对照表。 </summary>
    public void Save()
    {
        Dictionary<string, string> copy;
        lock (_lock)
        {
            copy = new Dictionary<string, string>(_installerSource, StringComparer.Ordinal);
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

    /// <summary> 载入窗口翻译目录下的全部插件表（启动时；之后由编辑/机翻增量维护）。 </summary>
    public void LoadWindowTables()
    {
        try
        {
            var dir = WindowTableDir;
            if (!Directory.Exists(dir)) return;
            lock (_lock)
            {
                _windowSources.Clear();
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                        if (data == null) continue;
                        _windowSources[Path.GetFileNameWithoutExtension(file)] = Normalize(data);
                    }
                    catch { /* 单个文件坏了跳过 */ }
                }
            }
            RebuildMerged();
            _appLog.Info($"[替换] 已载入窗口文字表：{_windowSources.Count} 个插件");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 窗口文字表载入失败：" + ex.Message);
        }
    }

    /// <summary> 重建合并查找表（安装器表优先，窗口表不覆盖同键）。
    /// ⚠ 旧中文指针**不在运行时释放**：渲染线程可能正拿着某个指针绘制（后台机翻线程重建表时会并发），
    /// 立即 Free 会造成 use-after-free 崩溃。统一塞进 _graveyard，只在 Dispose 释放；重建不频繁，内存代价可忽略。 </summary>
    private void RebuildMerged()
    {
        lock (_lock)
        {
            foreach (var ptr in _ptrs.Values) _graveyard.Add(ptr);
            _ptrs.Clear();
            _table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _hashes = new HashSet<ulong>();
            foreach (var (en, zh) in _installerSource) AddMerged(en, zh);
            foreach (var table in _windowSources.Values)
            {
                foreach (var (en, zh) in table)
                {
                    if (_table.ContainsKey(en)) continue; // 安装器表优先
                    AddMerged(en, zh);
                }
            }
        }
    }

    private void AddMerged(string en, string zh)
    {
        if (_table.ContainsKey(en)) return;
        _table[en] = Encoding.UTF8.GetBytes(zh);
        _ptrs[en] = Marshal.StringToCoTaskMemUTF8(zh);
        _hashes.Add(FnvUtf8(en));
    }

    /// <summary> 清洗：去空白、去空值、去同文。 </summary>
    private static Dictionary<string, string> Normalize(Dictionary<string, string> data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, zh) in data)
        {
            var k = en.Trim();
            var v = (zh ?? "").Trim();
            if (k.Length < 2 || v.Length == 0 || k == v) continue;
            result[k] = v;
        }
        return result;
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
            _appLog.Info("[替换] 未找到 FuckDalamudCN 的 translations.json（未安装），使用内置翻译包");
            return -1;
        }
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(path));
            if (raw == null) return -1;
            var added = 0;
            lock (_lock)
            {
                foreach (var fields in raw.Values)
                {
                    foreach (var f in fields.Values)
                    {
                        var en = f.TryGetProperty("Original", out var o) ? o.GetString()?.Trim() : null;
                        var zh = f.TryGetProperty("Translated", out var t) ? t.GetString()?.Trim() : null;
                        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh) || en == zh) continue;
                        if (_installerSource.ContainsKey(en!)) continue;
                        _installerSource[en!] = zh!;
                        added++;
                    }
                }
            }
            if (added > 0) RebuildMerged();
            _appLog.Info($"[替换] 已从 FuckDalamudCN 机翻表导入 {added} 条（当前安装器表 {_installerSource.Count} 条）");
            return added;
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 机翻表导入失败：" + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// 启动时的 FDCN 同步策略：文件指纹（大小+mtime）与上次一致则跳过（省一次全量导入），
    /// 变了（FDCN 更新）则自动重导增量；未装 FDCN 则载入随插件分发的内置翻译包打底。
    /// </summary>
    public void SyncFdcnOnStartup()
    {
        try
        {
            var path = FindFdcnFile();
            if (path == null)
            {
                LoadBundle(); // 没装 FDCN：内置翻译包打底
                return;
            }
            var stamp = $"{new FileInfo(path).Length}:{new FileInfo(path).LastWriteTimeUtc.Ticks}";
            var stampPath = Path.Combine(_configDir(), FdcnStampFileName);
            var lastStamp = "";
            try
            {
                if (File.Exists(stampPath))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(stampPath));
                    lastStamp = d != null && d.TryGetValue("stamp", out var s) ? s : "";
                }
            }
            catch { /* 状态文件坏了当作没同步过 */ }

            if (stamp == lastStamp)
            {
                _appLog.Info("[替换] FDCN 表未变化，跳过同步");
                return;
            }
            var added = ImportFdcn();
            if (added > 0) Save();
            File.WriteAllText(stampPath,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["stamp"] = stamp }, Indented),
                Encoding.UTF8);
            if (added > 0) _appLog.Info($"[替换] FDCN 表有更新：自动导入 {added} 条新翻译");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] FDCN 启动同步失败：" + ex.Message);
        }
    }

    /// <summary> 载入随插件分发的内置翻译包（devPlugins\&lt;ID&gt;\内置翻译包.json，构建时生成）。 </summary>
    private void LoadBundle()
    {
        try
        {
            string? bundle = null;
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir())); // launcher 根
            // 安装版：插件目录在 launcher\installedPlugins\<ID>\<版本>\；dev 版：launcher\devPlugins\<ID>\
            foreach (var root in new[]
                     {
                         Path.Combine(dir!, "devPlugins", "FFXIV_Plugin_Localizer"),
                         Path.Combine(dir!, "installedPlugins", "FFXIV_Plugin_Localizer"),
                     })
            {
                if (!Directory.Exists(root)) continue;
                var candidates = Directory.EnumerateFiles(root, BundleFileName, SearchOption.AllDirectories);
                bundle = candidates.OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
                if (bundle != null) break;
            }
            if (bundle == null)
            {
                _appLog.Info("[替换] 无内置翻译包（首次构建生成后随插件分发）");
                return;
            }
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bundle));
            if (data == null) return;
            var added = 0;
            lock (_lock)
            {
                foreach (var (en, zh) in Normalize(data!))
                {
                    if (_installerSource.ContainsKey(en)) continue;
                    _installerSource[en] = zh;
                    added++;
                }
            }
            if (added > 0)
            {
                RebuildMerged();
                Save();
            }
            _appLog.Info($"[替换] 已载入内置翻译包：{added} 条（{_installerSource.Count} 条总表）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 内置翻译包载入失败：" + ex.Message);
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
            return Directory.EnumerateFiles(root, "translations.json", SearchOption.AllDirectories)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 扫描已安装插件的清单（Name/Description/Punchline），收集对照表还没有翻译的条目（机翻 API 的输入）。
    /// </summary>
    public SortedDictionary<string, string> CollectMissing()
    {
        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        var root = Path.Combine(launcherDir!, "installedPlugins");
        if (!Directory.Exists(root)) return missing;
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
        return missing;
    }

    /// <summary>
    /// 扫描已安装插件的清单，列出对照表还没有翻译的条目，写入 安装器未翻译.json。返回缺失条数。
    /// </summary>
    public int ScanInstallerMissing()
    {
        try
        {
            var missing = CollectMissing();
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

    /// <summary> 把翻译结果并入安装器对照表（内存 + 中文指针 + 哈希），并落盘。返回实际新增条数。 </summary>
    public int MergeTranslations(Dictionary<string, string> translations)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var (en, zh) in translations)
            {
                var key = en.Trim();
                var val = (zh ?? "").Trim();
                if (key.Length < 2 || val.Length == 0 || key == val) continue;
                if (_installerSource.ContainsKey(key)) continue;
                _installerSource[key] = val;
                added++;
            }
        }
        if (added > 0)
        {
            RebuildMerged();
            Save();
        }
        return added;
    }

    // ═══════════════════════ 窗口文字表（每插件一份） ═══════════════════════

    /// <summary> 窗口表清单：插件名 →（候选总数, 已翻译数）。候选来自文案扫描输出，已翻译来自窗口表。 </summary>
    public List<(string Plugin, int Total, int Translated)> GetWindowPlugins()
    {
        var result = new List<(string Plugin, int Total, int Translated)>();
        var scanDir = Path.Combine(_configDir(), ScanDirName);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(scanDir))
            {
                foreach (var f in Directory.EnumerateFiles(scanDir, "*_未翻译.json"))
                    names.Add(Path.GetFileNameWithoutExtension(f)[..^"_未翻译".Length]);
            }
        }
        catch { /* 枚举失败忽略 */ }
        lock (_lock)
        {
            foreach (var n in _windowSources.Keys) names.Add(n);
        }
        foreach (var name in names)
        {
            var candidates = ReadJsonDict(Path.Combine(scanDir, $"{name}_未翻译.json"));
            int translated;
            lock (_lock)
            {
                translated = _windowSources.TryGetValue(name, out var t) ? t.Count : 0;
            }
            var total = Math.Max(candidates.Count, translated);
            result.Add((name, total, translated));
        }
        return result;
    }

    /// <summary> 某插件的窗口条目：返回（已翻译列表，未翻译英文列表）。每帧 UI 勿直接调，先缓存。 </summary>
    public (List<(string En, string Zh)> Translated, List<string> Untranslated) GetWindowEntries(string plugin)
    {
        var candidates = ReadJsonDict(Path.Combine(_configDir(), ScanDirName, $"{plugin}_未翻译.json"));
        List<(string, string)> translated;
        List<string> untranslated;
        lock (_lock)
        {
            var win = _windowSources.TryGetValue(plugin, out var t) ? t : new Dictionary<string, string>();
            translated = win.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
            untranslated = candidates.Keys.Where(k => !win.ContainsKey(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        return (translated, untranslated);
    }

    /// <summary> 把机翻/手动结果并入某插件的窗口表（写文件 + 重建合并表）。返回实际新增条数。 </summary>
    public int MergeWindowEntries(string plugin, Dictionary<string, string> translations)
    {
        var added = 0;
        lock (_lock)
        {
            if (!_windowSources.TryGetValue(plugin, out var table))
                _windowSources[plugin] = table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (en, zh) in translations)
            {
                var key = en.Trim();
                var val = (zh ?? "").Trim();
                if (key.Length < 2 || val.Length == 0 || key == val) continue;
                table[key] = val;
                added++;
            }
            if (added > 0) WriteWindowFile(plugin, table);
        }
        if (added > 0) RebuildMerged();
        return added;
    }

    /// <summary> 设置/删除（中文空 = 删）某插件窗口表的单条，写文件 + 重建。 </summary>
    public void SetWindowEntry(string plugin, string en, string zh)
    {
        en = en.Trim();
        zh = (zh ?? "").Trim();
        if (en.Length < 2) return;
        lock (_lock)
        {
            if (!_windowSources.TryGetValue(plugin, out var table))
                _windowSources[plugin] = table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (zh.Length == 0)
                table.Remove(en);
            else
                table[en] = zh;
            WriteWindowFile(plugin, table);
        }
        RebuildMerged();
    }

    /// <summary> 删除某插件窗口表的单条。 </summary>
    public void RemoveWindowEntry(string plugin, string en)
    {
        lock (_lock)
        {
            if (_windowSources.TryGetValue(plugin, out var table) && table.Remove(en))
                WriteWindowFile(plugin, table);
        }
        RebuildMerged();
    }

    private void WriteWindowFile(string plugin, Dictionary<string, string> table)
    {
        try
        {
            Directory.CreateDirectory(WindowTableDir);
            var copy = new Dictionary<string, string>(table, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(WindowTableDir, $"{plugin}.json"),
                JsonSerializer.Serialize(copy, Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _appLog.Error($"[替换] 窗口表保存失败（{plugin}）：" + ex.Message);
        }
    }

    private static Dictionary<string, string> ReadJsonDict(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    // ═══════════════════════ 手动编辑（安装器表） ═══════════════════════

    /// <summary> 安装器对照表条目（英文排序副本，手动翻译编辑器用）。 </summary>
    public List<(string En, string Zh)> GetEntries()
    {
        lock (_lock)
        {
            return _installerSource
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary> 设置单条安装器对照（中文为空 = 删除该条）。用于手动翻译编辑器。 </summary>
    public void SetTranslation(string en, string zh)
    {
        en = en.Trim();
        zh = (zh ?? "").Trim();
        if (en.Length < 2) return;
        lock (_lock)
        {
            if (zh.Length == 0)
            {
                _installerSource.Remove(en);
            }
            else
            {
                _installerSource[en] = zh;
            }
        }
        RebuildMerged();
        Save();
    }

    /// <summary> 删除单条安装器对照。 </summary>
    public void RemoveTranslation(string en)
    {
        lock (_lock)
        {
            _installerSource.Remove(en);
        }
        RebuildMerged();
        Save();
    }

    // ═══════════════════════ 导入 / 导出（用户间分享翻译成果） ═══════════════════════

    /// <summary>
    /// 导出翻译包（安装器表 + 全部窗口表）到指定文件。返回（安装器条数, 窗口表条数）。
    /// </summary>
    public (int Installer, int Window) ExportPack(string path)
    {
        TranslationPack pack;
        lock (_lock)
        {
            pack = new TranslationPack
            {
                Installer = new Dictionary<string, string>(_installerSource, StringComparer.Ordinal),
                Windows = _windowSources.ToDictionary(
                    kv => kv.Key,
                    kv => new Dictionary<string, string>(kv.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal),
            };
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(pack, Indented), Encoding.UTF8);
        var winTotal = pack.Windows.Values.Sum(w => w.Count);
        _appLog.Info($"[替换] 已导出翻译包：安装器 {pack.Installer.Count} 条 + 窗口 {winTotal} 条（{pack.Windows.Count} 个插件）→ {path}");
        return (pack.Installer.Count, winTotal);
    }

    /// <summary>
    /// 导入翻译包并与现有表合并（**不覆盖已有条目**，先到先得，本机编辑优先）。返回（新增安装器条数, 新增窗口条数）。
    /// 自动识别两种文件：①本工具导出的翻译包（含 Format 字段）②FuckDalamudCN 的 translations.json 原始格式。
    /// </summary>
    public (int Installer, int Window) ImportPack(string path)
    {
        var text = File.ReadAllText(path);
        var addedInstaller = 0;
        var addedWindow = 0;

        // 先试本工具翻译包格式。
        // ⚠ 只靠 Format 字段不可靠：反序列化 FDCN 文件成 TranslationPack 时 Format 会取默认值（非 null），
        //   必须同时确认文件里确实带本工具标志串与结构键，才按本工具格式解析。
        TranslationPack? pack = null;
        var looksLikePack = text.Contains("\"FFXIVPluginLocalizer.Translations\"") &&
                            text.Contains("\"Installer\"") && text.Contains("\"Windows\"");
        if (looksLikePack)
        {
            try
            {
                pack = JsonSerializer.Deserialize<TranslationPack>(text);
                if (pack == null || pack.Format != "FFXIVPluginLocalizer.Translations") looksLikePack = false;
            }
            catch
            {
                pack = null;
                looksLikePack = false;
            }
        }
        if (looksLikePack && pack != null)
        {
            lock (_lock)
            {
                foreach (var (en, zh) in Normalize(pack.Installer ?? new()))
                {
                    if (_installerSource.ContainsKey(en)) continue;
                    _installerSource[en] = zh;
                    addedInstaller++;
                }
                foreach (var (plugin, table) in pack.Windows ?? new())
                {
                    if (!_windowSources.TryGetValue(plugin, out var dst))
                        _windowSources[plugin] = dst = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (en, zh) in Normalize(table))
                    {
                        if (dst.ContainsKey(en)) continue;
                        dst[en] = zh;
                        addedWindow++;
                    }
                }
            }
            if (addedWindow > 0)
            {
                foreach (var (plugin, table) in pack.Windows!) WriteWindowFile(plugin, table);
            }
            if (addedInstaller > 0) Save();
            RebuildMerged();
            _appLog.Info($"[替换] 已导入翻译包：安装器 +{addedInstaller} 条，窗口 +{addedWindow} 条（{Path.GetFileName(path)}）");
            return (addedInstaller, addedWindow);
        }

        // 回退：按 FDCN 原始 translations.json 格式解析
        var fdAdded = ImportFdcnFile(path);
        if (fdAdded >= 0)
        {
            _appLog.Info($"[替换] 已按 FuckDalamudCN 格式导入：+{fdAdded} 条（{Path.GetFileName(path)}）");
            return (fdAdded, 0);
        }

        throw new InvalidDataException("无法识别的文件格式（既不是本工具翻译包，也不是 FuckDalamudCN 机翻表）");
    }

    /// <summary> 按 FDCN 原始格式解析指定文件并入安装器表。返回新增条数，格式不符返回 -1。 </summary>
    private int ImportFdcnFile(string path)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(path));
            if (raw == null) return -1;
            var added = 0;
            lock (_lock)
            {
                foreach (var fields in raw.Values)
                {
                    foreach (var f in fields.Values)
                    {
                        if (f.ValueKind != JsonValueKind.Object) return -1; // 结构不符
                        if (!f.TryGetProperty("Original", out var o) || !f.TryGetProperty("Translated", out var t)) continue;
                        var en = o.GetString()?.Trim();
                        var zh = t.GetString()?.Trim();
                        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh) || en == zh) continue;
                        if (_installerSource.ContainsKey(en!)) continue;
                        _installerSource[en!] = zh!;
                        added++;
                    }
                }
            }
            if (added > 0) { RebuildMerged(); Save(); }
            return added;
        }
        catch
        {
            return -1;
        }
    }

    // ═══════════════════════ 哈希 ═══════════════════════

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

    /// <summary> 卸载：释放全部中文指针（含弃用区）。此处渲染线程已停，可安全释放。 </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var ptr in _ptrs.Values) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in _graveyard) Marshal.FreeCoTaskMem(ptr);
            _ptrs.Clear();
            _graveyard.Clear();
        }
    }
}
