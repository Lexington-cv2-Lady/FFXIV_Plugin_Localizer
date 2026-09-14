using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> AI 设置窗口（抄自旧项目并本地化）：供应商 / Key（密文+显隐+粘贴）/ 模型 / 温度 / 批量 / 测试连接。
/// 默认智谱 GLM（glm-4-flash 模型免费）；12 家预设 + 自定义端点。Key 按服务商独立分存、只存本机。 </summary>
public sealed class AiSettingsWindow : Window
{
    private readonly Plugin _plugin;
    private readonly MtTranslateService _mt;
    private string _testResult = "";
    private Task<string>? _testTask;
    private bool _showKey; // API Key 明文显示开关

    public AiSettingsWindow(Plugin plugin, MtTranslateService mt) : base("AI 设置###PluginLocalizerAi")
    {
        _plugin = plugin;
        _mt = mt;
        Size = new Vector2(600, 470);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        ImGui.TextWrapped("配置 OpenAI 兼容接口（智谱 / 通义 / 混元 / 千帆 / DeepSeek / GPT 等），用于安装器介绍的自动翻译。\n" +
                          "免费推荐：智谱 GLM——注册 bigmodel.cn 创建 Key 填入即可，glm-4-flash 模型免费。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 供应商
        ImGui.TextUnformatted("供应商：");
        var currentName = MtTranslateService.CurrentProviderName(cfg);
        if (ImGui.BeginCombo("##Provider", currentName))
        {
            foreach (var item in MtTranslateService.Providers)
            {
                var sel = item.Name == currentName;
                if (ImGui.Selectable(item.Name, sel))
                {
                    cfg.AiProviderName = item.Name;
                    cfg.AiBaseUrl = ""; // 切换供应商清空覆盖，回预设
                    cfg.AiModel = "";
                    cfg.Save();
                    _testResult = $"已切换为「{item.Name}」，Key 按服务商独立保存";
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(item.Note);
                if (sel) ImGui.SetItemDefaultFocus();
            }
            if (ImGui.Selectable("自定义（手工填写 API 地址与模型）", currentName == "自定义"))
            {
                cfg.AiProviderName = "";
                cfg.Save();
                _testResult = "已切换为「自定义」，需在下方手填 API 地址与模型";
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        // BaseUrl 覆盖
        ImGui.TextUnformatted("API 地址（留空 = 供应商预设）：");
        var baseUrl = cfg.AiBaseUrl;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##AiBaseUrl", ref baseUrl, 512))
        {
            cfg.AiBaseUrl = baseUrl;
            cfg.Save();
        }
        Ui.Hint($"当前生效：{MtTranslateService.ResolveEndpoint(cfg).BaseUrl}/chat/completions");

        ImGui.Spacing();

        // API Key（密文 + 显隐 + 粘贴）
        ImGui.TextUnformatted("API Key（按服务商独立保存，只存本机）：");
        var key = MtTranslateService.GetApiKey(cfg);
        var showW = 52f;
        var pasteW = 52f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - pasteW - showW - 16f));
        var keyFlags = _showKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##AiKey", ref key, 512, keyFlags))
        {
            MtTranslateService.SetApiKey(cfg, key);
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(_showKey ? "隐藏" : "显示", new Vector2(showW, 0)))
        {
            _showKey = !_showKey;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_showKey ? "隐藏 API Key（恢复密文）" : "显示 API Key 明文（确认是否输错）");
        ImGui.SameLine();
        if (ImGui.Button("粘贴", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                MtTranslateService.SetApiKey(cfg, clip);
                cfg.Save();
                _testResult = "已从剪贴板粘贴 API Key";
            }
            else
            {
                _testResult = "剪贴板为空，粘贴失败";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("直接读取剪贴板填入，无需 Ctrl+V（避开游戏内焦点问题）");

        ImGui.Spacing();

        // 模型
        ImGui.TextUnformatted("模型（留空 = 供应商预设）：");
        var model = cfg.AiModel;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##AiModel", ref model, 256))
        {
            cfg.AiModel = model;
            cfg.Save();
        }
        Ui.Hint($"当前生效：{MtTranslateService.ResolveEndpoint(cfg).Model}");

        ImGui.Spacing();

        // 温度 + 批量
        var temp = cfg.AiTemperature;
        if (ImGui.SliderFloat("温度（越低越忠实原文）", ref temp, 0f, 1f))
        {
            cfg.AiTemperature = temp;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.Save(); // 拖动结束才落盘，避免拖动过程每帧写配置文件
        }
        var batch = cfg.AiBatchSize;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputInt("单批条数", ref batch, 10, 50))
        {
            cfg.AiBatchSize = Math.Clamp(batch, 1, 200);
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── wiki 官方术语表（术语优先于机翻，物品/技能名必须用官方译名） ──
        ImGui.TextUnformatted("wiki 术语表（官方译名，优先级最高）：");
        var wikiOn = cfg.WikiEnabled;
        if (ImGui.Checkbox("启用 wiki 术语表", ref wikiOn))
        {
            cfg.WikiEnabled = wikiOn;
            cfg.Save();
            _plugin.ReloadWiki(); // 立即生效
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("启用后，命中术语的英文一律用官方译名（如物品名、技能名），\n优先于机翻和普通对照表。中文化界面时更准确统一。");

        var wikiDir = cfg.WikiDir;
        ImGui.SetNextItemWidth(Math.Max(200f, ImGui.GetContentRegionAvail().X - 60f));
        if (ImGui.InputTextWithHint("##WikiDir", "术语表目录（含 物品.json / 技能_动作.json 等）", ref wikiDir, 512))
        {
            cfg.WikiDir = wikiDir.Trim();
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("加载"))
        {
            var n = _plugin.ReloadWiki();
            _testResult = n > 0
                ? $"wiki 术语已加载 {n} 条并生效（各类：{string.Join("、", _plugin.Wiki.CategoryCounts.Select(kv => $"{kv.Key} {kv.Value}"))}）"
                : $"未在目录中找到术语文件：{cfg.WikiDir}";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("从该目录读取全部术语 json（旧项目维护的 6 类官方译名对照）。");

        if (_plugin.Wiki.Loaded)
            Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f),
                $"✔ 已加载 {_plugin.Wiki.Count} 条官方术语（{string.Join("、", _plugin.Wiki.CategoryCounts.Select(kv => $"{kv.Key} {kv.Value}"))}）");
        else
            Ui.Hint("未加载术语表。可把旧项目的 wiki_术语对照 目录内容放入上方目录后点「加载」。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 测试连接
        if (_testTask != null && !_testTask.IsCompleted)
        {
            ImGui.TextDisabled("测试中…");
        }
        else
        {
            if (ImGui.Button("测试连接"))
            {
                _testResult = "";
                _testTask = Task.Run(() => _mt.TestAsync());
            }
            ImGui.SameLine();
            if (ImGui.Button("保存设置"))
            {
                cfg.Save();
                _testResult = "设置已保存";
            }
            ImGui.Spacing();
            if (_testTask != null && _testTask.IsCompleted)
            {
                _testResult = _testTask.Result;
                _testTask = null;
            }
            if (_testResult.Length > 0)
                Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), _testResult);
            else
                Ui.Hint("测试结果将显示在这里。");
        }
    }
}
