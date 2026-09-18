using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 游戏原生 UI（AtkAddon）文字钩子：挂 <c>AtkTextNode::SetText</c>。
///
/// 覆盖 KamiToolKit / FFXIVClientStructs 原生 UI 插件（如 LazyGatherer 的配置窗口）——
/// 这类窗口是 FFXIV 游戏原生 AtkUnitBase（TextNode/CheckboxNode 绘制到游戏原生 UI 层），
/// **完全绕过 ImGui 文字钩子**（igTextEx/igTextUnformatted 物理上拦不到，见 2026-09-18 诊断）。
/// 挂 SetText 后，原生文字在设置时即查表替换，中文由游戏原生字体渲染（国服客户端支持 CJK）。
///
/// ⚠ pattern 依赖：SetText 的 IDA pattern 随游戏更新可能变化。优先**运行时反射**
///   FFXIVClientStructs 的 MemberFunctionAttribute 自动取 pattern（跟随 FFXIVClientStructs 版本），
///   反射失败才回退硬编码 pattern（当前 7.56 版）。
/// </summary>
public sealed unsafe class AtkTextHookService : IDisposable
{
    // AtkTextNode::SetText(AtkTextNode* node, char* text)——x64：RCX=node, RDX=text
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetTextDelegate(nint node, nint text);

    /// <summary> 回退 pattern（FFXIVClientStructs 7.56 的 AtkTextNode.SetText；E8=call 相对调用）。 </summary>
    private const string SetTextPatternFallback = "E8 ?? ?? ?? ?? 33 F6 0F B7 D6";
    private const int MaxTextLen = 1024;

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ReplacementService _replacement;
    private Hook<SetTextDelegate>? _hook;

    /// <summary> 是否挂接成功。 </summary>
    public bool Hooked { get; private set; }

    /// <summary> 状态描述（主窗口展示用）。 </summary>
    public string HookStatus { get; private set; } = "未初始化";

    public AtkTextHookService(AppLog appLog, IPluginLog log, IGameInteropProvider interop,
        ISigScanner sigScanner, ReplacementService replacement, Func<bool> hooksEnabled)
    {
        _appLog = appLog;
        _log = log;
        _interop = interop;
        _replacement = replacement;
        if (!hooksEnabled())
        {
            HookStatus = "原生 UI 钩子已按配置关闭";
            _appLog.Info("[Atk钩子] " + HookStatus);
            return;
        }
        Install(sigScanner);
    }

    private void Install(ISigScanner scanner)
    {
        try
        {
            var pattern = ResolveSetTextPattern();
            // ⚠ 唯一性检查（2026-09-18）：pattern 是 E8 call 特征，游戏代码里可能多处匹配。
            //   多匹配时 ScanText 取第一处，若钩错函数会导致原生 UI 异常——必须告警。
            try
            {
                var all = scanner.ScanAllText(pattern);
                if (all.Length > 1)
                    _appLog.Warn($"[Atk钩子] SetText pattern 匹配 {all.Length} 处（取第一处，若原生 UI 异常或 LazyGatherer 不生效需更新 pattern）");
            }
            catch { /* 唯一性检查失败不阻塞安装 */ }
            nint callAddr;
            try
            {
                callAddr = scanner.ScanText(pattern);
            }
            catch
            {
                callAddr = 0;
            }
            if (callAddr == 0)
            {
                HookStatus = $"未找到 AtkTextNode::SetText（pattern {pattern}，可能游戏/卫月已更新）";
                _appLog.Warn("[Atk钩子] " + HookStatus);
                return;
            }
            // E8 相对调用：pattern 定位到 call 指令，函数地址 = call 目标（rel32 在 call 后 1 字节）
            var funcAddr = scanner.ResolveRelativeAddress(callAddr, 1);
            if (funcAddr == 0)
            {
                HookStatus = "AtkTextNode::SetText 地址解析失败";
                _appLog.Error("[Atk钩子] " + HookStatus);
                return;
            }
            _hook = _interop.HookFromAddress<SetTextDelegate>(funcAddr, Detour, IGameInteropProvider.HookBackend.Automatic);
            _hook.Enable();
            Hooked = true;
            HookStatus = $"已挂接 AtkTextNode::SetText（pattern {pattern}）";
            _appLog.Info("[Atk钩子] " + HookStatus);
        }
        catch (Exception ex)
        {
            HookStatus = "AtkTextNode::SetText 挂接失败：" + ex.Message;
            _appLog.Error("[Atk钩子] " + ex);
        }
    }

    /// <summary> 优先运行时反射 FFXIVClientStructs 的 MemberFunctionAttribute 拿 pattern（自动跟随版本）。 </summary>
    private static string ResolveSetTextPattern()
    {
        try
        {
            var atkType = Type.GetType("FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode, FFXIVClientStructs");
            if (atkType != null)
            {
                var cpType = atkType.Assembly.GetType("InteropGenerator.Runtime.CStringPointer");
                if (cpType == null) return SetTextPatternFallback;
                var m = atkType.GetMethod("SetText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { cpType }, null);
                var cd = m?.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.Name == "MemberFunctionAttribute");
                var sig = cd?.ConstructorArguments.FirstOrDefault().Value as string;
                if (!string.IsNullOrWhiteSpace(sig)) return sig!;
            }
        }
        catch { /* 反射失败用回退 pattern */ }
        return SetTextPatternFallback;
    }

    /// <summary> SetText 转发：命中对照表则用中文替换（原生 UI 文本按 UTF-8 存储）。 </summary>
    private void Detour(nint node, nint text)
    {
        if (text != 0 && _replacement.Enabled)
        {
            var rep = TryLookup(text);
            if (rep != 0)
            {
                _hook!.Original(node, rep);
                return;
            }
        }
        _hook!.Original(node, text);
    }

    /// <summary> 读 NUL 结尾 UTF-8 文本查表；命中返回中文指针，否则 0。异常绝不外抛（钩子内异常会中断渲染）。 </summary>
    private nint TryLookup(nint text)
    {
        try
        {
            var q = (byte*)text;
            var n = 0;
            while (n < MaxTextLen && q[n] != 0) n++;
            if (n < 2) return 0;
            return _replacement.TryReplace(q, n);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        _appLog.Info("[Atk钩子] 已卸载");
    }
}
