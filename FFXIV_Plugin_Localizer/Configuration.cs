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

    /// <summary> 控件标签桩钩子（采集按钮/滑条/复选框等标签）。⚠ 实测会崩游戏（igButton 桩路径，安装器 IconButton 崩溃实锤），默认关。
    /// 崩溃原因未完全定位（疑似打包桩的反向调用/重定位问题），文本钩子（igTextUnformatted/igBegin/igEnd）长期稳定不受影响。重载插件后生效。 </summary>
    public bool LabelHooks { get; set; } = false;

    /// <summary> 安装器替换开关（按对照表在绘制层把插件介绍换成中文，即时生效）。 </summary>
    public bool ReplacementEnabled { get; set; } = true;

    /// <summary> 智谱开放平台 API Key（glm-4-flash 免费模型，OpenAI 兼容端点）。
    /// ⚠ 每个用户自己填，只存本机 pluginConfigs 配置文件（APPDATA），严禁入库/写死在代码里。 </summary>
    public string ZhipuApiKey { get; set; } = "";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
