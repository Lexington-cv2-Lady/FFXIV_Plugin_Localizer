using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// ImGui 文字钩子（替换层的落点）：
///   优先挂 cimgui 导出 ImDrawList_AddText_FontPtr 解析出的真实函数体（全部文字绘制的终点）；
///   解析失败则兜底挂 igTextUnformatted 桩（托管侧 Text 类调用的必经桩，覆盖标准文本路径）。
/// 钩子只做替换查表 + 原样转发。文案来源 = 静态扫描（运行时采集已随控件桩钩子一并移除——
/// 控件桩钩子实测崩游戏，见工作记忆教训⑥；文本钩子长期稳定）。
/// </summary>
public sealed unsafe class ImGuiHookService : IDisposable
{
    // cimgui 全参重载：ImDrawList_AddText_FontPtr(self, font, font_size, pos, col, text_begin, text_end, wrap_width, cpu_fine_clip_rect)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AddTextFullDelegate(
        nint self, nint font, float fontSize, ImVec2 pos, uint col,
        nint textBegin, nint textEnd, float wrapWidth, nint cpuFineClipRect);

    // cimgui：igTextUnformatted(text_begin, text_end)——托管侧 Text 类调用的必经桩（兜底钩子）
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(nint textBegin, nint textEnd);

    // ── 窗口替换扩展桩（纯指针参数的 void 文本族）。⚠ 只敢用纯指针委托：igButton 崩溃的疑似机理是
    //    带按值结构体（ImVec2）参数的委托反向封送不可靠；纯指针委托与 igBegin/igTextUnformatted 同型，长期稳定。──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D1(nint a);          // igText / igTextDisabled / igTextWrapped / igBulletText / igSetTooltip
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D2(nint a, nint b);  // igLabelText(label, fmt) / igTextColored(col, fmt)

    [StructLayout(LayoutKind.Sequential)]
    private struct ImVec2
    {
        public float X;
        public float Y;
    }

    private const int MaxTextLen = 1024;

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ReplacementService _replacement;
    private readonly Func<bool> _hooksEnabled;
    private readonly Func<bool> _windowReplaceEnabled;

    private Hook<AddTextFullDelegate>? _addTextHook;
    private Hook<TextUnformattedDelegate>? _textHook;
    private readonly List<IDisposable> _windowHooks = new();

    /// <summary> 钩子是否挂接成功（替换可用的前提）。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 钩子状态描述（主窗口展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        Func<bool> hooksEnabled, Func<bool> windowReplaceEnabled, ReplacementService replacement)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _hooksEnabled = hooksEnabled;
        _windowReplaceEnabled = windowReplaceEnabled;
        _replacement = replacement;
        InstallHooks();
    }

    private void InstallHooks()
    {
        if (!_hooksEnabled())
        {
            HookStatus = "钩子已按配置关闭（安装器替换不可用，静态扫描不受影响）";
            _appLog.Info("[钩子] " + HookStatus);
            return;
        }
        try
        {
            if (!TryFindCimgui(out var baseAddr, out var moduleName, out var moduleSize))
            {
                HookStatus = "未找到 cimgui 模块（ImGui 原生库），替换不可用";
                var modules = string.Join(", ", SafeModuleNames().Take(60));
                _appLog.Error($"[钩子] {HookStatus}。已加载模块：{modules}");
                return;
            }

            // cimgui 对 AddText 两个重载用后缀命名：全参重载（ImGui::RenderText 的必经之路）= _FontPtr；
            // 裸名 ImDrawList_AddText 不存在（二进制 grep 必须整词匹配，子串会被后缀名误报）
            var addTextName = "ImDrawList_AddText_FontPtr";
            var addTextAddr = GetExport(baseAddr, addTextName);
            if (addTextAddr == 0)
            {
                addTextName = "ImDrawList_AddText"; // 未来 cimgui 改名的兜底
                addTextAddr = GetExport(baseAddr, addTextName);
            }
            if (addTextAddr == 0)
            {
                HookStatus = $"cimgui（{moduleName}）缺少导出函数，替换不可用";
                _appLog.Error($"[钩子] {HookStatus}：未找到 {addTextName}");
                return;
            }

            // 关键：cimgui 的导出全是极小的转发桩，ImGui 内部 C++ 渲染直调真实函数体、不经过导出桩——
            // 直接挂桩拦不到内部渲染。顺着桩首跳转指令解析真实函数体再挂钩；
            // 解析目标在模块外一律判失败（实测 abs32 槽会指向别的模块，挂上去是假钩子）。
            _appLog.Info($"[钩子] 导出桩字节：{addTextName}={Hex(addTextAddr, 16)}");
            var addTextHookAddr = addTextAddr;
            var realBody = TryResolveFunctionBody(addTextAddr, baseAddr, moduleSize, out var how);
            if (realBody != 0)
            {
                addTextHookAddr = realBody;
                _appLog.Info($"[钩子] {addTextName} 导出桩 {addTextAddr:x} → 真实函数体 {realBody:x}（{how}）");
            }
            else
            {
                // 兜底：igTextUnformatted 桩（E9 模块内转发，托管 Text 类调用必经）
                var textStub = GetExport(baseAddr, "igTextUnformatted");
                if (textStub != 0)
                {
                    _textHook = _interop.HookFromAddress<TextUnformattedDelegate>(textStub, TextUnformattedDetour, IGameInteropProvider.HookBackend.Automatic);
                    _textHook.Enable();
                    _appLog.Warn($"[钩子] AddText 真实函数体解析失败（{how}），已改挂 igTextUnformatted 桩兜底");
                }
                else
                {
                    _appLog.Error("[钩子] AddText 解析失败且 igTextUnformatted 导出不存在，替换不可用");
                }
            }

            _addTextHook = _interop.HookFromAddress<AddTextFullDelegate>(addTextHookAddr, AddTextDetour, IGameInteropProvider.HookBackend.Automatic);
            _addTextHook.Enable();

            Hooked = true;
            var mode = realBody != 0 ? "AddText 真实函数体" : _textHook != null ? "igTextUnformatted 兜底" : "AddText 导出桩";
            HookStatus = $"已挂接 {moduleName}：{mode}";
            _appLog.Info($"[钩子] {HookStatus}");

            // ── 窗口替换扩展桩：igText 等变体（纯指针 void 委托）。igTextUnformatted 桩只覆盖
            //    TextUnformatted 一条路；变体调用（Text/TextWrapped/LabelText 等）走各自导出。
            //    带结构体/bool 控件桩已证实危险（教训⑥），纯指针 void 桩与稳定钩子同型。 ──
            if (!_windowReplaceEnabled())
            {
                _appLog.Info("[钩子] 窗口替换扩展桩已按配置关闭");
            }
            else
            {
                var missing = new List<string>();
                Hook<D1> hText = null!;
                void TextD(nint a) { try { hText!.Original(RepArg(a)); } catch { /* detour 内异常绝不外抛（曾因 hook 未赋值抛 NRE 导致 UI 静默失效） */ } }
                InstallWindowHook("igText", baseAddr, ref hText, TextD, missing);
                Hook<D1> hTd = null!;
                void TdD(nint a) { try { hTd!.Original(RepArg(a)); } catch { } }
                InstallWindowHook("igTextDisabled", baseAddr, ref hTd, TdD, missing);
                Hook<D1> hTw = null!;
                void TwD(nint a) { try { hTw!.Original(RepArg(a)); } catch { } }
                InstallWindowHook("igTextWrapped", baseAddr, ref hTw, TwD, missing);
                Hook<D1> hBt = null!;
                void BtD(nint a) { try { hBt!.Original(RepArg(a)); } catch { } }
                InstallWindowHook("igBulletText", baseAddr, ref hBt, BtD, missing);
                Hook<D1> hTip = null!;
                void TipD(nint a) { try { hTip!.Original(RepArg(a)); } catch { } }
                InstallWindowHook("igSetTooltip", baseAddr, ref hTip, TipD, missing);
                Hook<D2> hLt = null!;
                void LtD(nint a, nint b) { try { hLt!.Original(RepArg(a), b); } catch { } }
                InstallWindowHook("igLabelText", baseAddr, ref hLt, LtD, missing);
                Hook<D2> hTc = null!;
                void TcD(nint a, nint b) { try { hTc!.Original(a, RepArg(b)); } catch { } }
                InstallWindowHook("igTextColored", baseAddr, ref hTc, TcD, missing);
                _appLog.Info($"[钩子] 窗口替换扩展桩：{_windowHooks.Count}/7 挂接成功" +
                             (missing.Count > 0 ? $"，缺导出（{string.Join("、", missing)}）" : ""));
            }
        }
        catch (Exception ex)
        {
            HookStatus = "钩子挂接失败：" + ex.Message;
            _appLog.Error("[钩子] 挂接失败：" + ex);
        }
    }

    // ═══════════════════════ 原生钩子（替换转发，异常绝不外抛） ═══════════════════════

    private void AddTextDetour(
        nint self, nint font, float fontSize, ImVec2 pos, uint col,
        nint textBegin, nint textEnd, float wrapWidth, nint cpuFineClipRect)
    {
        var repBegin = textBegin;
        if (textBegin != 0 && _replacement.Enabled)
        {
            try
            {
                var q = (byte*)textBegin;
                var n = 0;
                if (textEnd != 0) { n = (int)(textEnd - textBegin); if (n <= 0) n = 0; }
                else while (n < MaxTextLen && q[n] != 0) n++;
                if (n > MaxTextLen) n = MaxTextLen;
                var rep = _replacement.TryReplace(q, n);
                if (rep != 0) { repBegin = rep; textEnd = 0; } // 中文按 NUL 结尾
            }
            catch { /* 钩子内异常绝不外抛 */ }
        }
        _addTextHook!.Original(self, font, fontSize, pos, col, repBegin, textEnd, wrapWidth, cpuFineClipRect);
    }

    private void TextUnformattedDetour(nint textBegin, nint textEnd)
    {
        if (textBegin != 0 && _replacement.Enabled)
        {
            try
            {
                var q = (byte*)textBegin;
                var n = 0;
                while (n < MaxTextLen && q[n] != 0) n++;
                var rep = _replacement.TryReplace(q, n);
                if (rep != 0)
                {
                    _textHook!.Original(rep, 0); // 中文按 NUL 结尾，end 传 0
                    return;
                }
            }
            catch { /* 钩子内异常绝不外抛 */ }
        }
        _textHook!.Original(textBegin, textEnd);
    }

    /// <summary> 挂窗口替换扩展桩。hook 必须 ref 传回调用方——detour 闭包捕获的是调用方变量，
    /// 按值传参会让调用方变量永远为 null，detour 一触发就 NRE，UI 静默失效（踩过）。 </summary>
    private void InstallWindowHook<T>(string export, nint baseAddr, ref Hook<T>? hook, T detour, List<string> missing)
        where T : Delegate
    {
        var addr = GetExport(baseAddr, export);
        if (addr == 0)
        {
            missing.Add(export);
            return;
        }
        hook = _interop.HookFromAddress<T>(addr, detour, IGameInteropProvider.HookBackend.Automatic);
        hook.Enable();
        _windowHooks.Add(hook);
    }

    /// <summary> 替换查表：命中返回 NUL 结尾中文指针（转发时按 NUL 结尾传），未命中返回原指针。 </summary>
    private nint RepArg(nint p)
    {
        if (!_replacement.Enabled || p == 0) return p;
        try
        {
            var q = (byte*)p;
            var n = 0;
            while (n < 1024 && q[n] != 0) n++;
            return _replacement.TryReplace(q, n);
        }
        catch
        {
            return p;
        }
    }

    // ═══════════════════════ cimgui 定位 / 桩解析 ═══════════════════════

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

    /// <summary>
    /// 解析 cimgui 导出桩背后的真实 C++ 函数体。桩形态：E9 rel32（直跳）、
    /// FF 24 25 abs32（经绝对地址槽跳，本机 FontPtr 即此形态）、兼容 FF 25 rel32（RIP 相对槽）。
    /// 槽读取前用 VirtualQuery 校验可读（.NET 读野指针会直接崩进程）；目标在模块外判失败。
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
            // 目标在模块外：实测 FontPtr 桩的 abs32 槽给出的地址在别的模块里，挂上去永远拦不到
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

    /// <summary> 内存前若干字节的十六进制（诊断日志用）。 </summary>
    private static string Hex(nint addr, int count)
        => string.Join(" ", Enumerable.Range(0, count).Select(i => ((byte*)addr)[i].ToString("X2")));

    public void Dispose()
    {
        try { _addTextHook?.Dispose(); } catch { }
        try { _textHook?.Dispose(); } catch { }
        foreach (var h in _windowHooks)
        {
            try { h.Dispose(); } catch { }
        }
        _windowHooks.Clear();
        _addTextHook = null;
        _textHook = null;
        Hooked = false;
    }
}
