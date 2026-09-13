using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// ImGui 文字绘制层钩子（所有插件 UI 文字的公共必经之路，本项目成败点）。
/// 挂接 cimgui 的三个原生导出：
///   ImDrawList_AddText —— 全部文字绘制的终点（ImGui::RenderText 最终都调它）；
///   igBegin / igEnd    —— 窗口进入/退出，用于把文字归属到所在窗口（每插件一份对照表的归属依据）。
/// MVP 为只读采集：屏幕上实际渲染的、清单里没有的英文收集到待翻译清单；不做任何替换。
/// </summary>
public sealed unsafe class ImGuiHookService : IDisposable
{
    // ── cimgui 原生函数签名（x64 统一调用约定；C++ bool 返回按单字节，用 byte 接最稳） ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(nint name, nint pOpen, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndDelegate();

    [StructLayout(LayoutKind.Sequential)]
    private struct ImVec2
    {
        public float X;
        public float Y;
    }

    // cimgui 全参重载：ImDrawList_AddText(self, font, font_size, pos, col, text_begin, text_end, wrap_width, cpu_fine_clip_rect)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AddTextFullDelegate(
        nint self, nint font, float fontSize, ImVec2 pos, uint col,
        nint textBegin, nint textEnd, float wrapWidth, nint cpuFineClipRect);

    // cimgui：igTextUnformatted(text_begin, text_end)——托管侧 Text/TextWrapped 等的底层必经桩（兜底钩子用）
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(nint textBegin, nint textEnd);

    // ImGuiWindowFlags 内部旗标（imgui.h，多年未变）：子窗口/提示/弹出/模态都不算「顶层窗口」
    private const uint FlagChild = 1u << 24;
    private const uint FlagTooltip = 1u << 25;
    private const uint FlagPopup = 1u << 26;
    private const uint FlagModal = 1u << 27;
    private const uint FlagNonTop = FlagChild | FlagTooltip | FlagPopup | FlagModal;

    private const int MaxNameLen = 512;
    private const int MaxTextLen = 2048;
    private const int MaxEntries = 30000;

    /// <summary> 待翻译清单文件名（沿用旧项目「_未翻译.json」管线格式：英文键 → 空中文值）。 </summary>
    public const string FlatFileName = "全部界面_未翻译.json";
    /// <summary> 窗口归属明细文件名（窗口名 → 英文清单，人工核对/按插件分组用）。 </summary>
    public const string WindowMapFileName = "采集归属.json";

    /// <summary> 本插件自身窗口标题前缀：这些窗口里的文字不再采集（否则查看清单会把自己显示的英文再采进去）。 </summary>
    private static readonly byte[] OwnWindowPrefix = Encoding.UTF8.GetBytes("插件界面汉化");

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly Func<string> _configDir;

    private Hook<BeginDelegate>? _beginHook;
    private Hook<EndDelegate>? _endHook;
    private Hook<AddTextFullDelegate>? _addTextHook;
    private Hook<TextUnformattedDelegate>? _textHook;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nint dwLength);

    /// <summary> 槽地址可读性校验（MEM_COMMIT 且非 PAGE_NOACCESS）——读野指针在 .NET 里会直接崩进程，必须先查。 </summary>
    private static bool IsReadableMemory(nint addr)
    {
        try
        {
            if (VirtualQuery(addr, out var mbi, (nint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
            return mbi.State == 0x1000 && (mbi.Protect & 0xFF) != 0x01;
        }
        catch
        {
            return false;
        }
    }

    private readonly object _lock = new();

    // ── 窗口栈跟踪（Begin/End 严格配对；只存哈希与是否顶层，零字符串分配热路径） ──
    private readonly List<(ulong Hash, bool TopLevel)> _winStack = new();
    private readonly Dictionary<ulong, string> _winNames = new();
    private readonly HashSet<ulong> _ownWins = new();
    private ulong _curTopHash;

    // ── 采集数据 ──
    private readonly Dictionary<string, string> _flat = new();               // 英文 → ""（管线格式）
    private readonly Dictionary<string, HashSet<string>> _byWindow = new();  // 窗口名 → 英文集
    private bool _dirty;
    private bool _capWarned;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _collecting;
    private long _sessionNew;

    /// <summary> 钩子是否全部挂接成功。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 钩子状态描述（挂接成功/失败原因，主窗口直接展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    /// <summary> 采集开关（关闭时自动保存一次清单）。 </summary>
    public bool Collecting
    {
        get => _collecting;
        set
        {
            if (_collecting == value) return;
            _collecting = value;
            if (!value) Save();
            _appLog.Info(value ? "[采集] 开始采集（只读，不改任何文字）" : "[采集] 停止采集，清单已保存");
        }
    }

    public int TotalCount { get { lock (_lock) return _flat.Count; } }
    public int WindowCount { get { lock (_lock) return _byWindow.Count; } }
    public long SessionNewCount { get { lock (_lock) return _sessionNew; } }

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop, Func<string> configDir)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _configDir = configDir;
        Load();
        InstallHooks();
    }

    // ═══════════════════════ 钩子安装 ═══════════════════════

    private void InstallHooks()
    {
        try
        {
            if (!TryFindCimgui(out var baseAddr, out var moduleName, out var moduleSize))
            {
                HookStatus = "未找到 cimgui 模块（ImGui 原生库），钩子未挂接";
                var modules = string.Join(", ", SafeModuleNames().Take(60));
                _appLog.Error($"[钩子] {HookStatus}。已加载模块：{modules}");
                return;
            }

            // cimgui 对 AddText 两个重载用后缀命名：全参重载（ImGui::RenderText 的必经之路）= _FontPtr；
            // 裸名 ImDrawList_AddText 不存在（早前二进制 grep 被后缀名子串误报，实测 26-09-11-01 与 dev 一致）
            var addTextName = "ImDrawList_AddText_FontPtr";
            var addTextAddr = GetExport(baseAddr, addTextName);
            if (addTextAddr == 0)
            {
                addTextName = "ImDrawList_AddText"; // 未来 cimgui 改名的兜底
                addTextAddr = GetExport(baseAddr, addTextName);
            }
            var beginAddr = GetExport(baseAddr, "igBegin");
            var endAddr = GetExport(baseAddr, "igEnd");
            if (addTextAddr == 0 || beginAddr == 0 || endAddr == 0)
            {
                HookStatus = $"cimgui（{moduleName}）缺少导出函数，钩子未挂接";
                _appLog.Error($"[钩子] {HookStatus}：{addTextName}={addTextAddr:x} igBegin={beginAddr:x} igEnd={endAddr:x}");
                return;
            }

            // 关键：cimgui 的导出全是极小的转发桩（实测 igBegin/igEnd 相邻仅 16 字节），ImGui 内部 C++
            // 渲染文字时直调真实函数体、不经过导出桩——直接挂桩只能拦到极少数直接 P/Invoke 该导出的调用
            //（此前曾因此采到 0 条）。顺着桩首的跳转指令解析出真实 C++ 函数体再挂钩。
            _appLog.Info($"[钩子] 导出桩字节：{addTextName}={Hex(addTextAddr, 16)} igBegin={Hex(beginAddr, 16)}");
            var addTextHookAddr = addTextAddr;
            var realBody = TryResolveFunctionBody(addTextAddr, baseAddr, moduleSize, out var how);
            if (realBody != 0)
            {
                addTextHookAddr = realBody;
                _appLog.Info($"[钩子] {addTextName} 导出桩 {addTextAddr:x} → 真实函数体 {realBody:x}（{how}）");
            }
            else
            {
                // 解析失败：AddText 桩不在内部渲染路径上，改挂 igTextUnformatted 桩兜底——
                // 托管侧所有 Text/TextWrapped/TextColored 等调用都必经该桩；按钮/标签等走各自导出，可能漏采
                var textStub = GetExport(baseAddr, "igTextUnformatted");
                if (textStub != 0)
                {
                    _textHook = _interop.HookFromAddress<TextUnformattedDelegate>(textStub, TextUnformattedDetour, IGameInteropProvider.HookBackend.Automatic);
                    _textHook.Enable();
                    _appLog.Warn("[钩子] AddText 真实函数体解析失败，已改挂 igTextUnformatted 桩兜底（Text 类可采集，按钮/复选框等标签可能漏采）");
                }
                else
                {
                    _appLog.Error("[钩子] AddText 解析失败且 igTextUnformatted 导出不存在，文字采集不可用");
                }
            }

            _addTextHook = _interop.HookFromAddress<AddTextFullDelegate>(addTextHookAddr, AddTextDetour, IGameInteropProvider.HookBackend.Automatic);
            _beginHook = _interop.HookFromAddress<BeginDelegate>(beginAddr, BeginDetour, IGameInteropProvider.HookBackend.Automatic);
            _endHook = _interop.HookFromAddress<EndDelegate>(endAddr, EndDetour, IGameInteropProvider.HookBackend.Automatic);
            _addTextHook.Enable();
            _beginHook.Enable();
            _endHook.Enable();

            Hooked = true;
            var mode = realBody != 0 ? "AddText 真实函数体" : _textHook != null ? "igTextUnformatted 兜底" : "AddText 导出桩";
            HookStatus = $"已挂接 {moduleName}：{mode} / igBegin / igEnd";
            _appLog.Info($"[钩子] {HookStatus}");
        }
        catch (Exception ex)
        {
            HookStatus = "钩子挂接失败：" + ex.Message;
            _appLog.Error("[钩子] 挂接失败：" + ex);
        }
    }

    private static bool TryFindCimgui(out nint baseAddr, out string moduleName, out int moduleSize)
    {
        try
        {
            var self = Process.GetCurrentProcess();
            self.Refresh();
            foreach (ProcessModule m in self.Modules)
            {
                if (m.ModuleName.StartsWith("cimgui", StringComparison.OrdinalIgnoreCase))
                {
                    baseAddr = m.BaseAddress;
                    moduleName = m.ModuleName;
                    moduleSize = m.ModuleMemorySize;
                    return true;
                }
            }
        }
        catch
        {
            /* 模块枚举失败则退回按名加载 */
        }
        if (NativeLibrary.TryLoad("cimgui", out var handle))
        {
            baseAddr = handle;
            moduleName = "cimgui.dll";
            moduleSize = 0; // 拿不到大小时跳过越界校验
            return true;
        }
        baseAddr = 0;
        moduleName = "";
        moduleSize = 0;
        return false;
    }

    /// <summary>
    /// 解析 cimgui 导出桩背后的真实 C++ 函数体。桩是同签名尾调用转发，实测形态：
    /// E9 rel32（直跳）、FF 24 25 abs32（经绝对地址槽跳，槽里存目标，本机 cimgui 即此形态）、
    /// 兼容 FF 25 rel32（RIP 相对槽）。槽读取前用 VirtualQuery 校验可读，防野指针崩进程。
    /// </summary>
    private static nint TryResolveFunctionBody(nint stub, nint moduleBase, int moduleSize, out string how)
    {
        how = "";
        try
        {
            var p = (byte*)stub;
            nint target;
            if (p[0] == 0xE9)
            {
                target = stub + 5 + *(int*)(p + 1);
                how = "jmp rel32";
            }
            else if (p[0] == 0xFF && p[1] == 0x25)
            {
                if (!TryReadPointer(stub + 6 + *(int*)(p + 2), out target)) return 0;
                how = "jmp [rip+rel32]";
            }
            else if (p[0] == 0xFF && p[1] == 0x24 && p[2] == 0x25)
            {
                var slot = (nint)(uint)*(int*)(p + 3); // 绝对地址槽（按无符号 32 位地址）
                if (!TryReadPointer(slot, out target)) return 0;
                how = "jmp [abs32]";
            }
            else
            {
                return 0;
            }
            if (target == 0) return 0;
            if (moduleSize > 0 && target >= moduleBase && target < moduleBase + moduleSize)
                return target; // 目标在本模块内，最可信
            // 目标在模块外：实测（2026-09-14 日志）FontPtr 桩的 abs32 槽给出的地址在别的模块里，
            // 挂上去永远拦不到调用 → 一律视为解析失败走兜底；顺带记下目标属于哪个模块，帮定位这个槽的真身
            how += $"（目标在模块外：{ModuleOf(target)}，弃用）";
            return 0;
        }
        catch
        {
            how = "";
            return 0;
        }
    }

    private static bool TryReadPointer(nint slot, out nint value)
    {
        value = 0;
        if (!IsReadableMemory(slot)) return false;
        value = *(nint*)slot;
        return value != 0;
    }

    /// <summary> 地址所属模块名（诊断日志用；找不到返回「未知模块」）。 </summary>
    private static string ModuleOf(nint addr)
    {
        try
        {
            var self = Process.GetCurrentProcess();
            self.Refresh();
            foreach (ProcessModule m in self.Modules)
            {
                if (addr >= m.BaseAddress && addr < m.BaseAddress + m.ModuleMemorySize)
                    return m.ModuleName;
            }
        }
        catch { /* 枚举失败不影响主流程 */ }
        return "未知模块";
    }

    /// <summary> 导出桩前若干字节的十六进制（诊断日志用）。 </summary>
    private static string Hex(nint addr, int count)
        => string.Join(" ", Enumerable.Range(0, count).Select(i => ((byte*)addr)[i].ToString("X2")));

    private static nint GetExport(nint baseAddr, string name)
        => NativeLibrary.TryGetExport(baseAddr, name, out var addr) ? addr : 0;

    private static IEnumerable<string> SafeModuleNames()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            self.Refresh();
            return self.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
        }
        catch (Exception ex)
        {
            return new[] { "<模块枚举失败：" + ex.Message + ">" };
        }
    }

    // ═══════════════════════ 原生钩子（都在渲染线程高频调用，热路径只做字节扫描） ═══════════════════════

    private byte BeginDetour(nint name, nint pOpen, uint flags)
    {
        var r = _beginHook!.Original(name, pOpen, flags);
        try { TrackBegin(name, flags); } catch { /* 钩子内异常绝不外抛 */ }
        return r;
    }

    private void TrackBegin(nint name, uint flags)
    {
        if (name == 0) return;
        var p = (byte*)name;
        var len = 0;
        while (len < MaxNameLen && p[len] != 0) len++;
        if (len == 0) return;

        var own = false;
        if (len >= OwnWindowPrefix.Length)
        {
            own = true;
            for (var i = 0; i < OwnWindowPrefix.Length; i++)
            {
                if (p[i] != OwnWindowPrefix[i]) { own = false; break; }
            }
        }
        var h = Fnv1a(p, len);

        // 窗口标题一并采集：兜底模式下标题走不到文字钩子；主模式下也无害（去重兜底）。
        // 注意此时栈还没压入本窗口，归属就是「正在 Begin 的这个窗口」，所以 own 判定用本窗口自身。
        if (_collecting && !own)
        {
            try
            {
                var shown = len; // 标题显示部分截到 ## 为止（### 之后是 ID 后缀，不是显示文字）
                for (var i = 0; i + 1 < len; i++)
                {
                    if (p[i] == (byte)'#' && p[i + 1] == (byte)'#') { shown = i; break; }
                }
                CollectBytes(p, shown, own);
            }
            catch { /* 钩子内异常绝不外抛 */ }
        }

        lock (_lock)
        {
            if (!_winNames.ContainsKey(h) && _winNames.Count < 4096)
                _winNames[h] = Encoding.UTF8.GetString(p, len);
            if (own) _ownWins.Add(h);
            var topLevel = (flags & FlagNonTop) == 0;
            _winStack.Add((h, topLevel));
            if (topLevel || _curTopHash == 0) _curTopHash = h;
        }
    }

    private void EndDetour()
    {
        _endHook!.Original();
        try
        {
            lock (_lock)
            {
                if (_winStack.Count == 0) return;
                _winStack.RemoveAt(_winStack.Count - 1);
                // 回退到栈里最近一个顶层窗口（提示/弹出/子窗口结束后回到所属主窗口）
                _curTopHash = 0;
                for (var i = _winStack.Count - 1; i >= 0; i--)
                {
                    _curTopHash = _winStack[i].Hash;
                    if (_winStack[i].TopLevel) break;
                }
            }
        }
        catch { /* 同上 */ }
    }

    private void AddTextDetour(
        nint self, nint font, float fontSize, ImVec2 pos, uint col,
        nint textBegin, nint textEnd, float wrapWidth, nint cpuFineClipRect)
    {
        if (_collecting && textBegin != 0)
        {
            try { Collect(textBegin, textEnd); } catch { /* 钩子内异常绝不外抛 */ }
        }
        _addTextHook!.Original(self, font, fontSize, pos, col, textBegin, textEnd, wrapWidth, cpuFineClipRect);
    }

    /// <summary> 兜底钩子：igTextUnformatted 桩（托管侧 Text 类调用的必经桩）。 </summary>
    private void TextUnformattedDetour(nint textBegin, nint textEnd)
    {
        if (_collecting && textBegin != 0)
        {
            try { Collect(textBegin, textEnd); } catch { /* 钩子内异常绝不外抛 */ }
        }
        _textHook!.Original(textBegin, textEnd);
    }

    /// <summary> 由指针区间计算采集长度并进入采集判定。 </summary>
    private void Collect(nint textBegin, nint textEnd)
    {
        var p = (byte*)textBegin;
        var n = 0;
        if (textEnd != 0)
        {
            n = (int)(textEnd - textBegin);
            if (n <= 0) return;
            if (n > MaxTextLen) n = MaxTextLen;
        }
        else
        {
            while (n < MaxTextLen && p[n] != 0) n++;
        }
        CollectBytes(p, n, false, checkCurTop: true);
    }

    /// <summary>
    /// 采集判定核心：纯可打印 ASCII、至少含一个字母、非本插件窗口 → 入清单。
    /// isOwnText：调用方已确认属于本插件窗口（如自身标题）直接跳过；
    /// checkCurTop：文字归属当前顶层窗口时，本插件窗口在前台则跳过（防止查看清单时把回显英文再采进去）。
    /// </summary>
    private void CollectBytes(byte* p, int n, bool isOwnText, bool checkCurTop = false)
    {
        if (n < 2) return;
        if (n > MaxTextLen) n = MaxTextLen;

        var hasLetter = false;
        for (var i = 0; i < n; i++)
        {
            var b = p[i];
            if (b < 0x20 || b > 0x7E) return; // 含非可打印 ASCII（中文/日文/控制符）→ 不采集；本插件界面纯中文，天然被过滤
            if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')) hasLetter = true;
        }
        if (!hasLetter) return; // 纯数字/符号（如 "100"、"1/3"）不是待翻译文案

        var s = Encoding.UTF8.GetString(p, n);
        lock (_lock)
        {
            if (isOwnText) return;
            if (checkCurTop && _ownWins.Contains(_curTopHash)) return;
            if (_flat.ContainsKey(s)) return;
            if (_flat.Count >= MaxEntries)
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    _appLog.Warn($"[采集] 待翻译清单已达上限 {MaxEntries} 条，停止新增");
                }
                return;
            }
            _flat[s] = "";
            var win = _curTopHash != 0 && _winNames.TryGetValue(_curTopHash, out var w) ? w : "（无窗口）";
            if (!_byWindow.TryGetValue(win, out var set))
                _byWindow[win] = set = new HashSet<string>();
            set.Add(s);
            _dirty = true;
            _sessionNew++;
        }
    }

    private static ulong Fnv1a(byte* p, int len)
    {
        ulong h = 14695981039346656037UL;
        for (var i = 0; i < len; i++)
        {
            h ^= p[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    // ═══════════════════════ 清单载入/保存/查询 ═══════════════════════

    private void Load()
    {
        try
        {
            var dir = _configDir();
            Directory.CreateDirectory(dir);

            var flatPath = Path.Combine(dir, FlatFileName);
            if (File.Exists(flatPath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(flatPath));
                if (data != null)
                {
                    foreach (var k in data.Keys)
                    {
                        if (!string.IsNullOrEmpty(k)) _flat[k] = "";
                    }
                }
            }

            var mapPath = Path.Combine(dir, WindowMapFileName);
            if (File.Exists(mapPath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(mapPath));
                if (data != null)
                {
                    foreach (var (win, list) in data)
                    {
                        var set = _byWindow.TryGetValue(win, out var s) ? s : _byWindow[win] = new HashSet<string>();
                        foreach (var item in list)
                        {
                            if (_flat.ContainsKey(item)) set.Add(item); // 只归属清单里仍存在的条目
                        }
                    }
                }
            }

            if (_flat.Count > 0)
                _appLog.Info($"[采集] 已载入既有清单：{_flat.Count} 条（{FlatFileName}）");
        }
        catch (Exception ex)
        {
            _appLog.Error("[采集] 清单载入失败：" + ex.Message);
        }
    }

    /// <summary> 立即保存清单（未变则跳过）。 </summary>
    public void Save()
    {
        Dictionary<string, string> flatCopy;
        Dictionary<string, string[]> mapCopy;
        lock (_lock)
        {
            if (!_dirty && _lastSave != DateTime.MinValue) return;
            flatCopy = new Dictionary<string, string>(_flat);
            mapCopy = _byWindow.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            _dirty = false;
            _lastSave = DateTime.Now;
        }
        try
        {
            var dir = _configDir();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FlatFileName),
                JsonSerializer.Serialize(flatCopy, JsonFile.Indented), Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, WindowMapFileName),
                JsonSerializer.Serialize(mapCopy, JsonFile.Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _appLog.Error("[采集] 清单保存失败：" + ex.Message);
        }
    }

    /// <summary> 周期保存（框架线程调用，清单有变化且距上次保存超 60 秒才落盘）。 </summary>
    public void TickSave()
    {
        if (!_dirty) return;
        if ((DateTime.Now - _lastSave).TotalSeconds < 60) return;
        Save();
    }

    /// <summary> 窗口摘要（按条数降序）。 </summary>
    public List<(string Window, int Count)> WindowSummaries()
    {
        lock (_lock)
        {
            return _byWindow
                .Select(kv => (kv.Key, kv.Value.Count))
                .OrderByDescending(t => t.Item2)
                .ThenBy(t => t.Key, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary> 某窗口采集到的英文（字母序）。 </summary>
    public List<string> StringsOfWindow(string window)
    {
        lock (_lock)
        {
            return _byWindow.TryGetValue(window, out var s)
                ? s.OrderBy(x => x, StringComparer.Ordinal).ToList()
                : new List<string>();
        }
    }

    /// <summary> 清空全部采集数据并保存。 </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _flat.Clear();
            _byWindow.Clear();
            _dirty = true;
            _sessionNew = 0;
            _capWarned = false;
        }
        Save();
        _appLog.Info("[采集] 已清空全部采集数据");
    }

    public void Dispose()
    {
        // 先摘钩子再保存，避免保存期间又有采集写入
        try { _addTextHook?.Dispose(); } catch { }
        try { _textHook?.Dispose(); } catch { }
        try { _beginHook?.Dispose(); } catch { }
        try { _endHook?.Dispose(); } catch { }
        _addTextHook = null;
        _textHook = null;
        _beginHook = null;
        _endHook = null;
        Hooked = false;
        Save();
    }
}
