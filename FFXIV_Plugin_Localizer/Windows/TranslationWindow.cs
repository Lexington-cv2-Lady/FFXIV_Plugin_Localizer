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
    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private readonly FileDialogManager _fileDialog = new();
    private string _summary = "";

    public TranslationWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("后台自动翻译###PluginLocalizerInstaller")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(820, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("插件安装器里各插件的介绍（简介/描述）会自动翻成中文并就地显示；不修改任何启动器或插件文件。\n" +
                "「全部插件」和「已安装」两个列表都生效。\n" +
                "自动化流程：启动约 10 秒后自动扫描缺口 → 已填 API Key 就在后台静默补翻（可在下方关掉）→ 插件更新带来的新文案下次启动自动覆盖。\n" +
                "表来源：FuckDalamudCN 机翻表（启动自动导入）+ 智谱 AI 自动翻译 + 下方手动编辑。");

        ImGui.Separator();
        ImGui.Text($"对照表：{_replacement.Count} 条（{ReplacementService.TableFileName}）");

        // ── 自动化开关（2026-09-18：主开关+子开关，全部默认关） ──
        var autoT = _plugin.Configuration.AutoTranslate;
        if (ImGui.Checkbox("后台自动翻译（主开关）", ref autoT))
        {
            _plugin.Configuration.AutoTranslate = autoT;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("总开关：关了则一切自动行为都停（不扫描、不提示、不下载仓库清单）。\n" +
                             "开着才允许下面两个子功能；默认关，需你主动勾选。");

        if (_plugin.Configuration.AutoTranslate)
        {
            ImGui.Indent();
            // ⚠ 2026-09-23 司令官要求：此开关**很花钱**，必须常驻高亮，避免手滑误开
            //   （之前误开把几千个未安装插件的介绍全送付费 AI，白花 30 元）。
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.25f, 1f));
            var extractAll = _plugin.Configuration.AutoExtractAllPlugins;
            if (ImGui.Checkbox("主动预翻仓库全部插件（含未安装）", ref extractAll))
            {
                _plugin.Configuration.AutoExtractAllPlugins = extractAll;
                _plugin.Configuration.Save();
                if (extractAll) _replacement.EnsureRepoCacheAsync(_plugin.BuildRepoUrls());   // 勾上立刻后台拉仓库清单
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("勾了：启动检查不只查已装插件，还拉卫月仓库清单，把**未安装**插件的介绍也送翻" +
                                 "（用于分包前提前预翻常用插件）。\n不勾：只翻已安装插件的介绍。\n" +
                                 "⚠ 仓库有几千个插件，勾选会一次性送翻大量条目，API 费用明显增加。");
            ImGui.TextColored(new Vector4(0.95f, 0.35f, 0.25f, 1f),
                "⚠ 很花钱：会把您配置的仓库（主库+启用的第三方）里几百个【未安装】插件的介绍送进 AI——用付费模型费用明显（免费 glm-4-flash 则 0 元）；仅打包分发前预翻才需要，平时别开！");
            var silent = _plugin.Configuration.SilentTranslate;
            if (ImGui.Checkbox("后台静默执行", ref silent))
            {
                _plugin.Configuration.SilentTranslate = silent;
                _plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("开启：有新缺口直接后台翻译，无感完成（仅写日志）。\n关闭：只提示有新缺口，等你手动点按钮。");

            ImGui.Spacing();
            ImGui.TextUnformatted("运行时发现的英文，何时自动翻（满足任意一条）：");
            var minHits = _plugin.Configuration.MissedMinHits;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("出现次数（次）", ref minHits))
            {
                _plugin.Configuration.MissedMinHits = Math.Max(1, minHits);
                _plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("同一句英文**反复出现**达到此次数才自动翻（默认 3）。\n反复看到=真在用；设为 1 = 出现一次就翻（最积极、最费额度）。");
            ImGui.SameLine();
            var minAge = _plugin.Configuration.MissedMinAgeSec;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("距首次出现（秒）", ref minAge))
            {
                _plugin.Configuration.MissedMinAgeSec = Math.Max(0, minAge);
                _plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("从**首次看到**这句英文起，过这么多秒仍没卸载插件，就自动翻（默认 180=3分钟）。\n防「下载看两眼就删」白烧额度；设 0 = 不看这条。");
            Ui.Hint("两条门槛**满足任意一条**就自动翻：反复出现够多次，或开够久没卸载。两者都不满足（看一眼就关）→ 不翻、不花额度。");
            ImGui.Unindent();
        }

        // ── 表工具行（按钮按需换行，窄窗口不会裁掉）──
        if (ImGui.Button("重新导入机翻表"))
        {
            var added = _replacement.ImportFdcn();
            _summary = added >= 0 ? $"导入完成（本次新增 {added} 条），当前 {_replacement.Count} 条。" : "未找到机翻表（需已安装 FuckDalamudCN）。";
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("扫描缺失翻译"));
        if (ImGui.Button("扫描缺失翻译"))
        {
            var n = _replacement.ScanInstallerMissing();
            _summary = n >= 0 ? $"已安装插件里还有 {n} 条介绍没有中文，缺口在 {ReplacementService.MissingFileName}" : "扫描失败，见日志。";
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("保存对照表"));
        if (ImGui.Button("保存对照表"))
        {
            _replacement.Save();
            _summary = $"对照表已保存：{_replacement.TablePath}";
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("导出翻译包"));
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
        Ui.SameLineIfFits(Ui.ButtonWidth("导入翻译包"));
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

        // ── 手动翻译：按钮打开二级窗口（避免主界面表格挤成一团） ──
        if (ImGui.Button("手动翻译（安装器对照表）"))
        {
            _plugin.ToggleManualEditUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开独立的编辑窗口：修改**插件安装器列表里**各插件介绍（简介/描述）的译文。\n" +
                             "FDCN 机翻表/AI 翻错某条介绍时，在这里手动修。\n" +
                             "（插件配置窗口里的文字在「插件翻译」窗口管，不在这里。）");

        // 文件选择对话框（导出/导入用，须每帧调用）
        _fileDialog.Draw();
    }
}
