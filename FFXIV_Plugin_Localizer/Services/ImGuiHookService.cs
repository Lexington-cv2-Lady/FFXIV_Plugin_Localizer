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
            var rep = TryLookup(textBegin, textEnd);
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
            var rep = TryLookup(text, textEnd);
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
    private nint TryLookup(nint textBegin, nint textEnd)
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
            return _replacement.TryReplace(q, n);
        }
        catch
        {
            return 0;
        }
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
        }

        // 各控件统一模式：把第 1 个参数（label）替换后再转发，返回值原样透传。
        var bCheck = new HookBox<W2>();
        Add("igCheckbox", bCheck, (a, b) => bCheck.Hook!.Original(Label(a), b), _ => { });
        var bTree = new HookBox<W1>();
        Add("igTreeNode_Str", bTree, a => bTree.Hook!.Original(Label(a)), _ => { });
        var bTreeEx = new HookBox<W2u>();
        Add("igTreeNodeEx_Str", bTreeEx, (a, b) => bTreeEx.Hook!.Original(Label(a), b), _ => { });
        var bCol = new HookBox<W3u>();
        Add("igCollapsingHeader_BoolPtr", bCol, (a, b, c) => bCol.Hook!.Original(Label(a), b, c), _ => { });
        var bCol2 = new HookBox<W2u>();
        Add("igCollapsingHeader_TreeNodeFlags", bCol2, (a, b) => bCol2.Hook!.Original(Label(a), b), _ => { });
        var bTab = new HookBox<W3u>();
        Add("igBeginTabItem", bTab, (a, b, c) => bTab.Hook!.Original(Label(a), b, c), _ => { });
        var bCombo = new HookBox<W3u>();
        Add("igBeginCombo", bCombo, (a, b, c) => bCombo.Hook!.Original(Label(a), Label(b), c), _ => { });
        var bMenu = new HookBox<W2b>();
        Add("igBeginMenu", bMenu, (a, b) => bMenu.Hook!.Original(Label(a), b), _ => { });
        var bMItem = new HookBox<W4bb>();
        Add("igMenuItem_Bool", bMItem, (a, b, c, d) => bMItem.Hook!.Original(Label(a), Label(b), c, d), _ => { });
        var bRadio = new HookBox<W2b>();
        Add("igRadioButton_Bool", bRadio, (a, b) => bRadio.Hook!.Original(Label(a), b), _ => { });
        var bSF = new HookBox<WSliderFloat>();
        Add("igSliderFloat", bSF, (a, b, c, d, e, f) => bSF.Hook!.Original(Label(a), b, c, d, e, f), _ => { });
        var bSI = new HookBox<WSliderInt>();
        Add("igSliderInt", bSI, (a, b, c, d, e, f) => bSI.Hook!.Original(Label(a), b, c, d, e, f), _ => { });
        var bDF = new HookBox<WDragFloat>();
        Add("igDragFloat", bDF, (a, b, c, d, e, f, g) => bDF.Hook!.Original(Label(a), b, c, d, e, f, g), _ => { });
        var bDI = new HookBox<WDragInt>();
        Add("igDragInt", bDI, (a, b, c, d, e, f, g) => bDI.Hook!.Original(Label(a), b, c, d, e, f, g), _ => { });
        var bCE3 = new HookBox<W3u>();
        Add("igColorEdit3", bCE3, (a, b, c) => bCE3.Hook!.Original(Label(a), b, c), _ => { });
        var bCE4 = new HookBox<W3u>();
        Add("igColorEdit4", bCE4, (a, b, c) => bCE4.Hook!.Original(Label(a), b, c), _ => { });
        var bIT = new HookBox<WInputText>();
        Add("igInputText", bIT, (a, b, c, d, e, f) => bIT.Hook!.Original(Label(a), b, c, d, e, f), _ => { });
        var bITH = new HookBox<WInputTextHint>();
        Add("igInputTextWithHint", bITH, (a, b, c, d, e, f, g) => bITH.Hook!.Original(Label(a), Label(b), c, d, e, f, g), _ => { });

        _appLog.Info($"[钩子] 控件标签桩：{_widgetHooks.Count} 个挂接成功" +
                     (missing.Count > 0 ? $"，缺导出（{string.Join("、", missing)}）" : ""));
    }

    /// <summary> 控件标签查表：命中返回中文指针，否则原指针。异常绝不外抛。 </summary>
    private nint Label(nint p)
    {
        if (p == 0 || !_replacement.Enabled || SuppressReplacement) return p;
        var rep = TryLookup(p, 0);
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
