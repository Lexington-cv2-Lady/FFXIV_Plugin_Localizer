using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 日志窗口（逻辑复刻自旧项目、命名本地化）：环形操作日志（新→旧）占主区，底部固定一条「最近报错」；
/// 支持一键导出「报错日志」zip（汉化日志.log + dalamud.log），导出目录默认插件数据目录、可修改。 </summary>
public class LogWindow : Window
{
    private readonly Plugin _plugin;
    private readonly AppLog _log;
    private readonly FileDialogManager _fileDialog = new();
    private bool _autoScroll = true;
    private string _openMsg = "";
    private string _exportMsg = "";

    public LogWindow(Plugin plugin) : base("日志###PluginLocalizerLog")
    {
        Size = new Vector2(680, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _plugin = plugin;
        _log = plugin.AppLog;
    }

    /// <summary> 导出目录：配置了用配置，否则默认插件数据目录（pluginConfigs\&lt;ID&gt;\）。 </summary>
    private string ExportDir => string.IsNullOrWhiteSpace(_plugin.Configuration.LogExportPath)
        ? Plugin.PluginInterface.GetPluginConfigDirectory()
        : _plugin.Configuration.LogExportPath.Trim();

    public override void Draw()
    {
        // ── 顶部工具条 ──
        ImGui.Checkbox("自动滚动", ref _autoScroll);
        ImGui.SameLine();
        if (ImGui.Button("清空日志"))
        {
            _log.Clear();
            _openMsg = "";
        }
        ImGui.SameLine();
        if (ImGui.Button("打开日志文件"))
        {
            OpenLogFile();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_log.FilePath != null
                ? "用系统默认程序打开日志文件：\n" + _log.FilePath
                : "日志文件不可用（落盘失败，仅内存日志）");
        }
        if (_openMsg.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), _openMsg);
        }
        ImGui.Spacing();

        // ── 导出报错日志：汉化日志 + dalamud.log 打包 zip，发给别人排查问题用 ──
        if (ImGui.Button("导出报错日志"))
        {
            ExportLogs();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把 汉化日志.log 与 Dalamud 的 dalamud.log 打包为「报错日志_时间戳.zip」\n（不含 API Key，可放心发给别人）");
        }
        ImGui.SameLine();
        if (ImGui.Button("打开导出目录"))
        {
            OpenFolder(ExportDir);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("在资源管理器打开导出目录");
        }
        if (_exportMsg.Length > 0)
        {
            ImGui.TextWrapped(_exportMsg);
        }

        DrawExportDirRow();

        ImGui.Spacing();

        // ── 日志列表（可滚动，占主区） ──
        // 注意：必须用块级 using 让 Child 在此处结束；若用 using 声明，EndChild 会推迟到方法末尾，
        // 底部报错条就会被画进滚动列表内部（导致列表里日志后面全是报错、窗口底部却空一块）。
        var avail = ImGui.GetContentRegionAvail();
        var bottomH = 76f * ImGuiHelpers.GlobalScale;
        var listH = Math.Max(80f, avail.Y - bottomH - ImGui.GetStyle().ItemSpacing.Y * 2f);
        using (var list = ImRaii.Child("##LogList", new Vector2(0, listH), true))
        {
            if (list.Success)
            {
                // 画内容前取上一帧滚动位置：用于判断是否「用户本就在顶部」，避免自动滚动每帧强制回顶
                var wasAtTop = ImGui.GetScrollY() <= 4f;
                var entries = _log.Snapshot();
                foreach (var e in entries)
                {
                    var color = e.Lv switch
                    {
                        AppLog.Level.Error => new Vector4(1f, 0.45f, 0.45f, 1f),
                        AppLog.Level.Warn => new Vector4(1f, 0.8f, 0.4f, 1f),
                        _ => new Vector4(0.85f, 0.9f, 0.95f, 1f)
                    };
                    var tag = e.Lv switch
                    {
                        AppLog.Level.Error => "[错误]",
                        AppLog.Level.Warn => "[警告]",
                        _ => "[信息]"
                    };
                    ImGui.TextColored(color, $"{e.Time:HH:mm:ss} {tag} {e.Text}");
                }
                // 自动滚动（列表新→旧，顶部即最新一条）：仅「开了自动滚动 且 用户本就在顶部」时吸顶。
                // 用户手动上滑看旧日志时 wasAtTop=false，不再每帧强制回顶，可随意停在任意位置。
                if (_autoScroll && wasAtTop)
                {
                    ImGui.SetScrollY(0f);
                }
            }
        }

        // ── 底部报错条（固定在列表之外，长文本自动换行） ──
        ImGui.Spacing();
        using (var err = ImRaii.Child("##LogLastError", new Vector2(0, -1), true))
        {
            if (err.Success)
            {
                if (string.IsNullOrEmpty(_log.LastError))
                {
                    ImGui.TextDisabled("无报错");
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.45f, 1f));
                    ImGui.TextWrapped(_log.LastError);
                    ImGui.PopStyleColor();
                }
            }
        }

        // 文件选择对话框（浏览导出目录用，须每帧调用）
        _fileDialog.Draw();
    }

    /// <summary> 导出报错日志：汉化日志.log + dalamud.log → 报错日志_时间戳.zip。 </summary>
    private void ExportLogs()
    {
        try
        {
            var dir = ExportDir;
            if (string.IsNullOrWhiteSpace(dir))
            {
                _exportMsg = "导出目录不可用：路径为空";
                return;
            }
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, $"报错日志_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");

            var files = new List<(string Src, string ArcName)>();
            if (_log.FilePath is { } pluginLog && File.Exists(pluginLog))
            {
                files.Add((pluginLog, "汉化日志.log"));
            }
            // dalamud.log 与 pluginConfigs 同级（自动兼容 国服 XIVLauncherCN / 国际服 XIVLauncher）
            var cfgDir = Plugin.PluginInterface.GetPluginConfigDirectory();
            var launcherDir = Directory.GetParent(Directory.GetParent(cfgDir)!.FullName!)?.FullName;
            if (!string.IsNullOrEmpty(launcherDir))
            {
                var dalamud = Path.Combine(launcherDir, "dalamud.log");
                if (File.Exists(dalamud))
                {
                    files.Add((dalamud, "dalamud.log"));
                }
            }
            if (files.Count == 0)
            {
                _exportMsg = "没有可导出的日志文件";
                return;
            }
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (src, arcName) in files)
                {
                    zip.CreateEntryFromFile(src, arcName);
                }
            }
            _exportMsg = $"已导出 {files.Count} 个日志 → {zipPath}";
        }
        catch (Exception ex)
        {
            _exportMsg = "导出失败：" + ex.Message;
        }
    }

    /// <summary> 导出目录行：输入框 + 打开/浏览/粘贴 三按钮。 </summary>
    private void DrawExportDirRow()
    {
        ImGui.TextWrapped("导出目录（默认为插件数据目录，可修改）：");
        var path = ExportDir;
        var btnW = 56f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - btnW * 3 - 24f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##ExportPath", ref path, 512))
        {
            _plugin.Configuration.LogExportPath = path;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _plugin.Configuration.Save(); // 修改即保存（回车/失焦时落盘，避免每键写盘）
        }
        ImGui.SameLine();
        if (ImGui.Button("打开##ExportOpen", new Vector2(btnW, 0)))
        {
            OpenFolder(ExportDir);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("用资源管理器打开该目录");
        }
        ImGui.SameLine();
        if (ImGui.Button("浏览##ExportBrowse", new Vector2(btnW, 0)))
        {
            _fileDialog.OpenFolderDialog("选择日志导出目录", (ok, p) =>
            {
                if (ok && !string.IsNullOrWhiteSpace(p))
                {
                    _plugin.Configuration.LogExportPath = p.Trim();
                    _plugin.Configuration.Save();
                }
            });
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("弹出文件夹选择框，选择导出目录");
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##ExportPaste", new Vector2(btnW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                _plugin.Configuration.LogExportPath = clip.Trim();
                _plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("读取剪贴板中的路径填入（无需 Ctrl+V）");
        }
    }

    /// <summary> 用资源管理器打开目录（不存在则先创建）。 </summary>
    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _openMsg = "打开目录失败：" + ex.Message;
        }
    }

    /// <summary> 用系统默认程序打开日志文件（不存在则先建空文件）。 </summary>
    private void OpenLogFile()
    {
        var path = _log.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            _openMsg = "日志文件不可用";
            return;
        }
        try
        {
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, "", Encoding.UTF8);
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            _openMsg = "";
        }
        catch (Exception ex)
        {
            _openMsg = "打开失败：" + ex.Message;
        }
    }
}
