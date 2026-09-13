using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 文案扫描窗口（独立窗口）：静态扫描本机已安装插件 DLL 的字符串堆，按插件提取英文文案候选。
/// 运行时采集只能覆盖实际打开过的窗口，这里补全「装了但没开过」的插件的文案；
/// 已内置中文（如 Artisan 双语汉化版）或自带语言文件（如 DailyRoutines）的插件自动跳过。 </summary>
public sealed class ScanWindow : Window
{
    private const int PreviewCount = 60;

    private readonly Plugin _plugin;
    private readonly PluginScanService _scan;
    private List<PluginScanService.InstalledPlugin> _plugins = new();
    private readonly Dictionary<string, PluginScanService.ScanOutcome> _results = new();
    private string _summary = "";
    private bool _listLoaded;

    public ScanWindow(Plugin plugin, PluginScanService scan)
        : base("文案扫描###PluginLocalizerScan")
    {
        _plugin = plugin;
        _scan = scan;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!_listLoaded) RefreshList();

        Ui.Hint("扫描本机已安装插件（installedPlugins + devPlugins）的 DLL 字符串堆，静态提取英文界面文案候选。" +
                "结果写入 数据目录\\文案扫描\\<插件名>_未翻译.json，供翻译管线使用。" +
                "已内置近乎完整中文双语（如 Artisan，按中/英文条数占比判定）或自带语言文件（如 DailyRoutines）的插件自动跳过。");

        if (ImGui.Button("扫描全部插件"))
        {
            ScanAll();
        }
        ImGui.SameLine();
        if (ImGui.Button("刷新列表"))
        {
            RefreshList();
        }
        ImGui.SameLine();
        if (ImGui.Button("打开输出目录"))
        {
            OpenOutputDir();
        }
        if (_summary.Length > 0)
        {
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _summary);
        }

        var avail = ImGui.GetContentRegionAvail();
        using (var list = ImRaii.Child("##扫描列表", new Vector2(-1f, avail.Y), true))
        {
            if (list.Success)
            {
                if (_plugins.Count == 0)
                {
                    Ui.Hint("没有找到已安装的插件（未发现 installedPlugins/devPlugins 下的插件 DLL）。");
                }
                else
                {
                    for (var i = 0; i < _plugins.Count; i++)
                    {
                        ImGui.PushID(i);
                        var p = _plugins[i];
                        if (ImGui.CollapsingHeader(MakeLabel(p)) && _results.TryGetValue(p.Name, out var r) && !r.Skipped)
                        {
                            for (var j = 0; j < r.Strings.Count && j < PreviewCount; j++)
                                ImGui.TextWrapped(r.Strings[j]);
                            if (r.Strings.Count > PreviewCount)
                                ImGui.TextDisabled($"……其余 {r.Strings.Count - PreviewCount} 条见输出文件");
                        }
                        ImGui.PopID();
                    }
                }
            }
        }
    }

    private string MakeLabel(PluginScanService.InstalledPlugin p)
    {
        if (!_results.TryGetValue(p.Name, out var r)) return $"{p.Name}（未扫描）";
        if (r.SkipBilingualZh) return $"{p.Name}（已内置中文 {r.ChineseCount} 条，中/英约 {r.ChineseRatioPercent}%，跳过）✔";
        if (r.SkipLangFiles) return $"{p.Name}（自带语言文件，跳过）✔";
        return $"{p.Name}（{r.Strings.Count} 条）";
    }

    private void RefreshList()
    {
        try
        {
            _plugins = _scan.ListInstalled();
            _listLoaded = true;
            _summary = $"已找到 {_plugins.Count} 个已安装插件。";
        }
        catch (Exception ex)
        {
            _summary = "枚举已安装插件失败：" + ex.Message;
            _plugin.AppLog.Error("[扫描] " + _summary);
        }
    }

    private void ScanAll()
    {
        RefreshList();
        var withStrings = 0;
        var skipped = 0;
        var total = 0;
        foreach (var p in _plugins)
        {
            try
            {
                var r = _scan.ScanAndSave(p);
                _results[p.Name] = r;
                if (r.Skipped)
                {
                    skipped++;
                }
                else
                {
                    total += r.Strings.Count;
                    if (r.Strings.Count > 0) withStrings++;
                }
            }
            catch (Exception ex)
            {
                _plugin.AppLog.Error($"[扫描] {p.Name} 扫描失败：{ex.Message}");
            }
        }
        _summary = $"扫描完成：{_plugins.Count} 个插件，{skipped} 个已汉化跳过，{withStrings} 个有英文文案候选，共 {total} 条，已写入 {_scan.OutputDir}";
        _plugin.AppLog.Info("[扫描] " + _summary);
    }

    private void OpenOutputDir()
    {
        try
        {
            Directory.CreateDirectory(_scan.OutputDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_scan.OutputDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _plugin.AppLog.Error("[扫描] 打开输出目录失败：" + ex.Message);
        }
    }
}
