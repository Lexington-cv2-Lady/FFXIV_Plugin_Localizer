using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 安装器翻译窗口（独立窗口）：插件安装器里插件介绍的中文化。
/// 替换层按对照表在文字绘制层换字，「全部插件」和「已安装」两个列表都生效，不改启动器文件。
/// 自动化：FDCN 机翻表启动即导入；填 Key 后自动翻译缺口；插件更新后的新文案启动时自动扫描翻译（可关）。
/// 手动翻译：仿旧项目详情区——对照表条目直接内联编辑中文，失焦即存。 </summary>
public sealed class TranslationWindow : Window
{
    private const int MaxManualRows = 200;

    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private string _summary = "";

    // 手动翻译编辑区状态
    private readonly Dictionary<string, string> _editBufs = new();
    private string _search = "";
    private string _newEn = "";
    private string _newZh = "";

    public TranslationWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("安装器翻译###PluginLocalizerInstaller")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("让插件安装器里的插件介绍显示中文：替换层按对照表在文字绘制层换字，不修改任何启动器/插件文件，\n" +
                "「全部插件」和「已安装」两个列表都生效。表来源：FuckDalamudCN 机翻表（启动自动导入）+ 智谱自动翻译 + 手动编辑。");

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

        // ── 机翻（智谱） ──
        var key = _plugin.Configuration.ZhipuApiKey;
        ImGui.SetNextItemWidth(Math.Max(240f, ImGui.GetContentRegionAvail().X - 180f));
        ImGui.InputTextWithHint("##MtKey", "智谱 API Key（bigmodel.cn，glm-4-flash 免费；只存本机）", ref key, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _plugin.Configuration.ZhipuApiKey = key.Trim();
            _plugin.Configuration.Save();
            // 填完 Key 自动开始翻译缺口（用户要求：填写后后台自动翻译）
            if (_plugin.Configuration.AutoTranslate && key.Trim().Length > 0 && !_mt.Running)
            {
                _mt.Start();
            }
        }
        ImGui.SameLine();
        if (_mt.Running)
        {
            ImGui.TextDisabled(_mt.Status);
        }
        else
        {
            if (ImGui.Button("自动翻译缺失条目"))
            {
                if (string.IsNullOrWhiteSpace(_plugin.Configuration.ZhipuApiKey))
                {
                    _summary = "请先填智谱 API Key（免费，bigmodel.cn 注册后创建）。";
                }
                else
                {
                    _summary = "";
                    _mt.Start();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把当前缺口分批送智谱 glm-4-flash 翻译，结果自动并入对照表并保存。");
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
                    // 原文（灰字）+ 删除
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    ImGui.TextWrapped(en);
                    ImGui.PopStyleColor();
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
