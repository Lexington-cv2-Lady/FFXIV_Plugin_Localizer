using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// ImGui 文字钩子（替换层的落点）：只挂 <c>igTextUnformatted(const char*, const char*)</c> 导出——
/// 托管侧 Text/TextUnformatted 类调用的必经桩，**参数全是指针、非 varargs、无按值结构体**，长期稳定。
///
/// ⚠ 本类刻意不挂的（都用血换来，见工作记忆教训⑥⑨⑩）：
///   · ImDrawList_AddText_FontPtr / igButton 等含按值 <c>ImVec2</c> 的函数——x64 下 ImVec2 走 XMM 寄存器，
///     .NET 封送可能按通用寄存器传 → 每次调用 ABImismatch → 弄坏调用方栈/ImGui 内部状态（UI 静默失灵、崩溃）。
///   · igText / igTextWrapped / igLabelText / igTextColored 等 varargs 函数——x64 要求调用方预留 XMM 溢出区并置 AL，
///     固定签名委托不满足 → 栈腐蚀。
///   · 控件族（按钮/复选框/滑条）桩——历史上崩过游戏。
/// 要恢复全覆盖，正路是原生跳板（C++ detour）或换钩子后端，不用托管委托挂这些函数。
/// </summary>
public sealed unsafe class ImGuiHookService : IDisposable
{
    // igTextUnformatted(const char* text, const char* text_end)：两个指针、void 返回，纯安全签名。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(nint textBegin, nint textEnd);

    /// <summary>
    /// igTextEx(const char* text, const char* text_end, ImGuiTextFlags flags)——**ImGui 文字族的总入口**。
    /// ⚠ 关键事实（2026-09-14 实测定性）：<c>ImGui::Text/TextWrapped/TextColored/TextDisabled/BulletText</c>
    /// 都走 <c>TextV → TextEx</c>，**不经过 TextUnformatted**；只钩 TextUnformatted 会漏掉绝大多数段落文字
    /// （表现为"同一张表里有的翻得了、有的翻不了"）。TextEx 是固定签名（2 指针 + int），安全可挂。
    /// 注意不能钩 <c>igText</c>/<c>igTextWrapped</c> 本身——它们是 varargs（栈腐蚀）且含 ImVec2（封送错位）。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextExDelegate(nint text, nint textEnd, int flags);

    // ── 控件标签桩（**只用 指针/float/int 签名**的控件；含 ImVec2 结构体的 igButton/igSelectable
    //    与 varargs 的 igText 族**坚决不挂**——这两类是崩溃根源）。全部返回 bool（用 byte 接并原样返回）。──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W1(nint a);                                            // igTreeNode_Str
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W2(nint a, nint b);                                    // igCheckbox
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W2u(nint a, uint b);                                   // igCollapsingHeader_TreeNodeFlags / igTreeNodeEx_Str
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W2b(nint a, byte b);                                   // igBeginMenu / igRadioButton_Bool
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W3u(nint a, nint b, uint c);                           // igBeginCombo / igCollapsingHeader_BoolPtr / igBeginTabItem / igColorEdit3/4
    /// <summary> igTableSetupColumn(label, flags, float init_width_or_weight, uint user_id)——表格列标题。
    /// ⚠ 精确签名经绑定程序集核实为 **4 参数**（label, ImGuiTableColumnFlags, float, ImGuiID），不是 2 个。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void V4(nint a, uint flags, float width, uint userId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W4bb(nint a, nint b, byte c, byte d);                  // igMenuItem_Bool
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WDragFloat(nint label, nint v, float speed, float min, float max, nint fmt, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WDragInt(nint label, nint v, float speed, int min, int max, nint fmt, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WSliderFloat(nint label, nint v, float min, float max, nint fmt, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WSliderInt(nint label, nint v, int min, int max, nint fmt, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WInputText(nint label, nint buf, nuint bufSize, uint flags, nint cb, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WInputTextHint(nint label, nint hint, nint buf, nuint bufSize, uint flags, nint cb, nint data);

    private const int MaxTextLen = 1024;

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ReplacementService _replacement;
    private readonly Func<bool> _hooksEnabled;
    private readonly Func<bool> _widgetHooksEnabled;

    private Hook<TextUnformattedDelegate>? _textHook;
    private Hook<TextExDelegate>? _textExHook;
    private readonly List<IDisposable> _widgetHooks = new();
    /// <summary> 实际挂接成功的控件导出名（诊断用：日志会列出，便于确认某控件是否真挂上）。 </summary>
    private readonly List<string> _hookedWidgetNames = new();
    // ── 调试统计（仅 DebugHookLog 开启时输出）：各钩子调用次数 / 查表命中次数 / 未命中样本 ──
    private readonly Dictionary<string, long> _dbgCalls = new();
    private readonly Dictionary<string, long> _dbgHits = new();
    private readonly List<string> _dbgMissSamples = new();
    private long _dbgFrame;
    public bool DebugStats { get; set; }

    /// <summary> 钩子是否挂接成功（替换可用的前提）。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 抑制替换（本插件自己绘制窗口期间置 true）：本插件 UI 永远显示原文，不做替换。 </summary>
    public bool SuppressReplacement { get; set; }

    /// <summary> 钩子状态描述（主窗口展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        Func<bool> hooksEnabled, Func<bool> widgetHooksEnabled, ReplacementService replacement)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _hooksEnabled = hooksEnabled;
        _widgetHooksEnabled = widgetHooksEnabled;
        _replacement = replacement;
        InstallHooks();
    }

    private void InstallHooks()
    {
        if (!_hooksEnabled())
        {
            HookStatus = "钩子已按配置关闭（替换不可用，静态扫描不受影响）";
            _appLog.Info("[钩子] " + HookStatus);
            return;
        }
        try
        {
            if (!TryFindCimgui(out var baseAddr, out var moduleName))
            {
                HookStatus = "未找到 cimgui 模块（ImGui 原生库），替换不可用";
                var modules = string.Join(", ", SafeModuleNames().Take(60));
                _appLog.Error($"[钩子] {HookStatus}。已加载模块：{modules}");
                return;
            }

            // ── 挂两个文字桩 ──
            // ① igTextEx：ImGui::Text/TextWrapped/TextColored/TextDisabled/BulletText 的总入口（TextV→TextEx），
            //    覆盖绝大多数段落/说明文字——只钩 TextUnformatted 会大面积漏（实测"同表内有的翻有的不翻"）。
            var exStub = GetExport(baseAddr, "igTextEx");
            if (exStub != 0)
            {
                _textExHook = _interop.HookFromAddress<TextExDelegate>(exStub, TextExDetour, IGameInteropProvider.HookBackend.Automatic);
                _textExHook.Enable();
            }
            // ② igTextUnformatted：部分代码直接调它（不经 TextEx），补漏
            var stub = GetExport(baseAddr, "igTextUnformatted");
            if (stub != 0)
            {
                _textHook = _interop.HookFromAddress<TextUnformattedDelegate>(stub, TextUnformattedDetour, IGameInteropProvider.HookBackend.Automatic);
                _textHook.Enable();
            }

            Hooked = _textExHook != null || _textHook != null;
            if (!Hooked)
            {
                HookStatus = $"cimgui（{moduleName}）缺少 igTextEx / igTextUnformatted 导出，替换不可用";
                _appLog.Error($"[钩子] {HookStatus}");
                return;
            }
            HookStatus = $"已挂接 {moduleName}：" +
                         (_textExHook != null ? "igTextEx" : "") +
                         (_textExHook != null && _textHook != null ? " + " : "") +
                         (_textHook != null ? "igTextUnformatted" : "");
            // ── 控件标签桩（安全子集）：覆盖按钮之外的绝大多数配置控件的标签
            //    （滑条/拖拽条/复选框/下拉框/折叠头/标签页/树节点/菜单项/单选框/颜色编辑/输入框）。
            //    这些都是「指针 + 基本类型」签名；含 ImVec2 的 igButton/igSelectable 与 varargs 的 igText 族不挂。──
            if (_widgetHooksEnabled())
            {
                InstallWidgetHooks(baseAddr);
                HookStatus += $" + 控件标签 {_widgetHooks.Count} 个";
            }
            else
            {
                _appLog.Info("[钩子] 控件标签桩已按配置关闭");
            }
            _appLog.Info($"[钩子] {HookStatus}");
        }
        catch (Exception ex)
        {
            HookStatus = "钩子挂接失败：" + ex.Message;
            _appLog.Error("[钩子] 挂接失败：" + ex);
        }
    }

    /// <summary> 文字绘制转发：命中对照表则换中文指针（NUL 结尾），否则原样透传。异常绝不外抛。 </summary>
    private void TextUnformattedDetour(nint textBegin, nint textEnd)
    {
        if (textBegin != 0 && _replacement.Enabled && !SuppressReplacement)
        {
            var rep = TryLookup(textBegin, textEnd, "TextUnformatted");
            if (rep != 0)
            {
                _textHook!.Original(rep, 0); // 中文按 NUL 结尾，end 传 0
                return;
            }
        }
        _textHook!.Original(textBegin, textEnd);
    }

    /// <summary> igTextEx 转发（Text/TextWrapped 等文字族的总入口，flags 原样透传）。 </summary>
    private void TextExDetour(nint text, nint textEnd, int flags)
    {
        if (text != 0 && _replacement.Enabled && !SuppressReplacement)
        {
            var rep = TryLookup(text, textEnd, "TextEx");
            if (rep != 0)
            {
                _textExHook!.Original(rep, 0, flags);
                return;
            }
        }
        _textExHook!.Original(text, textEnd, flags);
    }

    /// <summary>
    /// 公共查表：按调用方给的 text_end（为空则扫到 NUL）取出文案、查对照表；命中返回中文指针，否则 0。
    /// 异常绝不外抛（钩子内异常会中断整个 UI 绘制）。
    /// </summary>
    private nint TryLookup(nint textBegin, nint textEnd, string source = "文字")
    {
        try
        {
            var q = (byte*)textBegin;
            // ⚠ 必须尊重调用方的 text_end：非空表示「切片」（未必 NUL 结尾），
            //   此时绝不能扫 NUL——曾无条件扫到 1024 字节，缓冲区较小就会读到未映射内存 → AV。
            int n;
            if (textEnd != 0)
            {
                n = (int)(textEnd - textBegin);
                if (n <= 0) return 0;
                if (n > MaxTextLen) n = MaxTextLen;
            }
            else
            {
                n = 0;
                while (n < MaxTextLen && q[n] != 0) n++;
            }
            var hit = _replacement.TryReplace(q, n);
            if (DebugStats && n >= 2)
            {
                lock (_dbgCalls)
                {
                    _dbgCalls[source] = _dbgCalls.TryGetValue(source, out var c) ? c + 1 : 1;
                    if (hit != 0)
                    {
                        _dbgHits[source] = _dbgHits.TryGetValue(source, out var h) ? h + 1 : 1;
                    }
                    else if (_dbgMissSamples.Count < 40)
                    {
                        var text = System.Text.Encoding.UTF8.GetString(q, n).Trim();
                        // ⚠ 只记录**纯 ASCII 英文**样本：界面里已是中文的（安装器/Dalamud 自带汉化）不是问题，
                        //    全记下来会淹没有效信息（此前版本即如此，看不出真正漏网的英文）。
                        if (text.Length >= 3 && IsPureAscii(text))
                            _dbgMissSamples.Add($"[{source}] {text}");
                    }
                }
            }
            return hit;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary> 纯 ASCII（不含中文/日文/全角）——用于过滤"已经是中文"的噪音。 </summary>
    private static bool IsPureAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x7E) return false;
            if (c < 0x20 && c != '\t') return false;
        }
        return true;
    }

    /// <summary> 周期性输出调试统计（由 Plugin 的框架回调每 5 秒调用一次）。 </summary>
    public void TickDebugLog()
    {
        if (!DebugStats || _hookedWidgetNames.Count == 0) return;
        string report;
        lock (_dbgCalls)
        {
            // ⚠ 即使某钩子 0 调用也要列出（否则无法区分「没注册」与「注册了但没被调用」）
            _dbgFrame++;
            var sb = new System.Text.StringBuilder();
            // 先列"被调用过"的（按调用次数降序，便于一眼看到主通道）
            foreach (var (k, v) in _dbgCalls.OrderByDescending(kv => kv.Value))
            {
                _dbgHits.TryGetValue(k, out var h);
                sb.Append($"{k}: {v}次/{h}命中; ");
            }
            report = sb.ToString();
            // 未命中样本（**只含纯英文**，去重后最多 12 条）——这些才是真正"表里没有、界面仍是英文"的候选
            if (_dbgMissSamples.Count > 0)
            {
                var uniq = _dbgMissSamples.Distinct().Take(12).ToList();
                report += "\n    未命中英文（可能是漏网的界面文字）: " + string.Join(" | ", uniq);
                _dbgMissSamples.Clear();
            }
            _dbgCalls.Clear();
            _dbgHits.Clear();
        }
        _appLog.Info($"[钩子调试] {report}");
    }

    /// <summary> 安装控件标签桩（安全子集：指针 + 基本类型签名，无 ImVec2、非 varargs）。 </summary>
    private void InstallWidgetHooks(nint baseAddr)
    {
        var missing = new List<string>();
        var backend = IGameInteropProvider.HookBackend.Automatic;

        void Add<T>(string export, HookBox<T> box, T detour, Action<string> record) where T : Delegate
        {
            var addr = GetExport(baseAddr, export);
            if (addr == 0)
            {
                missing.Add(export);
                return;
            }
            box.Hook = _interop.HookFromAddress<T>(addr, detour, backend);
            box.Hook.Enable();
            _widgetHooks.Add(box.Hook);
            _hookedWidgetNames.Add(export);
        }

        // 各控件统一模式：把第 1 个参数（label）替换后再转发，返回值原样透传。
        var bCheck = new HookBox<W2>();
        Add("igCheckbox", bCheck, (a, b) => bCheck.Hook!.Original(Label(a, "igCheckbox"), b), _ => { });
        var bTree = new HookBox<W1>();
        Add("igTreeNode_Str", bTree, a => bTree.Hook!.Original(Label(a, "igTreeNode_Str")), _ => { });
        var bTreeEx = new HookBox<W2u>();
        Add("igTreeNodeEx_Str", bTreeEx, (a, b) => bTreeEx.Hook!.Original(Label(a, "igTreeNodeEx_Str"), b), _ => { });
        var bCol = new HookBox<W3u>();
        Add("igCollapsingHeader_BoolPtr", bCol, (a, b, c) => bCol.Hook!.Original(Label(a, "igCollapsingHeader_BoolPtr"), b, c), _ => { });
        var bCol2 = new HookBox<W2u>();
        Add("igCollapsingHeader_TreeNodeFlags", bCol2, (a, b) => bCol2.Hook!.Original(Label(a, "igCollapsingHeader_TreeNodeFlags"), b), _ => { });
        var bTab = new HookBox<W3u>();
        Add("igBeginTabItem", bTab, (a, b, c) => bTab.Hook!.Original(Label(a, "igBeginTabItem"), b, c), _ => { });
        var bCombo = new HookBox<W3u>();
        Add("igBeginCombo", bCombo, (a, b, c) => bCombo.Hook!.Original(Label(a, "igBeginCombo"), Label(b, "igBeginCombo"), c), _ => { });
        var bMenu = new HookBox<W2b>();
        Add("igBeginMenu", bMenu, (a, b) => bMenu.Hook!.Original(Label(a, "igBeginMenu"), b), _ => { });
        var bMItem = new HookBox<W4bb>();
        Add("igMenuItem_Bool", bMItem, (a, b, c, d) => bMItem.Hook!.Original(Label(a, "igMenuItem_Bool"), Label(b, "igMenuItem_Bool"), c, d), _ => { });
        var bRadio = new HookBox<W2b>();
        Add("igRadioButton_Bool", bRadio, (a, b) => bRadio.Hook!.Original(Label(a, "igRadioButton_Bool"), b), _ => { });
        var bSF = new HookBox<WSliderFloat>();
        Add("igSliderFloat", bSF, (a, b, c, d, e, f) => bSF.Hook!.Original(Label(a, "igSliderFloat"), b, c, d, e, f), _ => { });
        var bSI = new HookBox<WSliderInt>();
        Add("igSliderInt", bSI, (a, b, c, d, e, f) => bSI.Hook!.Original(Label(a, "igSliderInt"), b, c, d, e, f), _ => { });
        var bDF = new HookBox<WDragFloat>();
        Add("igDragFloat", bDF, (a, b, c, d, e, f, g) => bDF.Hook!.Original(Label(a, "igDragFloat"), b, c, d, e, f, g), _ => { });
        var bDI = new HookBox<WDragInt>();
        Add("igDragInt", bDI, (a, b, c, d, e, f, g) => bDI.Hook!.Original(Label(a, "igDragInt"), b, c, d, e, f, g), _ => { });
        var bCE3 = new HookBox<W3u>();
        Add("igColorEdit3", bCE3, (a, b, c) => bCE3.Hook!.Original(Label(a, "igColorEdit3"), b, c), _ => { });
        var bCE4 = new HookBox<W3u>();
        Add("igColorEdit4", bCE4, (a, b, c) => bCE4.Hook!.Original(Label(a, "igColorEdit4"), b, c), _ => { });
        var bIT = new HookBox<WInputText>();
        Add("igInputText", bIT, (a, b, c, d, e, f) => bIT.Hook!.Original(Label(a, "igInputText"), b, c, d, e, f), _ => { });
        var bITH = new HookBox<WInputTextHint>();
        Add("igInputTextWithHint", bITH, (a, b, c, d, e, f, g) => bITH.Hook!.Original(Label(a, "igInputTextWithHint"), Label(b, "igInputTextWithHint"), c, d, e, f, g), _ => { });
        // 表格列标题（TableSetupColumn）与工具提示（SetTooltip）——常见但此前漏挂：
        // 前者是"表格里的列名"（实测 TeleporterPlugin 的 Alias/Aetheryte 就是它），后者是悬停提示。
        var bTsc = new HookBox<V4>();
        Add("igTableSetupColumn", bTsc, (a, b, c, d) => bTsc.Hook!.Original(Label(a, "igTableSetupColumn"), b, c, d), _ => { });
        // ⚠ **不挂 igSetTooltip**：cimgui 对可变参数函数有独立的 `V` 后缀导出（igSetTooltipV），
        //    说明 `igSetTooltip` 是 varargs（`SetTooltip(const char* fmt, ...)`）。
        //    用固定签名委托挂 varargs → x64 调用方需预留 XMM 溢出区而托管封送不保证 →
        //    **栈腐蚀** → 破坏调用方栈帧（实测表现为「Ctrl+V 要按多次才粘贴成功」等输入异常）。
        //    曾误挂过，2026-09-14 移除。

        _appLog.Info($"[钩子] 控件标签桩：{_widgetHooks.Count} 个挂接成功" +
                     (missing.Count > 0 ? $"，缺导出（{string.Join("、", missing)}）" : "") +
                     $"\n        已挂：{string.Join("、", _hookedWidgetNames)}");
    }

    /// <summary>
    /// 控件标签查表：命中返回中文指针，否则原指针。异常绝不外抛。
    /// exportName 仅用于调试统计（按具体控件分别计数，便于定位"哪个控件的标签没被替换"）。
    /// </summary>
    private nint Label(nint p, string exportName = "控件")
    {
        if (p == 0 || !_replacement.Enabled || SuppressReplacement) return p;
        var rep = TryLookup(p, 0, DebugStats ? exportName : "控件");
        return rep != 0 ? rep : p;
    }

    /// <summary> 控件标签桩的钩子容器：detour 闭包通过它拿到自己的 Hook 实例来调 Original。 </summary>
    private sealed class HookBox<T> where T : Delegate
    {
        public Hook<T>? Hook;
    }

    // ═══════════════════════ cimgui 定位 ═══════════════════════

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

    /// <summary> 内存前若干字节的十六进制（诊断日志用）。 </summary>
    private static string Hex(nint addr, int count)
        => string.Join(" ", Enumerable.Range(0, count).Select(i => ((byte*)addr)[i].ToString("X2")));

    public void Dispose()
    {
        try { _textHook?.Dispose(); } catch { }
        try { _textExHook?.Dispose(); } catch { }
        foreach (var h in _widgetHooks)
        {
            try { h.Dispose(); } catch { }
        }
        _widgetHooks.Clear();
        _textHook = null;
        _textExHook = null;
        Hooked = false;
    }
}
