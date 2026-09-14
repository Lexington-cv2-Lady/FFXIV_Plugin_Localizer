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
    private List<(string Name, string RepoUrl)> _plugins = new();
    private bool _listLoaded;
    private string _manualUrl = "";
    private string _summary = "";
    private readonly Dictionary<string, string> _lastResult = new();
    private readonly Dictionary<string, int> _chineseCache = new(); // 插件名 → 已装 DLL 的中文字符串条数（0=原版英文）
    private bool _testing;          // 连接测试进行中
    private bool? _testOk;          // 上次测试结果（null=未测）
    private string _testMessage = "";

    public SourceExtractWindow(Plugin plugin, SourceExtractService svc)
        : base("源码提取###PluginLocalizerSource")
    {
        _plugin = plugin;
        _svc = svc;
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
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"✔ {_testMessage}");
        else if (_testOk == false)
            Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), $"✘ {_testMessage}");
        else if (!cfg.UseProxy)
            Ui.ColoredWrapped(new Vector4(0.75f, 0.8f, 0.85f, 1f),
                "当前为「直连」模式（未启用代理）——点「测试直连」确认能否访问 GitHub。");
        else if (string.IsNullOrWhiteSpace(cfg.ProxyPort))
            Ui.ColoredWrapped(new Vector4(1f, 0.6f, 0.35f, 1f), "✘ 已勾选启用代理，但端口为空：请填写端口。");
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
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.3f, 1f), _svc.Status);
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
        if (ImGui.Button("刷新列表##src"))
        {
            RefreshList();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新枚举已装插件，并检测各插件当前 DLL 是否已是中文版。");

        using (var child = ImRaii.Child("##源码插件列表", new Vector2(-1f, -1f), true))
        {
            if (child.Success)
            {
                if (_plugins.Count == 0)
                {
                    Ui.Hint("没有找到带 GitHub 地址的已装插件（可在上方手动填写仓库地址）。");
                }
                for (var i = 0; i < _plugins.Count; i++)
                {
                    var (name, url) = _plugins[i];
                    ImGui.PushID(i);
                    if (ImGui.Button("提取##go"))
                    {
                        StartExtract(name, url);
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(name);
                    // 预先标注已装 DLL 是否已是中文版（避免白点）
                    if (_chineseCache.TryGetValue(name, out var zh))
                    {
                        ImGui.SameLine();
                        if (zh > 0)
                            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"（已是中文版·{zh} 条，无需提取）");
                        else
                            Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.4f, 1f), "（原版英文）");
                    }
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextUnformatted(url);
                    ImGui.PopStyleColor();
                    if (_lastResult.TryGetValue(name, out var res))
                    {
                        Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), "　" + res);
                    }
                    ImGui.PopID();
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
        foreach (var (name, _) in _plugins)
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
    {        if (_svc.Running) return;
        _summary = "";
        _svc.SetStatus($"正在拉取 {name} 的仓库…");
        _svc.Running = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, count, funcs, msg) = await _svc.ExtractAsync(name, url);
                _lastResult[name] = (ok ? "✔ " : "✘ ") + msg;
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
}
