using System;
using System.IO;
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

        // 自定义模式：让用户给端点起个名（Key 按这个名分存）——仅在当前选了「自定义」时才显示
        if (MtTranslateService.IsCustomProvider(cfg))
        {
            ImGui.TextUnformatted("自定义名称：");
            var customName = cfg.AiCustomName;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##AiCustomName", "给这个自定义端点起个名（如「我的智谱中转」）", ref customName, 64))
            {
                cfg.AiCustomName = customName.Trim();
                cfg.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("自定义模式下给这个端点起个好认的名字（Key 会按这个名字保存）。留空则显示「自定义」。");
            ImGui.Spacing();
        }

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
        string key = MtTranslateService.GetApiKey(cfg);
        var showW = 52f;
        var pasteW = 52f;
        // Key 框宽按密文长度自适应：短 Key 窄、长 Key 放宽（封顶），不再占满整行
        var charW = ImGui.CalcTextSize("A").X;
        var keyW = Math.Clamp(key.Length * charW + 44f, 160f, 320f);
        ImGui.SetNextItemWidth(keyW);
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
