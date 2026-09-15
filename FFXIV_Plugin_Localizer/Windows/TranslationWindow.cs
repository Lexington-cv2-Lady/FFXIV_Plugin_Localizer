using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> **后台自动翻译**窗口（原「安装器翻译」，2026-09-15 按用户要求改名）：
/// 自动把**插件安装器里各插件的介绍**（Punchline / Description）翻成中文并就地替换显示。
/// 替换层在文字绘制层换字，「全部插件」和「已安装」两个列表都生效，不改启动器文件。
/// 之所以叫「后台自动」：启动后约 10 秒自动扫缺口 → 有 Key 就静默补翻 → 插件更新带来的新文案下次启动自动覆盖，
/// 用户零操作；本窗口主要用来看进度、手动补译、导出/导入翻译包。
/// 表来源：FuckDalamudCN 机翻表（启动自动导入）+ 智谱 AI 自动翻译 + 手动编辑。 </summary>
public sealed class TranslationWindow : Window
{
    private const int MaxManualRows = 200;

    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private readonly FileDialogManager _fileDialog = new();
    private string _summary = "";

    // 手动翻译编辑区状态
    private readonly Dictionary<string, string> _editBufs = new();
    private string _search = "";
    private string _newEn = "";
    private string _newZh = "";

    public TranslationWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("后台自动翻译###PluginLocalizerInstaller")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("**后台自动翻译**：插件安装器里各插件的介绍（简介/描述）自动翻成中文并就地显示，不修改任何启动器/插件文件，\n" +
                "「全部插件」和「已安装」两个列表都生效。\n" +
                "自动化流程：启动约 10 秒后自动扫描缺口 → 已填 API Key 就在后台静默补翻（可在下方关掉）→ 插件更新带来的新文案下次启动自动覆盖。\n" +
                "表来源：FuckDalamudCN 机翻表（启动自动导入）+ 智谱 AI 自动翻译 + 下方手动编辑。");

        var enabled = _plugin.Configuration.ReplacementEnabled;
        if (ImGui.Checkbox("启用安装器替换（即时生效，无需重载）", ref enabled))
        {
            _plugin.Configuration.ReplacementEnabled = enabled;
            _replacement.Enabled = enabled;
            _plugin.Configuration.Save();
            _plugin.AppLog.Info($"[替换] 安装器替换 {(enabled ? "开启" : "关闭")}");
        }

        ImGui.Separator();
        ImGui.Text($"对照表：{_replacement.Count} 条（{ReplacementService.TableFileName}）");

        // ── 自动化开关 ──
        var autoT = _plugin.Configuration.AutoTranslate;
        if (ImGui.Checkbox("启动时自动扫描并翻译缺口", ref autoT))
        {
            _plugin.Configuration.AutoTranslate = autoT;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件更新产生新文案后，下次启动自动扫描并补翻（覆盖「未来更新自动读取」场景）。");
        ImGui.SameLine();
        var silent = _plugin.Configuration.SilentTranslate;
        if (ImGui.Checkbox("后台静默执行", ref silent))
        {
            _plugin.Configuration.SilentTranslate = silent;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：有新缺口直接后台翻译，无感完成（仅写日志）。\n关闭：只提示有新缺口，等你手动点按钮。");

        // ── 表工具行 ──
        if (ImGui.Button("重新导入机翻表"))
        {
            var added = _replacement.ImportFdcn();
            _summary = added >= 0 ? $"导入完成（本次新增 {added} 条），当前 {_replacement.Count} 条。" : "未找到机翻表（需已安装 FuckDalamudCN）。";
        }
        ImGui.SameLine();
        if (ImGui.Button("扫描缺失翻译"))
        {
            var n = _replacement.ScanInstallerMissing();
            _summary = n >= 0 ? $"已安装插件里还有 {n} 条介绍没有中文，缺口在 {ReplacementService.MissingFileName}" : "扫描失败，见日志。";
        }
        ImGui.SameLine();
        if (ImGui.Button("保存对照表"))
        {
            _replacement.Save();
            _summary = $"对照表已保存：{_replacement.TablePath}";
        }
        ImGui.SameLine();
        if (ImGui.Button("导出翻译包"))
        {
            var defaultName = $"翻译包_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            _fileDialog.SaveFileDialog("导出翻译包", ".json", defaultName, ".json", (ok, path) =>
            {
                if (!ok || string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    var (inst, win) = _replacement.ExportPack(path);
                    _summary = $"已导出：安装器 {inst} 条 + 窗口 {win} 条 → {path}";
                }
                catch (Exception ex)
                {
                    _summary = "导出失败：" + ex.Message;
                    _plugin.AppLog.Error("[替换] 导出翻译包失败：" + ex.Message);
                }
            }, Plugin.PluginInterface.GetPluginConfigDirectory());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把对照表 + 全部窗口翻译导出成一个 json 文件，可备份或分享给别人。");
        ImGui.SameLine();
        if (ImGui.Button("导入翻译包"))
        {
            _fileDialog.OpenFileDialog("选择翻译包（本工具导出包 / FuckDalamudCN 机翻表）", ".json", (ok, paths) =>
            {
                if (!ok || paths.Count == 0) return;
                try
                {
                    var (inst, win) = _replacement.ImportPack(paths[0]);
                    _summary = $"已导入合并（不覆盖已有条目）：安装器 +{inst} 条，窗口 +{win} 条";
                }
                catch (Exception ex)
                {
                    _summary = "导入失败：" + ex.Message;
                    _plugin.AppLog.Error("[替换] 导入翻译包失败：" + ex.Message);
                }
            }, 1, Plugin.PluginInterface.GetPluginConfigDirectory());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("合并导入，不覆盖你已有的翻译（本机编辑优先）。\n支持本工具导出的翻译包，也支持 FuckDalamudCN 的 translations.json。");

        // ── 机翻（AI 供应商/Key/模型在「AI 设置」独立窗口） ──
        var cfg = _plugin.Configuration;
        var keySet = !string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(cfg));
        ImGui.Text($"AI 供应商：{MtTranslateService.CurrentProviderName(cfg)}，Key {(keySet ? "已配置" : "未配置")}");
        if (ImGui.Button("AI 设置"))
        {
            _plugin.ToggleAiSettingsUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("供应商 / API Key / 模型 / 温度 / 批量 / 测试连接。");
        ImGui.SameLine();
        if (_mt.Running)
        {
            ImGui.TextDisabled(_mt.Status);
        }
        else
        {
            if (ImGui.Button("自动翻译缺失条目"))
            {
                if (!keySet)
                {
                    _summary = "请先在「AI 设置」填写当前服务商的 API Key。";
                }
                else
                {
                    _summary = "";
                    _mt.Start();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把当前缺口分批送 AI 翻译，结果自动并入对照表并保存。");
        }
        if (!_mt.Running && _mt.Status.Length > 0)
        {
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _mt.Status);
        }

        if (_summary.Length > 0)
        {
            ImGui.Spacing();
            Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _summary);
        }

        // ── 手动翻译（仿旧项目详情区：内联编辑，失焦即存） ──
        if (ImGui.CollapsingHeader("手动翻译（对照表条目，失焦即存）"))
        {
            DrawManualEditor();
        }

        // 文件选择对话框（导出/导入用，须每帧调用）
        _fileDialog.Draw();
    }

    private void DrawManualEditor()
    {
        ImGui.InputTextWithHint("##手动筛选", "筛选：英文或中文包含…", ref _search, 128);

        // 新增条目行
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##新增英", "新增英文原文", ref _newEn, 1024);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##新增中", "中文译文", ref _newZh, 1024);
        ImGui.SameLine();
        if (ImGui.Button("添加") && _newEn.Trim().Length >= 2 && _newZh.Trim().Length > 0)
        {
            _replacement.SetTranslation(_newEn.Trim(), _newZh.Trim());
            _replacement.Save();
            _plugin.AppLog.Info($"[替换] 手动添加对照：{_newEn.Trim()}");
            _newEn = "";
            _newZh = "";
        }

        var search = _search.Trim();
        var entries = _replacement.GetEntries();
        var shown = 0;
        var filtered = 0;
        var avail = ImGui.GetContentRegionAvail();
        using (var list = ImRaii.Child("##手动翻译列表", new Vector2(-1f, Math.Max(120f, avail.Y)), true))
        {
            if (list.Success)
            {
                foreach (var (en, zh) in entries)
                {
                    if (search.Length > 0 &&
                        !en.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                        !zh.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    filtered++;
                    if (shown >= MaxManualRows) continue;
                    shown++;

                    if (!_editBufs.TryGetValue(en, out var buf))
                    {
                        buf = zh;
                        _editBufs[en] = buf;
                    }

                    ImGui.PushID(en);
                    // 英文原文（明确标注、灰色只读；本插件窗口已免疫替换，永远显示原样英文供对照）
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextUnformatted("原文");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.TextWrapped(en);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("删除"))
                    {
                        _replacement.RemoveTranslation(en);
                        _replacement.Save();
                        _editBufs.Remove(en);
                        ImGui.PopID();
                        break; // 删除后本帧列表已失效，下帧重画
                    }

                    // 中文译文（内联编辑，失焦即存）
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.85f, 1f, 1f));
                    ImGui.TextUnformatted("译文");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##中文", ref buf, 1024))
                    {
                        _editBufs[en] = buf;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            _replacement.SetTranslation(en, buf.Trim());
                            _replacement.Save();
                        }
                    }
                    ImGui.Spacing();
                    ImGui.PopID();
                }

                if (filtered > MaxManualRows)
                {
                    ImGui.TextDisabled($"……共 {filtered} 条，仅显示前 {MaxManualRows} 条，请用筛选缩小范围");
                }
                if (filtered == 0)
                {
                    Ui.Hint(search.Length > 0 ? "没有匹配的条目。" : "对照表是空的。可导入机翻表、自动翻译或手动添加。");
                }
            }
        }
    }
}
