using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FFXIVPluginLocalizer;

/// <summary> 插件配置（Dalamud 托管，存 pluginConfigs\FFXIV_Plugin_Localizer.json）。 </summary>
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    // ── wiki 术语表（官方译名；来自旧项目维护的 6.2 万条对照：物品/技能/任务/成就/种族/情感动作）──
    /// <summary> 是否启用 wiki 术语表。启用后术语优先于机翻与普通对照（物品名机翻几乎必错，用官方译名）。 </summary>
    public bool WikiEnabled { get; set; } = true;
    /// <summary> wiki 术语表目录（含 任务.json / 物品.json 等的目录）。 </summary>
    public string WikiDir { get; set; } = "";

    /// <summary>
    /// **本项目自己的**词典目录（含「我的翻译.json」等），用于「预翻译」套用现成译文。
    /// ⚠ 与旧项目**完全独立**：不读取/写入旧项目的词典目录。默认 = 插件数据目录\词典目录（留空即用默认）。
    /// （wiki 官方术语表是另一回事，它按官方译名联动旧项目，属只读共享。）
    /// </summary>
    public string DictDir { get; set; } = "";

    /// <summary> 报错日志导出目录（空 = 用插件数据目录 pluginConfigs\&lt;ID&gt;\）。 </summary>
    public string LogExportPath { get; set; } = "";

    /// <summary> ImGui 钩子总开关（关掉 = 只用静态扫描，对游戏 UI 零干扰；重载插件后生效）。 </summary>
    public bool HooksEnabled { get; set; } = true;

    /// <summary> 钩子调试日志（排查"表里有译文但界面没变"）：开启后每 5 秒输出各钩子调用次数与查表命中情况。 </summary>
    public bool DebugHookLog { get; set; }

    /// <summary> 控件标签桩（滑条/复选框/下拉框等标签的替换）。
    /// 只挂「指针+基本类型」签名的安全控件（不含 ImVec2 的 igButton/igSelectable）；界面异常时关它重载可单独排除。 </summary>
    public bool WidgetHooks { get; set; } = true;

    // ── 源码提取（访问 GitHub）──
    /// <summary> 是否允许访问 GitHub 拉取插件源码。**必须勾选且填了端口才放行**（见 CanAccessGitHub）。 </summary>
    public bool UseProxy { get; set; }

    /// <summary> 代理协议（http / socks5）。 </summary>
    public string ProxyScheme { get; set; } = "http";

    /// <summary> 代理地址主机（默认本机回环）。 </summary>
    public string ProxyHost { get; set; } = "127.0.0.1";

    /// <summary> 代理端口（如 Clash 默认 7890）。用户通常只需填这一项。 </summary>
    public string ProxyPort { get; set; } = "";

    /// <summary> 组装完整代理地址：scheme://host:port。 </summary>
    public string ProxyAddress
    {
        get
        {
            var scheme = string.IsNullOrWhiteSpace(ProxyScheme) ? "http" : ProxyScheme.Trim();
            var host = string.IsNullOrWhiteSpace(ProxyHost) ? "127.0.0.1" : ProxyHost.Trim();
            return $"{scheme}://{host}:{ProxyPort?.Trim()}";
        }
    }

    /// <summary> 旧字段兼容：早期版本直接存完整地址字符串（读取后自动拆解到新字段）。 </summary>
    public string? ProxyAddressLegacy { get; set; }

    /// <summary>
    /// 是否**可以尝试**访问 GitHub（仅表示表单合法，**不代表网络真的通**）。
    /// 勾选代理 → 需填端口；不勾选 → 直连（部分网络环境可直接访问 GitHub）。
    /// 真实可用性必须靠「测试连接」确认（SourceExtractService.TestConnectionAsync）。
    /// </summary>
    public bool CanAccessGitHub => !UseProxy || !string.IsNullOrWhiteSpace(ProxyPort);

    /// <summary> 安装器替换开关（按对照表在绘制层把插件介绍换成中文，即时生效）。 </summary>
    public bool ReplacementEnabled { get; set; } = true;

    /// <summary> 【Penumbra 专用开关】是否对 Penumbra 插件做任何翻译。
    /// 默认**关**（2026-09-25 司令官要求）：关 = 本插件完全不翻译 Penumbra 的任何内容
    /// （模组名、UI 标签、说明文字一律保持英文原文）；开 = 照常对 Penumbra 做全局翻译。
    /// ⚠ 判定依据：渲染时按当前 ImGui 窗口名是否含 "Penumbra" 决定是否抑制（见 ImGuiHookService）。
    ///   开关即时生效（运行时按窗口上下文判断），不改动任何翻译表/核心逻辑。
    ///   代价：关掉时 Penumbra 的 UI 也不会被翻（Penumbra 目前没有专门窗口表，影响有限）。 </summary>
    public bool TranslatePenumbra { get; set; }

    /// <summary> 【联动旧项目单词黑名单】开启后，自动探测旧项目（FFXIV 模组汉化工具）的词典目录，
    /// 把它的「单词黑名单.json」**并入**本插件黑名单（命中词保持英文，不翻译、不替换）。
    /// 默认**开**（2026-09-25 司令官报 bug：Penumbra 里旧项目已拉黑的 MOD 专名——Lavabod/YAB/TBSE 等——
    /// 被本插件机翻/替换成了中文）。与「翻译 Penumbra」开关正交：那个管"翻不翻 Penumbra"，
    /// 这个管"翻了也要保住旧项目拉黑的专名"。
    /// ⚠ ①只**读**旧项目文件，从不修改；探测不到旧项目（未装/未配词典目录）时自动跳过、无任何影响。
    ///   ②黑名单是**整串精确匹配**（大小写不敏感），只拦"整个标签恰好等于该词"的情况，
    ///   不会误伤含该词的长句（如拉黑 Lava 不影响 "Lava Burst"）。
    ///   ③只联动旧项目的「单词黑名单.json」（专名保留）；**不联动**其 wiki_术语对照_黑名单.json
    ///   （那是旧项目内部 wiki 加载逻辑，含 yes/no/play/cancel 等基础词，全局拉黑会把别的插件的
    ///   "取消/播放/开始"也错留英文）。 </summary>
    public bool LinkOldProjectBlacklist { get; set; } = true;

    /// <summary> 智谱开放平台 API Key（旧版单一字段，已迁移到按服务商分存的 AiApiKeys；保留字段仅为兼容旧配置文件）。 </summary>
    public string ZhipuApiKey { get; set; } = "";

    // ── AI 设置（OpenAI 兼容端点；默认智谱 glm-4-flash 免费模型，12 家预设见 MtTranslateService.Providers） ──
    /// <summary> AI 供应商名（预设表名称；空 = 自定义手工端点）。 </summary>
    public string AiProviderName { get; set; } = "智谱 GLM";
    /// <summary> 自定义模式下用户给端点起的显示名（空则显示「自定义」）。仅用于 UI 展示与 Key 分存键，不影响端点解析。 </summary>
    public string AiCustomName { get; set; } = "";
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

    /// <summary> 后台自动翻译**主开关**（总闸）。关了则启动时不自动扫描缺口、不自动翻译（一切自动行为都停）。
    /// 默认**关**（2026-09-18 用户要求：全部默认关闭，需用户主动勾选）。 </summary>
    public bool AutoTranslate { get; set; }

    /// <summary> 【子开关，仅主开关开启时生效】主动预翻卫月仓库**全部**插件（含未安装）的介绍。
    /// 默认**关**——关则只翻已安装插件的介绍；勾则启动检查时拉取仓库清单，把未安装插件的
    /// Description/Punchline 也送翻（用于分包前提前预翻常用插件）。
    /// ⚠ **很花钱**：勾选会拉取您配置的仓库（主库+启用的第三方）清单，把其中**未安装**插件的介绍也送翻。
    ///   实测司令官这套配置共约 500 个插件、其中约 480 个未安装——量不小；且**只有用付费模型才花钱**
    ///   （免费 glm-4-flash 为 0 元）。2026-09-23 司令官误开此开关 + 用了付费模型，白花 30 元，已在界面常驻红色高亮警告。平时务必保持关闭。 </summary>
    public bool AutoExtractAllPlugins { get; set; }

    /// <summary> 后台静默执行：有新缺口直接后台翻译不问人；关闭则只提示等手动点。默认**关**（2026-09-18：全部默认关）。 </summary>
    public bool SilentTranslate { get; set; }

    /// <summary> 机翻**结束后自动**把新译文沉淀进「词典目录\我的翻译.json」（只增不覆盖已有键）。
    /// 默认**开**——用户老是忘记手动点「翻译结果写入词典」；想关就取消勾选（2026-09-19）。 </summary>
    public bool AutoMergeToDict { get; set; } = true;

    /// <summary> 【F4 自动翻译闭环】开启后，运行时发现并翻好的译文除写进词典外，还会：
    /// ①**注入生效替换表**（作**最低兜底**第四源：窗口 &gt; 安装器 &gt; wiki &gt; 词典，界面才会真正变中文）；
    /// ②从"未命中队列"移除（不再重复送翻、不再白烧配额）。
    /// 默认**关**（2026-09-24 F4 v2 裁决；亦合 09-18 用户要求「全部默认关」）。
    /// ⚠ **两步必须配套**：只做②不做①会造成「不再送翻、界面却仍英文」，问题被藏起来，
    /// 且该原文不再进入「运行时发现」，等于**永久失去补救机会**——故二者同由本开关控制、同步生效。
    /// 关闭时行为与改动前完全一致（译文仍照常写词典，只是不进生效表、不清队列）。 </summary>
    public bool AutoClosureEnabled { get; set; }

    // 2026-09-19 运行时发现自动翻门槛（**满足任意一个**即送机翻，防看两眼就删白烧额度）：
    //   ①同一英文累计出现 MinHits 次（反复看到=真在用）；②距首次出现满 MinAgeSec 秒（装稳了没卸）。
    public int MissedMinHits { get; set; } = 3;
    public int MissedMinAgeSec { get; set; } = 180;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
