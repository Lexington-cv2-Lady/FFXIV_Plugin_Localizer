using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FFXIVPluginLocalizer;

/// <summary> 插件配置（Dalamud 托管，存 pluginConfigs\FFXIV_Plugin_Localizer.json）。 </summary>
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    /// <summary> 报错日志导出目录（空 = 用插件数据目录 pluginConfigs\&lt;ID&gt;\）。 </summary>
    public string LogExportPath { get; set; } = "";

    /// <summary> ImGui 钩子总开关（关掉 = 只用静态扫描，对游戏 UI 零干扰；重载插件后生效）。 </summary>
    public bool HooksEnabled { get; set; } = true;

    /// <summary> 控件标签桩（滑条/复选框/下拉框等标签的替换）。
    /// 只挂「指针+基本类型」签名的安全控件（不含 ImVec2 的 igButton/igSelectable）；界面异常时关它重载可单独排除。 </summary>
    public bool WidgetHooks { get; set; } = true;

    /// <summary> 安装器替换开关（按对照表在绘制层把插件介绍换成中文，即时生效）。 </summary>
    public bool ReplacementEnabled { get; set; } = true;

    /// <summary> 智谱开放平台 API Key（旧版单一字段，已迁移到按服务商分存的 AiApiKeys；保留字段仅为兼容旧配置文件）。 </summary>
    public string ZhipuApiKey { get; set; } = "";

    // ── AI 设置（OpenAI 兼容端点；默认智谱 glm-4-flash 免费模型，12 家预设见 MtTranslateService.Providers） ──
    /// <summary> AI 供应商名（预设表名称；空 = 自定义手工端点）。 </summary>
    public string AiProviderName { get; set; } = "智谱 GLM";
    /// <summary> API 地址覆盖（留空 = 供应商预设）。 </summary>
    public string AiBaseUrl { get; set; } = "";
    /// <summary> 模型名覆盖（留空 = 供应商预设）。 </summary>
    public string AiModel { get; set; } = "";
    /// <summary> 采样温度（越低越忠实原文）。 </summary>
    public float AiTemperature { get; set; } = 0.1f;
    /// <summary> 单批翻译条数（条目过多自动分批）。 </summary>
    public int AiBatchSize { get; set; } = 10;
    /// <summary> 按服务商分存的 API Key。⚠ 每个用户自己填，只存本机 pluginConfigs（APPDATA），严禁入库/写死在代码里。 </summary>
    public Dictionary<string, string>? AiApiKeys { get; set; }

    /// <summary> 启动时自动扫描缺口并翻译（需已填 Key）。默认开。 </summary>
    public bool AutoTranslate { get; set; } = true;

    /// <summary> 后台静默执行：有新缺口直接后台翻译不问人；关闭则只提示等手动点。默认开。 </summary>
    public bool SilentTranslate { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
