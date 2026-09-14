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
    public const string CandidateDirName = "文案扫描"; // 候选文案目录（源码提取 + 历史 DLL 扫描都写这里）
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
    /// <summary> 带 ##ID 的完整标签 → 保号中文指针（键是**原始完整串**，中文里保留 ##ID 后缀）。 </summary>
    private readonly Dictionary<string, nint> _idPtrs = new(StringComparer.Ordinal);
    /// <summary> 首尾带空白的完整串 → 保留空白的中文指针（键是**原始完整串**，中文里保留原空白）。 </summary>
    private readonly Dictionary<string, nint> _wsPtrs = new(StringComparer.Ordinal);
    private readonly List<nint> _graveyard = new();                            // 已弃用的中文指针（仅 Dispose 释放，避免渲染线程 use-after-free）
    private Dictionary<string, string>? _wikiTerms;                            // wiki 官方术语（优先级最高，可选）
    // 候选文案缓存（避免每帧读磁盘 + JSON 解析；键=插件名，值=(候选, 目录指纹时间)）
    private readonly Dictionary<string, (Dictionary<string, string> Cand, DateTime Stamp)> _candCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 还原英文：立即清空**生效的合并表**（安装器表 + 窗口表的内存副本一并清掉，替换立刻失效），
    /// 但**不动磁盘文件**——重新载入即可恢复。用于「主窗口 → 还原英文」一键回到英文界面。
    /// </summary>
    public void ClearActive()
    {
        lock (_lock)
        {
            _table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _hashes = new HashSet<ulong>();
            foreach (var ptr in _ptrs.Values) _graveyard.Add(ptr);
            _ptrs.Clear();
            foreach (var ptr in _idPtrs.Values) _graveyard.Add(ptr);
            _idPtrs.Clear();
            foreach (var ptr in _wsPtrs.Values) _graveyard.Add(ptr);
            _wsPtrs.Clear();
        }
        _appLog.Info("[替换] 已清空生效对照表（界面还原英文；磁盘文件保留，重新载入可恢复）");
    }

    /// <summary> 从磁盘重新载入安装器表 + 窗口表，重建生效表（还原后可恢复中文）。 </summary>
    public void Reload()
    {
        Load();              // 内部会重建
        LoadWindowTables();  // 内部会重建（这次含最新的窗口表，已是最终结果）
        _windowDirStamp = ComputeWindowDirStamp();   // 已按当前磁盘状态重载，记下指纹免重复触发
    }

    /// <summary> 译文目录指纹（文件名+修改时间+大小）。 </summary>
    private string ComputeWindowDirStamp()
    {
        try
        {
            var dir = WindowTableDir;
            if (!Directory.Exists(dir)) return "<none>";
            var entries = Directory.EnumerateFiles(dir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name}:{f.LastWriteTimeUtc.Ticks}:{f.Length}");
            return string.Join("|", entries);
        }
        catch
        {
            return "<err>";
        }
    }

    /// <summary>
    /// 检测**外部**对译文目录的增删改（用户在资源管理器里删/改/加 json），有变化就 Reload；返回是否重载了。
    ///
    /// ⚠ 以前这个检查**只写在「插件翻译」窗口的 Draw 里**——于是「窗口没开时在外部改了 json，
    ///    界面毫无变化」，用户会以为改动不生效（曾被投诉"删了 json 还显示已翻译"）。
    ///    现改为本服务自持指纹 + 自带降频，由框架回调周期性调用，**与窗口是否打开无关**。
    /// </summary>
    public bool CheckExternalChanges()
    {
        var now = Environment.TickCount64;
        if (now - _lastStampCheckMs < 3000) return false;   // 降频：3 秒一次（列目录便宜，但没必要每帧做）
        _lastStampCheckMs = now;
        var stamp = ComputeWindowDirStamp();
        if (stamp == _windowDirStamp) return false;
        _windowDirStamp = stamp;
        Reload();
        _appLog.Info("[替换] 检测到译文目录变化，已自动重读（外部增删改 json 生效）");
        return true;
    }

    private long _lastStampCheckMs;
    private string _windowDirStamp = "";

    /// <summary> 设置 wiki 官方术语表（null 或空 = 不启用）。优先级最高，重建生效表。 </summary>
    public void SetWikiTerms(Dictionary<string, string>? terms)
    {
        _wikiTerms = terms is { Count: > 0 } ? terms : null;
        RebuildMerged();
        _appLog.Info($"[wiki] 生效术语 {_wikiTerms?.Count ?? 0} 条（优先级最高）");
    }

    /// <summary> 替换开关（只影响绘制替换；表的管理不受影响）。 </summary>
    public bool Enabled { get; set; }

    public int Count { get { lock (_lock) return _table.Count; } }

    public ReplacementService(AppLog appLog, Func<string> configDir)
    {
        _appLog = appLog;
        _configDir = configDir;
        Load();
        LoadWindowTables();
        // 记下启动时的目录指纹，免得 CheckExternalChanges 首次调用误判为"外部改动"而白重载一次
        _windowDirStamp = ComputeWindowDirStamp();
    }

    /// <summary> 对照表文件路径。 </summary>
    public string TablePath => Path.Combine(_configDir(), TableFileName);

    /// <summary> 窗口文字表目录。 </summary>
    public string WindowTableDir => Path.Combine(_configDir(), WindowTableDirName);

    /// <summary> 启动载入安装器对照表（原文/译文成对数组格式，兼容旧字典格式）。 </summary>
    public void Load()
    {
        try
        {
            var path = TablePath;
            var data = TranslationFile.Load(path);
            if (data.Count == 0) return;
            _installerSource = Normalize(data);
            RebuildMerged();
            _appLog.Info($"[替换] 已载入安装器对照表：{_installerSource.Count} 条（{TableFileName}）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 对照表载入失败：" + ex.Message);
        }
    }

    /// <summary> 保存安装器对照表（原文/译文成对数组格式）。 </summary>
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
            TranslationFile.Save(TablePath, copy);
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
            // ⚠ 即使目录不存在也要先清空内存表：否则用户删掉整个目录后，界面仍显示旧的已翻译内容
            lock (_lock)
            {
                _windowSources.Clear();
                if (!Directory.Exists(dir))
                {
                    // 目录都没了 → 视为无译文，重建生效表（让替换立即失效）
                    _appLog.Info("[替换] 插件翻译目录不存在，已清空窗口表");
                }
                else
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        var data = TranslationFile.Load(file);
                        if (data.Count == 0) continue;
                        _windowSources[Path.GetFileNameWithoutExtension(file)] = Normalize(data);
                    }
                }
            }
            RebuildMerged();
            _appLog.Info($"[替换] 已载入插件翻译表：{_windowSources.Count} 个插件");
        }
        catch (Exception ex)
        {
            _appLog.Error("[替换] 窗口文字表载入失败：" + ex.Message);
        }
    }

    /// <summary> 重建合并查找表。优先级：**wiki 官方术语 > 安装器表 > 各插件窗口表**（先写入者优先）。
    /// ⚠ 旧中文指针**不在运行时释放**：渲染线程可能正拿着某个指针绘制（后台机翻线程重建表时会并发），
    /// 立即 Free 会造成 use-after-free 崩溃。统一塞进 _graveyard，只在 Dispose 释放；重建不频繁，内存代价可忽略。 </summary>
    private void RebuildMerged()
    {
        lock (_lock)
        {
            foreach (var ptr in _ptrs.Values) _graveyard.Add(ptr);
            _ptrs.Clear();
            foreach (var ptr in _idPtrs.Values) _graveyard.Add(ptr);
            _idPtrs.Clear();
            foreach (var ptr in _wsPtrs.Values) _graveyard.Add(ptr);
            _wsPtrs.Clear();
            _table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _hashes = new HashSet<ulong>();
            // ① wiki 官方术语最高优先（物品/技能名机翻几乎必错，必须用官方译名）
            if (_wikiTerms is { Count: > 0 })
            {
                foreach (var (en, zh) in _wikiTerms) AddMerged(en, zh);
            }
            // ② 安装器对照表
            foreach (var (en, zh) in _installerSource) AddMerged(en, zh);
            // ③ 各插件窗口表（AddMerged 内部已是"已有键跳过"，天然实现优先级）
            foreach (var table in _windowSources.Values)
            {
                foreach (var (en, zh) in table) AddMerged(en, zh);
            }
        }
    }

    /// <summary>
    /// 写入合并表（**只存字符串与哈希，不预分配原生指针**）。
    /// ⚠ 性能关键：合并表含 6 万+ 条 wiki 术语，若每条都预分配（原实现），
    /// 每次重建需 6 万次 StringToCoTaskMemUTF8 → 单次 80~220ms 卡顿，
    /// 而 ImGui 逐帧处理按键，卡顿会**丢输入事件**（表现为「Ctrl+V 要按好几次才粘贴成功」）。
    /// 现改为惰性分配：只有真正被渲染命中的条目才建指针（见 GetOrCreatePtr）。
    /// </summary>
    private void AddMerged(string en, string zh)
    {
        if (_table.ContainsKey(en)) return;
        _table[en] = Encoding.UTF8.GetBytes(zh);
        _hashes.Add(FnvUtf8(en));
    }

    /// <summary> 惰性取中文指针：命中才分配并缓存（避免为 6 万条未被渲染的术语预分配）。 </summary>
    private nint GetOrCreatePtr(string key)
    {
        if (_ptrs.TryGetValue(key, out var ptr)) return ptr;
        if (!_table.TryGetValue(key, out var bytes)) return 0;
        ptr = Marshal.StringToCoTaskMemUTF8(Encoding.UTF8.GetString(bytes));
        _ptrs[key] = ptr;
        return ptr;
    }

    /// <summary> 增量更新单条窗口译文（避免写入时全量重建）。调用方须持有 _lock。 </summary>
    private void ApplyOneWindowEntry(string en, string zh)
    {
        if (_wikiTerms?.ContainsKey(en) == true || _installerSource.ContainsKey(en)) return; // 高优先级来源已占该键
        if (_ptrs.Remove(en, out var old)) _graveyard.Add(old);   // 旧指针作废（延迟释放）
        _table[en] = Encoding.UTF8.GetBytes(zh);
        _hashes.Add(FnvUtf8(en));
    }

    /// <summary> 增量移除单条窗口译文（其它窗口表或高优先级来源仍有时保留）。调用方须持有 _lock。 </summary>
    private void RemoveOneWindowEntry(string en)
    {
        if (_wikiTerms?.ContainsKey(en) == true || _installerSource.ContainsKey(en)) return;
        foreach (var t in _windowSources.Values)
        {
            if (t.ContainsKey(en)) return; // 别的插件表还有同键
        }
        if (_ptrs.Remove(en, out var old)) _graveyard.Add(old);
        _table.Remove(en);
        // 不摘 _hashes：防止哈希碰撞误伤同哈希的其它键（多一次未命中，无害）
    }

    /// <summary> 清洗：去空白、去空值、去同文，并剔除 ImGui 内部 ID（<c>##</c> 开头，无显示文字）。 </summary>
    private static Dictionary<string, string> Normalize(Dictionary<string, string> data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, zh) in data)
        {
            var k = en.Trim();
            var v = (zh ?? "").Trim();
            if (k.Length < 2 || v.Length == 0 || k == v) continue;
            if (k.StartsWith("##")) continue; // ImGui 内部 ID，不参与替换
            result[k] = v;
        }
        return result;
    }

    /// <summary>
    /// 热路径查表：命中返回 NUL 结尾的中文指针（转发时 text_end 传 0），未命中返回 0。
    /// **双段匹配**：先按整串查（含 <c>##ID</c> 的完整标签）；未命中再按去掉 <c>##ID</c> 的显示部分查。
    /// 原因：采集/源码提取入库时存的是「显示文字」（如 <c>设置</c>），而 ImGui 收到的完整串是 <c>设置##bdp</c>——
    /// 只按整串匹配会导致带 ID 的控件标签全部替换不了。
    /// </summary>
    public nint TryReplace(byte* p, int n)
    {
        if (!Enabled || _table.Count == 0 || n < 2 || n > 1024) return 0;

        // 热路径零分配预筛：算整串哈希，并**在字节层面**找 ## 分隔符（不构造字符串）
        ulong h;
        int hashIdx = -1;
        unsafe
        {
            h = FnvBytes(p, n);
            for (var i = 0; i + 1 < n; i++)
            {
                if (p[i] == (byte)'#' && p[i + 1] == (byte)'#') { hashIdx = i; break; }
            }
        }
        ulong hShown = 0;
        if (hashIdx > 0)
        {
            unsafe { hShown = FnvBytes(p, hashIdx); }
        }

        lock (_lock)
        {
            // ① 整串匹配
            if (_hashes.Contains(h))
            {
                var s = Encoding.UTF8.GetString(p, n);
                var ptr = GetOrCreatePtr(s);
                if (ptr != 0) return ptr;
            }

            // ② 截掉 ##ID 后的显示部分匹配
            // ⚠ 必须**独立判断**（不能嵌在①的命中分支内）：控件标签形如 `显示文字##内部ID`，
            //    而表里存的是 `显示文字`——整串哈希必然不命中，只有截断后才可能命中。
            //    原实现把它嵌在①内部，导致**带 ##ID 的控件标签全部替换不了**（已修）。
            // ⚠⚠ 返回的译文中**必须把 `##ID` 原样接回去**：ImGui 用 `##` 之后的部分算控件 ID，
            //    若只返回「别名」而丢掉「tab_id」，控件 ID 就从 `tab_id` 变成 `别名`——
            //    同一窗口里多个控件可能因此撞到同一个 ID（重复点击/状态互串），移动端布局记忆也会丢。
            if (hashIdx > 0 && _hashes.Contains(hShown))
            {
                var shown = Encoding.UTF8.GetString(p, hashIdx);
                // 命中才返回；未命中不 return（继续走下面的首尾空白容错）
                if (_table.TryGetValue(shown, out var zhBytes))
                {
                    var full = Encoding.UTF8.GetString(p, n);
                    if (_idPtrs.TryGetValue(full, out var cached)) return cached;
                    // 显示部分换成中文，其余（##ID 及其后）原样保留
                    var composed = Encoding.UTF8.GetString(zhBytes) + full.Substring(hashIdx);
                    var p3 = Marshal.StringToCoTaskMemUTF8(composed);
                    _idPtrs[full] = p3;
                    return p3;
                }
            }

            // ③ 首尾空白容错：源码里常用换行/缩进做间距，如 `ImGui.Text("\n[Cone 1]")`——
            //    运行时传给 ImGui 的是**带 \n 的整串**，而候选/对照表存的是 Trim 过的 `[Cone 1]`
            //    （提取侧 Unescape 后 Trim 掉了），整串哈希必然不命中 → 这类「缩进/间距文案」永远翻不了。
            //    这里按「去掉首尾空白后的核心」再查一次，命中则返回**把原空白原样拼回**的中文（保留排版间距）。
            int wsStart = 0, wsEnd = n;
            unsafe
            {
                while (wsStart < wsEnd && IsAsciiSpace(p[wsStart])) wsStart++;
                while (wsEnd > wsStart && IsAsciiSpace(p[wsEnd - 1])) wsEnd--;
            }
            if ((wsStart > 0 || wsEnd < n) && wsEnd - wsStart >= 2)
            {
                ulong hCore;
                unsafe { hCore = FnvBytes(p + wsStart, wsEnd - wsStart); }
                if (_hashes.Contains(hCore))
                {
                    var core = Encoding.UTF8.GetString(p + wsStart, wsEnd - wsStart);
                    if (_table.TryGetValue(core, out var zhB))
                    {
                        var full = Encoding.UTF8.GetString(p, n);
                        if (_wsPtrs.TryGetValue(full, out var cached2)) return cached2;
                        var composed = Encoding.UTF8.GetString(p, wsStart)
                                     + Encoding.UTF8.GetString(zhB)
                                     + Encoding.UTF8.GetString(p + wsEnd, n - wsEnd);
                        var p4 = Marshal.StringToCoTaskMemUTF8(composed);
                        _wsPtrs[full] = p4;
                        return p4;
                    }
                }
            }
            return 0;
        }
    }

    /// <summary> 是否 ASCII 空白（替换热路径用，避免调用 char.IsWhiteSpace 的开销/本地化差异）。 </summary>
    private static bool IsAsciiSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

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
            // 内置包当前是纯字典格式（构建时由 PowerShell 生成），用统一读取器兼容
            var data = TranslationFile.Load(bundle);
            if (data.Count == 0) return;
            var added = 0;
            lock (_lock)
            {
                foreach (var (en, zh) in Normalize(data))
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
            // ⚠ 清单路径存在两种布局，必须都认：
            //   · `installedPlugins\<插件ID>\<版本>\<插件ID>.json`  ← 正式安装（绝大多数）
            //   · `installedPlugins\<插件ID>\<插件ID>.json`          ← 少数/旧布局
            //   旧实现只比较**直接父目录**（`name != dir`）→ 正式安装的父目录是**版本号**
            //   （如 `1.1.0.15`），永远不相等 → **29 个已装清单里只有 1 个被读到**，
            //   于是 CollectMissing 长期"看不见缺口"、启动检查谎报"已覆盖全部介绍"、
            //   新插件的描述永远不会被自动翻译（2026-09-14 实测发现并修复）。
            var name = Path.GetFileNameWithoutExtension(manifestPath);
            var dir = Path.GetFileName(Path.GetDirectoryName(manifestPath));
            var grand = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(manifestPath)));
            if (name != dir && name != grand) continue;
            try
            {
                var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(manifestPath));
                if (m == null) continue;
                foreach (var field in new[] { "Description", "Punchline" })
                {
                    if (!m.TryGetValue(field, out var el) || el.ValueKind != JsonValueKind.String) continue;
                    var text = el.GetString()?.Trim();
                    if (string.IsNullOrEmpty(text) || text!.Length < 2) continue;
                    // 已经是中文的（不少国服/汉化分支插件把 Description 直接写成中文）不必再送翻——
                    // 否则会白耗机翻配额，还可能把中文"翻译"成别的语言。
                    if (TextHeuristics.HasCjk(text!)) continue;
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

    /// <summary> 候选来源文件名后缀（两种来源合并：源码提取优先，其次历史 DLL 扫描结果）。 </summary>
    private static readonly string[] CandidateSuffixes = { "_源码提取.json", "_未翻译.json" };

    /// <summary>
    /// 读取某插件的候选文案（合并两种来源：源码提取 + 历史 DLL 扫描）。
    /// ⚠ 带缓存：本方法会被**每帧**调用（插件翻译窗口列插件时），而候选文件可能很大
    /// （如 LightlessSync 1.1 万条，读+解析 ~44ms）——不缓存会严重掉帧。
    /// 缓存以「相关文件的最新写入时间」为失效依据，外部改动会自动刷新。
    /// </summary>
    private Dictionary<string, string> ReadCandidates(string plugin)
    {
        var dir = Path.Combine(_configDir(), CandidateDirName);
        // 计算指纹（两个候选文件的最后写入时间取最大；文件不存在记 MinValue）
        var stamp = DateTime.MinValue;
        foreach (var suffix in CandidateSuffixes)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(dir, plugin + suffix));
                if (fi.Exists && fi.LastWriteTimeUtc > stamp) stamp = fi.LastWriteTimeUtc;
            }
            catch { /* 取不到时间就按未缓存处理 */ }
        }

        lock (_lock)
        {
            if (_candCache.TryGetValue(plugin, out var cached) && cached.Stamp == stamp)
                return cached.Cand;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // 后读的优先（源码提取放前面先读，_未翻译 只补它没有的）
        foreach (var suffix in CandidateSuffixes)
        {
            foreach (var (k, v) in ReadJsonDict(Path.Combine(dir, plugin + suffix)))
            {
                if (!result.ContainsKey(k)) result[k] = v;
            }
        }

        lock (_lock)
        {
            _candCache[plugin] = (result, stamp);
        }
        return result;
    }

    /// <summary> 清空候选缓存（源码提取出新候选后调用）。 </summary>
    public void InvalidateCandidateCache()
    {
        lock (_lock) _candCache.Clear();
    }

    /// <summary> 列出所有有候选的插件名（两种来源的并集）。带缓存，避免每帧列目录。 </summary>
    private List<string>? _pluginNameCache;
    private DateTime _pluginNameCacheAt = DateTime.MinValue;

    private List<string> ListCandidatePlugins()
    {
        // 5 秒内的结果复用（列目录+文件名解析虽快，但每帧做也不必要）
        lock (_lock)
        {
            if (_pluginNameCache != null && (DateTime.UtcNow - _pluginNameCacheAt).TotalSeconds < 5)
                return _pluginNameCache;
        }
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(_configDir(), CandidateDirName);
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    foreach (var suffix in CandidateSuffixes)
                    {
                        var tag = Path.GetFileNameWithoutExtension(suffix);
                        if (stem.EndsWith(tag, StringComparison.Ordinal))
                        {
                            names.Add(stem[..^tag.Length]);
                            break;
                        }
                    }
                }
            }
        }
        catch { /* 枚举失败忽略 */ }
        lock (_lock)
        {
            foreach (var n in _windowSources.Keys) names.Add(n);
            _pluginNameCache = names.ToList();
            _pluginNameCacheAt = DateTime.UtcNow;
            return _pluginNameCache;
        }
    }

    /// <summary> 窗口表清单：插件名 →（候选总数, 已翻译数）。候选来自源码提取/扫描输出，已翻译来自窗口表。 </summary>
    public List<(string Plugin, int Total, int Translated)> GetWindowPlugins()
    {
        var result = new List<(string Plugin, int Total, int Translated)>();
        foreach (var name in ListCandidatePlugins())
        {
            var candidates = ReadCandidates(name);
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
        var candidates = ReadCandidates(plugin);
        List<(string, string)> translated;
        List<string> untranslated;
        lock (_lock)
        {
            var win = _windowSources.TryGetValue(plugin, out var t) ? t : new Dictionary<string, string>();
            translated = win.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
            // 待翻列表只列**值得翻译**的（排除 /命令、纯符号、键位名等，避免白送 AI 浪费额度）
            untranslated = candidates.Keys
                .Where(k => !win.ContainsKey(k) && TextHeuristics.IsTranslatable(k))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
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
            if (added > 0)
            {
                WriteWindowFile(plugin, table);
                foreach (var (en, zh) in translations)     // 增量写入合并表，避免全量重建
                {
                    var key = en.Trim();
                    var val = (zh ?? "").Trim();
                    if (key.Length >= 2 && val.Length > 0) ApplyOneWindowEntry(key, val);
                }
            }
        }
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
            if (zh.Length == 0) RemoveOneWindowEntry(en);
            else ApplyOneWindowEntry(en, zh);
        }
    }

    /// <summary> 删除某插件窗口表的单条。 </summary>
    public void RemoveWindowEntry(string plugin, string en)
    {
        lock (_lock)
        {
            if (_windowSources.TryGetValue(plugin, out var table) && table.Remove(en))
            {
                WriteWindowFile(plugin, table);
                RemoveOneWindowEntry(en);
            }
        }
    }

    /// <summary>
    /// 某插件的翻译完成情况：返回（候选总数, 已翻译数, 是否已完成）。
    /// 完成判定：只看**值得翻译**的候选（排除命令、纯符号、键位名等无需翻译项），
    /// 且它们都在译文表里有非空译文。用于「源码提取」列表标注「已翻译」，避免重复提取。
    /// </summary>
    public (int Total, int Translated, bool Done) GetTranslationProgress(string plugin)
    {
        var candidates = ReadCandidates(plugin);
        // 只统计"需要翻译"的条目：像 /tp、纯符号、Ctrl 这类本就不该翻，
        // 否则插件永远卡在"14/15"显示未完成。
        var need = candidates.Keys.Where(TextHeuristics.IsTranslatable).ToList();
        int translatedCount;
        List<string> missing;
        lock (_lock)
        {
            var win = _windowSources.TryGetValue(plugin, out var t) ? t : new Dictionary<string, string>(StringComparer.Ordinal);
            translatedCount = need.Count(k => win.ContainsKey(k));
            missing = need.Where(k => !win.ContainsKey(k)).ToList();
        }
        var done = need.Count > 0 && missing.Count == 0;
        return (need.Count, translatedCount, done);
    }

    /// <summary>
    /// **还原某插件为英文**：清空该插件的全部译文（删除其译文文件并移出内存表）。
    /// 候选文件**不动**——重新翻译时仍在（属"题目"与"答卷"分离的设计）。
    /// 返回被清除的译文条数。
    /// </summary>
    public int ClearPluginTranslations(string plugin)
    {
        var removed = 0;
        lock (_lock)
        {
            if (_windowSources.TryGetValue(plugin, out var table))
            {
                removed = table.Count;
                _windowSources.Remove(plugin);
                // 先移出 _windowSources 再逐条增量移除（这样"别的表是否还有同键"的判断才准确）
                foreach (var en in table.Keys.ToList()) RemoveOneWindowEntry(en);
            }
            try
            {
                var file = Path.Combine(WindowTableDir, $"{plugin}.json");
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                _appLog.Warn($"[替换] 删除 {plugin} 的译文文件失败：{ex.Message}");
            }
            _windowDirStamp = ComputeWindowDirStamp();   // 同 WriteWindowFile：自己的改动不该被当成外部改动
        }
        _appLog.Info($"[替换] 已还原 {plugin} 为英文（清除 {removed} 条译文，候选文件保留）");
        return removed;
    }

    private void WriteWindowFile(string plugin, Dictionary<string, string> table)
    {
        try
        {
            Directory.CreateDirectory(WindowTableDir);
            TranslationFile.Save(Path.Combine(WindowTableDir, $"{plugin}.json"), table);
            // ⚠ 本插件自己写的文件也要刷新指纹：否则 CheckExternalChanges 会把**自己的写入**
            //    误判成"外部改动"，3 秒后白做一次全量 Reload（每次编辑译文都多付一次重建代价）。
            _windowDirStamp = ComputeWindowDirStamp();
        }
        catch (Exception ex)
        {
            _appLog.Error($"[替换] 窗口表保存失败（{plugin}）：" + ex.Message);
        }
    }

    /// <summary> 读扫描候选文件（文案扫描输出，仍是「英文→空值」字典格式）。 </summary>
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

    /// <summary> 导出的翻译包结构：安装器表 + 每插件窗口表；条目用「原文/译文」成对数组（抄旧项目格式）。 </summary>
    public sealed class TranslationPack
    {
        public string Format { get; set; } = "FFXIVPluginLocalizer.Translations";
        public int Version { get; set; } = 2;
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        /// <summary> 安装器介绍对照（原文/译文成对数组）。 </summary>
        public List<TranslationFile.Pair> Installer { get; set; } = new();
        /// <summary> 窗口文字对照：插件名 →（原文/译文成对数组）。 </summary>
        public Dictionary<string, List<TranslationFile.Pair>> Windows { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 导出翻译包（安装器表 + 全部窗口表）到指定文件。返回（安装器条数, 窗口表条数）。
    /// </summary>
    public (int Installer, int Window) ExportPack(string path)
    {
        TranslationPack pack;
        int winTotal;
        lock (_lock)
        {
            pack = new TranslationPack
            {
                Installer = ToPairs(_installerSource),
                Windows = _windowSources.ToDictionary(kv => kv.Key, kv => ToPairs(kv.Value), StringComparer.Ordinal),
            };
            winTotal = _windowSources.Values.Sum(w => w.Count);
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(pack, Indented), Encoding.UTF8);
        _appLog.Info($"[替换] 已导出翻译包：安装器 {pack.Installer.Count} 条 + 窗口 {winTotal} 条（{pack.Windows.Count} 个插件）→ {path}");
        return (pack.Installer.Count, winTotal);
    }

    private static List<TranslationFile.Pair> ToPairs(Dictionary<string, string> table)
        => table.Select(kv => new TranslationFile.Pair { 原文 = kv.Key, 译文 = kv.Value }).ToList();

    private static Dictionary<string, string> FromPairs(List<TranslationFile.Pair>? pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pairs == null) return result;
        foreach (var p in pairs)
        {
            var k = (p.原文 ?? "").Trim();
            var v = (p.译文 ?? "").Trim();
            if (k.Length >= 1 && v.Length > 0 && k != v) result[k] = v;
        }
        return result;
    }

    /// <summary>
    /// 导入翻译包并与现有表合并（**不覆盖已有条目**，先到先得，本机编辑优先）。返回（新增安装器条数, 新增窗口条数）。
    /// 自动识别：①本工具翻译包（Format 标志 + Installer/Windows 键）②FuckDalamudCN 的 translations.json 原始格式
    /// ③纯字典格式 {en: zh}（旧版本工具表 / 用户手工制作）。
    /// </summary>
    public (int Installer, int Window) ImportPack(string path)
    {
        var text = File.ReadAllText(path);
        var addedInstaller = 0;
        var addedWindow = 0;

        // ① 本工具翻译包
        if (text.Contains("\"FFXIVPluginLocalizer.Translations\"") &&
            text.Contains("\"Installer\"") && text.Contains("\"Windows\""))
        {
            var pack = JsonSerializer.Deserialize<TranslationPack>(text);
            if (pack != null && pack.Format == "FFXIVPluginLocalizer.Translations")
            {
                lock (_lock)
                {
                    foreach (var (en, zh) in Normalize(FromPairs(pack.Installer)))
                    {
                        if (_installerSource.ContainsKey(en)) continue;
                        _installerSource[en] = zh;
                        addedInstaller++;
                    }
                    foreach (var (plugin, pairs) in pack.Windows ?? new())
                    {
                        if (!_windowSources.TryGetValue(plugin, out var dst))
                            _windowSources[plugin] = dst = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (en, zh) in Normalize(FromPairs(pairs)))
                        {
                            if (dst.ContainsKey(en)) continue;
                            dst[en] = zh;
                            addedWindow++;
                        }
                    }
                }
                if (addedWindow > 0)
                {
                    foreach (var plugin in pack.Windows!.Keys) WriteWindowFile(plugin, _windowSources[plugin]);
                }
                if (addedInstaller > 0) Save();
                RebuildMerged();
                _appLog.Info($"[替换] 已导入翻译包：安装器 +{addedInstaller} 条，窗口 +{addedWindow} 条（{Path.GetFileName(path)}）");
                return (addedInstaller, addedWindow);
            }
        }

        // ② FDCN 原始 translations.json 格式（插件ID → 字段 → {Original,Translated}）
        var fdAdded = ImportFdcnFile(path);
        if (fdAdded >= 0)
        {
            _appLog.Info($"[替换] 已按 FuckDalamudCN 格式导入：+{fdAdded} 条（{Path.GetFileName(path)}）");
            return (fdAdded, 0);
        }

        // ③ 纯字典 / 成对数组（通用）
        var generic = TranslationFile.Load(path);
        if (generic.Count == 0) throw new InvalidDataException("无法识别的文件格式（不是本工具翻译包，也不是 FuckDalamudCN 机翻表）");
        lock (_lock)
        {
            foreach (var (en, zh) in Normalize(generic))
            {
                if (_installerSource.ContainsKey(en)) continue;
                _installerSource[en] = zh;
                addedInstaller++;
            }
        }
        if (addedInstaller > 0) { RebuildMerged(); Save(); }
        _appLog.Info($"[替换] 已按通用格式导入：+{addedInstaller} 条（{Path.GetFileName(path)}）");
        return (addedInstaller, 0);
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
            foreach (var ptr in _idPtrs.Values) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in _wsPtrs.Values) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in _graveyard) Marshal.FreeCoTaskMem(ptr);
            _ptrs.Clear();
            _idPtrs.Clear();
            _wsPtrs.Clear();
            _graveyard.Clear();
        }
    }
}
