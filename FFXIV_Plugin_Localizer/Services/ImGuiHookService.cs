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

    private const int MaxTextLen = 1024;

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ReplacementService _replacement;
    private readonly Func<bool> _hooksEnabled;

    private Hook<TextUnformattedDelegate>? _textHook;

    /// <summary> 钩子是否挂接成功（替换可用的前提）。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 钩子状态描述（主窗口展示）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public ImGuiHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        Func<bool> hooksEnabled, ReplacementService replacement)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _hooksEnabled = hooksEnabled;
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

            var stub = GetExport(baseAddr, "igTextUnformatted");
            if (stub == 0)
            {
                HookStatus = $"cimgui（{moduleName}）缺少 igTextUnformatted 导出，替换不可用";
                _appLog.Error($"[钩子] {HookStatus}");
                return;
            }

            _textHook = _interop.HookFromAddress<TextUnformattedDelegate>(stub, TextUnformattedDetour, IGameInteropProvider.HookBackend.Automatic);
            _textHook.Enable();
            Hooked = true;
            HookStatus = $"已挂接 {moduleName}：igTextUnformatted（文本类）";
            _appLog.Info($"[钩子] {HookStatus}（桩首字节 {Hex(stub, 12)}）");
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
        _textHook = null;
        Hooked = false;
    }
}
