using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 源码提取窗口：从插件的公开 GitHub 仓库直接提取界面文案。
/// ⚠ 网络铁律：**必须勾选「启用代理」且填写代理地址**，二者同时满足才允许访问 GitHub；
///   未勾选时即使填了地址也一律拒绝，绝不尝试直连。 </summary>
public sealed class SourceExtractWindow : Window
{
    private readonly Plugin _plugin;
    private readonly SourceExtractService _svc;
    private readonly ReplacementService _replacement;
    private readonly Dictionary<string, (int Total, int Translated, bool Done)> _progressCache = new();
    private List<(string Name, string DisplayName, string RepoUrl)> _plugins = new();
    private string _filter = "";   // 搜索过滤（内部名/显示名/仓库地址）
    private bool _listLoaded;
    private string _manualUrl = "";
    private string _summary = "";
    private readonly Dictionary<string, string> _lastResult = new();
    private readonly Dictionary<string, int> _chineseCache = new(); // 插件名 → 已装 DLL 的中文字符串条数（0=原版英文）
    private bool _testing;          // 连接测试进行中
    private bool? _testOk;          // 上次测试结果（null=未测）
    private string _testMessage = "";
    private bool _batchRunning;     // 「全部提取」进行中（批量任务自己驱动，不走 _svc.Running 判断）
    private int _batchDone;         // 已完成的插件数
    private int _batchTotal;        // 本轮批量总数

    public SourceExtractWindow(Plugin plugin, SourceExtractService svc, ReplacementService replacement)
        : base("源码提取###PluginLocalizerSource")
    {
        _plugin = plugin;
        _svc = svc;
        _replacement = replacement;
        Size = new Vector2(680, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        // ── 说明（用户定稿文案）──
        Ui.Hint("汉化是获取插件公开源码提取的，闭源的无法翻译。\n需要能访问 GitHub（先点「测试直连」；不通则勾选「启用代理」填端口并点「测试代理」）。");

        ImGui.Separator();

        // ── 代理设置（铁律：勾选 + 填地址，二者缺一不可）──
        ImGui.TextUnformatted("访问 GitHub 的代理设置（一般只需填端口）：");
        var useProxy = cfg.UseProxy;
        if (ImGui.Checkbox("启用代理", ref useProxy))
        {
            cfg.UseProxy = useProxy;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只有勾选此项、并填写端口后，本功能才会访问 GitHub。\n未勾选时即使填了端口也不会联网。");

        ImGui.SameLine();
        // 协议下拉（http / socks5）
        ImGui.SetNextItemWidth(90f);
        var scheme = cfg.ProxyScheme;
        if (ImGui.BeginCombo("##ProxyScheme", scheme))
        {
            foreach (var s in new[] { "http", "socks5" })
            {
                if (ImGui.Selectable(s, s == scheme))
                {
                    cfg.ProxyScheme = s;
                    cfg.Save();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("代理协议。Clash / v2ray 一般用 http。");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f); // 容纳 127.0.0.1 完整显示
        var host = cfg.ProxyHost;
        if (ImGui.InputText("##ProxyHost", ref host, 64)) // 默认 127.0.0.1，通常无需改
        {
            cfg.ProxyHost = host.Trim();
            cfg.Save(); // 改动即存（失焦事件在游戏内不可靠，曾导致配置丢失）
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        var port = cfg.ProxyPort;
        if (ImGui.InputTextWithHint("##ProxyPort", "端口", ref port, 8))
        {
            // 只允许数字，防止误填整串地址
            cfg.ProxyPort = new string(port.Where(char.IsDigit).ToArray());
            cfg.Save(); // 改动即存（同上：不能依赖失焦事件）
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只填端口号即可，如 Clash 默认 7890、v2ray 常见 10809（http）或 1080（socks5）。\n只保存在本机配置，不会随插件分发。");

        // 常见端口快捷填充
        ImGui.SameLine();
        if (ImGui.SmallButton("7890")) { cfg.ProxyPort = "7890"; cfg.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clash 默认端口");
        ImGui.SameLine();
        if (ImGui.SmallButton("10809")) { cfg.ProxyPort = "10809"; cfg.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("v2rayN 默认 http 端口");
        ImGui.SameLine();
        if (ImGui.SmallButton("1080")) { cfg.ProxyScheme = "socks5"; cfg.ProxyPort = "1080"; cfg.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("常见 socks5 端口（会自动切换为 socks5 协议）");

        // 填写状态（中性提示，**不用绿色**——避免让用户误以为代理已通） + 真实连接测试按钮
        ImGui.SameLine();
        ImGui.BeginDisabled(_testing);
        if (ImGui.Button(_testing ? "测试中…" : "测试直连"))
        {
            StartTest(useProxy: false);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("测试不使用代理时能否访问 GitHub（部分网络环境可直连）。");

        ImGui.SameLine();
        ImGui.BeginDisabled(_testing || !cfg.CanAccessGitHub || !cfg.UseProxy);
        if (ImGui.Button("测试代理"))
        {
            StartTest(useProxy: true);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("测试通过当前代理能否访问 GitHub。\n只有测试成功才说明代理可用。");

        // 状态：中性色说明填写情况；绿色**只在测试通过后**出现
        if (_testOk == true)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"【成功】 {_testMessage}");
        else if (_testOk == false)
            Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), $"【失败】 {_testMessage}");
        else if (!cfg.UseProxy)
            Ui.ColoredWrapped(new Vector4(0.75f, 0.8f, 0.85f, 1f),
                "当前为「直连」模式（未启用代理）——点「测试直连」确认能否访问 GitHub。");
        else if (string.IsNullOrWhiteSpace(cfg.ProxyPort))
            Ui.ColoredWrapped(new Vector4(1f, 0.6f, 0.35f, 1f), "【需填写】 已勾选启用代理，但端口为空：请填写端口。");
        else
            Ui.ColoredWrapped(new Vector4(0.75f, 0.8f, 0.85f, 1f),
                $"已填写代理 {cfg.ProxyAddress}（尚未验证连通性）——建议先点「测试代理」确认。");

        ImGui.Separator();

        // ── 手动填仓库地址 ──
        ImGui.TextUnformatted("仓库地址（可手动填写，也可从下方已装插件列表选择）：");
        ImGui.SetNextItemWidth(Math.Max(200f, ImGui.GetContentRegionAvail().X - 90f));
        ImGui.InputTextWithHint("##ManualUrl", "https://github.com/作者/仓库", ref _manualUrl, 512);
        ImGui.SameLine();
        var canGo = cfg.CanAccessGitHub && !_svc.Running;
        ImGui.BeginDisabled(!canGo);
        if (ImGui.Button("提取"))
        {
            StartExtract("（手动）", _manualUrl.Trim());
        }
        ImGui.EndDisabled();

        // 打开提取结果目录（未翻译 json 所在处）——删掉扫描窗口后曾丢失此入口
        ImGui.SameLine();
        if (ImGui.Button("打开提取目录"))
        {
            OpenOutputDir();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在资源管理器打开提取结果目录（数据目录\\文案扫描\\<插件名>_源码提取.json）。\n未翻译清单就放在这里，可交给翻译管线或外部 AI。");
        ImGui.SameLine();
        if (ImGui.Button("打开仓库目录"))
        {
            OpenRepoDir();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在资源管理器打开已克隆的源码仓库目录（数据目录\\源码仓库\\）。");

        if (_svc.Running)
        {
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.3f, 1f),
                _batchRunning ? $"{_svc.Status}（全部提取中，请勿关闭游戏）" : _svc.Status);
        }
        else if (_summary.Length > 0)
        {
            ImGui.TextWrapped(_summary);
        }

        ImGui.Separator();

        // ── 已装插件列表（带仓库地址的直接提取）──
        if (!_listLoaded)
        {
            RefreshList();
        }
        ImGui.TextDisabled($"已安装且带 GitHub 地址的插件：{_plugins.Count} 个");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##srcFilter", "搜索插件（内部名/显示名）", ref _filter, 128);
        ImGui.SameLine();
        if (ImGui.Button("刷新列表##src"))
        {
            RefreshList();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新枚举已装插件，并检测各插件当前 DLL 是否已是中文版。");

        // ── 全部提取（尊重上方搜索框：有筛选时只提取筛选结果）──
        ImGui.SameLine();
        {
            var canBatch = cfg.CanAccessGitHub && !_svc.Running && !_batchRunning;
            ImGui.BeginDisabled(!canBatch);
            if (ImGui.Button(_batchRunning ? $"全部提取中 {_batchDone}/{_batchTotal}…" : "全部提取"))
            {
                StartExtractAll();
            }
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把列表里的插件**逐个**提取一遍（自动跳过：已是中文版、源码已汉化、已翻译完成的）。\n" +
                             "有搜索筛选时只处理筛选出的那些；已在别处克隆过的仓库只会 `git pull`，很快。");

        using (var child = ImRaii.Child("##源码插件列表", new Vector2(-1f, -1f), true))
        {
            if (child.Success)
            {
                if (_plugins.Count == 0)
                {
                    Ui.Hint("没有找到带 GitHub 地址的已装插件（可在上方手动填写仓库地址）。");
                }
                var filter = _filter.Trim();
                var shown = 0;
                for (var i = 0; i < _plugins.Count; i++)
                {
                    var (name, displayName, url) = _plugins[i];
                    // 搜索过滤：内部名 / 显示名 / 仓库地址，任一包含即可
                    if (!MatchesFilter(_plugins[i], filter))
                    {
                        continue;
                    }
                    shown++;
                    ImGui.PushID(i);

                    // ── 两行式排版，避免单行过长被窗口右缘截断 ──
                    // 第一行：[提取] 内部名（显示名：X）  状态标注
                    if (ImGui.Button("提取##go"))
                    {
                        StartExtract(name, url);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("复制名##cp"))
                    {
                        // 复制**显示名**（安装器里看到的名字），便于搜索/交流/查资料
                        var copyText = displayName.Length > 0 ? displayName : name;
                        ImGui.SetClipboardText(copyText);
                        _summary = $"已复制插件名到剪贴板：{copyText}";
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("复制该插件的显示名（安装器里显示的名字）到剪贴板。");
                    ImGui.SameLine();
                    // 名称显示：**显示名【内部名】**（用户在安装器里看到的是显示名，故把显示名放前、内部名用【】标注）
                    if (displayName.Length > 0 &&
                        !string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        ImGui.TextUnformatted($"{displayName}【{name}】");
                    }
                    else
                    {
                        ImGui.TextUnformatted(name);
                    }

                    // 状态标注：另起一行（不与名字抢宽度）
                    var pg = _progressCache.TryGetValue(name, out var p0) ? p0 : (Total: 0, Translated: 0, Done: false);
                    if (_chineseCache.TryGetValue(name, out var zh) && zh > 0)
                        Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"    （已是中文版·{zh} 条，无需提取）");
                    else if (pg.Done)
                        Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"    【已翻译】{pg.Translated}/{pg.Total} 条（无需重复提取）");
                    else if (pg.Total > 0)
                        Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.4f, 1f), $"    待翻译 {pg.Translated}/{pg.Total} 条");
                    else
                        Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.4f, 1f), "    （未提取）");

                    // 第二行：仓库地址（灰色，自动换行）
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextWrapped("    " + url);
                    ImGui.PopStyleColor();

                    if (_lastResult.TryGetValue(name, out var res))
                    {
                        Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), "    " + res);
                    }
                    ImGui.Spacing();

                    ImGui.PopID();
                }
                if (filter.Length > 0)
                {
                    ImGui.TextDisabled(filter.Length > 0 && shown == 0
                        ? $"没有匹配「{filter}」的插件。"
                        : $"（筛选后显示 {shown} 个）");
                }
            }
        }
    }

    /// <summary> 刷新插件列表 + 检测各插件已装 DLL 的中文情况（用于列表标注与提前跳过）。 </summary>
    private void RefreshList()
    {
        _plugins = _svc.ListPluginsWithRepo();
        _listLoaded = true;
        _chineseCache.Clear();
        _progressCache.Clear();
        foreach (var (name, _, _) in _plugins)
        {
            try
            {
                var (isChinese, zh, _) = _svc.CheckInstalledChinese(name);
                _chineseCache[name] = isChinese ? zh : 0;
            }
            catch
            {
                _chineseCache[name] = 0;
            }
            try
            {
                _progressCache[name] = _replacement.GetTranslationProgress(name);
            }
            catch
            {
                _progressCache[name] = (0, 0, false);
            }
        }
    }

    /// <summary> 打开提取结果目录（未翻译 json 所在处）。 </summary>
    private void OpenOutputDir() => OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), Services.SourceExtractService.OutputDirName));

    /// <summary> 打开已克隆的源码仓库目录。 </summary>
    private void OpenRepoDir() => OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), Services.SourceExtractService.RepoDirName));

    /// <summary> 用资源管理器打开目录（不存在则先创建）。 </summary>
    private void OpenDir(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _summary = "打开目录失败：" + ex.Message;
            _plugin.AppLog.Error("[源码] 打开目录失败：" + ex.Message);
        }
    }

    /// <summary> 连接测试（后台跑，结果更新状态区）。useProxy=false 测直连。 </summary>
    private void StartTest(bool useProxy)
    {
        if (_testing) return;
        _testing = true;
        _testOk = null;
        _testMessage = "测试中…";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, msg) = useProxy ? await _svc.TestProxyAsync() : await _svc.TestDirectAsync();
                _testOk = ok;
                _testMessage = msg;
            }
            catch (Exception ex)
            {
                _testOk = false;
                _testMessage = "测试异常：" + ex.Message;
            }
            finally
            {
                _testing = false;
            }
        });
    }

    private void StartExtract(string name, string url)
    {
        if (_svc.Running) return;
        _summary = "";
        _svc.SetStatus($"正在拉取 {name} 的仓库…");
        _svc.Running = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, count, funcs, msg) = await _svc.ExtractAsync(name, url);
                if (ok) _replacement.InvalidateCandidateCache();   // 候选已更新，让窗口读到新数据
                _lastResult[name] = (ok ? "【正常】 " : "【异常】 ") + msg;
                _summary = msg;
                _plugin.AppLog.Info($"[源码] {name}：{msg}");
            }
            catch (Exception ex)
            {
                _summary = "提取失败：" + ex.Message;
                _plugin.AppLog.Error("[源码] 提取失败：" + ex.Message);
            }
            finally
            {
                _svc.Running = false;
            }
        });
    }

    /// <summary> 该插件名是否匹配当前搜索框（内部名 / 显示名 / 仓库地址任一命中）。 </summary>
    private static bool MatchesFilter((string Name, string DisplayName, string RepoUrl) p, string filter)
        => filter.Length == 0
           || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || p.RepoUrl.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该插件是否**需要**提取：已是中文版的、
    /// 已翻译完成的（缺口为 0）都不必再联网拉仓库——否则「全部提取」会为几十个插件白跑网络。
    /// </summary>
    private bool NeedsExtract(string name)
    {
        if (_chineseCache.TryGetValue(name, out var zh) && zh > 0) return false;   // 已装 DLL 就是中文版
        if (_progressCache.TryGetValue(name, out var pg) && pg.Done) return false; // 候选已全部翻完
        return true;
    }

    /// <summary>
    /// **全部提取**：把（筛选后的）插件逐个提取一遍。
    /// ⚠ 串行执行 + 温和限速：ExtractAsync 内部要跑 git，并发既无必要也容易触发 GitHub 限流。
    /// 单个插件失败不影响后续（逐个 try/catch，结果写进各自的 _lastResult）。
    /// </summary>
    private void StartExtractAll()
    {
        if (_svc.Running || _batchRunning) return;
        var filter = _filter.Trim();
        var targets = _plugins
            .Where(p => MatchesFilter(p, filter) && NeedsExtract(p.Name))
            .ToList();
        if (targets.Count == 0)
        {
            _summary = filter.Length > 0
                ? $"筛选出的插件都无需提取（已是中文版 / 已翻译完成）。"
                : "没有需要提取的插件（都已是中文版或已翻译完成）。";
            return;
        }

        _batchRunning = true;
        _batchDone = 0;
        _batchTotal = targets.Count;
        _svc.Running = true;
        _summary = "";
        _plugin.AppLog.Info($"[源码] 全部提取开始：{targets.Count} 个插件");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                foreach (var (name, _, url) in targets)
                {
                    _svc.SetStatus($"[全部提取] {_batchDone + 1}/{_batchTotal}：{name}");
                    try
                    {
                        var (ok, count, funcs, msg) = await _svc.ExtractAsync(name, url);
                        _replacement.InvalidateCandidateCache();
                        _lastResult[name] = (ok ? "【正常】 " : "【异常】 ") + msg;
                        _plugin.AppLog.Info($"[源码] 全部提取：{name} → {msg}");
                    }
                    catch (Exception ex)
                    {
                        _lastResult[name] = "【异常】 " + ex.Message;
                        _plugin.AppLog.Error($"[源码] 全部提取：{name} 失败：{ex.Message}");
                    }
                    _batchDone++;
                    await System.Threading.Tasks.Task.Delay(700);   // 限速，别让 GitHub 判定为滥用
                }
                _summary = $"全部提取完成：共处理 {_batchTotal} 个插件（详见各条目结果与日志）。";
                _plugin.AppLog.Info($"[源码] 全部提取完成：{_batchTotal} 个插件");
            }
            finally
            {
                _batchRunning = false;
                _svc.Running = false;
            }
        });
    }
}
