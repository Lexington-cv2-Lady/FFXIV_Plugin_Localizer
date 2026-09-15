using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 源码提取：从插件的公开 GitHub 仓库拉取源码，抓出**界面文案**及其**绘制函数**。
/// 相比扫 DLL 字符串堆的两大优势：
///   ① 只拿界面文字（不混日志/异常/内部常量）；② 知道每句由哪个 ImGui 函数绘制 → 直接指出需要哪些钩子。
///
/// ⚠ 网络铁律：**必须勾选「启用代理」且填了代理地址**才允许访问 GitHub（Configuration.CanAccessGitHub）。
///   未勾选时即使填了地址也一律拒绝，绝不尝试直连。
/// </summary>
public sealed class SourceExtractService
{
    /// <summary>
    /// 连接测试：通过当前代理实际访问 GitHub，验证代理是否真的可用。
    /// ⚠ 只有本测试**成功**才说明能拉源码——仅"填了端口"不代表代理通（用户反馈过状态灯误导）。
    /// 用 git ls-remote（轻量、不需要仓库存在性，只验证与 github.com 的连通性）。
    /// </summary>
    public async Task<(bool Ok, string Message)> TestProxyAsync()
    {
        return await TestConnectionAsync(useProxy: true);
    }

    /// <summary> 测试「不使用代理」的直连是否可用（有些网络环境可直接访问 GitHub）。 </summary>
    public async Task<(bool Ok, string Message)> TestDirectAsync()
    {
        return await TestConnectionAsync(useProxy: false);
    }

    /// <summary> 连接测试：useProxy=true 走配置的代理，false 直连。 </summary>
    private async Task<(bool Ok, string Message)> TestConnectionAsync(bool useProxy)
    {
        if (useProxy && !_cfg.CanAccessGitHub)
        {
            return (false, "请先勾选「启用代理」并填写端口");
        }
        var proxy = useProxy ? _cfg.ProxyAddress : "";
        // ⚠ 不要用 --exit-code 与 -h + 具体 ref 组合：实测 git 2.55 下会误报退出码 2（明明能连）。
        //   直接跑最朴素的 ls-remote，靠退出码判断连通性即可。
        var (code, output) = await RunGitAsync(
            new[] { "ls-remote", "https://github.com/octocat/Hello-World.git" }, proxy);

        var label = useProxy ? $"代理 {proxy}" : "直连（不使用代理）";
        if (code == 0)
        {
            _appLog.Info($"[源码] 连接测试成功：{label}");
            return (true, $"连接成功：{label} 可以访问 GitHub");
        }

        var why = output.Contains("Could not connect", StringComparison.OrdinalIgnoreCase) ||
                  output.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            ? (useProxy ? "无法通过该代理连接 GitHub —— 请确认代理软件已开启、端口与协议正确"
                        : "无法直连 GitHub —— 需要启用代理（梯子）后重试")
            : output.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)
                ? "DNS 解析失败 —— 可能是代理未开启或网络受限"
                : Tail(output);
        _appLog.Warn($"[源码] 连接测试失败（{label}）：{why}");
        return (false, $"连接失败：{why}（{label}）");
    }

    /// <summary> 提取结果子目录（仓库克隆到此处）。 </summary>
    public const string RepoDirName = "源码仓库";
    /// <summary> 提取结果子目录（产出的对照表放此处）。 </summary>
    public const string OutputDirName = "文案扫描";

    /// <summary> 界面文案调用：ImGui.Xxx("…") / Im.Xxx("…") / ImRaii.Xxx("…") 等常见封装。
    /// ⚠ `ImRaii2` 要显式列出（Craftimizer 的包装库叫这名，而 `ImRaii` 前缀后要求跟 `.`，匹配不到 `ImRaii2.`）。 </summary>
    private static readonly Regex CallRe = new(
        @"\b(?:ImGui|ImGuiHelpers|ImRaii2?|Im|Luna)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""(?:\s*u8)?",
        RegexOptions.Compiled);

    /// <summary> Dalamud 官方本地化：Loc.Localize("Key", "默认英文")，取第二个参数。 </summary>
    private static readonly Regex LocRe = new(
        @"Loc\.Localize\s*\(\s*(?:\$)?@?""(?:[^""\\]|\\.)*""\s*,\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary> u8 后缀字面量单独一档（Glamourer 风格）：Im.Text("…"u8)。 </summary>
    private static readonly Regex U8Re = new(
        @"\b(?:Im|ImGui)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*""((?:[^""\\]|\\.)*)""\s*u8",
        RegexOptions.Compiled);

    /// <summary> 任意字符串字面量（用于统计源码里的中文字符串，判断是否已源码级汉化）。 </summary>
    private static readonly Regex StringLiteralRe = new(
        @"""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// **自定义封装函数**的文字实参：<c>DrawOption("Enable Synthesis Helper", …)</c>、
    /// <c>TabItem("General")</c>、<c>ImGuiUtils.Tooltip("Open Settings")</c> 等。
    ///
    /// ⚠ 为什么必须加这条（2026-09-15 Craftimizer 血泪）：该插件的配置窗**几乎全部**文案都这么写——
    ///   自定义封装 + **跨行调用**：
    ///   <code>
    ///   DrawOption(
    ///       "Enable Synthesis Helper",
    ///       "Adds a helper next to your synthesis window…",
    ///       Config.EnableSynthHelper, …);
    ///   </code>
    ///   而它内部是用 <c>ImGui.Checkbox(label, …)</c> / <c>ImGui.InputText(label, …)</c> 画的——
    ///   **替换层完全拦得到，但提取器一条也没采到**（函数名不是 `ImGui.*`、字符串又在下一行），
    ///   结果整个界面全英文（用户实测截图）。实测该插件漏采 500+ 条（光 Settings.cs 就 245 条）。
    /// </summary>
    private static readonly Regex BareCallRe = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// **赋值式 / switch 表达式的字符串**：<c>=&gt; "Open a Window"</c>、<c>Name = "Foo Bar"</c>、
    /// <c>{ "Key", "标签" }</c>、<c>new("标签", …)</c> 等。
    ///
    /// ⚠ 为什么需要（2026-09-15 Craftimizer 实测）：`"Open a Window"` 之类文案常写成
    ///   <c>CopyType.OpenWindow =&gt; "Open a Window",</c>（switch 表达式返回值），**后面没有括号**，
    ///   所以"函数调用式"的两条规则都抓不到，而它最终会被显示成下拉框的选项文字。
    /// </summary>
    private static readonly Regex AssignStringRe = new(
        @"(?:=>|=\s*[^=]|,\s*|\(\s*|\[\s*)\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// 裸调用的**排除名单**：这些函数的字符串实参是路径/日志/异常消息等，不是界面文字
    /// （采进来只会白耗机翻配额）。注意只对「裸标识符调用」生效——`ImGui.*` 那批由精确规则负责。
    /// </summary>
    private static readonly HashSet<string> NonUiCallNames = new(StringComparer.Ordinal)
    {
        "Path", "File", "Directory", "FileInfo", "DirectoryInfo", "FileStream", "StreamReader", "StreamWriter",
        "Console", "Debug", "Log", "Logger", "Trace",
        "string", "String", "Format", "Join", "Concat", "Equals", "Compare", "IsNullOrEmpty", "IsNullOrWhiteSpace",
        "Exception", "ArgumentException", "ArgumentNullException", "InvalidOperationException", "NotImplementedException",
        "Convert", "Enum", "Type", "Activator", "Guid", "Uri", "Regex", "Encoding",
        "JsonSerializer", "Deserialize", "Serialize", "XmlSerializer", "JsonDocument", "Parse",
        "Array", "Task", "Thread", "Process", "Environment", "Math", "DateTime", "TimeSpan", "Version", "Assembly",
        "nameof", "throw", "new", "get", "set", "return", "if", "foreach", "while", "switch", "catch", "using", "lock",
        "Marshal", "Interlocked", "BitConverter", "MemoryMarshal", "Unsafe",
        "List", "Dictionary", "HashSet", "Queue", "Stack", "Comparer", "EqualityComparer", "Tuple",
        "GetType", "ToString", "Contains", "StartsWith", "EndsWith", "IndexOf", "Substring", "Replace", "Split", "Trim",
        "Add", "Remove", "Clear", "Insert", "TryGetValue", "ContainsKey", "ToList", "ToArray", "Any", "All", "Where", "Select",
    };

    /// <summary> 判定「已汉化」的中文字符串条数下限（对已安装 DLL 与仓库源码通用）。 </summary>
    private const int BilingualZhThreshold = 50;
    /// <summary> 中文占比判据：中文条数 × 该值 ≥ 英文条数（即中文占 1/N 以上）才认定已汉化。 </summary>
    private const int BilingualZhRatioDiv = 3;

    /// <summary>
    /// 检查**已安装的插件 DLL** 是否已是中文版（如 Glamourer 343 条、Penumbra 641 条中文）。
    /// ⚠ 这才是用户视角的正确判据：插件安装器给的 RepoUrl 是**原作者仓库**（纯英文），
    /// 但用户装的可能是一份**中文编译版**（源码英文 + 二进制含中文）。只看仓库会误判成"未汉化"。
    /// </summary>
    public (bool IsChinese, int ZhCount, string DllPath) CheckInstalledChinese(string pluginName)
    {
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        foreach (var rootName in new[] { "installedPlugins", "devPlugins" })
        {
            var dir = Path.Combine(launcherDir ?? "", rootName, pluginName);
            if (!Directory.Exists(dir)) continue;
            var dll = Directory.EnumerateFiles(dir, pluginName + ".dll", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal).LastOrDefault();
            if (dll == null) continue;
            var n = CountUserStringCjk(dll);
            return (n >= BilingualZhThreshold, n, dll);
        }
        return (false, 0, "");
    }

    /// <summary> 统计 DLL 的 #US 用户字符串堆里含中日韩字符的条数（手解 CLI 元数据，无需反射）。 </summary>
    private static int CountUserStringCjk(string dllPath)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            var cor = pe.PEHeaders.CorHeader;
            if (cor == null) return 0;
            var md = pe.GetSectionData(cor.MetadataDirectory.RelativeVirtualAddress).GetContent().ToArray();
            var pos = 0;
            if (md.Length < 20 || ReadU32(md, ref pos) != 0x424A5342) return 0; // BSJB
            pos += 6 + 2;
            var versionLen = (int)ReadU32(md, ref pos);
            pos += versionLen + 2;
            var streams = ReadU16(md, ref pos);
            for (var i = 0; i < streams; i++)
            {
                var offset = (int)ReadU32(md, ref pos);
                var size = (int)ReadU32(md, ref pos);
                var nameStart = pos;
                while (pos < md.Length && md[pos] != 0) pos++;
                var name = Encoding.ASCII.GetString(md, nameStart, pos - nameStart);
                pos++; // 跳过 \0
                pos += (4 - (pos - nameStart) % 4) % 4;
                if (name != "#US") continue;
                return CountCjkInUserStringHeap(md, offset, size);
            }
        }
        catch { /* 读不了就当非中文版 */ }
        return 0;
    }

    private static int CountCjkInUserStringHeap(byte[] b, int start, int size)
    {
        var p = start;
        var end = start + size;
        if (p < end && b[p] == 0) p++;
        var count = 0;
        while (p < end)
        {
            uint len;
            var b0 = b[p++];
            if ((b0 & 0x80) == 0) len = b0;
            else if ((b0 & 0xC0) == 0x80) { if (p >= end) break; len = (uint)(((b0 & 0x3F) << 8) | b[p]); p += 1; }
            else if ((b0 & 0xE0) == 0xC0) { if (p + 2 >= end) break; len = (uint)(((b0 & 0x1F) << 24) | (b[p] << 16) | (b[p + 1] << 8) | b[p + 2]); p += 3; }
            else break;
            if (len == 0) continue;
            var dataBytes = (int)(len & ~1u);
            if (p + dataBytes > end || dataBytes == 0) { p += dataBytes; continue; }
            var s = Encoding.Unicode.GetString(b, p, dataBytes);
            p += dataBytes;
            if (s.Any(c => c >= 0x4E00 && c <= 0x9FFF)) count++;
        }
        return count;
    }

    private static uint ReadU32(byte[] b, ref int pos)
    {
        var v = (uint)(b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24));
        pos += 4;
        return v;
    }

    private static ushort ReadU16(byte[] b, ref int pos)
    {
        var v = (ushort)(b[pos] | (b[pos + 1] << 8));
        pos += 2;
        return v;
    }

    /// <summary> 判定"绘制函数"是否为纯文本绘制（决定该句走文字通道还是控件通道）。 </summary>
    private static readonly HashSet<string> TextFuncs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextWrapped", "TextColored", "TextDisabled", "BulletText", "LabelText", "SetTooltip",
        "TextUnformatted", "TextV", "TextEx",
    };

    /// <summary>
    /// 首参是「窗口名 / 内部 ID」而非可见文案的函数：**不作为翻译候选**。
    /// 依据（2026-09-14 用户决定「窗口标题不必翻译」）：
    ///   · `Begin` 的字符串是**窗口标题**（画在标题栏）——不翻；
    ///   · `BeginChild`/`BeginTable`/`BeginTabBar`/`PushID` 的字符串是 ImGui ID（不显示给用户），
    ///     提出来只会污染待翻清单（如 BTS 的 `BTSConfigTabs`/`SettingsConfigTable`）。
    /// ⚠ **`Child`/`Table` 也在列**：包装库（如 Craftimizer 的 `ImRaii.Table("table", 3, …)`、
    ///   `ImRaii.Child("macros", …)`）里，首参同样是**ImGui ID** 而非界面文字。实测 Craftimizer 的
    ///   10 个表 ID（`table`/`stats`/`buffBars`/`params`…）曾被当成待翻译文案——**更危险的是真去替换它们**：
    ///   翻译成中文会**改变 table 的内部 ID**，可能导致列宽/排序等状态错乱。必须排除。
    /// ⚠ 以下**不在**此列——它们的标签是**画出来的文字**，必须保留为候选：
    ///     `BeginTabItem`（标签页名）、`BeginCombo`/`BeginMenu`/`BeginPopupModal`（控件标签）、
    ///     `TableSetupColumn`（**表头列名**，实测 TeleporterPlugin 的 `Alias`/`Aetheryte` 就是它）、
    ///     `GroupPanel`（其实现内部 `ImGui.TextUnformatted(name)` 把 name **画出来**，见 Craftimizer ImGuiUtils）。
    /// </summary>
    private static readonly HashSet<string> IdOnlyFuncs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Begin", "BeginChild", "BeginTable", "BeginTabBar", "PushID",
        "Child", "Table",          // 包装库形态（ImRaii.Table / ImRaii.Child）：首参 = ImGui ID
    };

    private readonly AppLog _appLog;
    private readonly Configuration _cfg;
    private readonly Func<string> _configDir;

    /// <summary> 是否在跑（窗口据此禁用按钮）。 </summary>
    public volatile bool Running;
    /// <summary> 进度/结果。 </summary>
    public string Status { get; private set; } = "";

    /// <summary> 由窗口设置进度文本（Status 的 setter 对外只读）。 </summary>
    public void SetStatus(string text) => Status = text;

    public SourceExtractService(AppLog appLog, Configuration cfg, Func<string> configDir)
    {
        _appLog = appLog;
        _cfg = cfg;
        _configDir = configDir;
    }

    /// <summary> 已装插件（名称、GitHub 地址）；取不到地址的也列出，便于手工补。 </summary>
    /// <summary>
    /// 列出已装且带 GitHub 地址的插件：**内含显示名**（清单的 Name 字段，如 `Character Data Sync`），
    /// 因为用户在安装器里看到的是显示名，而内部名是 `Dalamud.CharacterSync`——两者不同会导致"找不到"。
    /// </summary>
    public List<(string Name, string DisplayName, string RepoUrl)> ListPluginsWithRepo()
    {
        var result = new List<(string, string, string)>();
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        foreach (var rootName in new[] { "installedPlugins", "devPlugins" })
        {
            var root = Path.Combine(launcherDir ?? "", rootName);
            if (!Directory.Exists(root)) continue;
            foreach (var pluginDir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(pluginDir);
                string url = "";
                var displayName = "";
                try
                {
                    var manifest = Directory.EnumerateFiles(pluginDir, name + ".json", SearchOption.AllDirectories).FirstOrDefault();
                    if (manifest != null)
                    {
                        var text = File.ReadAllText(manifest);
                        var m = Regex.Match(text, "\"RepoUrl\"\\s*:\\s*\"([^\"]*)\"");
                        if (m.Success) url = m.Groups[1].Value;
                        var n = Regex.Match(text, "\"Name\"\\s*:\\s*\"([^\"]*)\"");
                        if (n.Success) displayName = n.Groups[1].Value;
                    }
                }
                catch { /* 清单坏了就留空 */ }
                if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((name, displayName, url.TrimEnd('/')));
                }
            }
        }
        return result.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 克隆（或更新）插件仓库并提取界面文案。返回（文案数, 绘制函数统计）。
    /// 未满足代理条件时直接返回失败并给出提示，绝不尝试联网。
    /// </summary>
    public async Task<(bool Ok, int Count, Dictionary<string, int> FuncStats, string Message)>
        ExtractAsync(string pluginName, string repoUrl)
    {
        if (!_cfg.CanAccessGitHub)
        {
            return (false, 0, new(), "未启用代理或端口为空：请勾选「启用代理」并填写端口后重试");
        }
        if (string.IsNullOrWhiteSpace(repoUrl) || !repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, new(), "仓库地址无效（需为 github.com 链接）");
        }

        // ── 判据①：**已安装的 DLL** 是否已是中文版（用户视角最准）──
        // 插件安装器给的 RepoUrl 是原作者仓库（纯英文），但用户装的可能是一份中文编译版
        // （如 Glamourer 已装 DLL 含 343 条中文、Penumbra 641 条）——这种情况无需再翻，直接跳过。
        var (installedZh, zhInstalled, dllPath) = CheckInstalledChinese(pluginName);
        if (installedZh)
        {
            var msg = $"已安装的插件本身就是中文版（DLL 含 {zhInstalled} 条中文字符串），无需汉化，跳过。";
            _appLog.Info($"[源码] {pluginName} 跳过：已装 DLL 为中文版（{zhInstalled} 条中文）");
            return (true, 0, new(), msg);
        }

        var repoRoot = Path.Combine(_configDir(), RepoDirName);
        Directory.CreateDirectory(repoRoot);
        var bare = Regex.Match(repoUrl, @"github\.com/([^/]+)/([^/#?]+)");
        if (!bare.Success) return (false, 0, new(), "无法解析仓库地址");
        var dirName = $"{bare.Groups[1].Value}__{bare.Groups[2].Value}".TrimEnd('.');
        var dir = Path.Combine(repoRoot, dirName);
        // 勾选代理才走代理；未勾选则直连（部分网络环境可直连 GitHub）
        var proxy = _cfg.UseProxy ? _cfg.ProxyAddress : "";

        var gitArgs = dir is not null && Directory.Exists(dir)
            ? new[] { "-C", dir, "pull", "--ff-only" }                                  // 已克隆过 → 更新
            : new[] { "clone", "--depth", "1", repoUrl, dir };                          // 首次 → 浅克隆
        var (code, output) = await RunGitAsync(gitArgs, proxy);
        if (code != 0 && !Directory.Exists(dir))
        {
            // 给出可操作的诊断：代理连不上时明确提示检查代理是否开着/端口是否正确
            var hint = output.Contains("Could not connect", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
                ? $"\n提示：无法通过代理 {proxy} 连接 GitHub —— 请确认代理软件已开启、端口正确（当前用 {proxy}）。"
                : "";
            return (false, 0, new(), $"拉取失败（退出码 {code}）：{Tail(output)}{hint}");
        }

        var (strings, funcStats, zhCount) = ExtractFromDirectory(dir);
        if (strings.Count == 0)
        {
            return (true, 0, funcStats, "仓库已拉取，但未找到可识别的界面文案（可能用了不支持的写法）");
        }

        // ── 已是源码级汉化的插件：直接跳过（如 Artisan 源码里就有 2700+ 处中文）──
        // 判据：该仓库源码里的中文字符串达到阈值 → 说明作者/汉化分支已把界面写成中文。
        // 注意：Glamourer/Penumbra 这类源码纯净（0 中文）但「装好后显示中文」的，是**别的汉化插件**
        // 在做运行时替换，源码提取对它们依然有效，不能跳过。
        if (zhCount >= BilingualZhThreshold && zhCount * BilingualZhRatioDiv >= strings.Count)
        {
            var msg = $"该插件源码已内置中文（中文字符串 {zhCount} 条 / 英文字符串 {strings.Count} 条，约占 " +
                      $"{zhCount * 100 / Math.Max(1, zhCount + strings.Count)}%），判定为已汉化，跳过提取。";
            _appLog.Info($"[源码] {pluginName} 跳过：源码已内置中文 {zhCount} 条");
            return (true, 0, funcStats, msg);
        }

        // 写出：<插件名>_源码提取.json（与原字典格式一致，空值待翻译）
        var outDir = Path.Combine(_configDir(), OutputDirName);
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{pluginName}_源码提取.json");
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in strings) dict[s] = "";
        File.WriteAllText(outPath,
            System.Text.Json.JsonSerializer.Serialize(dict, JsonFile.Indented), Encoding.UTF8);

        var funcSummary = string.Join("、", funcStats.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => $"{kv.Key}×{kv.Value}"));
        _appLog.Info($"[源码] {pluginName} 提取 {strings.Count} 条界面文案 → {Path.GetFileName(outPath)}（{funcSummary}）");
        return (true, strings.Count, funcStats, $"提取 {strings.Count} 条 → {Path.GetFileName(outPath)}（主要绘制：{funcSummary}）");
    }

    /// <summary> 扫描目录下所有 .cs，抓界面文案与绘制函数统计。 </summary>
    /// <summary> 扫描目录下所有 .cs，抓界面文案与绘制函数统计，同时统计含中文的字符串（判断是否已汉化）。 </summary>
    private (SortedSet<string> Strings, Dictionary<string, int> FuncStats, int ZhCount) ExtractFromDirectory(string dir)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        var funcStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var zhSeen = new HashSet<string>(StringComparer.Ordinal); // 去重，避免同一句重复计数
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // 跳过构建产物与第三方库目录
            var lower = file.ToLowerInvariant();
            if (lower.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                lower.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); } catch { continue; }
            // ⚠ **整文件匹配，不逐行**：跨行调用（`DrawOption(\n  "标签",\n …)`）极常见，
            //   逐行看的话字符串在下一行、永远匹配不到（这正是 Craftimizer 漏采 500+ 条的根因之一）。
            //   正则里的 `\s*` 本身能跨行，前提是把整段文本交给它。
            {
                // 统计中文字符串字面量（判断"源码级汉化"）
                foreach (Match zm in StringLiteralRe.Matches(text))
                {
                    var lit = Unescape(zm.Groups[1].Value).Trim();
                    if (lit.Length >= 2 && TextHeuristics.HasCjk(lit)) zhSeen.Add(lit);
                }
                // ① 精确规则：ImGui / ImGuiHelpers / ImRaii / Im / Luna 的调用（含 u8、Loc.Localize）
                foreach (Match m in CallRe.Matches(text))
                {
                    AddCandidate(strings, funcStats, m.Groups[1].Value, m.Groups[2].Value);
                }
                foreach (Match m in U8Re.Matches(text))
                {
                    AddCandidate(strings, funcStats, m.Groups[1].Value, m.Groups[2].Value);
                }
                foreach (Match m in LocRe.Matches(text))
                {
                    AddCandidate(strings, funcStats, "Localize", m.Groups[1].Value);
                }
                // ② 宽口径规则：自定义封装函数（裸标识符/.点号限定）的文字实参
                foreach (Match m in BareCallRe.Matches(text))
                {
                    AddBareCandidate(strings, funcStats, m.Groups[1].Value, m.Groups[2].Value);
                }
                // ③ 赋值式 / switch 表达式（`=> "Open a Window"`、`Name = "Foo Bar"`）——没有括号，上面两条抓不到
                foreach (Match m in AssignStringRe.Matches(text))
                {
                    AddBareCandidate(strings, funcStats, "赋值", m.Groups[1].Value);
                }
            }
        }
        return (strings, funcStats, zhSeen.Count);
    }

    private static void AddCandidate(SortedSet<string> strings, Dictionary<string, int> funcStats, string func, string raw)
    {
        // 窗口名 / 内部 ID 不作为翻译候选（用户决定：标题不翻；ID 无显示意义）
        if (IdOnlyFuncs.Contains(func)) return;
        // ## 是 ImGui 内部 ID 分隔符：只取显示部分（"显示文字##内部ID" → "显示文字"），
        // 与替换层的「截断后匹配」呼应（ImGui 实际收到的是完整串）。
        var s = TextHeuristics.StripIdSuffix(Unescape(raw)).Trim();

        if (s.Length < 2 || s.Length > 300) return;
        if (TextHeuristics.HasCjk(s)) return;           // 已是中文
        if (TextHeuristics.IsKeyName(s)) return;        // 纯键位名
        if (!TextHeuristics.HasAsciiLetter(s)) return;  // 必须含英文字母
        if (s.Contains("://")) return;                  // URL
        // 含 C# 插值残留（{...}）说明是动态拼接，静态值不可靠 → 不采
        if (s.Contains('{') || s.Contains('}')) return;
        strings.Add(s);
        funcStats[func] = funcStats.TryGetValue(func, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// 宽口径候选：自定义封装函数的文字实参（`DrawOption("Enable Synthesis Helper", …)` 等）。
    ///
    /// ⚠ 与 <see cref="AddCandidate"/> 的区别与取舍（重要）：
    ///   · 精确规则只认 `ImGui.Xxx("…")` 这类**已知绘制 API**，覆盖不了"插件自己包一层"的写法——
    ///     而后者在成熟插件里极常见（Craftimizer 几乎全这么写），漏掉就是**整窗英文**。
    ///   · 宽口径难免带上少量噪音（日志/异常消息等自然语言），但**多采基本无害**：
    ///     替换是「按内容精确匹配」，界面没传这串就永远用不到，顶多多花一点机翻配额；
    ///     而**漏采是致命的**（界面保持英文，用户直接看到）。故此处宁可多采。
    ///   · 两道闸门仍然保留：① 函数名黑名单（路径/日志/异常等，见 <see cref="NonUiCallNames"/>）；
    ///     ② **函数名 ID 黑名单**（<see cref="IdOnlyFuncs"/>：`Table`/`Child`/`Begin*`/`PushID`… 首参是 ImGui ID 而非文字）。
    ///
    /// ⚠ **单词标签也要收，但要挑函数**（2026-09-15 修正两次）：
    ///   · 早期版本要求字符串「含空格」，理由是避开 ImGui ID（`table`/`stats`/`desc`）。但那把真界面标签一并挡掉——
    ///     实测 Craftimizer 的 `ImGuiUtils.TextCentered("Crafter")` / `("Recipe")` 都是单词，于是永远采不到、界面一直英文。
    ///   · 单纯放开单词又会灌进大量噪音（`MacroMate.CreateOrUpdateMacro`、`CRAFT_MAXMS`、`Graphics.icon.png`、
    ///     `arrayValue` 这类标识符/常量/文件名，实测 +66 条里大半是噪音，会污染待翻清单）。
    ///   · 最终判据：**单词只在该函数名"像在画文字"时放行**（见 <see cref="TextHintWords"/>：Text*/Label*/Tooltip*/
    ///     Button*/Title*… 见名知义），并排除文件名形态（含 `.`/`_`）。
    ///     **判据从"字符串长什么样"（不可靠）换成"它被传给了谁"（可靠）** —— 实测 +21 条、零丢失、无噪音。
    /// </summary>
    private static void AddBareCandidate(SortedSet<string> strings, Dictionary<string, int> funcStats, string func, string raw)
    {
        // 已由精确规则覆盖的限定名（ImGui.Xxx / ImRaii.Xxx …）跳过，避免同一串被采两次、统计被拆成两个键
        if (func.StartsWith("ImGui", StringComparison.Ordinal) ||
            func.StartsWith("ImRaii", StringComparison.Ordinal) ||
            func.StartsWith("Luna", StringComparison.Ordinal))
            return;
        // 取最后一段做黑名单判定（Namespace.Class.Method → Method）
        var simple = func;
        var dot = func.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < func.Length) simple = func[(dot + 1)..];
        if (NonUiCallNames.Contains(simple)) return;
        // 首参是 ImGui ID 的函数（Table/Child/Begin*/PushID…）不作为文字候选 —— 与精确规则同一份名单
        if (IdOnlyFuncs.Contains(simple)) return;

        var s = TextHeuristics.StripIdSuffix(Unescape(raw)).Trim();
        if (!s.Contains(' '))
        {
            // 单词串：只认"像在画文字"的函数，且排除标识符/文件名；赋值路径的 simple 是「赋值」，自然被排除
            if (!LooksLikeTextFunc(simple)) return;
            if (s.Contains('.') || s.Contains('_')) return;
        }
        if (s.Length < 2 || s.Length > 300) return;
        if (TextHeuristics.HasCjk(s)) return;
        if (TextHeuristics.IsKeyName(s)) return;
        if (!TextHeuristics.HasAsciiLetter(s)) return;
        if (s.Contains("://")) return;
        if (s.Contains('{') || s.Contains('}')) return;
        strings.Add(s);
        funcStats[simple] = funcStats.TryGetValue(simple, out var c) ? c + 1 : 1;
    }

    /// <summary> 函数名里出现这些词 → 认为它的首个字符串实参是**画出来的文字**（用于单词标签的放行判定）。 </summary>
    private static readonly string[] TextHintWords =
    {
        "text", "label", "tooltip", "caption", "title", "heading", "note", "help", "button", "menu",
        "item", "header", "message", "info", "hint", "desc", "stat", "option", "choice", "entry",
        "bullet", "link", "warn", "error", "notify",
    };

    private static bool LooksLikeTextFunc(string simpleName)
    {
        var f = simpleName.ToLowerInvariant();
        foreach (var h in TextHintWords)
            if (f.Contains(h, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var c = s[i + 1];
                sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary> 执行 git（带代理环境变量：HTTPS_PROXY / HTTP_PROXY，二者都设以兼容 http/https）。 </summary>
    private static async Task<(int Code, string Output)> RunGitAsync(string[] args, string proxy)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, // git 输出含中文路径（数据目录名），必须按 UTF-8 读，否则乱码
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // 代理只作用于本次 git 进程，不污染全局
        psi.Environment["HTTPS_PROXY"] = proxy;
        psi.Environment["HTTP_PROXY"] = proxy;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // 不弹交互式登录
        psi.Environment["LC_ALL"] = "C.UTF-8";        // 让 git 用 UTF-8 输出路径，避免中文乱码

        try
        {
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout + "\n" + stderr);
        }
        catch (Exception ex)
        {
            return (-1, "无法启动 git：" + ex.Message);
        }
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 240 ? s : s[^240..];
    }
}
