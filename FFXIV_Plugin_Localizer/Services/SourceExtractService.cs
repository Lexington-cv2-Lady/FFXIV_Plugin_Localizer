using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary> 提取结果子目录（仓库克隆到此处）。 </summary>
    public const string RepoDirName = "源码仓库";
    /// <summary> 提取结果子目录（产出的对照表放此处）。 </summary>
    public const string OutputDirName = "文案扫描";

    /// <summary> 界面文案调用：ImGui.Xxx("…") / Im.Xxx("…") / ImRaii.Xxx("…") 等常见封装。 </summary>
    private static readonly Regex CallRe = new(
        @"\b(?:ImGui|ImGuiHelpers|ImRaii|Im|Luna)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""(?:\s*u8)?",
        RegexOptions.Compiled);

    /// <summary> Dalamud 官方本地化：Loc.Localize("Key", "默认英文")，取第二个参数。 </summary>
    private static readonly Regex LocRe = new(
        @"Loc\.Localize\s*\(\s*(?:\$)?@?""(?:[^""\\]|\\.)*""\s*,\s*(?:\$)?@?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary> u8 后缀字面量单独一档（Glamourer 风格）：Im.Text("…"u8)。 </summary>
    private static readonly Regex U8Re = new(
        @"\b(?:Im|ImGui)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*""((?:[^""\\]|\\.)*)""\s*u8",
        RegexOptions.Compiled);

    /// <summary> 判定"绘制函数"是否为纯文本绘制（决定该句走文字通道还是控件通道）。 </summary>
    private static readonly HashSet<string> TextFuncs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextWrapped", "TextColored", "TextDisabled", "BulletText", "LabelText", "SetTooltip",
        "TextUnformatted", "TextV", "TextEx",
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
    public List<(string Name, string RepoUrl)> ListPluginsWithRepo()
    {
        var result = new List<(string, string)>();
        var launcherDir = Path.GetDirectoryName(Path.GetDirectoryName(_configDir()));
        foreach (var rootName in new[] { "installedPlugins", "devPlugins" })
        {
            var root = Path.Combine(launcherDir ?? "", rootName);
            if (!Directory.Exists(root)) continue;
            foreach (var pluginDir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(pluginDir);
                string url = "";
                try
                {
                    var manifest = Directory.EnumerateFiles(pluginDir, name + ".json", SearchOption.AllDirectories).FirstOrDefault();
                    if (manifest != null)
                    {
                        var text = File.ReadAllText(manifest);
                        var m = Regex.Match(text, "\"RepoUrl\"\\s*:\\s*\"([^\"]*)\"");
                        if (m.Success) url = m.Groups[1].Value;
                    }
                }
                catch { /* 清单坏了就留空 */ }
                if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((name, url.TrimEnd('/')));
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
            return (false, 0, new(), "未启用代理或代理地址为空：访问 GitHub 需要勾选「启用代理」并填写代理地址");
        }
        if (string.IsNullOrWhiteSpace(repoUrl) || !repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, new(), "仓库地址无效（需为 github.com 链接）");
        }

        var repoRoot = Path.Combine(_configDir(), RepoDirName);
        Directory.CreateDirectory(repoRoot);
        var bare = Regex.Match(repoUrl, @"github\.com/([^/]+)/([^/#?]+)");
        if (!bare.Success) return (false, 0, new(), "无法解析仓库地址");
        var dirName = $"{bare.Groups[1].Value}__{bare.Groups[2].Value}".TrimEnd('.');
        var dir = Path.Combine(repoRoot, dirName);
        var proxy = _cfg.ProxyAddress.Trim();

        var gitArgs = dir is not null && Directory.Exists(dir)
            ? new[] { "-C", dir, "pull", "--ff-only" }                                  // 已克隆过 → 更新
            : new[] { "clone", "--depth", "1", repoUrl, dir };                          // 首次 → 浅克隆
        var (code, output) = await RunGitAsync(gitArgs, proxy);
        if (code != 0 && !Directory.Exists(dir))
        {
            return (false, 0, new(), $"拉取失败（退出码 {code}）：{Tail(output)}");
        }

        var (strings, funcStats) = ExtractFromDirectory(dir);
        if (strings.Count == 0)
        {
            return (true, 0, funcStats, "仓库已拉取，但未找到可识别的界面文案（可能用了不支持的写法）");
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
    private (SortedSet<string> Strings, Dictionary<string, int> FuncStats) ExtractFromDirectory(string dir)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        var funcStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // 跳过构建产物与第三方库目录
            var lower = file.ToLowerInvariant();
            if (lower.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                lower.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); } catch { continue; }
            foreach (var line in lines)
            {
                foreach (Match m in CallRe.Matches(line))
                {
                    AddCandidate(strings, funcStats, m.Groups[1].Value, m.Groups[2].Value);
                }
                foreach (Match m in U8Re.Matches(line))
                {
                    AddCandidate(strings, funcStats, m.Groups[1].Value, m.Groups[2].Value);
                }
                foreach (Match m in LocRe.Matches(line))
                {
                    AddCandidate(strings, funcStats, "Localize", m.Groups[1].Value);
                }
            }
        }
        return (strings, funcStats);
    }

    private static void AddCandidate(SortedSet<string> strings, Dictionary<string, int> funcStats, string func, string raw)
    {
        var s = Unescape(raw).Trim();
        if (s.Length < 2 || s.Length > 300) return;
        if (s.StartsWith("##")) return;                 // ImGui 内部 ID
        if (TextHeuristics.HasCjk(s)) return;           // 已是中文
        if (TextHeuristics.IsKeyName(s)) return;        // 纯键位名
        if (!TextHeuristics.HasAsciiLetter(s)) return;  // 必须含英文字母
        if (s.Contains("://") || s.Contains("{")) return; // URL / 插值残留
        strings.Add(s);
        funcStats[func] = funcStats.TryGetValue(func, out var c) ? c + 1 : 1;
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
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // 代理只作用于本次 git 进程，不污染全局
        psi.Environment["HTTPS_PROXY"] = proxy;
        psi.Environment["HTTP_PROXY"] = proxy;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // 不弹交互式登录

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
