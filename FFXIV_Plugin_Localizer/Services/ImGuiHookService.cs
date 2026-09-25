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
    private delegate byte W4u(nint a, uint b, nint c, nint d);                   // igTreeNodeBehavior(id, flags, label, labelEnd)
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
    private delegate void EndWDelegate();

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

    // ── 输入框「标量」族（2026-09-14 补齐，与上面 Scalar 族同型）──
    // ⚠ **实测路由**：托管 `ImGui.InputInt` / `InputFloat` / `InputDouble` 的 IL 都是
    //    `call ImGui::InputScalar`，最终落到 `ImGuiNative::InputScalar` → cimgui **`igInputScalar`**；
    //    多分量版（InputInt2/3/4、InputFloat2/3/4）同理落到 `igInputScalarN`。
    //    也就是说**只挂 igInputText 覆盖不到输入数字的框** —— 而插件配置窗里 InputInt/InputFloat 极常见。
    //    签名同 SliderScalar 家族：纯指针 + int/uint，安全。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte InputScalarD(nint label, int dataType, nint pData, nint pStep, nint pStepFast, nint format, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte InputScalarND(nint label, int dataType, nint pData, int components, nint pStep, nint pStepFast, nint format, uint flags);
    /// <summary> igCombo_Str(label, int* current_item, const char* items_separated_by_zeros, int popup_max_height)
    /// ——不是 `igBeginCombo`（那是下拉框「展开时」的定义），这个才是**组合框本身**的标签。
    /// ⚠ 触发条件是调用方使用「以 \0 分隔的选项串」这种 Combo 重载（实测 BTS/多数插件都用）。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboStrD(nint label, nint currentItem, nint items, int popupMaxHeight);
    /// <summary> igColorPicker4(label, float* col, flags, float* ref_col)——颜色选择器的标签
    /// （`igColorEdit3/4` 是「小色块」，`igColorPicker3/4` 是「大取色器」，是两个不同控件）。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte W4uN(nint label, nint col, uint flags, nint refCol);
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
    /// <summary> igInputTextEx(label, hint, buf, bufSize, sizeArg, flags, callback, user_data)
    /// —— **2026-09-16 实锤**：新 Dalamud（09-11 更新）把 `ImGui.InputText` 的实现改为
    ///    P/Invoke 这个导出（不再走 `igInputText`），实测 Craftimizer 数值输入框标签全漏网。
    ///    签名经 Bindings 程序集元数据核实：`sizeArg` 是 **`ref Vector2`（引用=指针）**，不是按值结构体，
    ///    全部参数为「指针 + int」，**安全可挂**（不同于按值 ImVec2 的 igButton/igSelectable）。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WInputTextEx(nint label, nint hint, nint buf, int bufSize, nint sizeArg, int flags, nint cb, nint data);
    /// <summary> 按钮/选项族（2026-09-16 新增）。此前「含按值 ImVec2 永不挂」的结论在 **x64 下不成立**：
    /// 8 字节结构体与指针同为 GPR 槽位、大小一致，用 nint 占位透传完全安全（cimgui.h 实测）。
    /// igButton(label, ImVec2 size)；igSmallButton(label)（无 size）；igSelectable_Bool(label, bool, flags, ImVec2 size)。 </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WButton(nint label, nint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WSmallButton(nint label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WSelectable(nint label, byte selected, int flags, nint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte WSelectablePtr(nint label, nint pSelected, int flags, nint size);

    private const int MaxTextLen = 1024;

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ReplacementService _replacement;
    private readonly Func<bool> _hooksEnabled;
    private readonly Func<bool> _widgetHooksEnabled;
    private readonly Func<bool> _translatePenumbra;

    private Hook<TextUnformattedDelegate>? _textHook;
    private Hook<TextExDelegate>? _textExHook;
    private readonly List<IDisposable> _widgetHooks = new();
    private Hook<BeginWDelegate>? _beginProdHook;
    private Hook<EndWDelegate>? _endProdHook;
    // ── Penumbra 专用开关：用「嵌套栈 + 深度计数」追踪当前是否处于 Penumbra 窗口上下文 ──
    // 旧实现只在每次 igBegin 用窗口名判定、设单个 bool，导致 Penumbra 内部若嵌套了名字不含 "Penumbra" 的
    // igBegin 窗格（如 MOD 信息面板/弹层/独立面板），会把标志重置成 false、从而漏翻
    // （2026-09-25 司令官报 bug：MOD 作者名 Konekomods 被机翻成「小猫模组」）。
    // 改为栈深度：只要当前嵌套链里还有任一 Penumbra 窗体未关闭，就持续抑制——子窗格的名字不再能把它清零。
    // ⚠ 仅顶层 igBegin/igEnd 维护栈（子窗口 BeginChild 走的是 igBeginChild，不经过这两个钩，故不影响栈）。
    private readonly Stack<bool> _penumbraStack = new();
    private int _penumbraDepth;
    // Penumbra 的两类顶层窗体标识（用于「翻译 Penumbra 开关关」时判定窗口归属）：
    // ① 主窗 "Penumbra###PenumbraConfigWindow" —— 可见名含 penumbra；
    // ② 模组编辑窗 "{mod.Name}###SubModEdit{N}" —— 以 MOD 名命名、可见名不含 Penumbra，靠 SubModEdit 标识辨认。
    // ⚠ 二者都是独立顶层窗（兄弟关系，非嵌套），故必须都认，否则任一类窗体漏翻（2026-09-25 司令官二次报 bug：手动安装/编辑窗作者名仍被翻）。
    private static readonly byte[] PenumbraLower = { (byte)'p', (byte)'e', (byte)'n', (byte)'u', (byte)'m', (byte)'b', (byte)'r', (byte)'a' };
    private static readonly byte[] SubModEditLower = { (byte)'s', (byte)'u', (byte)'b', (byte)'m', (byte)'o', (byte)'d', (byte)'e', (byte)'d', (byte)'i', (byte)'t' };
    /// <summary> 实际挂接成功的控件导出名（诊断用：日志会列出，便于确认某控件是否真挂上）。 </summary>
    private readonly List<string> _hookedWidgetNames = new();
    /// <summary> 实际挂接成功的文字桩名（诊断用，与控件名一起输出调用计数）。 </summary>
    private readonly List<string> _hookedTextNames = new();
    // ── 调试统计（仅 DebugHookLog 开启时输出）：各钩子调用次数 / 查表命中次数 / 未命中样本 ──
    private readonly Dictionary<string, long> _dbgCalls = new();
    private readonly Dictionary<string, long> _dbgHits = new();
    private readonly List<string> _dbgMissSamples = new();
    /// <summary> 折叠头专用诊断（2026-09-25）：igTreeNodeEx_Str 的**全部**输入原文（命中/未命中都记，不设门槛）。 </summary>
    private readonly List<string> _dbgTreeNodeSamples = new();
    private readonly HashSet<string> _dbgWindowNames = new();   // 调试：累计出现过的窗口名（**不清空**，避免错过只在两个 tick 之间短暂渲染的窗口）
    private readonly List<string> _dbgWindowNew = new();        // 调试：本 tick 新见到的窗口名（打印后清空）
    private long _dbgFrame;
    public bool DebugStats { get; set; }

    /// <summary> 体检模式：即使「钩子调试」关着，也累积未命中英文——供「体检」按钮逐个开窗检测漏网文字。 </summary>
    public bool HealthCollecting { get; set; }

    /// <summary> 体检：清空未命中累积。 </summary>
    public void ResetMissedSamples() { lock (_dbgCalls) _dbgMissSamples.Clear(); }

    /// <summary> 体检：取走并清空当前累积的未命中英文（去重）。 </summary>
    public List<string> DrainMissedSamples()
    {
        lock (_dbgCalls)
        {
            var r = _dbgMissSamples.Distinct().ToList();
            _dbgMissSamples.Clear();
            return r;
        }
    }

    /// <summary> 体检：取走并清空「折叠头（igTreeNodeEx_Str）」样本（去重，含 [命中]/[未命中] 标记）。
    /// 2026-09-25：用于定位 Heliosphere「Commands / Download speed limits / One-click install / Miscellaneous」类
    /// 折叠头没替换的问题——看运行时它到底有没有经过该导出、查表是命中还是未命中。
    /// 与 DrainMissedSamples 的区别：那个只记**未命中且纯 ASCII** 的文本，这个记**全部**折叠头。 </summary>
    public List<string> DrainTreeNodeSamples()
    {
        lock (_dbgCalls)
        {
            var r = _dbgTreeNodeSamples.Distinct().ToList();
            _dbgTreeNodeSamples.Clear();
            return r;
        }
    }

    /// <summary> 钩子是否挂接成功（替换可用的前提）。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 抑制替换（本插件自己绘制窗口期间置 true）：本插件 UI 永远显示原文，不做替换。 </summary>
    public bool SuppressReplacement { get; set; }

    /// <summary> 钩子状态描述（主窗口展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        Func<bool> hooksEnabled, Func<bool> widgetHooksEnabled, ReplacementService replacement,
        bool debugStats, Func<bool> translatePenumbra)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _hooksEnabled = hooksEnabled;
        _widgetHooksEnabled = widgetHooksEnabled;
        _translatePenumbra = translatePenumbra;
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
            // ── Penumbra 专用开关：igBegin / igEnd 常驻钩子（维护窗口嵌套栈，见 BeginDetour / EndDetour）──
            var beginStub2 = GetExport(baseAddr, "igBegin");
            if (beginStub2 != 0)
            {
                _beginProdHook = _interop.HookFromAddress<BeginWDelegate>(beginStub2, BeginDetour, IGameInteropProvider.HookBackend.Automatic);
                _beginProdHook.Enable();
            }
            var endStub = GetExport(baseAddr, "igEnd");
            if (endStub != 0)
            {
                _endProdHook = _interop.HookFromAddress<EndWDelegate>(endStub, EndDetour, IGameInteropProvider.HookBackend.Automatic);
                _endProdHook.Enable();
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
        if (textBegin != 0 && _replacement.Enabled && !SuppressReplacement && !SuppressForPenumbra())
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
        if (text != 0 && _replacement.Enabled && !SuppressReplacement && !SuppressForPenumbra())
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
            if ((DebugStats || HealthCollecting) && n >= 2)
            {
                lock (_dbgCalls)
                {
                    _dbgCalls[source] = _dbgCalls.TryGetValue(source, out var c) ? c + 1 : 1;
                    if (hit != 0)
                    {
                        _dbgHits[source] = _dbgHits.TryGetValue(source, out var h) ? h + 1 : 1;
                    }
                    else if (_dbgMissSamples.Count < (HealthCollecting ? 500 : 40))
                    {
                        var text = System.Text.Encoding.UTF8.GetString(q, n).Trim();
                        // ⚠ 只记录**纯 ASCII 英文**样本：界面里已是中文的（安装器/Dalamud 自带汉化）不是问题，
                        //    全记下来会淹没有效信息（此前版本即如此，看不出真正漏网的英文）。
                        if (text.Length >= 3 && IsPureAscii(text))
                            _dbgMissSamples.Add($"[{source}] {text}");
                    }
                    // ── 折叠头专用诊断（2026-09-25）──
                    // Heliosphere 的 Commands/Download speed limits/One-click install/Miscellaneous 反复"没替换成功"，
                    // 而钩子本体验证是通的（曾有 4940 次调用/780 命中）。为一次定位，凡经过 igTreeNodeEx_Str 的串
                    // **无论命中/未命中、不设 ASCII/长度门槛**，都原样记下（含命中标记），便于看清运行时真实字节。
                    // ⚠ 滚动窗口（满 400 就丢最早的 100）：采样窗口内若用户切换页面，末尾样本才是最后看的那一页，
                    //   固定上限会让"先看的页面"占满名额、把真正要看的页面挤掉（此前 60 条上限即此问题）。
                    // ⚠ 2026-09-25：单参 TreeNodeEx/TreeNode 走的是 **igTreeNodeBehavior**（不是 igTreeNodeEx_Str），
                    //   故折叠头诊断必须同时覆盖这个导出，否则"采不到样本"（这正是此前排查屡屡落空的原因之一）。
                    if (source == "igTreeNodeEx_Str" || source == "igTreeNodeBehavior")
                    {
                        if (_dbgTreeNodeSamples.Count >= 400) _dbgTreeNodeSamples.RemoveRange(0, 100);
                        _dbgTreeNodeSamples.Add($"[{source}][{(hit != 0 ? "命中" : "未命中")}] {System.Text.Encoding.UTF8.GetString(q, n)}");
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

    /// <summary> igBegin 钩子（生产常驻）：①按窗口名更新「当前是否属 Penumbra」（Penumbra 专用开关用）；
    /// ②开启「钩子调试日志」时额外记录窗口名（诊断"窗口到底有没有被渲染"）。
    /// 不修改任何参数、异常绝不外抛。⚠ 仅在「关闭翻译 Penumbra」时才扫描窗口名，
    /// 开关开时几乎零开销（只置 false 透传）。 </summary>
    private byte BeginDetour(nint name, nint pOpen, uint flags)
    {
        if (name != 0)
        {
            try
            {
                // ① Penumbra 上下文标记：开关开时恒 false（不付扫描开销）；开关关时才判定窗口名。
                //    入栈维护嵌套深度——只要当前窗口归属 Penumbra（主窗 penumbra 或模组编辑窗 submodedit），
                //    无论它是嵌套还是独立的顶层兄弟窗，深度都 >0，抑制持续生效（2026-09-25 修复漏翻）。
                // ⚠ 安全阀：万一嵌套配对失衡导致栈无限增长，超阈值整栈重置（避免内存膨胀与永久误抑制）。
                if (_penumbraStack.Count > 8192) { _penumbraStack.Clear(); _penumbraDepth = 0; }
                bool isPen = !_translatePenumbra() && IsPenumbraWindow((byte*)name);
                _penumbraStack.Push(isPen);
                if (isPen) _penumbraDepth++;
                // ② 诊断：记录窗口名（仅 DebugStats 时；**累计**，避免漏掉只在两次 tick 间短暂渲染的窗口）
                if (DebugStats)
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
                            if (_dbgWindowNames.Add(win) && _dbgWindowNew.Count < 60) _dbgWindowNew.Add(win);
                        }
                    }
                }
            }
            catch { /* 绝不外抛 */ }
        }
        return _beginProdHook!.Original(name, pOpen, flags);
    }

    /// <summary> igEnd 钩子（生产常驻）：与 BeginDetour 配对，出栈维护 Penumbra 嵌套深度。
    /// 不修改任何参数、异常绝不外抛。igBegin 返回 false（窗口折叠）时调用方仍须 igEnd，故配对平衡。 </summary>
    private void EndDetour()
    {
        try
        {
            if (_penumbraStack.Count > 0)
            {
                if (_penumbraStack.Pop()) _penumbraDepth--;
            }
        }
        catch { /* 绝不外抛 */ }
        _endProdHook!.Original();
    }

    /// <summary> 在 UTF-8 字节流中查找任一 Penumbra 窗体标识（penumbra 或 submodedit，不区分大小写），
    /// 最多扫 256 字节、遇 NUL 即停，零分配。 </summary>
    private static bool IsPenumbraWindow(byte* p)
        => ScanKeyword(p, PenumbraLower) || ScanKeyword(p, SubModEditLower);

    /// <summary> 在字节流中查找关键字 kw（不区分大小写），KMP 式单状态扫描，零分配。 </summary>
    private static bool ScanKeyword(byte* p, byte[] kw)
    {
        int state = 0;
        for (int i = 0; i < 256; i++)
        {
            byte c = p[i];
            if (c == 0) return false;
            byte lc = c;
            if (lc >= (byte)'A' && lc <= (byte)'Z') lc = (byte)(lc + 32);
            if (lc == kw[state])
            {
                if (++state == kw.Length) return true;
            }
            else
            {
                state = lc == kw[0] ? 1 : 0;
            }
        }
        return false;
    }

    /// <summary> 是否应因「Penumbra 开关关」而抑制本次替换（按当前窗口嵌套上下文）。
    /// 开关开时恒 false（不付窗口判定开销，直接走全局翻译）；开关关时只要嵌套链里还有 Penumbra 窗体即抑制。 </summary>
    private bool SuppressForPenumbra() => !_translatePenumbra() && _penumbraDepth > 0;

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
            if (_beginProdHook != null) names.Add("igBegin");
            if (_endProdHook != null) names.Add("igEnd");

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
            // 折叠头专用诊断（2026-09-25）：igTreeNodeEx_Str 的全部输入原文（命中/未命中都列），
            // 用于定位 Heliosphere「折叠头没替换」的根因（看清运行时真实字节、是否命中）。
            if (_dbgTreeNodeSamples.Count > 0)
            {
                var uniqT = _dbgTreeNodeSamples.Distinct().Take(30).ToList();
                report += "\n    折叠头 igTreeNodeEx_Str 输入（命中/未命中）: " + string.Join(" | ", uniqT);
                _dbgTreeNodeSamples.Clear();
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
        // ⚠ 2026-09-25 补挂：**单参** `ImGui.TreeNodeEx("…")` / `ImGui.TreeNode("…")` 并不走上面的 igTreeNodeEx_Str，
        //    而是走 Dalamud 自己实现的 `ImGuiP.TreeNodeBehavior` → `ImGuiPNative.funcTable[1212] = igTreeNodeBehavior`。
        //    实证（ilspycmd 反编译本机 Dalamud.Bindings.ImGui.dll）：
        //      ImGui.TreeNodeEx(ImU8String id, flags, label)  →  ImGuiP.TreeNodeBehavior(id, flags, label, labelEnd)
        //      ImGuiP.TreeNodeBehavior(byte* …)               →  ImGuiP.funcTable[1212] "igTreeNodeBehavior"
        //    而**两参** `TreeNodeEx(label, flags)`（byte* 重载）才走 igTreeNodeEx_Str。
        //    后果：Heliosphere 的 `TreeNodeEx("Commands")`/`("Download speed limits")`/`("One-click install")`/`("Miscellaneous")`
        //    四个**单参**折叠头长期全英文（而两参的 "Installs and updates"/"Penumbra" 一直是中文）——即司令官反复反馈的那条。
        //    签名 (uint id, ImGuiTreeNodeFlags flags, const char* label, const char* labelEnd)：
        //    id/flags 为整数、label/labelEnd 为指针，**无按值结构体、非 varargs** → 与既有控件桩同级安全。
        // ⚠⚠ 关键（2026-09-25 实证踩坑）：这是 **「区间」语义**，不是「双指针」语义！
        //    原实现按 `labelEnd - label` 决定画多少字节。因此**不能**把两个参数分别拿去查表替换——
        //    那样 label 指向 9 字节的中文、labelEnd 指向 5 字节的英文副本末端，区间长度按旧串算
        //    → 中文串被截断/读到 NUL 之后 → **界面直接显示空白**（Heliosphere 四个折叠头当时的症状）。
        //    正确做法：只查一次表拿到**新指针 + 新长度**，再把 labelEnd 设为 `新指针 + 新长度`。
        var bTreeBeh = new HookBox<W4u>();
        Add("igTreeNodeBehavior", bTreeBeh, (id, flags, label, labelEnd) =>
        {
            var rep = LabelWithEnd(label, labelEnd, out var repEnd);
            return rep != 0
                ? bTreeBeh.Hook!.Original(id, flags, rep, repEnd)
                : bTreeBeh.Hook!.Original(id, flags, label, labelEnd);
        }, _ => { });
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
        // 2026-09-16：新 Dalamud 的 ImGui.InputText 改走 igInputTextEx（见委托注释），label 与 hint 都替换（hint 常为 null，Label 对 p==0 安全）。
        var bITEx = new HookBox<WInputTextEx>();
        Add("igInputTextEx", bITEx, (a, b, c, d, e, f, g, h) => bITEx.Hook!.Original(Label(a, "igInputTextEx"), Label(b, "igInputTextEx"), c, d, e, f, g, h), _ => { });
        // 2026-09-16：x64 下按值 ImVec2（8 字节）与 nint 同为 GPR 槽位 → 按钮/选项可安全替换 label（含 Reset to Default）。
        var bBtn = new HookBox<WButton>();
        Add("igButton", bBtn, (a, b) => bBtn.Hook!.Original(Label(a, "igButton"), b), _ => { });
        var bSmallBtn = new HookBox<WSmallButton>();
        Add("igSmallButton", bSmallBtn, a => bSmallBtn.Hook!.Original(Label(a, "igSmallButton")), _ => { });
        var bSel = new HookBox<WSelectable>();
        Add("igSelectable_Bool", bSel, (a, b, c, d) => bSel.Hook!.Original(Label(a, "igSelectable_Bool"), b, c, d), _ => { }); // 算法列表/行动池选项（Oneshot/Stepwise/Optimal 等）
        var bSelPtr = new HookBox<WSelectablePtr>();
        Add("igSelectable_BoolPtr", bSelPtr, (a, b, c, d) => bSelPtr.Hook!.Original(Label(a, "igSelectable_BoolPtr"), b, c, d), _ => { });
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

        // ── 输入框标量族 + 组合框 + 取色器 + 表头（2026-09-14 补齐「安全但漏挂」的一批）──
        //    这批的共同点：**标签是画出来的文字**，且签名只有指针/整数/浮点（无按值结构体、非 varargs）。
        //    对照实验证明同类签名的 igSliderScalar 长期稳定，故风险与既有钩子同级。
        var bIS = new HookBox<InputScalarD>();
        Add("igInputScalar", bIS, (a, b, c, d, e, f, g) => bIS.Hook!.Original(Label(a, "igInputScalar"), b, c, d, e, f, g), _ => { });
        var bISN = new HookBox<InputScalarND>();
        Add("igInputScalarN", bISN, (a, b, c, d, e, f, g, h) => bISN.Hook!.Original(Label(a, "igInputScalarN"), b, c, d, e, f, g, h), _ => { });
        var bCmbS = new HookBox<ComboStrD>();
        Add("igCombo_Str", bCmbS, (a, b, c, d) => bCmbS.Hook!.Original(Label(a, "igCombo_Str"), b, c, d), _ => { });
        var bCp3 = new HookBox<W3u>();
        Add("igColorPicker3", bCp3, (a, b, c) => bCp3.Hook!.Original(Label(a, "igColorPicker3"), b, c), _ => { });
        var bCp4 = new HookBox<W4uN>();
        Add("igColorPicker4", bCp4, (a, b, c, d) => bCp4.Hook!.Original(Label(a, "igColorPicker4"), b, c, d), _ => { });
        var bTh = new HookBox<W1>();
        Add("igTableHeader", bTh, a => bTh.Hook!.Original(Label(a, "igTableHeader")), _ => { });
        var bSa = new HookBox<WSliderFloat>();   // (label, float* vRad, float min, float max, fmt, flags) 同型
        Add("igSliderAngle", bSa, (a, b, c, d, e, f) => bSa.Hook!.Original(Label(a, "igSliderAngle"), b, c, d, e, f), _ => { });
        // ── 纯 label 的按钮类（2026-09-15 补）：**参数里没有 ImVec2**，因此可以安全挂（与 igButton 不同）。
        //    ⚠ `igButton`/`igSelectable_Bool` 含按值 ImVec2（实测反汇编确认），**永远不能**用托管委托挂；
        //    但 `igSmallButton(label)` / `igTabItemButton(label, flags)` 只有字符串 + 整数，是安全的。
        //    实测当前 5 个仓库都没用到，属"补全白名单"性质（将来有用到 SmallButton 的插件即可覆盖）。 ──
        // ⚠ 2026-09-23 审查 F1b：igSmallButton 已在上方的「按钮/选项族」块（:504）挂过一次，
        //   此处原是重复挂接同一导出 → 会导致重复钩子链/加载异常，已移除。
        var bTib = new HookBox<W2u>();
        Add("igTabItemButton", bTib, (a, b) => bTib.Hook!.Original(Label(a, "igTabItemButton"), b), _ => { });
        // ⚠ **不挂 igSetTooltip**：cimgui 对可变参数函数有独立的 `V` 后缀导出（igSetTooltipV），
        //    说明 `igSetTooltip` 是 varargs（`SetTooltip(const char* fmt, ...)`）。
        //    用固定签名委托挂 varargs → x64 调用方需预留 XMM 溢出区而托管封送不保证 →
        //    **栈腐蚀** → 破坏调用方栈帧（实测表现为「Ctrl+V 要按多次才粘贴成功」等输入异常）。
        //    曾误挂过，2026-09-14 移除。

        // ── 诊断用 igBegin：已并入生产常驻的 BeginDetour（见 InstallHooks），此处不再单独安装 ──

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
        if (p == 0 || !_replacement.Enabled || SuppressReplacement || SuppressForPenumbra()) return p;
        // ⚠ 2026-09-25：source **始终**传具体导出名（原来非调试时统一传"控件"），
        //   否则单插件检查时无法按导出名筛出折叠头样本，诊断只能靠"钩子调试日志"开关——而该开关默认是关的。
        //   传的是常量字符串引用，无分配、无额外开销（统计本身仍受 DebugStats/HealthCollecting 门槛保护）。
        var rep = TryLookup(p, 0, exportName);
        return rep != 0 ? rep : p;
    }

    /// <summary>
    /// 「区间式」标签控件的查表（2026-09-25 新增，供 <c>igTreeNodeBehavior</c> 用）：
    /// 命中返回**中文指针**并把 <paramref name="repEnd"/> 设为「中文指针 + 中文 UTF-8 字节数」；
    /// 未命中返回 0（<paramref name="repEnd"/> 置 0，调用方原样透传旧参数）。
    /// <b>为什么必须成对返回</b>：这类 API 按 <c>labelEnd - label</c> 决定绘制字节数，
    /// 分开替换两个指针会让区间长度与中文串长度不符 → 显示空白（详见 ReplacementService.TryReplaceWithLen 注释）。
    /// </summary>
    private nint LabelWithEnd(nint p, nint labelEnd, out nint repEnd)
    {
        repEnd = 0;
        if (p == 0 || !_replacement.Enabled || SuppressReplacement || SuppressForPenumbra()) return 0;
        // 区间长度：labelEnd 非 0 时按它算（尊重调用方的切片）；为 0 时扫到 NUL 兜底
        //（正常 TreeNodeBehavior 调用 labelEnd 恒为 label+len，此处只是防御）。
        var n = labelEnd != 0 ? (int)(labelEnd - p) : 0;
        if (n < 0) return 0;
        unsafe
        {
            if (n == 0)
            {
                var q = (byte*)p;
                while (n < 1024 && q[n] != 0) n++;
            }
            if (n < 2) return 0;
            var rep = _replacement.TryReplaceWithLen((byte*)p, n, out var zhLen);
            if (rep == 0) return 0;
            repEnd = rep + zhLen;
            // 统计与折叠头诊断：与 TryLookup 同口径（供「钩子调试日志」与单插件检查看这个导出的命中情况）。
            if ((DebugStats || HealthCollecting) && n >= 2)
            {
                lock (_dbgCalls)
                {
                    _dbgCalls["igTreeNodeBehavior"] = _dbgCalls.TryGetValue("igTreeNodeBehavior", out var c) ? c + 1 : 1;
                    _dbgHits["igTreeNodeBehavior"] = _dbgHits.TryGetValue("igTreeNodeBehavior", out var h) ? h + 1 : 1;
                    var text = System.Text.Encoding.UTF8.GetString((byte*)p, n);
                    if (_dbgTreeNodeSamples.Count >= 400) _dbgTreeNodeSamples.RemoveRange(0, 100);
                    _dbgTreeNodeSamples.Add($"[igTreeNodeBehavior][命中] {text}");
                }
            }
            return rep;
        }
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

    public void Dispose()
    {
        // ⚠ 2026-09-18 全面审查：原为**全空 catch**。这里失败意味着**游戏进程里仍留着我们的补丁**，
        //   而插件已卸载 → 补丁会调用已卸载插件的代码路径（崩溃来源），且用户毫无线索。
        //   钩子释放失败必须留日志（不抛，尽最大努力卸载其余钩子）。
        TryDisposeHook(_textHook, "Text");
        TryDisposeHook(_textExHook, "TextEx");
        TryDisposeHook(_beginProdHook, "Begin");
        TryDisposeHook(_endProdHook, "End");
        _beginProdHook = null;
        _endProdHook = null;
        foreach (var h in _widgetHooks) TryDisposeHook(h, "Widget");
        _widgetHooks.Clear();
        _textHook = null;
        _textExHook = null;
        Hooked = false;
    }

    /// <summary> 释放单个钩子；失败时记日志（含钩子类别），不让一个失败阻断其余卸载。 </summary>
    private void TryDisposeHook(IDisposable? hook, string kind)
    {
        if (hook == null) return;
        try
        {
            hook.Dispose();
        }
        catch (Exception ex)
        {
            _appLog.Error($"[钩子] {kind} 钩子释放失败（该补丁可能仍留在游戏进程内）：" + ex.Message);
        }
    }
}
