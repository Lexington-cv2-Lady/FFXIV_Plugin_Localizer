using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 安装器翻译窗口（独立窗口）：插件安装器里插件介绍的中文化。
/// 替换层按对照表在文字绘制层换字，「全部插件」和「已安装」两个列表都生效，不改启动器文件。
/// 表来源：①FuckDalamudCN 现成机翻表（439 条）②机翻 API 补缺（待接）。 </summary>
public sealed class TranslationWindow : Window
{
    private readonly Plugin _plugin;
    private readonly ReplacementService _replacement;
    private readonly MtTranslateService _mt;
    private string _summary = "";

    public TranslationWindow(Plugin plugin, ReplacementService replacement, MtTranslateService mt)
        : base("安装器翻译###PluginLocalizerInstaller")
    {
        _plugin = plugin;
        _replacement = replacement;
        _mt = mt;
        Size = new Vector2(560, 430);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        Ui.Hint("让插件安装器里的插件介绍显示中文：替换层按对照表在文字绘制层换字，不修改任何启动器/插件文件，\n" +
                "「全部插件」和「已安装」两个列表都生效。表先从 FuckDalamudCN 机翻表导入，缺口以后接机翻 API 自动补。");

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

        if (ImGui.Button("导入机翻表（FuckDalamudCN）"))
        {
            var added = _replacement.ImportFdcn();
            _summary = added >= 0
                ? $"导入完成，当前对照表 {_replacement.Count} 条。"
                : "未找到机翻表（需要已安装 FuckDalamudCN 插件）。";
        }
        ImGui.SameLine();
        if (ImGui.Button("保存对照表"))
        {
            _replacement.Save();
            _summary = $"对照表已保存：{_replacement.TablePath}";
        }
        ImGui.SameLine();
        if (ImGui.Button("扫描缺失翻译"))
        {
            var n = _replacement.ScanInstallerMissing();
            _summary = n >= 0
                ? $"已安装插件里还有 {n} 条介绍没有中文，缺口清单在 {ReplacementService.MissingFileName}"
                : "扫描失败，见日志。";
        }

        // ── 机翻补缺（智谱 glm-4-flash，免费模型；Key 由用户自己填，只存本机） ──
        ImGui.Separator();
        var key = _plugin.Configuration.ZhipuApiKey;
        ImGui.SetNextItemWidth(Math.Max(240f, ImGui.GetContentRegionAvail().X - 170f));
        if (ImGui.InputText("智谱 API Key##MtKey", ref key, 128))
        {
            _plugin.Configuration.ZhipuApiKey = key.Trim();
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("bigmodel.cn 注册后在控制台创建（glm-4-flash 模型免费）。\nKey 只保存在本机插件配置文件里，不会随插件仓库分发。");
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
                ImGui.SetTooltip("把「扫描缺失翻译」的缺口分批送智谱 glm-4-flash 翻译，\n结果自动并入对照表并保存（免费模型，限速间隔自动处理）。");
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

        ImGui.Spacing();
        Ui.Hint("说明：已安装列表显示的介绍来自本机清单文件，「全部插件」来自仓库主清单——两处原文一致时同一张表都能命中。\n" +
                "替换是精确匹配：原文一个字不差才命中，插件更新改了文案就需要补一条新对照。");
    }
}
