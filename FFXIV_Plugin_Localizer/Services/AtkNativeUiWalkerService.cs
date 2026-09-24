using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 游戏原生 UI（AtkAddon）文字汉化——**通用遍历版**（2026-09-18 通用化）。
///
/// 背景：KamiToolKit 系插件（LazyGatherer 等）的配置窗口是游戏原生 AtkUnitBase（AtkTextNode 绘制），
///       ImGui 文字钩子物理上拦不到；SetText 钩子被 Reloaded.Hooks 拒编 → 只能遍历组件树。
///
/// 通用化（不再写死某个插件）：
///   ① 从「窗口翻译」目录动态读取插件名列表（**有翻译表的插件才参与**，30s 刷新）；
///   ② 每 100ms 枚举 AtkUnitManager.AllLoadedUnitsList，addon 名 Contains 任一插件名 → 匹配；
///   ③ 对每个匹配的 addon：UldManager.RootNode 树 + NodeList 扁平数组 + Component 深入
///      （KamiToolKit 的 Label/子节点挂在组件自己的 UldManager.NodeList——2026-09-18 实锤），
///      对每个 AtkTextNode 读 NodeText → 查表命中 → SetText(中文)。
///   ④ 新 addon 首次遍历打印统计 + 唯一文本样本（诊断键匹配）；窗口标题（addon 标识）**不改**。
///
/// 性能：枚举约 130 个 addon × 几十个插件名 Contains（微秒级）；遍历只发生在匹配的插件 addon
///       （通常 0~3 个），每 100ms 一次，开销 < 1ms。
/// </summary>
public sealed unsafe class AtkNativeUiWalkerService : IDisposable
{
    private const int MaxTextLen = 1024;
    private const double ScanIntervalMs = 100;          // 100ms：窗口打开后极短延迟内替换，避免"先英文再覆盖"
    private const double PluginListRefreshMs = 30000;   // 插件名列表 30s 刷新（翻译表增删后自动反映）

    private readonly AppLog _appLog;
    private readonly IPluginLog _log;
    private readonly ReplacementService _replacement;
    private readonly IFramework _framework;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastListRefresh = DateTime.MinValue;
    private List<string> _pluginNames = new();
    private readonly HashSet<string> _diagAddons = new();
    private int _textCount, _compCount, _hitCount;
    private readonly List<string> _samples = new();
    private readonly HashSet<nint> _visited = new();

    /// <summary> 状态描述（主窗口展示用）。 </summary>
    public string Status { get; private set; } = "未初始化";

    public AtkNativeUiWalkerService(AppLog appLog, IPluginLog log, IFramework framework, ReplacementService replacement)
    {
        _appLog = appLog;
        _log = log;
        _framework = framework;
        _replacement = replacement;
        RefreshPluginList();
        Status = $"原生 UI 遍历服务已启用（100ms 轮询 {_pluginNames.Count} 个插件的 Atk 窗口）";
        _appLog.Info("[Atk遍历] " + Status);
        _framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastScan).TotalMilliseconds < ScanIntervalMs) return;
        _lastScan = now;

        try
        {
            if ((now - _lastListRefresh).TotalMilliseconds >= PluginListRefreshMs)
                RefreshPluginList();
            if (_pluginNames.Count == 0) return;

            foreach (var (addon, name) in EnumerateMatchedAddons())
                WalkAddon(addon, name);
        }
        catch (Exception ex)
        {
            _appLog.Error("[Atk遍历] " + ex.Message);
        }
    }

    /// <summary> 从「窗口翻译」目录读取插件名列表（有翻译表的插件才参与 Atk 遍历）。 </summary>
    private void RefreshPluginList()
    {
        _lastListRefresh = DateTime.UtcNow;
        try
        {
            var dir = _replacement.WindowTableDir;
            var names = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    // 过滤：短名（<5）容易误伤游戏自身 addon（如 Chat）；下划线开头多为临时/备份
                    if (n.Length >= 5 && !n.StartsWith("_")) names.Add(n);
                }
            }
            _pluginNames = names;
            _appLog.Info($"[Atk遍历] 插件名列表刷新：{names.Count} 个 → {string.Join("、", names)}");
        }
        catch (Exception ex)
        {
            _appLog.Warn("[Atk遍历] 插件名列表刷新失败：" + ex.Message);
        }
    }

    /// <summary> 枚举当前所有 addon，返回 addon 名含任一插件名的（即"有翻译表的 Atk 插件窗口"）。 </summary>
    private List<(nint Addr, string Name)> EnumerateMatchedAddons()
    {
        var result = new List<(nint, string)>();
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return result;
        var um = &stage->RaptureAtkUnitManager->AtkUnitManager;
        var list = &um->AllLoadedUnitsList;
        var entries = list->Entries;
        for (int i = 0; i < list->Count; i++)
        {
            var addon = entries[i].Value;
            if (addon == null) continue;
            var nm = addon->NameString;
            if (string.IsNullOrEmpty(nm)) continue;
            foreach (var p in _pluginNames)
            {
                if (nm.Contains(p))
                {
                    result.Add(((nint)addon, nm));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 关闭指定插件的 Atk 配置窗口（2026-09-18「打开/关闭插件配置」toggle 用）。
    /// 官方 IExposedPlugin 只有 OpenConfigUi 没有 Close（dalamud.dev API 确认），只能在 Atk 层
    /// 遍历 AllLoadedUnitsList 找 addon 名含插件名的窗口，Hide(false, false, 0)。返回是否找到并关闭。
    /// </summary>
    public bool TryClosePluginWindow(string pluginName)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null) return false;
            var um = &stage->RaptureAtkUnitManager->AtkUnitManager;
            var list = &um->AllLoadedUnitsList;
            var entries = list->Entries;
            for (int i = 0; i < list->Count; i++)
            {
                var addon = entries[i].Value;
                if (addon == null) continue;
                var nm = addon->NameString;
                if (nm != null && nm.Contains(pluginName, StringComparison.OrdinalIgnoreCase))
                {
                    addon->Hide(false, false, 0);
                    _appLog.Info($"[Atk遍历] 已关闭窗口 {nm}（{pluginName}）");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _appLog.Warn("[Atk遍历] 关闭窗口失败：" + ex.Message);
            return false;
        }
    }

    private void WalkAddon(nint addr, string name)
    {
        var addon = (AtkUnitBase*)addr;
        _textCount = _compCount = _hitCount = 0;
        _samples.Clear();
        _visited.Clear();

        var isNew = _diagAddons.Add(name);
        if (isNew)
            _appLog.Info($"[Atk遍历] 发现 Atk 窗口 {name} (0x{(nint)addon:X})，遍历文字节点");

        // ⚠ KamiToolKit 插件节点挂在 UldManager 下（AtkUnitBase.RootNode 只有一个 Component 容器，2026-09-18 实锤）
        WalkTextNodes(addon->UldManager.RootNode, 0);
        // 兜底：NodeList 扁平数组（防漏网节点）
        try
        {
            var uld = &addon->UldManager;
            for (int i = 0; i < uld->NodeListCount; i++)
            {
                var n = uld->NodeList[i];
                if (n != null) WalkTextNodes(n, 0);
            }
        }
        catch (Exception ex) { WarnThrottled(ref _lastNodeListErrMs, "[Atk遍历] NodeList 兜底遍历异常（已跳过兜底，主树遍历不受影响）：" + ex.Message); }

        if (isNew)
        {
            _appLog.Info($"[Atk遍历] {name} 首轮统计：Text 节点 {_textCount}，Component 节点 {_compCount}，替换 {_hitCount}；唯一文本样本：" +
                         (_samples.Count == 0 ? "（无）" : string.Join(" | ", _samples)));
        }
        else if (_hitCount > 0)
        {
            _appLog.Info($"[Atk遍历] {name} 本轮替换 {_hitCount} 条");
        }
    }

    private void WalkTextNodes(AtkResNode* node, int depth)
    {
        if (node == null || depth > 128) return;

        // 去重：树遍历与 NodeList 兜底会重复访问同一节点（避免重复 SetText 与日志刷屏）
        if (!_visited.Add((nint)node)) return;

        if (node->Type == NodeType.Text)
        {
            _textCount++;
            var tn = (AtkTextNode*)node;
            try
            {
                var q = (byte*)tn->NodeText.StringPtr;
                if (q == null) goto Next;
                var n = 0;
                while (n < MaxTextLen && q[n] != 0) n++;
                if (n < 2) goto Next;

                if (_samples.Count < 60)
                {
                    try
                    {
                        var s = new CStringPointer(q).ToString();
                        if (!_samples.Contains(s)) _samples.Add(s);
                    }
                    catch (Exception ex) { WarnThrottled(ref _lastSampleErrMs, "[Atk遍历] 文本样本转换异常（仅影响日志样本收集，不中断替换）：" + ex.Message); }
                }

                var zh = _replacement.TryReplace(q, n);
                if (zh != 0)
                {
                    _hitCount++;
                    tn->SetText(new CStringPointer((byte*)zh));
                    _appLog.Info($"[Atk遍历] 替换: {new CStringPointer(q)} -> {new CStringPointer((byte*)zh)}");
                }
            }
            catch
            {
                // 单个节点异常绝不影响整树遍历
            }
        }
        else if (node->Type == NodeType.Component)
        {
            _compCount++;
            // ⚠ Checkbox/Button/DropDown 的文字节点（KamiToolKit 的 Label/子节点）通过 AttachNode 挂在
            //   组件自己的 UldManager.NodeList 里，不在 RootNode 树中（2026-09-18 实锤：addon 级 Text 只有 3 个，
            //   复选框 label 全在组件 NodeList）。因此深入组件时 RootNode 树 + NodeList 都要遍历。
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) goto Next;
            WalkTextNodes(comp->UldManager.RootNode, depth + 1);
            try
            {
                var culd = &comp->UldManager;
                for (int i = 0; i < culd->NodeListCount; i++)
                {
                    var n = culd->NodeList[i];
                    if (n != null) WalkTextNodes(n, depth + 1);
                }
            }
            catch (Exception ex) { WarnThrottled(ref _lastCompNodeListErrMs, "[Atk遍历] 组件 NodeList 遍历异常（已跳过该组件兜底）：" + ex.Message); }
        }

    Next:
        WalkTextNodes(node->ChildNode, depth + 1);
        WalkTextNodes(node->NextSiblingNode, depth);
    }

    // ── 异常日志降频（2026-09-24 审查方第二道校验：补日志，取代静默吞）──
    // 本服务每 100ms 轮询遍历，异常若逐条记录会把日志刷爆（反而制造新的问题）。
    // 故**同类异常 60 秒最多记一条**：既守住通用 B.20「异常不能静默吞」，又不牺牲可读性。
    private long _lastNodeListErrMs, _lastSampleErrMs, _lastCompNodeListErrMs;

    private void WarnThrottled(ref long lastMs, string msg)
    {
        var now = Environment.TickCount64;
        if (now - lastMs < 60_000) return;   // 60 秒内同类只记第一条
        lastMs = now;
        _appLog.Warn(msg);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _appLog.Info("[Atk遍历] 已卸载");
    }
}
