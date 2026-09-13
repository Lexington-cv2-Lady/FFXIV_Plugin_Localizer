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
            if (!TryFindCimgui(out var baseAddr, out var moduleName))
            {
                HookStatus = "未找到 cimgui 模块（ImGui 原生库），钩子未挂接";
                var modules = string.Join(", ", SafeModuleNames().Take(60));
                _appLog.Error($"[钩子] {HookStatus}。已加载模块：{modules}");
                return;
            }

            var addTextAddr = GetExport(baseAddr, "ImDrawList_AddText");
            var beginAddr = GetExport(baseAddr, "igBegin");
            var endAddr = GetExport(baseAddr, "igEnd");
            if (addTextAddr == 0 || beginAddr == 0 || endAddr == 0)
            {
                HookStatus = $"cimgui（{moduleName}）缺少导出函数，钩子未挂接";
                _appLog.Error($"[钩子] {HookStatus}：ImDrawList_AddText={addTextAddr:x} igBegin={beginAddr:x} igEnd={endAddr:x}");
                return;
            }

            _addTextHook = _interop.HookFromAddress<AddTextFullDelegate>(addTextAddr, AddTextDetour, IGameInteropProvider.HookBackend.Automatic);
            _beginHook = _interop.HookFromAddress<BeginDelegate>(beginAddr, BeginDetour, IGameInteropProvider.HookBackend.Automatic);
            _endHook = _interop.HookFromAddress<EndDelegate>(endAddr, EndDetour, IGameInteropProvider.HookBackend.Automatic);
            _addTextHook.Enable();
            _beginHook.Enable();
            _endHook.Enable();

            Hooked = true;
            HookStatus = $"已挂接 {moduleName}：ImDrawList_AddText / igBegin / igEnd";
            _appLog.Info($"[钩子] {HookStatus}");
        }
        catch (Exception ex)
        {
            HookStatus = "钩子挂接失败：" + ex.Message;
            _appLog.Error("[钩子] 挂接失败：" + ex);
        }
    }

    private static bool TryFindCimgui(out nint baseAddr, out string moduleName)
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
            return true;
        }
        baseAddr = 0;
        moduleName = "";
        return false;
    }

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
            try { Collect(textBegin, textEnd); } catch { /* 同上 */ }
        }
        _addTextHook!.Original(self, font, fontSize, pos, col, textBegin, textEnd, wrapWidth, cpuFineClipRect);
    }

    /// <summary> 采集判定：纯可打印 ASCII、至少含一个字母、非本插件窗口 → 入清单。 </summary>
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
        if (n < 2) return;

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
            if (_ownWins.Contains(_curTopHash)) return; // 本插件自身窗口显示的内容（含查看清单时回显的英文）
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
        try { _beginHook?.Dispose(); } catch { }
        try { _endHook?.Dispose(); } catch { }
        _addTextHook = null;
        _beginHook = null;
        _endHook = null;
        Hooked = false;
        Save();
    }
}
