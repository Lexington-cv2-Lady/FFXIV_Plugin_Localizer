using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVPluginLocalizer.Services;
using FFXIVPluginLocalizer.Windows;

namespace FFXIVPluginLocalizer;

/// <summary> 插件界面汉化：给其他 Dalamud 插件的界面文本做运行时汉化。MVP 为只读采集模式。 </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/plocalizer";

    public readonly WindowSystem WindowSystem = new("FFXIVPluginLocalizer");
    public Configuration Configuration { get; init; }
    public AppLog AppLog { get; }
    public ImGuiHookService Hook { get; }
    public ReplacementService Replacement { get; }
    public MtTranslateService Mt { get; }
    public MainWindow MainWindow { get; }
    public LogWindow LogWindow { get; }
    public TranslationWindow TranslationWindow { get; }
    public AiSettingsWindow AiSettingsWindow { get; }
    public WindowReplaceWindow WindowReplaceWindow { get; }
    public SourceExtractWindow SourceExtractWindow { get; }
    public SourceExtractService SourceExtract { get; }
    public WikiGlossaryService Wiki { get; }

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
        // wiki 官方术语表（可选）：加载后作为替换最高优先级词源 + 机翻参考
        Wiki = new WikiGlossaryService(AppLog);
        EnsureWikiDir();
        Replacement = new ReplacementService(AppLog, PluginInterface.GetPluginConfigDirectory);
        Replacement.Enabled = Configuration.ReplacementEnabled;
        Replacement.SyncFdcnOnStartup(); // 启动同步：FDCN 文件指纹变了才自动重导；未装 FDCN 用内置翻译包打底
        if (Configuration.WikiEnabled && !string.IsNullOrWhiteSpace(Configuration.WikiDir))
        {
            Wiki.Load(Configuration.WikiDir);
            Replacement.SetWikiTerms(new Dictionary<string, string>(Wiki.All, StringComparer.Ordinal));
        }
        Hook = new ImGuiHookService(AppLog, Log, Interop, () => Configuration.HooksEnabled,
            () => Configuration.WidgetHooks, Replacement);
        Mt = new MtTranslateService(AppLog, Replacement, Configuration);
        Mt.SetWiki(Wiki); // 机翻时附带官方术语对照，保证专有名词译名一致
        MainWindow = new MainWindow(this);
        LogWindow = new LogWindow(this);
        TranslationWindow = new TranslationWindow(this, Replacement, Mt);
        AiSettingsWindow = new AiSettingsWindow(this, Mt);
        WindowReplaceWindow = new WindowReplaceWindow(this, Replacement, Mt);
        SourceExtract = new SourceExtractService(AppLog, Configuration, PluginInterface.GetPluginConfigDirectory);
        SourceExtractWindow = new SourceExtractWindow(this, SourceExtract, Replacement);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(TranslationWindow);
        WindowSystem.AddWindow(AiSettingsWindow);
        WindowSystem.AddWindow(WindowReplaceWindow);
        WindowSystem.AddWindow(SourceExtractWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开插件界面汉化窗口"
        });

        PluginInterface.UiBuilder.Draw += DrawAll;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain; // 插件安装器的设置按钮：本插件暂无独立设置窗，打开主窗（官方模板同款回调，缺失会在安装器报校验警告）
        Framework.Update += OnFramework;

        AppLog.Info("[插件] 插件界面汉化 已加载（MVP：只读采集模式）");
        Log.Information("插件界面汉化 已加载");
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
    }

    private void OnCommand(string command, string args) => ToggleMain();

    private void ToggleMain() => MainWindow.Toggle();

    /// <summary> 打开/关闭日志窗口（主窗口「日志窗口」按钮入口）。 </summary>
    public void ToggleLogUi() => LogWindow.Toggle();

    /// <summary> 打开/关闭安装器翻译窗口（主窗口「安装器翻译」按钮入口）。 </summary>
    public void ToggleTranslationUi() => TranslationWindow.Toggle();

    /// <summary> 打开/关闭 AI 设置窗口（安装器翻译窗口「AI 设置」按钮入口）。 </summary>
    public void ToggleAiSettingsUi() => AiSettingsWindow.Toggle();

    /// <summary> 打开/关闭插件翻译窗口（主窗口「插件翻译」按钮入口）。 </summary>
    public void ToggleWindowReplaceUi() => WindowReplaceWindow.Toggle();

    /// <summary> 打开/关闭源码提取窗口（主窗口「源码提取」按钮入口）。 </summary>
    public void ToggleSourceExtractUi() => SourceExtractWindow.Toggle();

    /// <summary> 主窗口「还原英文」：清空生效对照表让界面立刻回到英文（磁盘文件保留）。 </summary>
    public void RestoreEnglish()
    {
        Replacement.ClearActive();
        AppLog.Info("[还原] 界面已还原为英文（对照表文件未删除）");
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
    private bool _startupCheckDone;

    private void StartupCheck()
    {
        try
        {
            var count = Replacement.CollectMissing().Values.Distinct().Count();
            if (count == 0)
            {
                AppLog.Info("[自动] 启动检查：对照表已覆盖全部已装插件介绍");
                return;
            }
            if (string.IsNullOrWhiteSpace(Configuration.ZhipuApiKey))
            {
                AppLog.Info($"[自动] 检测到 {count} 条介绍缺口，未配置 API Key，跳过（可在「安装器翻译」窗口填写）");
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

    public void Dispose()
    {
        Framework.Update -= OnFramework;
        PluginInterface.UiBuilder.Draw -= DrawAll;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        Hook.Dispose();
        Replacement.Dispose();
        AppLog.Info("[插件] 插件界面汉化 已卸载");
    }
}
