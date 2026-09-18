using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    /// <summary> 「还原英文」前的自动备份目录名（回收站性质，用户可自行取回）。 </summary>
    public const string WindowBackupDirName = "窗口翻译_还原备份";
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
    private readonly Configuration _config;
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
    // ④ 前缀模板候选（动态插值文案，2026-09-18）：按首字节索引 → 模板前缀列表。
    //    CharacterSync 的 $"The logged in character is {name} on {world}(...)" 这类 C# 插值串
    //    运行时拼出完整串（角色名/服务器/ID 是变量），整串哈希永远不命中；
    //    表里存 Trim 后的静态前缀，命中规则=输入以键开头且键后是空格。
    private Dictionary<byte, PrefixEntry[]> _prefixIndex = new();

    /// <summary>
    /// 派生缓存（<see cref="_prefixIndex"/>）需要重建的标记。
    ///
    /// ⚠ 2026-09-18 全面审查①（**高**）：`_idPtrs`/`_wsPtrs` 缓存的是"按某条译文献拼好的完整串"，
    ///   `_prefixIndex` 缓存的是模板索引——**三者都依赖 `_table` 的内容**。
    ///   而增量写路径（`ApplyOneWindowEntry`/`RemoveOneWindowEntry`）原先只动 `_table`/`_ptrs`，
    ///   不碰这三个 → **用户改完译文，带 `##ID`／首尾空白／前缀模板的界面仍显示旧译文**，
    ///   且因为 `WriteWindowFile` 刷新了目录指纹、`CheckExternalChanges` 不会触发重读，
    ///   只有手动「重新扫描」或重载插件才生效（核心编辑流程表面成功、实际不生效）。
    ///   `_idPtrs`/`_wsPtrs` 在增量写时**直接清空**（条目少，开销可忽略）；
    ///   `_prefixIndex` 用**惰性重建**（重建要遍历全部窗口表+安装器表，每次编辑都重建太贵），
    ///   由热路径 `TryReplace` 在持锁状态下按需重建一次。
    /// </summary>
    private bool _prefixDirty;

    /// <summary> 使「依赖 _table 内容的派生缓存」失效（审查①）。 </summary>
    private void InvalidateDerivedCaches()
    {
        foreach (var ptr in _idPtrs.Values) _graveyard.Add(ptr);
        _idPtrs.Clear();
        foreach (var ptr in _wsPtrs.Values) _graveyard.Add(ptr);
        _wsPtrs.Clear();
        _prefixDirty = true;
    }

    /// <summary> 前缀模板条目：静态前缀（UTF-8）+ 原文（黑名单检查用）+ 译文（UTF-8）。 </summary>
    private sealed class PrefixEntry
    {
        public byte[] Key;
        public string Text;
        public byte[] Zh;
        public PrefixEntry(byte[] key, string text, byte[] zh) { Key = key; Text = text; Zh = zh; }
    }
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
            _prefixIndex = new();
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
        // 2026-09-17：低频自动清理**已卸载**插件（60 秒一次；候选插件不在已装列表 → 删译文/候选/源码）
        if (now - _lastPurgeCheckMs > 60_000)
        {
            _lastPurgeCheckMs = now;
            if (AutoPurgeUninstalled().Count > 0)
                _windowDirStamp = "";   // 强制下次指纹检查重读（候选/译文已变）
        }
        var stamp = ComputeWindowDirStamp();
        if (stamp == _windowDirStamp) return false;
        _windowDirStamp = stamp;
        Reload();
        _appLog.Info("[替换] 检测到译文目录变化，已自动重读（外部增删改 json 生效）");
        return true;
    }

    private long _lastStampCheckMs;
    private long _lastPurgeCheckMs;
    private string _windowDirStamp = "";

    /// <summary> 设置 wiki 官方术语表（null 或空 = 不启用）。优先级最高，重建生效表。 </summary>
    public void SetWikiTerms(Dictionary<string, string>? terms)
    {
        _wikiTerms = terms is { Count: > 0 } ? terms : null;
        RebuildMerged();
        _appLog.Info($"[wiki] 生效术语 {_wikiTerms?.Count ?? 0} 条（优先级最高）");
    }

    /// <summary> 由调用方注入「单词黑名单」判定（命中则**永不替换**，保持英文）。null = 不启用。 </summary>
    private Func<string, bool>? _isBlacklisted;

    /// <summary>
    /// 注入单词黑名单判定（`词典目录\单词黑名单.json`）。
    /// ⚠ 这是**硬保证**：即使在 wiki/词典/译文表里命中了，只要该词在黑名单就保持英文——
    ///   黑名单优先级最高（对齐旧项目"单词黑名单 &gt; 个性翻译 &gt; 我的翻译 &gt; wiki"的优先级链）。
    /// </summary>
    public void SetBlacklist(Func<string, bool>? isBlacklisted) => _isBlacklisted = isBlacklisted;

    /// <summary>
    /// 该条目**是否需要翻译**：可翻译（非命令/非技术噪音…）**且未被单词黑名单拉黑**。
    ///
    /// ⚠ 必须把黑名单算进来（2026-09-15 自查发现的 bug）：黑名单词会被机翻跳过、也进不了词典，
    ///   若仍计入"待翻译"，进度就**永远到不了 100%**（显示 68/69 这种）——与早期 `[Cone 1]`
    ///   永远算未翻译是同一类问题。黑名单词的语义是"**本就不该翻**"，故与 `/命令`、纯符号同等待遇。
    /// </summary>
    private bool NeedsTranslation(string key)
    {
        if (!TextHeuristics.IsTranslatable(key)) return false;
        var bl = _isBlacklisted;
        return bl == null || !bl(key);
    }

    /// <summary> 替换开关（只影响绘制替换；表的管理不受影响）。 </summary>
    public bool Enabled { get; set; }

    public int Count { get { lock (_lock) return _table.Count; } }

    public ReplacementService(AppLog appLog, Func<string> configDir, Configuration config)
    {
        _appLog = appLog;
        _configDir = configDir;
        _config = config;
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

    /// <summary> 重建合并查找表。优先级：**各插件窗口表 > 安装器表 > wiki 官方术语**（先写入者优先）。
    /// ⚠ 2026-09-16 调整：原为「wiki 最高优先」，导致通用游戏术语（如技能_动作.json 的 Disable→封技）
    ///   压住插件设置界面的精确译文（HighFPSPhysics 的 Disable 按钮应显示"禁用"）。窗口表是插件特定语境
    ///   的精确匹配，理应优先；wiki 仅作兜底。
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
            // ① 各插件窗口表（精确匹配优先，插件特定语境；AddMerged 内部已是"已有键跳过"，天然实现优先级）
            // ⚠ **按插件名排序遍历**（2026-09-15 审查 M2）：同一英文若出现在多个插件表里，
            //    原实现依赖 Dictionary.Values 的遍历顺序（增删后可能变），结果不确定。
            //    排序后规则明确：**插件名靠前者优先**，且与增量路径 ApplyOneWindowEntry 的判定完全一致。
            foreach (var table in _windowSources.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))
            {
                foreach (var (en, zh) in table) AddMerged(en, zh);
            }
            // ② 安装器对照表
            foreach (var (en, zh) in _installerSource) AddMerged(en, zh);
            // ④ 前缀模板候选：仅窗口表+安装器表（wiki 是物品/技能术语，无动态模板）；
            //    模板特征=键长 ≥ 20 且 ≥ 3 个空格（C# 插值/拼接模板静态前缀的形态）。
            RebuildPrefixIndex();
            _prefixDirty = false;   // 审查①：已随全量重建刷新
            // ③ wiki 官方术语兜底（窗口表/安装器没有才用；物品/技能名机翻几乎必错，必须用官方译名）
            if (_wikiTerms is { Count: > 0 })
            {
                foreach (var (en, zh) in _wikiTerms) AddMerged(en, zh);
            }
        }
    }

    /// <summary>
    /// 重建 <see cref="_prefixIndex"/>（前缀模板索引）。调用方须持有 <see cref="_lock"/>。
    /// 审查①：从 <c>RebuildMerged</c> 里抽出来，供「全量重建」与「增量写后的惰性重建」共用。
    /// </summary>
    private void RebuildPrefixIndex()
    {
        var preIdx = new Dictionary<byte, List<PrefixEntry>>();
        foreach (var table in _windowSources.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))
        {
            foreach (var (en, zh) in table) AddPrefixCandidate(preIdx, en, zh);
        }
        foreach (var (en, zh) in _installerSource) AddPrefixCandidate(preIdx, en, zh);
        _prefixIndex = preIdx.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
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

    /// <summary> 前缀候选：模板特征筛选（长键 + 多空格）并按首字节索引（热路径只查首字节同组）。 </summary>
    private static void AddPrefixCandidate(Dictionary<byte, List<PrefixEntry>> idx, string en, string zh)
    {
        if (en.Length < 20) return;
        int sp = 0;
        foreach (var c in en) if (c == ' ') sp++;
        if (sp < 3) return;
        var kb = Encoding.UTF8.GetBytes(en);
        if (!idx.TryGetValue(kb[0], out var list)) idx[kb[0]] = list = new List<PrefixEntry>();
        list.Add(new PrefixEntry(kb, en, Encoding.UTF8.GetBytes(zh)));
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
    /// <summary>
    /// 增量写入单条窗口译文。
    /// ⚠ 语义必须与全量重建的 <see cref="AddMerged"/> 一致（2026-09-15 代码审查 M2）：
    ///   全量走 "先写入者优先"（`_table.ContainsKey` 命中即跳过，由 RebuildMerged 的
    ///   wiki → 安装器表 → 窗口表 顺序决定优先级）；此处原为 "后写入覆盖" → 同一英文跨来源冲突时，
    ///   **增量与全量结果可能不同**（例如预翻译后再机翻，路径不同结果不同）。
    ///   现在统一为：**高优先级来源已占该键则不覆盖**；窗口表自己的条目才更新（那是"同一插件改译文"）。
    /// </summary>
    private void ApplyOneWindowEntry(string plugin, string en, string zh)
    {
        // ⚠ 2026-09-16：不再因 wiki/安装器占键而 return（优先级已调整为窗口表 > 安装器 > wiki），
        //   窗口表条目对同键总是生效（用户对插件翻译有完全控制权）。
        // ⚠ 跨插件同键：与 RebuildMerged 用同一规则（**插件名靠前者优先**），否则增量与全量结果不一致（M2）。
        //   只比"名字排在我前面"的表——它们在全量重建时会先写入 _table。
        foreach (var (other, t) in _windowSources)
        {
            if (other == plugin) continue;
            if (string.CompareOrdinal(other, plugin) < 0 && t.ContainsKey(en)) return;
        }
        if (_ptrs.Remove(en, out var old)) _graveyard.Add(old);   // 旧指针作废（延迟释放）
        _table[en] = Encoding.UTF8.GetBytes(zh);
        _hashes.Add(FnvUtf8(en));
        InvalidateDerivedCaches();   // ⚠ 审查①：清 _idPtrs/_wsPtrs，并标记 _prefixIndex 待重建
    }

    /// <summary> 增量移除单条窗口译文（其它窗口表仍有同键时保留）。调用方须持有 _lock。
    /// ⚠ 2026-09-16：不再因 wiki/安装器占键而 return（优先级已调整为窗口表 > 安装器 > wiki）。 </summary>
    private void RemoveOneWindowEntry(string en)
    {
        foreach (var t in _windowSources.Values)
        {
            if (t.ContainsKey(en)) return; // 别的插件表还有同键
        }
        if (_ptrs.Remove(en, out var old)) _graveyard.Add(old);
        _table.Remove(en);
        // 不摘 _hashes：防止哈希碰撞误伤同哈希的其它键（多一次未命中，无害）
        // ⚠ 审查⑥：被删的键若在**安装器表 / wiki** 里也有译文，应**回退**到那个译文，而不是让界面变回英文。
        //   原实现无条件 `_table.Remove` → 通用译文一起消失（例：删掉某插件的 `Settings` 后，
        //   安装器表里的 `Settings` 本该接管，却用不上），要等下次 RebuildMerged（重载/改安装器表）才恢复。
        if (_installerSource.TryGetValue(en, out var fb))
        {
            _table[en] = Encoding.UTF8.GetBytes(fb);
            _hashes.Add(FnvUtf8(en));
        }
        else if (_wikiTerms is { Count: > 0 } && _wikiTerms.TryGetValue(en, out var fw))
        {
            _table[en] = Encoding.UTF8.GetBytes(fw);
            _hashes.Add(FnvUtf8(en));
        }
        InvalidateDerivedCaches();   // ⚠ 审查①
    }

    /// <summary> 清洗：去空白、去空值，并剔除 ImGui 内部 ID（<c>##</c> 开头，无显示文字）。
    /// ⚠ **保留「译文 == 原文」**（表示"保持原样/无需翻译"）；旧实现把它当噪音丢掉，
    /// 导致 URL/DPS 这类专有名词永远算未翻译（详见 MergeWindowEntries 的说明）。 </summary>
    private static Dictionary<string, string> Normalize(Dictionary<string, string> data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, zh) in data)
        {
            var k = en.Trim();
            var v = (zh ?? "").Trim();
            if (k.Length < 2 || v.Length == 0) continue;
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
            // ⚠ 黑名单检查放在**哈希命中之后**（2026-09-15 代码审查 H1 修正）：
            //   原实现放在预筛之前 → 渲染线程**每帧每条文字**都 `Encoding.UTF8.GetString`
            //   分配一次字符串，GC 压力随绘制文字量线性涨（且我的注释与代码相反，声称"只对可能命中的做"）。
            //   移到这里后，只有"表里真的有这个词"才查黑名单——**语义不变**（黑名单词既然拉黑，
            //   本来就不该有译文；若它因历史原因在表里，在此拦下即可），**分配降为命中才发生（≈0 次/帧）**。
            var bl = _isBlacklisted;

            // ① 整串匹配
            if (_hashes.Contains(h))
            {
                var s = Encoding.UTF8.GetString(p, n);
                if (bl != null)
                {
                    try { if (bl(s)) return 0; }
                    catch { /* 判定失败就按未拉黑处理 */ }
                }
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
                    // ② 路径同样要过黑名单（H1 同理：到这里才 GetString，不增加未命中时的分配）
                    if (bl != null)
                    {
                        try { if (bl(shown)) return 0; }
                        catch { /* 判定失败按未拉黑 */ }
                    }
                    var full = Encoding.UTF8.GetString(p, n);
                    if (_idPtrs.TryGetValue(full, out var cached)) return cached;
                    // 显示部分换成中文，其余（##ID 及其后）原样保留
                    // ⚠ 审查②（**中**）：`hashIdx` 是**字节**下标，而 `string.Substring` 要**字符**下标——
                    //   显示文字含非 ASCII（`Café`、`Pokémon`、`’`…）时两者不等 → 切错位置、丢掉一个 `#`
                    //   → `#id` 被当普通文字画到界面上，且控件 ID 变了（同窗口控件可能撞 ID、点击/状态互串）。
                    //   改用**字节切片**取尾部，与 hashIdx 同基准。
                    var tail = Encoding.UTF8.GetString(p + hashIdx, n - hashIdx);
                    var composed = Encoding.UTF8.GetString(zhBytes) + tail;
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
                        // ⚠ 审查④：①/②/④ 路径都有黑名单判定，**唯独这条③（首尾空白容错）漏了** →
                        //   `"\nURL"` 这类带首尾空白的串会**绕过黑名单**被替换，违背
                        //   OldDictionaryService 声明的"拉黑的词永远不要动它"。补上。
                        if (bl != null)
                        {
                            try { if (bl(core)) return 0; } catch { /* 判定失败按未拉黑 */ }
                        }
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

            // ④ 前缀模板匹配（动态插值文案，2026-09-18 加）：
            //    C# 插值串（$"...{name}..."）运行时拼出完整串（如 CharacterSync 的
            //    "The logged in character is All on 摩杜纳(FFXIV_CHR...)"），整串/##/空白路径都不命中；
            //    表里存模板静态前缀（Trim 后），此处要求"输入以键开头 且 键后一个字符是空格"
            //    （防 "SettingsXxx" 误伤 "Settings"），命中 → 译文 + 剩余原样（剩余自带前导空格）。
            //    同样走 _idPtrs 缓存：同一角色登录期内该串每帧重复，避免重复分配。
            if (_prefixIndex.Count > 0 || _prefixDirty)
            {
                // 审查①：增量写把 _prefixIndex 标记为脏 → 在此（已持 _lock）惰性重建一次
                if (_prefixDirty) { RebuildPrefixIndex(); _prefixDirty = false; }
                var preIdx = _prefixIndex;
                if (preIdx.TryGetValue(p[0], out var preList))
                {
                    for (int i = 0; i < preList.Length; i++)
                    {
                        var pe = preList[i];
                        var kl = pe.Key.Length;
                        if (n <= kl + 1 || p[kl] != (byte)' ') continue;
                        if (!BytesStartWith(p, pe.Key, kl)) continue;
                        if (bl != null)
                        {
                            // 审查⑤：原为 `break` → 会把**同首字节桶里后面的无关模板**一并跳过（拉黑一个模板
                            //   不该影响其它模板）；改 `continue` 只跳过这一条。
                            try { if (bl(pe.Text)) continue; } catch { }
                        }
                        var full = Encoding.UTF8.GetString(p, n);
                        if (_idPtrs.TryGetValue(full, out var cached3)) return cached3;
                        var remain = Encoding.UTF8.GetString(p + kl, n - kl);
                        var composed = Encoding.UTF8.GetString(pe.Zh) + remain;
                        var ptr5 = Marshal.StringToCoTaskMemUTF8(composed);
                        _idPtrs[full] = ptr5;
                        return ptr5;
                    }
                }
            }
            return 0;
        }
    }

    /// <summary> 是否 ASCII 空白（替换热路径用，避免调用 char.IsWhiteSpace 的开销/本地化差异）。 </summary>
    private static bool IsAsciiSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    /// <summary> 字节前缀比较（热路径，避免构造子串分配）。 </summary>
    private static unsafe bool BytesStartWith(byte* p, byte[] prefix, int len)
    {
        for (int i = 0; i < len; i++)
            if (p[i] != prefix[i]) return false;
        return true;
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
                        // 允许「译文==原文」：这是外部表给出的"保持原样"答案，丢掉会让该条永远算"缺失"（见 MergeWindowEntries 说明）
                        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh)) continue;
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
    /// 汇总缺口：已安装插件（本地 manifest）+【子开关开启时】仓库全部插件（含未安装，读本地缓存）。
    /// 调用方（StartupCheck / Mt.Start）无需关心来源——CollectMissing 内部按配置合并。
    /// </summary>
    public SortedDictionary<string, string> CollectMissing()
    {
        var missing = CollectInstalledMissing();
        if (_config.AutoExtractAllPlugins)
        {
            foreach (var (k, v) in CollectRepoMissing())
                missing[k] = v;
        }
        return missing;
    }

    /// <summary>
    /// 扫描已安装插件的清单（Name/Description/Punchline），收集对照表还没有翻译的条目（机翻 API 的输入）。
    /// </summary>
    private SortedDictionary<string, string> CollectInstalledMissing()
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

    /// <summary> 仓库清单缓存文件名（子开关 AutoExtractAllPlugins 用；后台下载，此处只读）。 </summary>
    private const string RepoCacheFileName = "pluginmaster_cache.json";
    /// <summary> 卫月官方仓库插件清单 URL（D17 staging；橙月兼容同结构）。 </summary>
    private const string RepoMasterUrl = "https://raw.githubusercontent.com/goatcorp/DalamudPluginsD17/main/staging/pluginmaster.json";

    /// <summary>
    /// 【子开关 AutoExtractAllPlugins】从本地缓存的仓库清单（pluginmaster_cache.json）收集**未安装**插件的
    /// Description/Punchline 缺口。**只读本地缓存、不联网**——下载由 EnsureRepoCacheAsync 在后台做，
    /// 避免阻塞游戏主线程（StartupCheck 在 OnFramework 主线程里跑）。
    /// 无缓存文件时返回空（下次后台下载完、下次启动检查才生效）。
    /// </summary>
    private SortedDictionary<string, string> CollectRepoMissing()
    {
        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var cachePath = Path.Combine(_configDir(), RepoCacheFileName);
            if (!File.Exists(cachePath)) return missing;
            var raw = File.ReadAllText(cachePath);
            var arr = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(raw);
            if (arr == null) return missing;
            var installed = GetInstalledPluginNames();
            foreach (var p in arr)
            {
                if (!p.TryGetValue("InternalName", out var inEl) || inEl.ValueKind != JsonValueKind.String) continue;
                var internalName = inEl.GetString();
                if (string.IsNullOrEmpty(internalName)) continue;
                if (installed.Contains(internalName)) continue;   // 已装的 CollectInstalledMissing 已管
                foreach (var field in new[] { "Description", "Punchline" })
                {
                    if (!p.TryGetValue(field, out var el) || el.ValueKind != JsonValueKind.String) continue;
                    var text = el.GetString()?.Trim();
                    if (string.IsNullOrEmpty(text) || text.Length < 2) continue;
                    if (TextHeuristics.HasCjk(text)) continue;
                    bool hasLetter = false;
                    foreach (var c in text) { if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') { hasLetter = true; break; } }
                    if (!hasLetter) continue;
                    lock (_lock)
                    {
                        if (!_table.ContainsKey(text)) missing[$"{internalName}｜{field}"] = text;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _appLog.Warn("[仓库缺口] 读本地缓存失败（下次后台下载后重试）：" + ex.Message);
        }
        return missing;
    }

    /// <summary>
    /// 【后台】拉取仓库清单到本地缓存（子开关开启时由 StartupCheck 触发，不在主线程做网络请求）。
    /// 24h 内已有缓存则跳过；走配置的代理（与源码提取同一套）。失败只记日志，不影响已装插件的翻译。
    /// 2026-09-18：改为**按卫月实际配置的仓库地址列表**拉（主库 + 启用的第三方），
    /// 不再硬编码官方 staging——否则和用户（橙月）实际用的仓库对不上。
    /// 每个仓库可能是 {Plugins:[...]} 对象或直接 [...] 数组，统一合并成数组存。
    /// </summary>
    public void EnsureRepoCacheAsync(IReadOnlyList<string> repoUrls)
    {
        var cachePath = Path.Combine(_configDir(), RepoCacheFileName);
        try
        {
            if (File.Exists(cachePath) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours < 24)
                return;   // 缓存还新鲜，不用重拉
        }
        catch { /* 取不到时间就当过期，重拉 */ }

        if (repoUrls == null || repoUrls.Count == 0)
        {
            _appLog.Warn("[仓库缺口] 没有可用仓库地址（未读到卫月配置），跳过预翻清单下载");
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var useProxy = _config.UseProxy && !string.IsNullOrWhiteSpace(_config.ProxyPort);
                var all = new List<Dictionary<string, JsonElement>>();
                // ⚠ 2026-09-18 全面审查：原用 `WebClient`（SYSLIB0014 已过时）且**无超时**——
                //   网络挂起时这个后台 Task 会长期存活（卸载后仍可能写缓存文件）。改用 HttpClient + 30s 超时。
                using var http = CreateRepoHttpClient(useProxy, _config.ProxyAddress, out var timeoutCts);
                using var _ = timeoutCts;

                foreach (var url in repoUrls)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    try
                    {
                        var json = http.GetStringAsync(url, timeoutCts.Token).GetAwaiter().GetResult();
                        using var doc = JsonDocument.Parse(json);
                        // 兼容 {Plugins:[...]} 和直接 [...] 两种结构
                        var arr = doc.RootElement.ValueKind == JsonValueKind.Object &&
                                  doc.RootElement.TryGetProperty("Plugins", out var pl)
                            ? pl : doc.RootElement;
                        if (arr.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            var d = new Dictionary<string, JsonElement>();
                            foreach (var prop in item.EnumerateObject())
                                d[prop.Name] = prop.Value.Clone();
                            all.Add(d);
                        }
                        _appLog.Info($"[仓库缺口] 已拉取仓库：{url}（{arr.GetArrayLength()} 个插件）");
                    }
                    catch (Exception exOne)
                    {
                        _appLog.Warn($"[仓库缺口] 拉取单个仓库失败（跳过）：{url} — {exOne.Message}");
                    }
                }

                if (all.Count == 0)
                {
                    _appLog.Warn("[仓库缺口] 所有仓库都没拉到有效内容");
                    return;
                }
                Directory.CreateDirectory(_configDir());
                File.WriteAllText(cachePath, JsonSerializer.Serialize(all, Indented));
                _appLog.Info($"[仓库缺口] 已合并 {repoUrls.Count} 个仓库、共 {all.Count} 个插件到本地缓存，下次启动检查可预翻未安装插件");
            }
            catch (Exception ex)
            {
                _appLog.Warn("[仓库缺口] 后台拉取仓库清单失败（不影响已装插件翻译）：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 为「拉取卫月仓库清单」创建带**超时**的 HttpClient（2026-09-18 全面审查：替换已过时的 `WebClient`）。
    /// ⚠ 原实现用 `WebClient`（SYSLIB0014）且**无超时** → 网络挂起时后台 Task 长期存活、卸载后仍可能写文件。
    /// 现在 30 秒超时；<paramref name="timeoutCts"/> 交调用方 `using`（同一超时窗口贯穿该次拉取的所有 URL）。
    /// </summary>
    private static HttpClient CreateRepoHttpClient(bool useProxy, string proxyAddress, out CancellationTokenSource timeoutCts)
    {
        timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handler = new HttpClientHandler();
        if (useProxy && !string.IsNullOrWhiteSpace(proxyAddress))
        {
            try
            {
                handler.Proxy = new WebProxy(proxyAddress);
                handler.UseProxy = true;
            }
            catch { /* 代理地址非法则退回直连 */ }
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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
                if (key.Length < 2 || val.Length == 0) continue;   // 允许「译文==原文」，见 MergeWindowEntries 说明
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
                .Where(k => !win.ContainsKey(k) && NeedsTranslation(k))
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
                // ⚠⚠ **必须允许「译文 == 原文」**：像 `URL` 这类专有名词，AI 会原样返回（这本身就是正确答案
                //    ="界面保持英文即可"）。旧实现在这里丢弃它 → 该条永远存不进译文表 → 进度永久停在
                //    "46/47 待翻译"，每次点机翻都重送一遍、AI 又返回原文、又被丢弃，形成死循环（2026-09-15
                //    由 Browsingway 的 `URL` 暴露）。同文不表示失败——**空译文才表示失败**（那才要留着重试）。
                if (key.Length < 2 || val.Length == 0) continue;
                // ⚠ **不覆盖「已有译文」**（2026-09-15 并发修复）：机翻是长任务（可能几分钟），期间用户
                //    可能用「全部预翻译」套了词典里更权威的译名，或手动改过某条。若机翻结束一把覆盖回去，
                //    用户刚做的编辑就白做了（且无提示）。故**已有译文优先**，机翻只补空缺。
                if (table.ContainsKey(key)) continue;
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
                    if (key.Length >= 2 && val.Length > 0 && table.ContainsKey(key) && table[key] == val)
                        ApplyOneWindowEntry(plugin, key, val);
                }
            }
        }
        return added;
    }

    /// <summary>
    /// **用词典覆盖已有译文**（`WindowReplaceWindow` 的「用词典刷新译文」）。
    ///
    /// ⚠ 与 <see cref="MergeWindowEntries"/> 的关键区别：那个**只补空缺**（避免机翻覆盖用户编辑），
    ///   这个**主动覆盖已存在的条目**——因为存在这样一个死角（2026-09-15 发现）：
    ///   某条被 AI 翻错后，即使你改进词典，`Prefill` 只作用于缺口、合并又跳过已有键
    ///   → **词典永远修不好它**，只能「还原英文」全删重来（代价极大）。
    ///   本方法让词典（人工维护，权威）能覆盖 AI 产出（尽力而为）。
    ///
    /// 仍然尊重更高优先级来源：`ApplyOneWindowEntry` 内部对 wiki/安装器表已有的键会让位
    /// （那两者在 RebuildMerged 时优先级本就更高），所以覆盖不会破坏优先级链。
    ///
    /// ⚠ **会覆盖"手动编辑过的"条目**——译文表里无法区分"AI 翻的"与"手改的"，这是本操作的
    ///   已知代价（"词典优先"正是它的目的）。故窗口侧做了 3 秒二次确认并在提示里写明。
    /// 返回（更新条数, 新增条数）。
    /// </summary>
    public (int Updated, int Added) OverwriteWindowEntries(string plugin, Dictionary<string, string> translations)
    {
        var updated = 0;
        var added = 0;
        lock (_lock)
        {
            if (!_windowSources.TryGetValue(plugin, out var table))
                _windowSources[plugin] = table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (en, zh) in translations)
            {
                var key = en.Trim();
                var val = (zh ?? "").Trim();
                if (key.Length < 2 || val.Length == 0) continue;
                if (table.TryGetValue(key, out var cur))
                {
                    if (cur == val) continue;          // 值相同，不算更新
                    table[key] = val;
                    ApplyOneWindowEntry(plugin, key, val);
                    updated++;
                }
                else
                {
                    table[key] = val;
                    ApplyOneWindowEntry(plugin, key, val);
                    added++;
                }
            }
            if (updated + added > 0) WriteWindowFile(plugin, table);
        }
        return (updated, added);
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
            else ApplyOneWindowEntry(plugin, en, zh);
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
        var need = candidates.Keys.Where(NeedsTranslation).ToList();
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
    /// 从给定候选里筛出**当前仍未被任何译文表覆盖**的那些（顺序保持）。
    /// 供机翻在**翻译过程中**重算队列：若某条在这期间已被「全部预翻译」或手动编辑解决，
    /// 就不必再花额度送翻（结果也会被"已有译文优先"丢弃）。
    /// </summary>
    public List<string> FilterStillMissing(IEnumerable<string> candidates)
    {
        var result = new List<string>();
        lock (_lock)
        {
            foreach (var raw in candidates)
            {
                var key = (raw ?? "").Trim();
                if (key.Length == 0) continue;
                var covered = false;
                foreach (var t in _windowSources.Values)
                {
                    if (t.ContainsKey(key)) { covered = true; break; }
                }
                if (!covered) result.Add(raw!);
            }
        }
        return result;
    }

    /// <summary>
    /// **删除文件（走系统回收站）**——删译文/删备份**一律用这个**，不要用 `File.Delete`。
    ///
    /// ⚠ 为什么（2026-09-15 真实事故 + 旧项目对照）：译文是**花 AI 额度换来的资产**，不是可随手重建的缓存。
    ///   原实现用 `File.Delete`（**不进回收站**）→ 用户点一次「一键还原英文」就**永久丢掉 416 条译文**，
    ///   连恢复的余地都没有。旧项目 `BackupManager` 早就用
    ///   `FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`（`Microsoft.VisualBasic.FileIO`），
    ///   本处对齐该做法：**误删可从回收站还原**。
    /// 文件不存在 / 回收站不可用（如网络盘）时回退普通删除，绝不因此抛异常中断流程。
    /// </summary>
    private static void DeleteToRecycleBin(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            try { File.Delete(path); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary> 目录送回收站删除（源码仓库等）。 </summary>
    private static void DeleteDirToRecycleBin(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            try { Directory.Delete(path, recursive: true); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// **还原前的自动备份**：把当前译文目录整体复制到
    /// <c>&lt;数据目录&gt;\窗口翻译_还原备份\&lt;时间戳&gt;\</c>，返回备份目录（无内容或失败返回 ""）。
    ///
    /// ⚠ 双保险：删除本身已走回收站（见 <see cref="DeleteToRecycleBin"/>），此处再留一份**插件内的**快照，
    ///   便于直接对照找回（回收站容易被清空）。备份目录由用户自行清理，不自动删。
    /// </summary>
    public string BackupWindowTables()
    {
        try
        {
            var src = WindowTableDir;
            if (!Directory.Exists(src)) return "";
            var files = Directory.GetFiles(src, "*.json");
            if (files.Length == 0) return "";
            var dst = Path.Combine(_configDir(), WindowBackupDirName, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dst);
            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true); }
                catch { /* 单个文件拷不动就跳过，尽力而为 */ }
            }
            // 只保留最近 10 份，避免无限堆积
            try
            {
                var root = Path.Combine(_configDir(), WindowBackupDirName);
                foreach (var old in Directory.GetDirectories(root).OrderByDescending(x => x).Skip(10))
                    Directory.Delete(old, recursive: true);
            }
            catch { /* 清理失败无所谓 */ }
            _appLog.Info($"[替换] 还原前已备份 {files.Length} 个译文文件 → {dst}");
            return dst;
        }
        catch (Exception ex)
        {
            _appLog.Warn("[替换] 还原前备份失败（继续执行还原）：" + ex.Message);
            return "";
        }
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
                DeleteToRecycleBin(file);   // ⚠ 走回收站，别用 File.Delete（见方法注释）
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

    /// <summary> 备份单个插件的译文文件到 窗口翻译_还原备份\single_时间戳\。无译文文件时返回空串。 </summary>
    public string BackupWindowTable(string plugin)
    {
        var src = Path.Combine(WindowTableDir, $"{plugin}.json");
        if (!File.Exists(src)) return "";
        var dst = Path.Combine(_configDir(), WindowBackupDirName, "single_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dst);
        File.Copy(src, Path.Combine(dst, $"{plugin}.json"), overwrite: true);
        return dst;
    }

    /// <summary>
    /// **彻底清空全部译文**（「还原卫月/彻底还原」用）：先备份全部译文 → 逐个删译文文件（回收站）→ 重载生效表。
    /// 候选清单保留（题目仍在，之后还能重新翻译）。返回（受影响插件数, 删除条数, 备份目录）。
    /// </summary>
    public (int Affected, int Removed, string Backup) PurgeAllWindowTranslations()
    {
        var backup = BackupWindowTables();
        var totalRemoved = 0;
        var affected = 0;
        foreach (var (name, _, _) in GetWindowPlugins())
        {
            var n = ClearPluginTranslations(name);
            if (n > 0) { affected++; totalRemoved += n; }
        }
        Reload();
        _appLog.Info($"[还原] 彻底清空全部译文：{affected} 个插件、{totalRemoved} 条（备份：{backup}）");
        return (affected, totalRemoved, backup);
    }

    /// <summary> 已安装插件内部名集合（launcher\installedPlugins + launcher\devPlugins 目录名）。 </summary>
    public HashSet<string> GetInstalledPluginNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        foreach (var rootName in new[] { "installedPlugins", "devPlugins" })
        {
            try
            {
                var root = Path.Combine(launcherDir ?? "", rootName);
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    // ⚠ 卫月/橙月卸载可能留下**空目录**（manifest/文件已删但目录未清）——空壳不算"已安装"，
                    //   否则 AutoPurge 会一直认为插件还装、不清理其翻译资产（2026-09-18 用户报 Craftimizer）。
                    try
                    {
                        if (Directory.EnumerateFileSystemEntries(dir).Any())
                            names.Add(Path.GetFileName(dir));
                    }
                    catch { /* 目录读不到按空处理（不加入） */ }
                }
            }
            catch { /* 目录读不到就跳过该来源 */ }
        }
        return names;
    }

    /// <summary>
    /// **自动清理已卸载插件的翻译资产**（2026-09-17 用户要求）：
    /// 候选插件（文案扫描）已不在「已安装」列表（installedPlugins/devPlugins 目录）→
    /// 备份译文 → 删译文（回收站）→ 删候选文件（回收站）→ 删源码仓库目录（回收站）。
    /// 返回本次清理的插件列表（空 = 无清理）。译文有备份 + 回收站，误删可找回；候选/源码可重新提取。
    /// </summary>
    public List<string> AutoPurgeUninstalled()
    {
        var installed = GetInstalledPluginNames();
        if (installed.Count == 0) return new();        // 两个目录都读不到（异常状态）就别乱删
        var removed = new List<string>();
        var dir = Path.Combine(_configDir(), CandidateDirName);
        if (!Directory.Exists(dir)) return removed;
        foreach (var plugin in ListCandidatePlugins())
        {
            if (installed.Contains(plugin)) continue;  // 还装着 → 不动
            try
            {
                // ① 译文先备份（防误删/临时卸载后重装可找回）
                var src = Path.Combine(WindowTableDir, $"{plugin}.json");
                if (File.Exists(src))
                {
                    var dst = Path.Combine(_configDir(), WindowBackupDirName,
                        "uninstalled_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(dst);
                    File.Copy(src, Path.Combine(dst, Path.GetFileName(src)), overwrite: true);
                }
                // ② 删译文（内存表 + 文件，走回收站）
                ClearPluginTranslations(plugin);
                // ③ 删候选文件（文案扫描\<插件>_*.json，走回收站）
                foreach (var f in Directory.EnumerateFiles(dir, plugin + "_*.json"))
                    DeleteToRecycleBin(f);
                // ④ 删源码仓库目录（源码仓库\作者__插件名，走回收站）
                var repoRoot = Path.Combine(_configDir(), "源码仓库");
                if (Directory.Exists(repoRoot))
                {
                    foreach (var d in Directory.GetDirectories(repoRoot))
                    {
                        if (d.EndsWith("__" + plugin, StringComparison.OrdinalIgnoreCase))
                            DeleteDirToRecycleBin(d);
                    }
                }
                InvalidateCandidateCache();
                lock (_lock) _pluginNameCache = null;  // 列表缓存失效 → 插件翻译窗口不再显示
                removed.Add(plugin);
                _appLog.Info($"[替换] 自动清理已卸载插件 {plugin}（译文/候选/源码已删，译文已备份）");
            }
            catch (Exception ex)
            {
                _appLog.Warn($"[替换] 自动清理已卸载插件 {plugin} 失败：{ex.Message}");
            }
        }
        return removed;
    }

    private readonly List<FileSystemWatcher> _uninstallWatchers = new();
    private long _lastWatcherPurgeMs;

    /// <summary>
    /// **行为联动**（2026-09-17 用户要求）：监听 launcher 的 installedPlugins / devPlugins 目录，
    /// 用户在卫月插件安装器里**卸载/删除**插件时，目录立刻变化 → 秒级触发 AutoPurgeUninstalled。
    /// 60 秒轮询（CheckExternalChanges）保留作兜底，两者互不冲突（清理幂等）。
    /// </summary>
    public void StartUninstallWatch()
    {
        try
        {
            if (_uninstallWatchers.Count > 0) return;
            var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
            foreach (var rootName in new[] { "installedPlugins", "devPlugins" })
            {
                var root = Path.Combine(launcherDir ?? "", rootName);
                if (!Directory.Exists(root)) continue;
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                watcher.Deleted += (_, _) => OnPluginDirChanged();
                watcher.Renamed += (_, _) => OnPluginDirChanged();
                watcher.Created += (_, _) => OnPluginDirChanged();
                _uninstallWatchers.Add(watcher);
            }
            _appLog.Info($"[替换] 行为联动已启用：卫月安装器里卸载插件 → 本插件自动清理其翻译资产（监听 {_uninstallWatchers.Count} 个目录）");
        }
        catch (Exception ex)
        {
            _appLog.Warn("[替换] 行为联动监听启动失败（60 秒轮询兜底仍有效）：" + ex.Message);
        }
    }

    private void OnPluginDirChanged()
    {
        var now = Environment.TickCount64;
        if (now - _lastWatcherPurgeMs < 5000) return;   // 合并 5 秒内的事件风暴（安装器批量操作）
        _lastWatcherPurgeMs = now;
        var purged = AutoPurgeUninstalled();
        if (purged.Count > 0)
            _windowDirStamp = "";   // 译文/候选变了，下次指纹检查强制重读
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
            // ⚠ 2026-09-18 全面审查：**不能**排除 `k == v`。本项目刻意保留“译文==原文”
            //   （含义="保持英文"，如 URL/DPS；见 MergeWindowEntries 说明），导出侧 Save 也是保留的。
            //   若这里丢弃 → **导出的翻译包再导入时会丢失这类条目** → 换机器/分享后它们
            //   重新变回"待翻译"死循环（正是当初修 URL 时要避免的现象）。
            if (k.Length >= 1 && v.Length > 0) result[k] = v;
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
            var seenValid = 0;   // 结构上像 FDCN 条目的数量（与"是否新增"无关，用于区分"格式不符"与"全已存在"）
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
                        // 允许「译文==原文」（= 外部表判定"保持原样"），见 MergeWindowEntries 说明
                        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(zh)) continue;
                        seenValid++;   // 只要有一条**结构合法**的 FDCN 条目，就说明"这是 FDCN 格式"
                        if (_installerSource.ContainsKey(en!)) continue;
                        _installerSource[en!] = zh!;
                        added++;
                    }
                }
            }
            if (added > 0) { RebuildMerged(); Save(); }
            // ⚠ 返回值必须区分两种情况（2026-09-18 全面审查修正——上一次的 L3 修法引入了回归）：
            //   · **一条合法条目都没有**（空对象 `{}` / 结构不符）→ -1 = "不是 FDCN 格式"，
            //     让调用方继续尝试通用格式解析（这是 L3 的原意）。
            //   · **格式对、但全部已存在**（对同一份表点两次导入）→ 返回 0 = "成功，新增 0 条"。
            //     上次只写 `added > 0 ? added : -1`，把这种情况也判成"格式不符" → 调用方继续走通用解析
            //     → 解析出空表 → 抛"无法识别的文件格式"，**重复导入反而报错**（已改回用 seenValid 判定）。
            return seenValid > 0 ? added : -1;
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
        foreach (var w in _uninstallWatchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* 尽力而为 */ }
        }
        _uninstallWatchers.Clear();
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
