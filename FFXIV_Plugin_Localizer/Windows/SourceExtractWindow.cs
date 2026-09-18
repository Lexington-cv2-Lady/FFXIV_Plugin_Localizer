using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPluginLocalizer.Services;

namespace FFXIVPluginLocalizer.Windows;

/// <summary> 源码提取窗口：从插件的公开 GitHub 仓库直接提取界面文案。
/// ⚠ 网络铁律：**必须勾选「启用代理」且填写代理地址**，二者同时满足才允许访问 GitHub；
///   未勾选时即使填了地址也一律拒绝，绝不尝试直连。 </summary>
public sealed class SourceExtractWindow : Window
{
    private readonly Plugin _plugin;
    private readonly SourceExtractService _svc;
    private readonly ReplacementService _replacement;
    private readonly Dictionary<string, (int Total, int Translated, bool Done)> _progressCache = new();
    private List<(string Name, string DisplayName, string RepoUrl)> _plugins = new();
    private string _filter = "";   // 搜索过滤（内部名/显示名/仓库地址）
    private bool _listLoaded;
    private string _manualUrl = "";
    private volatile string _summary = "";    // 后台任务也写（H2），volatile 保证 UI 线程及时看到新值
    /// <summary> 后台任务写、UI 读（H2 修正）：普通 Dictionary 跨线程读写属未定义行为，换并发容器。 </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastResult = new();
    private readonly Dictionary<string, int> _chineseCache = new(); // 插件名 → 已装 DLL 的中文字符串条数（0=原版英文）
    /// <summary> 插件名 → 是否有可打开的配置/主界面（2026-09-18：无界面插件不显示）。 </summary>
    private readonly Dictionary<string, bool> _hasUiCache = new();
    private int _hiddenNoUi;   // 被隐藏的无界面插件数
    private bool _testing;          // 连接测试进行中
    private bool? _testOk;          // 上次测试结果（null=未测）
    private string _testMessage = "";
    private bool _batchRunning;     // 「全部提取」进行中（批量任务自己驱动，不走 _svc.Running 判断）
    /// <summary> 已完成的插件数（后台任务 ++，UI 读 → 用 Interlocked，H2）。 </summary>
    private int _batchDone;
    private int _batchTotal;        // 本轮批量总数
    /// <summary>「网络设置」区块是否展开；null = 尚未决定（首次绘制时按"网络是否已配好"自动定）。 </summary>
    private bool? _netOpen;
    /// <summary> 上次绘制时间（用于判断"窗口刚被打开"：间隔 &gt;1 秒即视为重新打开，触发自动刷新）。 </summary>
    private DateTime _lastDrawUtc = DateTime.MinValue;
    /// <summary> 上次自动刷新列表的时间（窗口开着时每 5 秒轻量重扫一次）。 </summary>
    private DateTime _lastAutoRefreshUtc = DateTime.MinValue;

    public SourceExtractWindow(Plugin plugin, SourceExtractService svc, ReplacementService replacement)
        : base("源码提取###PluginLocalizerSource")
    {
        _plugin = plugin;
        _svc = svc;
        _replacement = replacement;
        Size = new Vector2(680, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        // ── 说明（精简为两行，细节放各区块的折叠里，避免开窗就是一大片文字）──
        Ui.Hint("从插件的公开源码提取界面文案；闭源插件无法翻译。需要能访问 GitHub。");

        // 首次打开时：网络尚未配好就把「网络设置」展开，已配好则收起——省掉每次开窗的视觉噪音。
        if (_netOpen == null)
            _netOpen = !cfg.CanAccessGitHub || string.IsNullOrWhiteSpace(cfg.ProxyPort);

        DrawNetworkSection(cfg);
        DrawManualUrlSection(cfg);

        // ── 运行状态/结果（放在区块下方，任何操作都看得到）──
        if (_svc.Running)
        {
            Ui.ColoredWrapped(new Vector4(1f, 0.8f, 0.3f, 1f),
                _batchRunning ? $"{_svc.Status}（全部提取中，请勿关闭游戏）" : _svc.Status);
        }
        else if (_summary.Length > 0)
        {
            ImGui.TextWrapped(_summary);
        }

        ImGui.Separator();

        DrawPluginList(cfg);
    }

    /// <summary>
    /// 网络设置区块（可折叠）：代理铁律 = **勾选 + 填端口**，二者缺一不可，未勾选时填了也不联网。
    /// 折叠起来的理由：配好之后这条基本不用再碰，长期占着半屏只是噪音。
    /// </summary>
    private void DrawNetworkSection(Configuration cfg)
    {
        var open = _netOpen ?? false;
        if (ImGui.CollapsingHeader($"网络设置（访问 GitHub）{(cfg.CanAccessGitHub ? "" : "　⚠ 未配好")}##net"))
        {
            _netOpen = true;
            using (var g = ImRaii.Group())
            {
                var useProxy = cfg.UseProxy;
                if (ImGui.Checkbox("启用代理", ref useProxy))
                {
                    cfg.UseProxy = useProxy;
                    cfg.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("只有勾选此项、并填写端口后，本功能才会走代理访问 GitHub。\n未勾选时即使填了端口也不会用它（直连）。");

                // ── 第一行尾部：协议 / 主机 / 端口 ──
                Ui.SameLineIfFits(90f + ImGui.GetStyle().ItemSpacing.X + 110f + ImGui.GetStyle().ItemSpacing.X + 80f);
                ImGui.SetNextItemWidth(90f);
                var scheme = cfg.ProxyScheme;
                if (ImGui.BeginCombo("##ProxyScheme", scheme))
                {
                    foreach (var s in new[] { "http", "socks5" })
                    {
                        if (ImGui.Selectable(s, s == scheme))
                        {
                            cfg.ProxyScheme = s;
                            cfg.Save();
                        }
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("代理协议。Clash / v2ray 一般用 http。");

                Ui.SameLineIfFits(110f + ImGui.GetStyle().ItemSpacing.X + 80f);
                ImGui.SetNextItemWidth(110f);
                var host = cfg.ProxyHost;
                if (ImGui.InputText("##ProxyHost", ref host, 64))
                {
                    cfg.ProxyHost = host.Trim();
                    cfg.Save(); // 改动即存（失焦事件在游戏内不可靠，曾导致配置丢失）
                }

                Ui.SameLineIfFits(80f);
                ImGui.SetNextItemWidth(80f);
                var port = cfg.ProxyPort;
                if (ImGui.InputTextWithHint("##ProxyPort", "端口", ref port, 8))
                {
                    cfg.ProxyPort = new string(port.Where(char.IsDigit).ToArray());
                    cfg.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("只填端口号即可，如 Clash 默认 7890、v2ray 常见 10809（http）或 1080（socks5）。\n只保存在本机配置，不会随插件分发。");

                // ── 第二行：端口快捷填充 + 两个连接测试 ──
                ImGui.TextDisabled("快捷端口：");
                ImGui.SameLine();
                if (ImGui.SmallButton("7890")) { cfg.ProxyPort = "7890"; cfg.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clash 默认端口");
                ImGui.SameLine();
                if (ImGui.SmallButton("10809")) { cfg.ProxyPort = "10809"; cfg.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("v2rayN 默认 http 端口");
                ImGui.SameLine();
                if (ImGui.SmallButton("1080")) { cfg.ProxyScheme = "socks5"; cfg.ProxyPort = "1080"; cfg.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("常见 socks5 端口（会自动切换为 socks5 协议）");

                Ui.SameLineIfFits(Ui.ButtonWidth("测试代理") + ImGui.GetStyle().ItemSpacing.X + Ui.ButtonWidth("测试直连"));
                ImGui.BeginDisabled(_testing);
                if (ImGui.Button(_testing ? "测试中…" : "测试直连"))
                    StartTest(useProxy: false);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("测试不使用代理时能否访问 GitHub（部分网络环境可直连）。");
                ImGui.SameLine();
                ImGui.BeginDisabled(_testing || !cfg.CanAccessGitHub || !cfg.UseProxy);
                if (ImGui.Button("测试代理"))
                    StartTest(useProxy: true);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("测试通过当前代理能否访问 GitHub。\n只有测试成功才说明代理可用。");

                // ── 状态：中性色说明填写情况；绿色**只在测试通过后**出现 ──
                if (_testOk == true)
                    Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"【成功】 {_testMessage}");
                else if (_testOk == false)
                    Ui.ColoredWrapped(new Vector4(1f, 0.45f, 0.4f, 1f), $"【失败】 {_testMessage}");
                else if (!cfg.UseProxy)
                    Ui.ColoredWrapped(new Vector4(0.75f, 0.8f, 0.85f, 1f),
                        "当前为「直连」模式（未启用代理）——点「测试直连」确认能否访问 GitHub。");
                else if (string.IsNullOrWhiteSpace(cfg.ProxyPort))
                    Ui.ColoredWrapped(new Vector4(1f, 0.6f, 0.35f, 1f), "【需填写】 已勾选启用代理，但端口为空：请填写端口。");
                else
                    Ui.ColoredWrapped(new Vector4(0.75f, 0.8f, 0.85f, 1f),
                        $"已填写代理 {cfg.ProxyAddress}（尚未验证连通性）——建议先点「测试代理」确认。");
            }
            Ui.FrameLastGroup(0.35f);   // 分组边框：让"这一坨是一组"一目了然
        }
        else
        {
            _netOpen = false;
            // 收起时给一行摘要，不让人猜当前是什么状态
            ImGui.SameLine();
            Ui.Hint(cfg.UseProxy
                ? (string.IsNullOrWhiteSpace(cfg.ProxyPort) ? "（已勾选但没填端口）" : $"（代理 {cfg.ProxyAddress}）")
                : "（直连模式）");
        }
    }

    /// <summary>
    /// 手动填写仓库地址区块（可折叠）。默认收起——绝大多数情况直接用下方已装插件列表。
    /// ⚠ 这里曾出过 UI bug：输入框按"只留 90px 给按钮"算宽度，但后面其实有 3 个按钮，
    ///   于是按钮被挤出窗口右缘（用户实测"打开按钮都出边框了"）。现改为**输入框占满一行**，
    ///   按钮另起一行并用 SameLineIfFits 自适应。
    /// </summary>
    private void DrawManualUrlSection(Configuration cfg)
    {
        if (!ImGui.CollapsingHeader("手动填写仓库地址（可选）##manual"))
            return;
        {
            using var g = ImRaii.Group();
            ImGui.TextDisabled("用于提取「未装 / 未列在下方」的插件，或想指定某个仓库时。");
            ImGui.SetNextItemWidth(-1f);   // 占满整行，按钮另起一行 → 绝不再被挤出去
            ImGui.InputTextWithHint("##ManualUrl", "https://github.com/作者/仓库", ref _manualUrl, 512);

            var canGo = cfg.CanAccessGitHub && !_svc.Running;
            ImGui.BeginDisabled(!canGo);
            if (ImGui.Button("提取此仓库"))
            {
                StartExtract("（手动）", _manualUrl.Trim());
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("拉取该仓库并按新规则提取界面文案。\n按钮灰掉说明：未配好网络（见上方「网络设置」）或正在提取中。");
        }
        Ui.FrameLastGroup(0.35f);
    }

    /// <summary> 已装插件列表：工具栏（搜索/刷新/全部提取）+ 目录入口 + 列表本体。 </summary>
    private void DrawPluginList(Configuration cfg)
    {
        // ── 自动刷新（2026-09-15 用户："列表需要手动刷新，很麻烦，能自动检测自动刷新吗"）──
        //    两种情况都自动重扫：
        //      ① **刚打开窗口**（两次 Draw 间隔 >1 秒 ⇒ 中间窗口是关闭的）——覆盖"装了新插件后开窗"；
        //      ② **窗口开着时**每 5 秒轻量重扫——覆盖"边开着边装插件 / 边翻译"。
        //    ⚠ 成本已压到很低：`SourceExtractService.CheckInstalledChinese` 加了"按 DLL + 版本目录
        //      时间戳"的缓存，重复刷新只做 stat（实测 28 个插件约 3ms；无缓存时才 137ms 解析元数据）。
        var now = DateTime.UtcNow;
        var reopened = (now - _lastDrawUtc).TotalSeconds > 1.0;
        var due = (now - _lastAutoRefreshUtc).TotalSeconds >= 5.0;
        if (!_listLoaded || reopened || due)
        {
            RefreshList();
            _lastAutoRefreshUtc = now;
        }
        _lastDrawUtc = now;

        // ── 工具栏：计数 + 搜索 + 操作（按钮按需换行，窄窗口不会裁掉）──
        ImGui.TextDisabled($"已装且带 GitHub 地址：{_plugins.Count} 个");
        if (_hiddenNoUi > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"（已隐藏 {_hiddenNoUi} 个无配置/主界面的插件）");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190f);
        ImGui.InputTextWithHint("##srcFilter", "搜索（内部名/显示名）", ref _filter, 128);
        ImGui.SameLine();
        if (ImGui.Button("刷新##src"))
        {
            RefreshList();
            _lastAutoRefreshUtc = DateTime.UtcNow;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立即重新枚举已装插件，并检测各插件当前 DLL 是否已是中文版。\n" +
                             "列表本身会自动刷新（每次打开窗口、以及开着时每 5 秒一次），\n" +
                             "所以装了新插件后通常不必手动点这里。");

        Ui.SameLineIfFits(Ui.ButtonWidth("全部提取"));
        {
            var canBatch = cfg.CanAccessGitHub && !_svc.Running && !_batchRunning;
            ImGui.BeginDisabled(!canBatch);
            if (ImGui.Button(_batchRunning ? $"全部提取中 {_batchDone}/{_batchTotal}…" : "全部提取"))
            {
                StartExtractAll();
            }
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把列表里的插件逐个提取一遍（自动跳过：已是中文版）。\n" +
                             "每次都会 git pull 更新源码并**覆盖**旧候选文件——插件上游更新后重新提取即可拿到新文案。\n" +
                             "有搜索筛选时只处理筛选出的那些；已翻译的条目不受影响（译文表独立于候选）。");

        // 目录入口（结果相关，跟列表放一起更顺手）
        Ui.SameLineIfFits(Ui.ButtonWidth("打开提取目录"));
        if (ImGui.Button("打开提取目录"))
            OpenOutputDir();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开提取结果目录（数据目录\\文案扫描\\<插件名>_源码提取.json）。\n未翻译清单就放在这里，可交给翻译管线或外部 AI。");
        Ui.SameLineIfFits(Ui.ButtonWidth("打开仓库目录"));
        if (ImGui.Button("打开仓库目录"))
            OpenRepoDir();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开已克隆的源码仓库目录（数据目录\\源码仓库\\）。");

        DrawList();
    }

    /// <summary> 插件列表本体（带搜索过滤与三态标注）。 </summary>
    private void DrawList()
    {
        using var child = ImRaii.Child("##源码插件列表", new Vector2(-1f, -1f), true);
        if (!child.Success) return;

        if (_plugins.Count == 0)
        {
            Ui.Hint("没有找到带 GitHub 地址的已装插件（可在上方「手动填写仓库地址」里填）。");
        }
        var filter = _filter.Trim();
        var shown = 0;
        for (var i = 0; i < _plugins.Count; i++)
        {
            var (name, displayName, url) = _plugins[i];
            if (!MatchesFilter(_plugins[i], filter)) continue;
            shown++;
            ImGui.PushID(i);

            // ── 两行式排版，避免单行过长被窗口右缘截断 ──
            // 第一行：[提取] 内部名（显示名：X）  状态标注
            if (ImGui.Button("提取##go"))
                StartExtract(name, url);
            ImGui.SameLine();
            if (ImGui.Button("复制名##cp"))
            {
                var copyText = displayName.Length > 0 ? displayName : name;
                ImGui.SetClipboardText(copyText);
                _summary = $"已复制插件名到剪贴板：{copyText}";
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("复制该插件的显示名（安装器里显示的名字）到剪贴板。");
            ImGui.SameLine();
            if (displayName.Length > 0 && !string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase))
                ImGui.TextUnformatted($"{displayName}【{name}】");
            else
                ImGui.TextUnformatted(name);

            // 状态标注：另起一行（不与名字抢宽度）
            var pg = _progressCache.TryGetValue(name, out var p0) ? p0 : (Total: 0, Translated: 0, Done: false);
            if (_chineseCache.TryGetValue(name, out var zh) && zh > 0)
                Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"    （已是中文版·{zh} 条，无需提取）");
            else if (pg.Done)
                Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), $"    【已翻译】{pg.Translated}/{pg.Total} 条（重新提取会覆盖更新候选）");
            else if (pg.Total > 0)
                Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.4f, 1f), $"    待翻译 {pg.Translated}/{pg.Total} 条");
            else
                Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.4f, 1f), "    （未提取）");

            // 第二行：仓库地址（灰色，自动换行）
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped("    " + url);
            ImGui.PopStyleColor();

            if (_lastResult.TryGetValue(name, out var res))
                Ui.ColoredWrapped(new Vector4(0.6f, 0.85f, 0.6f, 1f), "    " + res);
            ImGui.Spacing();

            ImGui.PopID();
        }
        if (filter.Length > 0)
        {
            ImGui.TextDisabled(shown == 0
                ? $"没有匹配「{filter}」的插件。"
                : $"（筛选后显示 {shown} 个）");
        }
    }


    /// <summary> 刷新插件列表 + 检测各插件已装 DLL 的中文情况（用于列表标注与提前跳过）。 </summary>
    private void RefreshList()
    {
        _hasUiCache.Clear();   // 插件启用/卸载后状态可能变，每次刷新重查
        var all = _svc.ListPluginsWithRepo();
        _plugins = all.Where(p => HasOpenableUi(p.Name)).ToList();   // 2026-09-18：无配置/主界面的插件不显示
        _hiddenNoUi = all.Count - _plugins.Count;
        _listLoaded = true;
        _chineseCache.Clear();
        _progressCache.Clear();
        foreach (var (name, _, _) in _plugins)
        {
            try
            {
                var (isChinese, zh, _) = _svc.CheckInstalledChinese(name);
                _chineseCache[name] = isChinese ? zh : 0;
            }
            catch
            {
                _chineseCache[name] = 0;
            }
            try
            {
                _progressCache[name] = _replacement.GetTranslationProgress(name);
            }
            catch
            {
                _progressCache[name] = (0, 0, false);
            }
        }
    }

    /// <summary>
    /// 该插件是否有可打开的界面（配置界面或主界面）。未加载/查不到视为**有**（避免误删：
    /// 只是当前没启用并不代表没有配置界面）。2026-09-18 用户要求"不能打开配置窗口的不显示"。
    /// </summary>
    private bool HasOpenableUi(string plugin)
    {
        if (_hasUiCache.TryGetValue(plugin, out var v)) return v;
        var result = true;
        try
        {
            var target = Plugin.PluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, plugin, StringComparison.OrdinalIgnoreCase));
            if (target != null && target.IsLoaded)
                result = target.HasConfigUi || target.HasMainUi;
        }
        catch { /* 查不到按保留处理 */ }
        _hasUiCache[plugin] = result;
        return result;
    }

    /// <summary> 打开提取结果目录（未翻译 json 所在处）。 </summary>
    private void OpenOutputDir() => OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), Services.SourceExtractService.OutputDirName));

    /// <summary> 打开已克隆的源码仓库目录。 </summary>
    private void OpenRepoDir() => OpenDir(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), Services.SourceExtractService.RepoDirName));

    /// <summary> 用资源管理器打开目录（不存在则先创建）。 </summary>
    private void OpenDir(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _summary = "打开目录失败：" + ex.Message;
            _plugin.AppLog.Error("[源码] 打开目录失败：" + ex.Message);
        }
    }

    /// <summary> 连接测试（后台跑，结果更新状态区）。useProxy=false 测直连。 </summary>
    private void StartTest(bool useProxy)
    {
        if (_testing) return;
        _testing = true;
        _testOk = null;
        _testMessage = "测试中…";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, msg) = useProxy ? await _svc.TestProxyAsync() : await _svc.TestDirectAsync();
                _testOk = ok;
                _testMessage = msg;
            }
            catch (Exception ex)
            {
                _testOk = false;
                _testMessage = "测试异常：" + ex.Message;
            }
            finally
            {
                _testing = false;
            }
        });
    }

    private void StartExtract(string name, string url)
    {
        if (_svc.Running) return;
        _summary = "";
        _svc.SetStatus($"正在拉取 {name} 的仓库…");
        _svc.Running = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, count, funcs, msg) = await _svc.ExtractAsync(name, url);
                if (ok) _replacement.InvalidateCandidateCache();   // 候选已更新，让窗口读到新数据
                _lastResult[name] = (ok ? "【正常】 " : "【异常】 ") + msg;
                _summary = msg;
                _plugin.AppLog.Info($"[源码] {name}：{msg}");
            }
            catch (Exception ex)
            {
                _summary = "提取失败：" + ex.Message;
                _plugin.AppLog.Error("[源码] 提取失败：" + ex.Message);
            }
            finally
            {
                _svc.Running = false;
            }
        });
    }

    /// <summary> 该插件名是否匹配当前搜索框（内部名 / 显示名 / 仓库地址任一命中）。 </summary>
    private static bool MatchesFilter((string Name, string DisplayName, string RepoUrl) p, string filter)
        => filter.Length == 0
           || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || p.RepoUrl.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该插件是否**需要**提取：已是中文版（已装 DLL 含大量中文）不必再联网拉仓库。
    /// ⚠ 2026-09-17：不再跳过"已翻译完成"的插件——插件上游源码更新后，重新提取会
    ///   git pull 更新源码并**覆盖**旧的候选文件，否则新版本的新文案永远进不来。
    /// </summary>
    private bool NeedsExtract(string name)
    {
        if (_chineseCache.TryGetValue(name, out var zh) && zh > 0) return false;   // 已装 DLL 就是中文版
        return true;
    }

    /// <summary>
    /// **全部提取**：把（筛选后的）插件逐个提取一遍。
    /// ⚠ 串行执行 + 温和限速：ExtractAsync 内部要跑 git，并发既无必要也容易触发 GitHub 限流。
    /// 单个插件失败不影响后续（逐个 try/catch，结果写进各自的 _lastResult）。
    /// </summary>
    private void StartExtractAll()
    {
        if (_svc.Running || _batchRunning) return;
        var filter = _filter.Trim();
        var targets = _plugins
            .Where(p => MatchesFilter(p, filter) && NeedsExtract(p.Name))
            .ToList();
        if (targets.Count == 0)
        {
            _summary = filter.Length > 0
                ? $"筛选出的插件都无需提取（已是中文版）。"
                : "没有需要提取的插件（都已是中文版）。";
            return;
        }

        _batchRunning = true;
        _batchDone = 0;
        _batchTotal = targets.Count;
        _svc.Running = true;
        _summary = "";
        _plugin.AppLog.Info($"[源码] 全部提取开始：{targets.Count} 个插件");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                foreach (var (name, _, url) in targets)
                {
                    _svc.SetStatus($"[全部提取] {_batchDone + 1}/{_batchTotal}：{name}");
                    try
                    {
                        var (ok, count, funcs, msg) = await _svc.ExtractAsync(name, url);
                        _replacement.InvalidateCandidateCache();
                        _lastResult[name] = (ok ? "【正常】 " : "【异常】 ") + msg;
                        _plugin.AppLog.Info($"[源码] 全部提取：{name} → {msg}");
                    }
                    catch (Exception ex)
                    {
                        _lastResult[name] = "【异常】 " + ex.Message;
                        _plugin.AppLog.Error($"[源码] 全部提取：{name} 失败：{ex.Message}");
                    }
                    System.Threading.Interlocked.Increment(ref _batchDone);
                    await System.Threading.Tasks.Task.Delay(700);   // 限速，别让 GitHub 判定为滥用
                }
                _summary = $"全部提取完成：共处理 {_batchTotal} 个插件（详见各条目结果与日志）。";
                _plugin.AppLog.Info($"[源码] 全部提取完成：{_batchTotal} 个插件");
            }
            finally
            {
                _batchRunning = false;
                _svc.Running = false;
            }
        });
    }
}
