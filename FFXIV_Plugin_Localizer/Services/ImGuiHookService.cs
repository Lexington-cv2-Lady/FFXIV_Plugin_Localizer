using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// ImGui 文字钩子（替换层的落点）：挂 <c>igTextEx</c> / <c>igTextUnformatted</c>（文字族必经桩）
/// 以及一批**签名安全**（只有指针/整型/浮点，无按值结构体、非 varargs）的控件标签桩，
/// 例如 <c>igSliderScalar</c>（= 托管 SliderFloat/SliderInt 的真实落点，见下）。
///
/// ⚠ 关键事实（2026-09-14 用绑定程序集 IL + 导出表双向核实）：托管绑定是「跳表」式的——
///   <c>ImGui.SliderFloat(...)</c> → <c>ImGui::SliderScalar&lt;float&gt;</c> → <c>ImGuiNative::SliderScalar</c>
///   → <c>HexaGen.Runtime.FunctionTable[185]</c>，而索引 185 对应的 cimgui 导出就是
///   <c>igSliderScalar</c>（**不是 igSliderFloat**）。所以要拦 SliderFloat/DragFloat 这类
///   「模板化标量」控件，必须挂 <c>igSliderScalar</c>/<c>igDragScalar</c>（本类已挂）。
///
/// ⚠ 本类刻意不挂的（都用血换来，见工作记忆教训⑥⑨⑩）：
///   · ImDrawList_AddText_FontPtr / igButton 等含按值 <c>ImVec2</c> 的函数——x64 下 ImVec2 走 XMM 寄存器，
///     .NET 封送可能按通用寄存器传 → ABImismatch → 弄坏调用方栈/ImGui 内部状态（UI 静默失灵、崩溃）。
///   · igText / igTextWrapped / igLabelText / igTextColored 等 varargs 函数——x64 要求调用方预留 XMM 溢出区并置 AL，
///     固定签名委托不满足 → 栈腐蚀。
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
    /// <summary> igBegin(name, bool* p_open, ImGuiWindowFlags flags)——窗口开始。
    /// **只用于诊断**（开启「钩子调试日志」时记录实际创建的窗口名，回答"某个窗口到底有没有被渲染"）。
    /// ⚠ 刻意**不做窗口标题替换**（2026-09-14 用户决定）：窗口标题保持原文即可，不必汉化；
    ///    这样也省掉"必须整串精确匹配、不能截断 ##/###ID"那一整套风险（ImGui 用窗口名算窗口 ID）。
    /// 该钩子仅在 `DebugStats` 为真时安装（生产环境零开销）。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginWDelegate(nint name, nint pOpen, uint flags);

    // ── 通用「标量」控件（Scalar 版）：所有 SliderXxx/DragXxx 的底层实现。
    //    绑定层把 `ref float` 传给 ImGui.SliderFloat 时，**有可能**路由到这里（而非 igSliderFloat）。
    //    签名均为「指针 + int/float + 指针 + uint」，安全可挂。 ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SliderScalarD(nint label, int dataType, nint pData, nint pMin, nint pMax, nint format, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SliderScalarND(nint label, int dataType, nint pData, int components, nint pMin, nint pMax, nint format, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte DragScalarD(nint label, int dataType, nint pData, float vSpeed, nint pMin, nint pMax, nint format, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte DragScalarND(nint label, int dataType, nint pData, int components, float vSpeed, nint pMin, nint pMax, nint format, uint flags);
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
    private Hook<BeginWDelegate>? _beginDbgHook;
    /// <summary> 实际挂接成功的控件导出名（诊断用：日志会列出，便于确认某控件是否真挂上）。 </summary>
    private readonly List<string> _hookedWidgetNames = new();
    /// <summary> 实际挂接成功的文字桩名（诊断用，与控件名一起输出调用计数）。 </summary>
    private readonly List<string> _hookedTextNames = new();
    // ── 调试统计（仅 DebugHookLog 开启时输出）：各钩子调用次数 / 查表命中次数 / 未命中样本 ──
    private readonly Dictionary<string, long> _dbgCalls = new();
    private readonly Dictionary<string, long> _dbgHits = new();
    private readonly List<string> _dbgMissSamples = new();
    private readonly HashSet<string> _dbgWindowNames = new();   // 调试：累计出现过的窗口名（**不清空**，避免错过只在两个 tick 之间短暂渲染的窗口）
    private readonly List<string> _dbgWindowNew = new();        // 调试：本 tick 新见到的窗口名（打印后清空）
    private long _dbgFrame;
    public bool DebugStats { get; set; }

    /// <summary> 钩子是否挂接成功（替换可用的前提）。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 抑制替换（本插件自己绘制窗口期间置 true）：本插件 UI 永远显示原文，不做替换。 </summary>
    public bool SuppressReplacement { get; set; }

    /// <summary> 钩子状态描述（主窗口展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        Func<bool> hooksEnabled, Func<bool> widgetHooksEnabled, ReplacementService replacement,
        bool debugStats)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _hooksEnabled = hooksEnabled;
        _widgetHooksEnabled = widgetHooksEnabled;
        _replacement = replacement;
        // ⚠ 必须**从构造函数传入**，不能像以前那样构造后再 `Hook.DebugStats = …` 赋值——
        //    InstallHooks 在构造期就跑完了，那时 DebugStats 还是 false，导致"按 DebugStats 才装"的
        //    诊断钩子永远装不上（纯死代码；2026-09-14 踩过，见工作记忆）。
        DebugStats = debugStats;
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
            _hookedTextNames.Clear();
            if (_textExHook != null) _hookedTextNames.Add("TextEx");
            if (_textHook != null) _hookedTextNames.Add("TextUnformatted");
            // ⚠ 调试统计里文字桩的键名就是这两个（见 TextUnformattedDetour / TextExDetour）——
            //    必须与实际计数用的 source 字符串一致，否则指针停在 0 次。 
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

    /// <summary> igBegin 诊断转发：只读记录窗口名（不修改任何参数）。异常绝不外抛。 </summary>
    private byte BeginDbgDetour(nint name, nint pOpen, uint flags)
    {
        if (name != 0)
        {
            try
            {
                var q = (byte*)name;
                var n = 0;
                while (n < 256 && q[n] != 0) n++;
                if (n >= 2)
                {
                    var win = System.Text.Encoding.UTF8.GetString(q, n);
                    lock (_dbgCalls)
                    {
                        _dbgCalls["igBegin"] = _dbgCalls.TryGetValue("igBegin", out var c) ? c + 1 : 1;
                        // **累计**记录：一个窗口可能只在两次 tick 之间短暂渲染，
                        // 只看「本 tick 的窗口名」会漏掉它（BigPlayerDebuffs 的配置窗口就是这么 elusive）。
                        if (_dbgWindowNames.Add(win) && _dbgWindowNew.Count < 60) _dbgWindowNew.Add(win);
                    }
                }
            }
            catch { /* 绝不外抛 */ }
        }
        return _beginDbgHook!.Original(name, pOpen, flags);
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
            _dbgFrame++;
            // 完整要报告的钩子名：文字桩 + 控件桩 + igBegin 诊断（**漏掉哪个就没法判断它是否被调用**）
            var names = new List<string>(_hookedTextNames.Count + _hookedWidgetNames.Count + 1);
            names.AddRange(_hookedTextNames);
            names.AddRange(_hookedWidgetNames);
            if (_beginDbgHook != null) names.Add("igBegin");

            var sb = new System.Text.StringBuilder();
            var idle = new List<string>();
            // 被调用过的（按次数降序，一眼看到主通道）
            foreach (var (k, v) in _dbgCalls.OrderByDescending(kv => kv.Value))
            {
                _dbgHits.TryGetValue(k, out var h);
                sb.Append($"{k}: {v}次/{h}命中; ");
            }
            report = sb.ToString();
            // ⚠ 关键：把「挂了但一次都没被调用」的显式列出来。只列 _dbgCalls 的话，
            //   0 调用的钩子根本不出现 → 无法区分「钩子没注册」与「注册了但这款插件不走这个导出」，
            //   而 BigPlayerDebuffs「界面不换」恰恰卡在这个判断上。
            foreach (var name in names)
                if (!_dbgCalls.ContainsKey(name)) idle.Add(name);
            if (idle.Count > 0)
                report += "\n    0 调用（已挂但没触发）: " + string.Join("、", idle);
            // 实际创建的窗口名（诊断"窗口到底有没有被渲染"）：**累计**，只打印本 tick 新出现的
            if (_dbgWindowNew.Count > 0)
            {
                report += "\n    新窗口: " + string.Join(" | ", _dbgWindowNew.Take(20));
                _dbgWindowNew.Clear();
            }
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
        // ── 保险：通用标量版（覆盖"ref 参数被路由到 Scalar 实现"的情况）──
        var bSS = new HookBox<SliderScalarD>();
        Add("igSliderScalar", bSS, (a, b, c, d, e, f, g) => bSS.Hook!.Original(Label(a, "igSliderScalar"), b, c, d, e, f, g), _ => { });
        var bSSN = new HookBox<SliderScalarND>();
        Add("igSliderScalarN", bSSN, (a, b, c, d, e, f, g, h) => bSSN.Hook!.Original(Label(a, "igSliderScalarN"), b, c, d, e, f, g, h), _ => { });
        var bDS = new HookBox<DragScalarD>();
        Add("igDragScalar", bDS, (a, b, c, d, e, f, g, h) => bDS.Hook!.Original(Label(a, "igDragScalar"), b, c, d, e, f, g, h), _ => { });
        var bDSN = new HookBox<DragScalarND>();
        Add("igDragScalarN", bDSN, (a, b, c, d, e, f, g, h, i) => bDSN.Hook!.Original(Label(a, "igDragScalarN"), b, c, d, e, f, g, h, i), _ => { });
        // ⚠ **不挂 igSetTooltip**：cimgui 对可变参数函数有独立的 `V` 后缀导出（igSetTooltipV），
        //    说明 `igSetTooltip` 是 varargs（`SetTooltip(const char* fmt, ...)`）。
        //    用固定签名委托挂 varargs → x64 调用方需预留 XMM 溢出区而托管封送不保证 →
        //    **栈腐蚀** → 破坏调用方栈帧（实测表现为「Ctrl+V 要按多次才粘贴成功」等输入异常）。
        //    曾误挂过，2026-09-14 移除。

        // ── 诊断用：igBegin（记录实际创建的窗口名）──
        // ⚠ 只在开启「钩子调试日志」时安装：这是诊断设施，不做替换（窗口标题不汉化），
        //    而 igBegin 是**极热**函数（实测每 5 秒数千次），生产环境没必要为它付一次托管往返。
        if (DebugStats)
        {
            var beginStub = GetExport(baseAddr, "igBegin");
            if (beginStub != 0)
            {
                _beginDbgHook = _interop.HookFromAddress<BeginWDelegate>(beginStub, BeginDbgDetour, IGameInteropProvider.HookBackend.Automatic);
                _beginDbgHook.Enable();
                _appLog.Info("[钩子] 已挂 igBegin（诊断：记录窗口名）");
            }
            else
            {
                missing.Add("igBegin");
            }
        }

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
        try { _beginDbgHook?.Dispose(); } catch { }
        _beginDbgHook = null;
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
