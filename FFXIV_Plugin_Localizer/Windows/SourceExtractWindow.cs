using System;
using System.Collections.Generic;
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
        Ui.Hint("汉化是获取插件公开源码提取的，闭源的无法翻译，同时也需要用户挂着梯子才能进行汉化。");

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
        ImGui.SetNextItemWidth(70f);
        var host = cfg.ProxyHost;
        if (ImGui.InputText("##ProxyHost", ref host, 64)) // 默认 127.0.0.1，通常无需改
        {
            cfg.ProxyHost = host.Trim();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) cfg.Save();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        var port = cfg.ProxyPort;
        if (ImGui.InputTextWithHint("##ProxyPort", "端口", ref port, 8))
        {
            // 只允许数字，防止误填整串地址
            cfg.ProxyPort = new string(port.Where(char.IsDigit).ToArray());
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) cfg.Save();
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

        // 状态灯：条件是否满足一目了然
        if (cfg.CanAccessGitHub)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"✔ 已启用代理：{cfg.ProxyAddress}（可以访问 GitHub 拉取源码）");
        else if (!cfg.UseProxy)
            Ui.ColoredWrapped(new Vector4(1f, 0.6f, 0.35f, 1f), "✘ 未勾选「启用代理」：不能访问 GitHub（即使填了端口也不会联网）。");
        else
            Ui.ColoredWrapped(new Vector4(1f, 0.6f, 0.35f, 1f), "✘ 已勾选启用代理，但端口为空：请填写端口后再试。");

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
            _plugins = _svc.ListPluginsWithRepo();
            _listLoaded = true;
        }
        ImGui.TextDisabled($"已安装且带 GitHub 地址的插件：{_plugins.Count} 个");
        ImGui.SameLine();
        if (ImGui.Button("刷新列表##src"))
        {
            _plugins = _svc.ListPluginsWithRepo();
        }

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
