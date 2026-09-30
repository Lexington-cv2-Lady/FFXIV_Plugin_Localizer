using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVPluginLocalizer.Services;
using FFXIVPluginLocalizer.Windows;
using Newtonsoft.Json.Linq;

namespace FFXIVPluginLocalizer;

/// <summary> 翻译插件的插件：给其他 Dalamud 插件的界面文本做运行时汉化。 </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName = "/ptp";

    public readonly WindowSystem WindowSystem = new("FFXIVPluginLocalizer");
    public Configuration Configuration { get; init; }
    public AppLog AppLog { get; }
    public ImGuiHookService Hook { get; }
    /// <summary> 游戏原生 UI（AtkAddon）文字汉化：遍历 AtkStage 组件树，覆盖 KamiToolKit 系插件（LazyGatherer 等）的配置窗口。 </summary>
    public AtkNativeUiWalkerService AtkHook { get; }
    public ReplacementService Replacement { get; }
    public MtTranslateService Mt { get; }
    public MainWindow MainWindow { get; }
    public LogWindow LogWindow { get; }
    public TranslationWindow TranslationWindow { get; }
    public AiSettingsWindow AiSettingsWindow { get; }
    public WindowReplaceWindow WindowReplaceWindow { get; }
    public SourceExtractWindow SourceExtractWindow { get; }
    public DictWindow DictWindow { get; }
    public ManualEditWindow ManualEditWindow { get; }
    public RepoWindow RepoWindow { get; }
    public HealthReportWindow HealthReportWindow { get; }
    public PluginEditorWindow PluginEditorWindow { get; }
    public SourceExtractService SourceExtract { get; }
    public WikiGlossaryService Wiki { get; }
    public OldDictionaryService OldDict { get; }

    /// <summary> 卫月仓库链接（只读展示：主库 + 第三方仓库；从 dalamudConfig.json 读，不改卫月任何设置）。 </summary>
    public string DalamudMainRepo { get; private set; } = "";
    public List<(string Url, bool IsEnabled)> DalamudThirdRepos { get; } = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // 代理配置由「完整地址字符串」改为「协议+主机+端口」三段：把旧值拆解过来（一次性迁移）
        if (!string.IsNullOrWhiteSpace(Configuration.ProxyAddressLegacy) &&
            string.IsNullOrWhiteSpace(Configuration.ProxyPort))
        {
            var legacy = Configuration.ProxyAddressLegacy!.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(legacy, @"^(?<scheme>[a-zA-Z0-9]+)://(?<host>[^:/]+):(?<port>\d+)$");
            if (m.Success)
            {
                Configuration.ProxyScheme = m.Groups["scheme"].Value.ToLowerInvariant();
                Configuration.ProxyHost = m.Groups["host"].Value;
                Configuration.ProxyPort = m.Groups["port"].Value;
                Configuration.Save();
                Log.Information($"[迁移] 代理配置已拆解：{Configuration.ProxyAddress}");
            }
        }
        Configuration.ProxyAddressLegacy = null;
        // 旧版单一 ZhipuApiKey → 按服务商分存（一次性迁移）
        if (!string.IsNullOrWhiteSpace(Configuration.ZhipuApiKey))
        {
            Configuration.AiApiKeys ??= new();
            if (!Configuration.AiApiKeys.ContainsKey("智谱 GLM"))
                Configuration.AiApiKeys["智谱 GLM"] = Configuration.ZhipuApiKey.Trim();
            Configuration.ZhipuApiKey = "";
            Configuration.Save();
        }
        AppLog = new AppLog(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "汉化日志.log"));
        ReadDalamudRepos(); // 读卫月仓库链接（只读展示，不改卫月设置）
        // ⚠ 顺序要求：Replacement 必须先建（EnsureWikiDir 内部要用它设置术语），否则会抛 NRE 被吞。
        Replacement = new ReplacementService(AppLog, PluginInterface.GetPluginConfigDirectory, Configuration);
        Replacement.Enabled = Configuration.ReplacementEnabled;
        Replacement.SyncFdcnOnStartup(); // 启动同步：FDCN 文件指纹变了才自动重导；未装 FDCN 用内置翻译包打底
        // wiki 官方术语表（可选）：联动旧项目词典目录，加载后作为替换最高优先级词源 + 机翻参考
        Wiki = new WikiGlossaryService(AppLog);
        EnsureWikiDir();   // 内部：探测目录 → Wiki.Load → Replacement.SetWikiTerms
        // 本项目词典（预翻译用）：独立于旧项目，放本插件数据目录
        OldDict = new OldDictionaryService(AppLog);
        EnsureDictDir();
        Hook = new ImGuiHookService(AppLog, Log, Interop, () => Configuration.HooksEnabled,
            () => Configuration.WidgetHooks, Replacement, Configuration.DebugHookLog,
            () => Configuration.TranslatePenumbra);
        // 原生 UI（AtkAddon）文字汉化：与 ImGui 钩子互补，覆盖 KamiToolKit 系插件的游戏原生配置窗口
        //（SetText 钩子曾被 Reloaded.Hooks 拒编，2026-09-18 改为 500ms 轮询遍历 AtkStage 组件树 + 官方 SetText）
        AtkHook = new AtkNativeUiWalkerService(AppLog, Log, Framework, Replacement);
        Mt = new MtTranslateService(AppLog, Replacement, Configuration);
        Mt.SetWiki(Wiki); // 机翻时附带官方术语对照，保证专有名词译名一致
        Mt.SetBlacklist(OldDict.BlacklistWords); // 机翻侧拉黑：黑名单词不送翻（原在 EnsureDictDir 里，因 Mt 未建而 NRE）
        MainWindow = new MainWindow(this);
        LogWindow = new LogWindow(this);
        TranslationWindow = new TranslationWindow(this, Replacement, Mt);
        AiSettingsWindow = new AiSettingsWindow(this, Mt);
        WindowReplaceWindow = new WindowReplaceWindow(this, Replacement, Mt);
        SourceExtract = new SourceExtractService(AppLog, Configuration, PluginInterface.GetPluginConfigDirectory);
        SourceExtractWindow = new SourceExtractWindow(this, SourceExtract, Replacement);
        DictWindow = new DictWindow(this);
        ManualEditWindow = new ManualEditWindow(this, Replacement);
        RepoWindow = new RepoWindow(this);
        HealthReportWindow = new HealthReportWindow(this);
        PluginEditorWindow = new PluginEditorWindow(this, Replacement, Mt);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(TranslationWindow);
        WindowSystem.AddWindow(AiSettingsWindow);
        WindowSystem.AddWindow(WindowReplaceWindow);
        WindowSystem.AddWindow(SourceExtractWindow);
        WindowSystem.AddWindow(DictWindow);
        WindowSystem.AddWindow(ManualEditWindow);
        WindowSystem.AddWindow(RepoWindow);
        WindowSystem.AddWindow(HealthReportWindow);
        WindowSystem.AddWindow(PluginEditorWindow);
        // 2026-09-19 全自动：钩子抓到未翻译英文 → 开了后台翻译就自动进机翻队列；翻完写词典
        Replacement.MissedCaptured += OnMissedCaptured;
        Mt.MissedBatchTranslated += OnMissedBatchTranslated;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开翻译插件的插件主窗口。/ptp close（或 关窗）：一键关闭其它所有插件的配置窗/主窗（不卸载插件，只收起界面）。"
        });

        PluginInterface.UiBuilder.Draw += DrawAll;
        // 2026-09-17：行为联动——卫月在安装器里卸载插件 → 立即清理其翻译资产（秒级；60 秒轮询兜底）
        Replacement.StartUninstallWatch();
        // 2026-09-19：仓库清单后台拉完 → 立刻启动一轮机翻（原来拉完就结束，要等下次启动才翻）
        Replacement.RepoCacheUpdated += OnRepoCacheUpdated;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain; // 插件安装器的设置按钮：本插件暂无独立设置窗，打开主窗（官方模板同款回调，缺失会在安装器报校验警告）
        Framework.Update += OnFramework;

        AppLog.Info("[插件] 翻译插件的插件 已加载");
        Log.Information("翻译插件的插件 已加载");
    }

    /// <summary> 统一绘制：给所有窗口加明显边框（与旧项目同风格）。 </summary>
    private void DrawAll()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.72f, 1f, 0.9f));
        try
        {
            // ⚠ 本插件自己的窗口**绝不参与替换**：否则窗口文字翻译编辑器里的「英文原文」会被自己的表
            //   替换成中文，对照参照消失（用户投诉过）。ImGui 是即时模式，文字绘制发生在本调用内，
            //   用一个抑制标志即可精确覆盖。
            Hook.SuppressReplacement = true;
            WindowSystem.Draw();
        }
        finally
        {
            Hook.SuppressReplacement = false;
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
        }
    }

    private void OnFramework(IFramework framework)
    {
        if (!_startupCheckDone && DateTime.Now >= _startupCheckAt)
        {
            _startupCheckDone = true;
            StartupCheck();
        }
        // 外部改了译文 json（资源管理器里增删改）也要即时生效——不能只在「插件翻译」窗口开着时才检测
        Replacement.CheckExternalChanges();
        AutoMergeTick();
        // 调试日志（可选）
        if (Configuration.DebugHookLog && (DateTime.Now - _lastDbgLog).TotalSeconds >= 5)
        {
            _lastDbgLog = DateTime.Now;
            Hook.TickDebugLog();
        }
    }

    /// <summary> 钩子抓到一条未翻译英文：开了后台自动翻译就直接送机翻队列（全自动）。 </summary>
    private void OnMissedCaptured(string en)
    {
        if (Configuration.AutoTranslate)
            Mt.EnqueueMissed(en);
    }

    /// <summary> 一批运行时发现文案翻完（en→zh）→ 直接写进本项目词典（全局命中）。 </summary>
    private void OnMissedBatchTranslated(Dictionary<string, string> batch)
    {
        try
        {
            var dir = Configuration.DictDir;
            if (string.IsNullOrWhiteSpace(dir) || !OldDict.Loaded || batch.Count == 0) return;
            var pairs = new System.Collections.Generic.List<(string En, string Zh)>();
            foreach (var kv in batch) pairs.Add((kv.Key, kv.Value));
            var (added, _) = OldDict.MergeIntoDict(dir, pairs);
            OldDict.Load(dir);
            // ── F4 v2 闭环：只写词典是不够的（词典不是替换源，界面不会变中文）──
            // ① 注入生效替换表（最低兜底第四源：窗口 > 安装器 > wiki > 词典）
            // ② 从"未命中队列"移除（不再反复送翻、不再白烧配额）
            // ⚠ 两步**同由 AutoClosureEnabled 控制**：只做②不做①会变成"不再送翻、界面却仍英文"，
            //   问题被藏起来且该原文永久失去补救机会——故绝不拆开上线。
            if (Configuration.AutoClosureEnabled)
            {
                Replacement.ApplyDictEntries(batch);                            // ① 增量注入（键已存在则让位）
                foreach (var en in batch.Keys) Replacement.RemoveMissed(en);    // ② 止血
            }
            if (added > 0) AppLog.Info("[自动翻] 运行时发现 → 写词典 +" + added + " 条");
        }
        catch (Exception ex) { AppLog.Error("[自动翻] 写词典失败：" + ex.Message); }
    }

    /// <summary> 机翻刚结束的瞬间，若开了 AutoMergeToDict，自动把已翻译文沉淀进 我的翻译.json（只增不覆盖）。 </summary>
    private void AutoMergeTick()
    {
        if (_mtWasRunning && !Mt.Running && Configuration.AutoMergeToDict)
        {
            try
            {
                var dir = Configuration.DictDir;
                if (!string.IsNullOrWhiteSpace(dir) && OldDict.Loaded)
                {
                    var pairs = new System.Collections.Generic.List<(string En, string Zh)>();
                    foreach (var (name, _, _) in Replacement.GetWindowPlugins())
                    {
                        var (tr, _) = Replacement.GetWindowEntries(name);
                        foreach (var (en, zh) in tr) pairs.Add((en, zh));
                    }
                    if (pairs.Count > 0)
                    {
                        var (added, _) = OldDict.MergeIntoDict(dir, pairs);
                        OldDict.Load(dir);
                        // F4 v2 止血：这些原文已沉淀进词典（窗口表本身也已在生效表中），
                        // 不必再留在"未命中队列"里反复送翻。与注入同源、同一开关，避免单边生效。
                        if (Configuration.AutoClosureEnabled)
                            foreach (var (en, _) in pairs) Replacement.RemoveMissed(en);
                        if (added > 0)
                            AppLog.Info("[自动沉淀] 机翻结束，自动写入词典 +" + added + " 条（已存在的跳过）");
                    }
                }
            }
            catch (Exception ex) { AppLog.Error("[自动沉淀] 失败：" + ex.Message); }
        }
        _mtWasRunning = Mt.Running;
    }

    private void OnCommand(string command, string args)
    {
        var a = (args ?? "").Trim().ToLowerInvariant();
        if (a is "close" or "cleanup" or "关窗" or "关" or "c")
            CloseAllOtherPluginWindows();
        else if (a.StartsWith("check") || a.StartsWith("检查") || a.StartsWith("单检") || a.StartsWith("体检单个"))
        {
            // 取关键字之后的插件名（保留原大小写；匹配本身不区分大小写）
            var ident = (args ?? "").Trim();
            foreach (var kw in new[] { "check", "检查", "单检", "体检单个" })
                if (ident.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) { ident = ident[kw.Length..].Trim(); break; }
            CheckSinglePlugin(ident);
        }
        else if (a is "health" or "体检")
            StartHealthCheck();
        else if (a is "help" or "?" or "h" or "帮助")
            AppLog.Info("/ptp：打开本插件主窗口。  /ptp close（关窗）：一键关闭其它所有插件的配置窗/主窗。"
                      + "  /ptp check <插件名>（检查）：对单个指定插件做窗内漏网英文检测。"
                      + "  /ptp health（体检）：仅输出已装插件清单（不开窗）。");
        else
            ToggleMain();
    }

    private void ToggleMain() => MainWindow.Toggle();

    /// <summary> 打开/关闭日志窗口（主窗口「日志窗口」按钮入口）。 </summary>
    public void ToggleLogUi() => LogWindow.Toggle();

    /// <summary> 打开/关闭后台自动翻译窗口（主窗口「后台自动翻译」按钮入口）。 </summary>
    public void ToggleTranslationUi() => TranslationWindow.Toggle();

    /// <summary> 打开/关闭 AI 设置窗口（后台自动翻译窗口「AI 设置」按钮入口）。 </summary>
    public void ToggleAiSettingsUi() => AiSettingsWindow.Toggle();

    /// <summary> 打开/关闭插件翻译窗口（主窗口「插件翻译」按钮入口）。 </summary>
    public void ToggleWindowReplaceUi() => WindowReplaceWindow.Toggle();

    /// <summary> 打开/关闭源码提取窗口（主窗口「源码提取」按钮入口）。 </summary>
    public void ToggleSourceExtractUi() => SourceExtractWindow.Toggle();

    public void ToggleDictUi() => DictWindow.Toggle();

    /// <summary> 打开/关闭手动翻译（安装器对照表）二级窗口。 </summary>
    public void ToggleManualEditUi() => ManualEditWindow.Toggle();

    /// <summary> 打开「插件编辑」二级窗口并切到指定插件（插件翻译列表点「编辑」时调用）。 </summary>
    public void OpenPluginEditor(string plugin) => PluginEditorWindow.Open(plugin);

    /// <summary> 打开/关闭仓库地址二级窗口。 </summary>
    public void ToggleRepoUi() => RepoWindow.Toggle();

    /// <summary> 收集当前卫月实际用的仓库地址（主库 + 启用的第三方），供后台预翻拉清单。
    /// 只读已读到的 dalamudConfig，不改卫月设置。 </summary>
    public List<string> BuildRepoUrls()
    {
        var urls = new List<string>();
        if (DalamudMainRepo.Length > 0) urls.Add(DalamudMainRepo);
        foreach (var (url, en) in DalamudThirdRepos)
            if (en && url.Length > 0) urls.Add(url);
        return urls;
    }

    /// <summary> 主窗口「还原英文」：清空生效对照表让界面立刻回到英文（磁盘文件保留）。 </summary>
    public void RestoreEnglish()
    {
        Replacement.ClearActive();
        AppLog.Info("[还原] 界面已还原为英文（对照表文件未删除）");
    }

    /// <summary>
    /// 只读卫月仓库配置（dalamudConfig.json）：主库 MainRepoUrl + 第三方 ThirdRepoList。
    /// 仅用于主窗口展示——知道「后台自动翻译勾了全部」会覆盖哪些仓库的插件；**绝不回写、不增删**。
    /// 橙月/土月把主库换成了镜像（如 DailyRoutines），这里如实显示用户实际用的地址。
    /// </summary>
    private void ReadDalamudRepos()
    {
        try
        {
            // ⚠ 2026-09-18 全面审查：原**硬编码 `XIVLauncherCN`**——国际服（`XIVLauncher`）用户
            //   永远读不到仓库配置，主窗口就一直显示"未找到"，第三方仓库列表始终为空。
            //   改为按候选顺序探测，且优先用「插件自身配置目录的上两级」推断（与 LogWindow 导日志同款，
            //   天然兼容任何启动器目录名 / 自定义安装位置），探测不到再退回按名遍历。
            var path = ResolveDalamudConfigPath();
            if (path == null)
            {
                AppLog.Warn("[仓库] 未找到 dalamudConfig.json（已探测国服/国际服与插件配置上级目录）");
                return;
            }
            var jobj = JObject.Parse(File.ReadAllText(path));
            DalamudMainRepo = (string?)jobj["MainRepoUrl"] ?? "";
            DalamudThirdRepos.Clear();
            var third = jobj["ThirdRepoList"]?["$values"] as JArray;
            if (third != null)
            {
                foreach (var item in third)
                {
                    var url = (string?)item["Url"] ?? "";
                    var en = (bool?)item["IsEnabled"] ?? true;
                    if (url.Length > 0) DalamudThirdRepos.Add((url, en));
                }
            }
            AppLog.Info($"[仓库] 主库 1 个 + 第三方 {DalamudThirdRepos.Count} 个（仅展示）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("[仓库] 读取卫月仓库配置失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 定位 <c>dalamudConfig.json</c>。返回 <c>null</c> 表示都没找到。
    /// **绝不创建文件**，仅只读探测。顺序：①插件配置目录上两级（最可靠，兼容自定义启动器目录名）
    /// ②Roaming 下常见启动器目录名（国服 `XIVLauncherCN` / 国际服 `XIVLauncher`）。
    /// </summary>
    private static string? ResolveDalamudConfigPath()
    {
        // ① 插件配置目录形如 …/{启动器}/{pluginConfigs}/{插件} → 上两级就是启动器目录
        try
        {
            var cfgDir = PluginInterface.GetPluginConfigDirectory();
            var launcher = Directory.GetParent(Directory.GetParent(cfgDir)?.FullName ?? "")?.FullName;
            if (!string.IsNullOrEmpty(launcher))
            {
                var p = Path.Combine(launcher, "dalamudConfig.json");
                if (File.Exists(p)) return p;
            }
        }
        catch { /* 推断失败就走 ② */ }

        // ② 按启动器目录名遍历（含国际服）
        foreach (var name in new[] { "XIVLauncherCN", "XIVLauncher" })
        {
            try
            {
                var p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    name, "dalamudConfig.json");
                if (File.Exists(p)) return p;
            }
            catch { /* 换下一个 */ }
        }
        return null;
    }

    /// <summary>
    /// 决定 wiki 术语目录并加载。**与旧项目联动（不复制文件）**：
    ///   ① 用户手动指定的目录（存在且含 json）→ 优先；
    ///   ② 否则**自动探测旧项目插件的词典目录**（读其配置里的 DictionaryPath）→ 直接读它的 wiki_术语对照；
    ///   ③ 都没有 → 不启用术语（正常走机翻）。
    /// 每次启动都重新探测/重读，故旧项目更新术语后本插件立即用上最新版，无需重新内置。
    /// </summary>
    private void EnsureWikiDir()
    {
        try
        {
            string? dir = null;
            var pluginConfigRoot = Path.GetDirectoryName(PluginInterface.GetPluginConfigDirectory()) ?? "";

            // ① 手动指定
            var manual = Configuration.WikiDir?.Trim() ?? "";
            if (manual.Length > 0 && Directory.Exists(manual) && Directory.EnumerateFiles(manual, "*.json").Any())
            {
                dir = manual;
                Log.Information($"[wiki] 使用手动指定的术语目录：{dir}");
            }
            else
            {
                // ② 自动探测旧项目
                var detected = WikiGlossaryService.DetectOldProjectWikiDir(pluginConfigRoot);
                if (detected != null)
                {
                    dir = detected;
                    Configuration.WikiDir = detected; // 记住探测结果，便于 UI 展示
                    Configuration.Save();
                    Log.Information($"[wiki] 已联动旧项目术语目录：{detected}");
                }
            }

            if (dir == null)
            {
                Log.Information("[wiki] 未找到术语表（未装旧项目插件或未指定目录）——直接使用机翻");
                return;
            }

            var n = Wiki.Load(dir);
            if (Configuration.WikiEnabled && n > 0)
            {
                Replacement.SetWikiTerms(new Dictionary<string, string>(Wiki.All, StringComparer.Ordinal));
                Log.Information($"[wiki] 已启用 {n} 条官方术语（联动目录：{dir}）");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[wiki] 术语目录准备失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 准备**本项目自己的**词典目录（默认 = 插件数据目录\词典目录），并加载其中的「我的翻译.json」等。
    /// ⚠ 与旧项目完全独立：不读旧项目词典；目录不存在则创建（供用户放入自己的译文）。
    /// </summary>
    private void EnsureDictDir()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Configuration.DictDir))
            {
                Configuration.DictDir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "词典目录");
                Configuration.Save();
            }
            Directory.CreateDirectory(Configuration.DictDir);
            CreateDefaultDictIfMissing(Configuration.DictDir);   // 首次自动生成模板文件
            UpdateLinkedBlacklistPath();   // 联动旧项目黑名单：先设好路径，Load 时并入（见下）
            var n = OldDict.Load(Configuration.DictDir);
            Replacement.SetBlacklist(OldDict.IsBlacklisted);   // 单词黑名单交给替换层（黑名单优先级最高）
            // F4 v2：把本项目词典交给替换层作**最低兜底第四源**（窗口 > 安装器 > wiki > 词典）。
            // 默认关（AutoClosureEnabled=false）→ 不注入，行为与改动前一致；开启后才用词典补缺。
            if (Configuration.AutoClosureEnabled)
                Replacement.SetDictTerms(new Dictionary<string, string>(OldDict.SnapshotEntries(), StringComparer.Ordinal));
            // 机翻侧黑名单在 Mt 创建后补设（Mt 此刻尚未 new，提前调会 NRE，2026-09-20 修）
            Log.Information($"[预翻译] 本项目词典目录 {Configuration.DictDir}，已载入 {n} 条，" +
                            $"单词黑名单 {OldDict.BlacklistCount} 条");
        }
        catch (Exception ex)
        {
            Log.Warning($"[预翻译] 词典目录准备失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 【联动旧项目单词黑名单】按开关 <see cref="Configuration.LinkOldProjectBlacklist"/> 探测旧项目
    /// （FFXIV 模组汉化工具）的词典目录，把它的「单词黑名单.json」设为本插件黑名单的联动源。
    /// 只**设定路径**——真正的并入发生在随后的 <c>OldDict.Load(...)</c>（Load 先清空再并入，
    /// 故重载词典时旧项目黑名单的增删能即时生效）。
    /// ⚠ 只读旧项目配置与词单、从不修改；探测不到（未装旧项目/未配词典目录）或开关关闭时置 null（不联动）。
    /// </summary>
    private void UpdateLinkedBlacklistPath()
    {
        try
        {
            if (!Configuration.LinkOldProjectBlacklist)
            {
                OldDict.SetLinkedBlacklistFile(null);
                return;
            }
            var root = Path.GetDirectoryName(PluginInterface.GetPluginConfigDirectory()) ?? "";
            var dictPath = WikiGlossaryService.DetectOldProjectDictionaryPath(root);
            if (string.IsNullOrWhiteSpace(dictPath))
            {
                OldDict.SetLinkedBlacklistFile(null);   // 未装旧项目/未配置词典目录 → 不联动
                return;
            }
            OldDict.SetLinkedBlacklistFile(Path.Combine(dictPath, OldDictionaryService.BlacklistFileName));
        }
        catch (Exception ex)
        {
            Log.Warning($"[预翻译] 联动旧项目黑名单探测失败（已跳过）：{ex.Message}");
            OldDict.SetLinkedBlacklistFile(null);
        }
    }

    /// <summary>
    /// 词典目录里没有「我的翻译.json」时**自动生成模板**（含格式说明与示例）。
    /// 否则用户不知道文件该叫什么、格式怎么写——预翻译就永远命中不了。
    /// 只在新目录（无任何词典文件）时创建，不覆盖用户已有的文件。
    /// </summary>
    private void CreateDefaultDictIfMissing(string dir)
    {
        try
        {
            // 已有任意已知词典文件就不动
            foreach (var f in new[] { "我的翻译.json", "个性翻译.json" })
            {
                if (File.Exists(Path.Combine(dir, f))) return;
            }

            // ⚠ 格式要求（2026-09-15 用户明确指定）：**原文与译文各占一行**，不要写在同一行——
            //   一行一条虽然紧凑，但原文/译文挤在一起时肉眼难对齐、diff 也难看。
            var template = """
            {
              "_说明": [
                "这是本插件（翻译插件的插件）的词典文件，供「插件翻译」窗口的「全部预翻译」使用。",
                "格式：terms 是通用术语（不限插件），mods 可留空。",
                "「原文」写界面上的英文（精确匹配，大小写敏感），「译文」写中文；两者各占一行。",
                "除了手改，也可以在「插件翻译」窗口点「翻译结果写入词典」自动汇总（只新增、不覆盖已有条目）。",
                "改完在主窗口点「词典目录」打开词典窗口，再点「重载词典」即可生效；不会自动翻译，命中即直接采用。"
              ],
              "terms": [
                {
                  "原文": "Own Buff/Debuff Scale",
                  "译文": "自身增益/减益比例"
                },
                {
                  "原文": "Retry Count",
                  "译文": "重试次数"
                }
              ],
              "mods": {}
            }
            """;
            File.WriteAllText(Path.Combine(dir, "我的翻译.json"), template, new System.Text.UTF8Encoding(true));

            // 单词黑名单模板（命中词保持英文）——搬自旧项目同名字段
            var blTemplate = """
            # 单词黑名单：这里列的英文永远保持原样，不翻译、也不参与替换。
            # 一行一个词，支持逗号分隔；以 # 开头的是注释。
            # 用途：① 插件名/专有缩写（乱译更糟）；② 被插件当标识符用的词（改了会影响显示或逻辑）。
            # 单独成行的词 = 整串精确匹配（大小写不敏感），不会误伤含该词的整句。
            # 改完在主窗口点「词典目录」打开词典窗口，再点「重载词典」生效。
            URL
            DPS
            GCD
            """;
            var blPath = Path.Combine(dir, OldDictionaryService.BlacklistFileName);
            if (!File.Exists(blPath))
                File.WriteAllText(blPath, blTemplate, new System.Text.UTF8Encoding(true));

            Log.Information($"[预翻译] 词典目录为空，已生成模板：我的翻译.json / 单词黑名单.json");
        }
        catch (Exception ex)
        {
            Log.Warning($"[预翻译] 生成词典模板失败：{ex.Message}");
        }
    }

    /// <summary> 重新加载本项目词典（UI 调用）。 </summary>
    public int ReloadOldDict()
    {
        EnsureDictDir();
        var n = OldDict.Load(Configuration.DictDir);
        Replacement.SetBlacklist(OldDict.IsBlacklisted);   // 黑名单同步刷新
        Mt.SetBlacklist(OldDict.BlacklistWords);
        // F4 v2：同步刷新第四源（最低兜底词典）。开关关闭时置空，确保不会有残留源继续生效。
        Replacement.SetDictTerms(Configuration.AutoClosureEnabled
            ? new Dictionary<string, string>(OldDict.SnapshotEntries(), StringComparer.Ordinal)
            : null);
        return n;
    }

    /// <summary> 重新加载 wiki 术语表并让替换层生效（UI「加载」按钮调用；含重新探测旧项目目录）。 </summary>
    public int ReloadWiki()
    {
        // 目录为空时先尝试重新探测旧项目（用户可能刚装/更新了旧插件）
        if (string.IsNullOrWhiteSpace(Configuration.WikiDir) || !Directory.Exists(Configuration.WikiDir))
        {
            var root = Path.GetDirectoryName(PluginInterface.GetPluginConfigDirectory()) ?? "";
            var detected = WikiGlossaryService.DetectOldProjectWikiDir(root);
            if (detected != null)
            {
                Configuration.WikiDir = detected;
                Configuration.Save();
            }
        }
        var n = Wiki.Load(Configuration.WikiDir);
        Replacement.SetWikiTerms(Configuration.WikiEnabled && n > 0
            ? new Dictionary<string, string>(Wiki.All, StringComparer.Ordinal)
            : null);
        return n;
    }

    // ── 启动自动检查：加载约 10 秒后扫一次缺口，静默/按配置翻译（插件更新后新文案也走这条） ──
    private readonly DateTime _startupCheckAt = DateTime.Now.AddSeconds(10);
    private DateTime _lastDbgLog = DateTime.MinValue;
    private bool _mtWasRunning;   // 机翻完成自动沉淀词典用
    private bool _startupCheckDone;

    private void StartupCheck()
    {
        try
        {
            // 2026-09-18：主开关「后台自动翻译」是总闸——关了则一切自动行为都停（不扫描、不提示、不下载）。
            // 默认关，需用户在「后台自动翻译」窗口主动开启。
            if (!Configuration.AutoTranslate)
            {
                AppLog.Info("[自动] 后台自动翻译主开关已关，启动检查跳过");
                return;
            }
            // 子开关「主动预翻仓库全部插件」：后台刷新仓库清单缓存（不阻塞主线程；CollectMissing 只读本地缓存）。
            // 即使当前无缺口也触发——缓存过期就该在后台更新，下次启动检查才能用上未安装插件的介绍。
            // 2026-09-18：按卫月实际配的仓库（主库+启用的第三方）拉，不再硬编码官方 staging。
            if (Configuration.AutoExtractAllPlugins)
                Replacement.EnsureRepoCacheAsync(BuildRepoUrls());

            var count = Replacement.CollectMissing().Values.Distinct().Count();
            if (count == 0)
            {
                AppLog.Info("[自动] 启动检查：对照表已覆盖全部已装插件介绍");
                return;
            }
            // ⚠ 必须用**解析后**的 Key（按当前服务商从 AiApiKeys 取名，并兼容旧版单一 ZhipuApiKey）——
            //    旧写法只读 `Configuration.ZhipuApiKey`，而该字段在构造期就被迁移清空了（见上方 Key 迁移），
            //    于是**明明配了 Key 也会被判成"未配置"** → 静默跳过自动翻译（2026-09-14 实测发现：
            //    日志说"未配置 API Key"，但机翻其实能用，自相矛盾）。
            if (string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(Configuration)))
            {
                AppLog.Info($"[自动] 检测到 {count} 条介绍缺口，未配置 API Key，跳过（可在「后台自动翻译」窗口填写）");
                Mt.Notify($"检测到 {count} 条新文案待翻译（未填 API Key）");
                return;
            }
            if (Configuration.AutoTranslate && Configuration.SilentTranslate)
            {
                AppLog.Info($"[自动] 检测到 {count} 条介绍缺口，后台静默翻译…");
                Mt.Start();
                return;
            }
            if (Configuration.AutoTranslate)
            {
                AppLog.Info($"[自动] 检测到 {count} 条介绍缺口（静默已关，等待手动开始）");
                Mt.Notify($"检测到 {count} 条新文案待翻译，点「自动翻译缺失条目」开始");
                return;
            }
            AppLog.Info($"[自动] 检测到 {count} 条介绍缺口（自动翻译已关闭）");
        }
        catch (Exception ex)
        {
            AppLog.Error("[自动] 启动检查失败：" + ex.Message);
        }
    }

    /// <summary> 仓库清单缓存后台拉完后触发（后台 Task 线程）：
    /// 若主开关+静默都开着、且配了 Key，立刻启动一轮机翻——新清单里 520 个插件的介绍缺口马上开始翻，
    /// 不必等下次启动。Mt.Start() 内部自己 CollectMissing 读新缓存；正在跑则自动跳过。 </summary>
    private void OnRepoCacheUpdated()
    {
        try
        {
            if (!Configuration.AutoTranslate || !Configuration.SilentTranslate)
                return;
            if (string.IsNullOrWhiteSpace(MtTranslateService.GetApiKey(Configuration)))
            {
                AppLog.Info("[仓库缺口] 新清单已就位，但未配置 API Key，暂不自动翻译");
                return;
            }
            AppLog.Info("[仓库缺口] 新清单已就位，启动一轮缺口翻译…");
            Mt.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("[仓库缺口] 拉完清单后启动翻译失败：" + ex.Message);
        }
    }

    // ── 体检：逐个打开已装插件的配置窗，收集"仍显示英文"的漏网文字（像用户肉眼看一样） ──
    private bool _healthRunning;
    /// <summary> 体检是否在跑（主窗口据此禁用按钮）。 </summary>
    public bool HealthRunning { get; private set; }
    /// <summary> 最近一次体检报告（主窗口显示摘要）。 </summary>
    public string HealthReport { get; private set; } = "";
    /// <summary> 最近一次体检的完整结果：(插件名, 漏网英文列表)——报告窗口据此逐项画出。 </summary>
    public List<(string Name, List<string> Items)> HealthResults { get; } = new();

    /// <summary> 体检（仅清单模式）列出的已装插件清单，供报告窗口渲染成可点击项：点名字→单插件检查/开窗口。
    /// 元素：(显示名, InternalName, 有配置窗, 有主窗, 是否自身)。</summary>
    public List<(string Name, string InternalName, bool HasConfig, bool HasMain, bool IsSelf)> HealthPluginList { get; } = new();

    /// <summary>
    /// 逐个打开已装插件的配置窗（OpenConfigUi），等 ~1.5 秒让它画出来，
    /// 收集这期间"未命中译文表的纯英文"（≈ 肉眼看着还是英文的），再关掉窗口，最后出报告。
    /// 原理：钩子在绘制那一刻查表，命中的已换成中文指针——所以未命中的英文就是真漏网。
    /// 会短暂自动开关窗口，故做成手动按钮，不在后台自动跑。
    /// </summary>
    /// <summary> 体检（2026-09-24 改：默认「仅清单模式」，不开窗）。
    /// 遍历已装插件，在日志输出「检查了哪些插件 / 是否有配置窗或主窗 / 是否已排除自身」，并保留安装器简介覆盖率的静态检查。
    /// 不开、不关任何窗口——彻底绕开「后台线程调 ImGui 关窗」的崩溃路径（见 ClosePluginWindowOnFrameworkThread）。
    /// 若要对某插件做「窗内漏网英文」深度检测，请用 <c>/ptp check &lt;插件名&gt;</c> 或体检报告窗点插件名。 </summary>
    public void StartHealthCheck()
    {
        if (_healthRunning) return;
        _healthRunning = true;
        HealthRunning = true;
        HealthReport = "";
        Task.Run(() =>
        {
            try
                {
                    var targets = PluginInterface.InstalledPlugins
                        .Where(p => p.IsLoaded && (p.HasConfigUi || p.HasMainUi))
                        .ToList();
                    AppLog.Info($"[体检] 开始（仅清单模式，不开窗）：共 {targets.Count} 个插件待检查");
                HealthResults.Clear();
                HealthPluginList.Clear();
                foreach (var p in targets)
                {
                    // 排除「汉化工具系列」（本项目 + Penumbra 汉化 + MOD 选项汉化）：都是司令官自己的翻译工具，无"英→中"意义，且属其他项目，不该出现在体检清单。
                    if (SourceExtractService.SelfPluginInternalNames.Contains(p.InternalName))
                    {
                        AppLog.Info($"[体检] （已排除汉化工具系列，非翻译目标）{p.Name}（{p.InternalName}）");
                        continue;
                    }
                    var flags = (p.HasConfigUi ? "配置窗" : "") + (p.HasMainUi ? "主窗" : "");
                    AppLog.Info($"[体检] {p.Name}（{p.InternalName}）：{flags}");
                    HealthPluginList.Add((p.Name, p.InternalName, p.HasConfigUi, p.HasMainUi, false));
                }
                // 按字母排序（2026-09-25 司令官要求）：插件清单按显示名字母序，不区分大小写（"a" 与 "A" 相邻，而不是大写全排在前）。
                // 用 List.Sort 原地排，不用 Clear+Add：避免排序中途出现"空列表中间态"被渲染线程读到。
                HealthPluginList.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                // ── 安装器介绍覆盖率（未安装插件的简介/描述，静态对比仓库清单 vs 译文表）──
                // 未安装的插件开不了配置窗，它们的介绍在仓库清单里——直接对比即可，不用开窗。
                var cachePath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "pluginmaster_cache.json");
                if (File.Exists(cachePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                    int introTotal = 0, introDone = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var name = el.TryGetProperty("Name", out var nEl) ? (nEl.GetString() ?? "?") : "?";
                        var punch = el.TryGetProperty("Punchline", out var pEl) ? pEl.GetString() ?? "" : "";
                        var desc = el.TryGetProperty("Description", out var dEl) ? dEl.GetString() ?? "" : "";
                        var missingItems = new List<string>();
                        if (!string.IsNullOrWhiteSpace(punch))
                        {
                            introTotal++;
                            if (Replacement.HasInstallerTranslation(punch)) introDone++;
                            else missingItems.Add("[简介] " + punch.Trim());
                        }
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            introTotal++;
                            if (Replacement.HasInstallerTranslation(desc)) introDone++;
                            else
                            {
                                var d = desc.Trim();
                                missingItems.Add("[描述] " + (d.Length > 80 ? d[..80] + "…" : d));
                            }
                        }
                        if (missingItems.Count > 0) HealthResults.Add((name, missingItems));
                    }
                    AppLog.Info($"[体检] 安装器介绍覆盖率：{introDone}/{introTotal} 条已汉化（全部仓库插件的简介+描述）");
                }
                else
                {
                    AppLog.Info("[体检] 无仓库清单缓存（pluginmaster_cache.json），跳过安装器介绍覆盖率检查");
                }

                // 漏网英文清单同样按插件名字母序（与插件清单一口径）。
                HealthResults.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

                HealthReport = HealthResults.Count == 0
                    ? "体检完成（仅清单模式）：已列出所有插件配置窗/主窗情况，详见日志。"
                    : $"体检完成（仅清单模式）：{HealthResults.Count} 个插件简介/描述仍有漏网英文（详见报告窗口/日志）。";
                AppLog.Info("[体检] 完成（仅清单模式）。\n" + HealthReport);
                HealthReportWindow.IsOpen = true;   // 跑完确保报告窗打开（用 IsOpen=true 而非 Toggle，避免报告窗已开着时被误关）
            }
            catch (Exception ex)
            {
                AppLog.Error("[体检] 失败：" + ex.Message);
                HealthReport = "体检失败：" + ex.Message;
            }
            finally
            {
                _healthRunning = false;
                HealthRunning = false;
            }
        });
    }

    /// <summary> 指定单个插件做「窗内漏网英文」深度检查（2026-09-24 指令：清单模式为主，按需对单个插件开窗检测）。
    /// 按名字/InternalName（不区分大小写、子串匹配）定位；排除自身；打开其配置窗/主窗 → 采样漏网英文 → 出报告。
    /// 不自动关窗：保留刚打开的插件窗口，供司令官当场核对漏网英文（关窗由司令官手动关闭或 /ptp close 处理）。
    /// 采样期间临时屏蔽自身窗口避免污染；开/等仅后台线程、无原生关窗调用，故不会重演体检崩溃。
    /// 供 <c>/ptp check &lt;插件名&gt;</c> 与体检报告窗「点插件名」调用。 </summary>
    public void CheckSinglePlugin(string identifier)
    {
        var id = (identifier ?? "").Trim();
        if (id.Length == 0) { AppLog.Warn("[体检] 请指定插件名：/ptp check <插件名>，或在主窗口输入插件名后点「检查单个插件窗口」"); return; }
        if (_healthRunning) { AppLog.Warn("[体检] 体检进行中，请稍后再试单插件检查"); return; }
        Task.Run(async () =>
        {
            bool ownMainWasOpen = false, ownLogWasOpen = false;
            try
            {
                var selfName = PluginInterface.InternalName;
                var p = PluginInterface.InstalledPlugins.FirstOrDefault(x => x.IsLoaded &&
                    (x.Name.Contains(id, StringComparison.OrdinalIgnoreCase) || x.InternalName.Contains(id, StringComparison.OrdinalIgnoreCase)));
                if (p is null) { AppLog.Warn($"[体检] 找不到匹配「{id}」的已加载插件"); return; }
                if (string.Equals(p.InternalName, selfName, StringComparison.OrdinalIgnoreCase)) { AppLog.Warn("[体检] 不检查自身插件"); return; }
                Hook.HealthCollecting = true;
                // 检查期间藏起自身窗口，避免全局采样把自家 UI 英文收进该插件报告
                ownMainWasOpen = MainWindow.IsOpen; ownLogWasOpen = LogWindow.IsOpen;
                MainWindow.IsOpen = false; LogWindow.IsOpen = false;
                Hook.ResetMissedSamples();
                Hook.DrainTreeNodeSamples();   // 清掉上一轮残留，本轮只看这次采样窗口内的折叠头
                try { if (p.HasConfigUi) p.OpenConfigUi(); else p.OpenMainUi(); }
                catch (Exception exOpen) { AppLog.Warn($"[体检] 打开 {p.Name} 失败：{exOpen.Message}"); }
                // 2026-09-25：留 6 秒给司令官切到要检查的页面。多数插件打开后停在首页/模组列表，
                // 而要看的折叠头在「设置」页——采样只有 1.5 秒时，采到的永远是首页（这正是此前反复定位失败的原因）。
                AppLog.Info($"[体检] 已打开 {p.Name}：请在这 6 秒内切到你要检查的页面（如「设置」）；采样结束后窗口保留供你核对。");
                await Task.Delay(6000);
                var missed = Hook.DrainMissedSamples();
                var treeSamples = Hook.DrainTreeNodeSamples();
                // 不自动关窗：保留刚打开的插件窗口，供司令官当场查看漏网英文（关窗由司令官手动或 /ptp close 处理）。
                var real = missed.Where(IsLikelyUiText).ToList();
                if (real.Count > 0)
                {
                    AppLog.Info($"[体检] {p.Name}（{p.InternalName}）：{real.Count} 条漏网英文");
                    foreach (var m in real.Take(15)) AppLog.Info("      " + m);
                    HealthResults.Add((p.Name, real));
                    // 连点多个插件时，结果列表也保持字母序（原地排序，不产生空列表中间态）
                    HealthResults.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                }
                else
                {
                    AppLog.Info($"[体检] {p.Name}：无漏网英文");
                }
                // 折叠头专项：Heliosphere 的 Commands/Download speed limits/One-click install/Miscellaneous
                // 反复报"没替换"。这里把采样窗口内**所有**经过 igTreeNodeEx_Str 的串原样列出（含命中标记），
                // 一次看清：它们到底有没有走到这个钩子、查表是命中还是未命中。
                if (treeSamples.Count > 0)
                {
                    AppLog.Info($"[体检] 折叠头（igTreeNodeEx_Str）样本 {treeSamples.Count} 条 —— [命中]=已替换，[未命中]=表里没查到：");
                    foreach (var s in treeSamples.Take(40)) AppLog.Info("      " + s);
                }
                else
                {
                    AppLog.Info("[体检] 折叠头（igTreeNodeEx_Str）无任何调用：该窗口当前页面没有 TreeNode 折叠头，或该页面在采样期间未渲染。");
                }
                HealthReport = $"{p.Name}：{(real.Count > 0 ? real.Count + " 条漏网英文" : "无漏网英文")}";
                HealthReportWindow.IsOpen = true;   // 确保报告窗开着：点名字时本就开着，Toggle 会误把它关掉
            }
            catch (Exception ex)
            {
                AppLog.Error("[体检] 单插件检查失败：" + ex.Message);
            }
            finally
            {
                Hook.HealthCollecting = false;
                if (ownMainWasOpen) MainWindow.IsOpen = true;   // 恢复被屏蔽的自身窗口
                if (ownLogWasOpen) LogWindow.IsOpen = true;
            }
        });
    }

    /// <summary> 在 framework（渲染）线程上安全关闭某插件的窗口：先关 Atk 原生窗，再反射关 ImGui 窗。
    /// <b>为什么必须封送到 framework 线程</b>：ImGui 上下文与 Atk 原生指针仅 framework 线程有效；在后台线程
    /// 调用会因 ImGui getter 触发 cimgui 访问违例（C0000005）而整进程崩溃——
    /// 2026-09-24 实锤：体检关窗用反射读了 Penumbra <c>ModTab.MaximumWidth</c> 的 getter，其内调
    /// <c>igGetWindowWidth</c>，而后台线程（.NET TP Worker）无 ImGui 上下文 → 崩。原生异常无法被 try/catch 捕获，
    /// 故只能从线程归属上根治（用 <c>IFramework.RunOnFrameworkThread</c> 把关窗整段挪到渲染线程）。
    /// 实例包装，供体检循环与一键关窗调用；核心反射逻辑见 ClosePluginUiWindowCore。 </summary>
    private void ClosePluginWindowOnFrameworkThread(object plugin, string internalName, string tag)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            try { if (!string.IsNullOrEmpty(internalName)) AtkHook.TryClosePluginWindow(internalName); } catch { /* 尽力关 Atk 原生窗 */ }
            ClosePluginUiWindowCore(AppLog, plugin, tag);
        });
    }

    /// <summary> 一键关闭其它所有插件的配置窗/主窗（ImGui 窗 + Atk 原生窗），不卸载任何插件，只收起界面。
    /// 复用体检同款反射关窗逻辑（ClosePluginUiWindowCore，已修「深度 2 下钻」）。司令官在游戏里用 <c>/ptp close</c>
    /// （或中文 <c>关窗</c>）即可触发，免去挨个点关闭；主窗口也有同款按钮。自身（按运行期 InternalName 排除）窗口不动。 </summary>
    public void CloseAllOtherPluginWindows()
    {
        Task.Run(async () =>
        {
            try
            {
                // 排除「汉化工具系列」（本项目 + Penumbra 汉化 + MOD 选项汉化）：都是司令官自己的翻译工具，关窗时不动它们。
                var targets = PluginInterface.InstalledPlugins
                    .Where(p => p.IsLoaded && !SourceExtractService.SelfPluginInternalNames.Contains(p.InternalName))
                    .ToList();
                foreach (var p in targets)
                {
                    try { ClosePluginWindowOnFrameworkThread(p, p.InternalName, "[关窗]"); } catch { /* 尽力关窗（Atk+ImGui） */ }
                    await Task.Delay(40);
                }
                AppLog.Info($"[关窗] 已对 {targets.Count} 个其它插件执行关闭窗口（ImGui 窗 + Atk 原生窗）。"
                           + "若仍有个别窗口留驻，多为插件自绘窗无 IsOpen、或 OpenConfigUi 每次新建窗，需手动关。");
            }
            catch (Exception ex)
            {
                AppLog.Error("[关窗] 失败：" + ex.Message);
            }
        });
    }

    /// <summary> 关闭其他插件 ImGui 配置窗/主窗（核心反射逻辑，静态可测；由 ClosePluginWindowOnFrameworkThread 在 framework 线程调用）。
    /// 官方 IExposedPlugin 只有 OpenConfigUi/OpenMainUi、没有 Close（dalamud.dev API 确认，本工程 AtkNativeUiWalkerService.cs:137 亦记）；
    /// Atk 原生窗与 ImGui 窗的关闭都封送在 framework（渲染）线程里执行（见 ClosePluginWindowOnFrameworkThread，2026-09-24 崩溃修复）。ImGui 窗只能反射：IExposedPlugin 接口本身不暴露插件实例，
    /// 故反射其**具体运行时类型**取出真正的 IDalamudPlugin 实例——注意新 Dalamud 下要**下钻两层**
    /// （InstalledPlugins 元素＝ExposedPlugin → 包着内部类 LocalPlugin → 其 Instance 才是 IDalamudPlugin，
    /// 见 GetPluginInstance），再关闭它身上所有带 IsOpen 的 Window 成员（直连或装在 WindowSystem 里）。
    /// 反射目标/成员名随插件而异（ConfigWindow/MainWindow/插件自定义窗…），属尽力而为；任一步失败静默跳过，不影响调用方。
    /// <param name="tag">日志前缀，默认"[体检]"；手动一键关窗传"[关窗]"以区分来源。</param> </summary>
    internal static void ClosePluginUiWindowCore(AppLog log, object plugin, string tag = "[体检]")
    {
        var pluginName = GetExposedName(plugin) ?? "?";
        try
        {
            var instance = GetPluginInstance(plugin);
            // 取不到 IDalamudPlugin 实例时，退而直接递归扫描包装本身（ExposedPlugin → LocalPlugin → …），
            // 递归找窗仍能命中藏在更深/非标准位置的窗；仅当整个图都找不到任何窗才跳过。
            var scanned = instance ?? plugin;
            if (instance is null)
                log.Warn($"{tag} 无法取得 {pluginName} 的插件实例（退回扫描包装本身）");
            var closed = 0;
            foreach (var w in EnumerateWindowMembers(scanned))
            {
                try
                {
                    var wt = w.GetType();
                    // 优先写 bool 字段（Dalamud Window.IsOpen 多为自动属性，字段最稳；避开任何非平凡 setter）
                    var fld = wt.GetField("IsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fld != null && fld.FieldType == typeof(bool)) { fld.SetValue(w, false); closed++; }
                    else { wt.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance)?.SetValue(w, false); closed++; }
                }
                catch { /* 尽力关 */ }
            }
            log.Info($"{tag} 已尝试关闭 {pluginName} 的 {closed} 个 ImGui 窗");
        }
        catch (Exception ex)
        {
            log.Warn($"{tag} 关闭 {pluginName} 的 ImGui 窗失败：{ex.Message}");
        }
    }

    private static string? GetExposedName(object plugin)
    {
        foreach (var n in new[] { "Name", "InternalName" })
        {
            var p = plugin.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { var v = p.GetValue(plugin); if (v is string s && s.Length > 0) return s; }
        }
        return null;
    }

    /// <summary> 从 IExposedPlugin 的具体运行时类型取出真正的 IDalamudPlugin 实例。
    /// 接口本身不暴露实例，只能反射——且要**下钻两层**：新 Dalamud 的 InstalledPlugins 元素是
    /// 公开类 <c>Dalamud.Plugin.ExposedPlugin</c>，它包着内部类 <c>Dalamud.Plugin.Internal.Types.LocalPlugin</c>，
    /// 真正的 IDalamudPlugin 在 <c>LocalPlugin.Instance</c>（ExposedPlugin 与 LocalPlugin **本身都不是** IDalamudPlugin）。
    /// 故先找深度 1，再沿 Dalamud 内部包装类型下钻（类名 LocalPlugin / 命名空间 Dalamud.Plugin.Internal）。 </summary>
    internal static object? GetPluginInstance(object plugin)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // 深度 1：直接成员里就有实例（老式包装 / 自定义包装）
        foreach (var (_, v) in EnumerateMembers(plugin, plugin.GetType(), flags))
            if (IsPluginInstance(v)) return v;
        // 深度 2~3：沿 Dalamud 内部包装（ExposedPlugin → LocalPlugin → Instance）下钻
        foreach (var (_, wrapper) in EnumerateMembers(plugin, plugin.GetType(), flags))
        {
            if (wrapper is null || !IsDalamudInternalWrapper(wrapper)) continue;
            foreach (var (_, inner) in EnumerateMembers(wrapper, wrapper.GetType(), flags))
            {
                if (IsPluginInstance(inner)) return inner;
                if (inner is null || !IsDalamudInternalWrapper(inner)) continue;
                foreach (var (_, deep) in EnumerateMembers(inner, inner.GetType(), flags))
                    if (IsPluginInstance(deep)) return deep;
            }
        }
        return null;
    }

    /// <summary> 是否为 Dalamud 内部包装类型（只对这类成员继续下钻找插件实例，避免误入插件业务对象）。
    /// 命中：类名 LocalPlugin，或命名空间以 Dalamud.Plugin.Internal 开头。 </summary>
    private static bool IsDalamudInternalWrapper(object v)
    {
        var ty = v.GetType();
        return ty.Name == "LocalPlugin"
            || (ty.Namespace ?? "").StartsWith("Dalamud.Plugin.Internal", StringComparison.Ordinal);
    }

    private static bool IsPluginInstance(object? v)
    {
        if (v is null) return false;
        return v.GetType().GetInterfaces().Any(i => i.Name == "IDalamudPlugin") || v.GetType().Name == "IDalamudPlugin";
    }

    /// <summary> 递归枚举某插件实例图里所有「可关闭的 Dalamud 窗」对象。
    /// 覆盖各种存法：① 直接字段/属性（ConfigWindow / MainWindow / _configWindow …）；
    /// ② 装在 Dalamud <c>WindowSystem.Windows</c> 集合；③ 装在 <c>List&lt;Window&gt;</c> / 数组等集合里；
    /// ④ 嵌套在子对象里（如某配置持有窗引用）。用引用去重 + 深度上限避免死循环/爆栈。
    /// 仅取值，不改内容；成员读取异常由 EnumerateMembers 内部吞掉。 </summary>
    private const int WindowScanMaxDepth = 8;

    private static IEnumerable<object> EnumerateWindowMembers(object instance)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(object obj, int depth)>();
        stack.Push((instance, 0));
        while (stack.Count > 0)
        {
            var (cur, depth) = stack.Pop();
            if (cur is null || cur is string) continue;
            if (!visited.Add(cur)) continue;
            var t = cur.GetType();
            if (t.IsPrimitive || t.IsValueType) continue;            // 基元/值类型不入图
            if (IsCloseableWindow(cur)) yield return cur;
            if (depth >= WindowScanMaxDepth) continue;
            foreach (var (_, v) in EnumerateMembers(cur, t, flags))
            {
                if (v is null || v is string) continue;
                if (v is System.Collections.IEnumerable en)
                {
                    foreach (var e in en)
                        if (e is not null && e is not string) stack.Push((e, depth + 1));
                }
                else
                {
                    stack.Push((v, depth + 1));
                }
            }
        }
    }

    /// <summary> 该对象是否为一个可关闭的 Dalamud 窗：精确匹配 <c>Dalamud.Interface.Windowing.Window</c>
    /// （含其子类）；兜底：带公开 IsOpen 且带 WindowName（自定义/老式窗，避免误伤普通 IsOpen 字段）。 </summary>
    private static bool IsCloseableWindow(object o)
    {
        for (var c = o.GetType(); c is not null; c = c.BaseType)
            if (c.FullName == "Dalamud.Interface.Windowing.Window") return true;
        var hasIsOpen = o.GetType().GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance) != null;
        var hasName = o.GetType().GetProperty("WindowName", BindingFlags.Public | BindingFlags.Instance) != null;
        return hasIsOpen && hasName;
    }

    private static IEnumerable<(string name, object? value)> EnumerateMembers(object target, Type t, BindingFlags flags)
    {
        // ⚠ 只枚举字段、绝不调属性 getter（2026-09-24 崩溃根治）：插件的属性 getter 可能内部调 ImGui 原生函数
        // （如 Penumbra ModTab.MaximumWidth → igGetWindowWidth）。即便封送到 framework 线程，RunOnFrameworkThread
        // 的动作也不在「活动 ImGui 窗口绘制」上下文中，igGetWindowWidth 取不到当前窗口 → cimgui 空指针（C0000005）整进程崩；
        // 原生违例 try/catch 抓不住。只读字段永不触发 getter 副作用，故任何线程都安全。
        // 覆盖率不降：窗口引用通常是字段；即便写成自动属性 public Xxx Window { get; }，其编译器生成后备字段
        // <Xxx>k__BackingField 也是 NonPublic 字段，已被 flags 覆盖。
        foreach (var f in t.GetFields(flags))
        {
            object? v = null; try { v = f.GetValue(target); } catch { v = null; }
            yield return (f.Name, v);
        }
    }

    private static object? GetMemberValue(object target, Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) { try { return p.GetValue(target); } catch { return null; } }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) { try { return f.GetValue(target); } catch { return null; } }
        return null;
    }

    /// <summary> 体检过滤：真正值得报的界面文字（去掉 "[igButton] " 前缀、排除 ImGui ###id 与太短/纯符号串）。 </summary>
    private static bool IsLikelyUiText(string s)
    {
        var t = s;
        var idx = t.IndexOf(']');
        if (idx >= 0 && idx < t.Length - 1) t = t[(idx + 1)..].Trim();   // 去掉 "[source] " 前缀
        if (t.Length < 3) return false;
        if (t.StartsWith("###")) return false;                            // ImGui 内部 id
        var letters = t.Count(char.IsAsciiLetter);
        return letters >= 2;                                              // 至少两个字母，排除纯数字/符号
    }

    public void Dispose()
    {
        Framework.Update -= OnFramework;
        PluginInterface.UiBuilder.Draw -= DrawAll;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        Replacement.RepoCacheUpdated -= OnRepoCacheUpdated;   // 退订，避免卸载后回调
        Replacement.MissedCaptured -= OnMissedCaptured;        // 2026-09-20 发布审查：补退订
        Mt.MissedBatchTranslated -= OnMissedBatchTranslated;
        Mt.Dispose();          // 中断进行中的翻译（否则卸载后后台任务还在跑）
        Hook.Dispose();
        AtkHook.Dispose();
        Replacement.Dispose();
        AppLog.Info("[插件] 翻译插件的插件 已卸载");
    }
}
