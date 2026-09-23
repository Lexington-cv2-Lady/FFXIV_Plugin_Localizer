using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 文案判断规则（扫描、翻译、替换共用一处，便于统一调整口径）。
/// 目的：只把**真正需要汉化的界面文案**挑出来，排除三类噪音——
/// ①已是中文（部分插件字符串堆含中文）；②ImGui 内部 ID（<c>##</c> 开头）；③纯键位名（Ctrl / F1 / Tab 等）。
/// </summary>
public static class TextHeuristics
{
    /// <summary> 单个键位名（整串恰好是其一，大小写不敏感）。短语类如 "Delete mod" 不算——那是真文案。
    ///
    /// ⚠ **只收「不可能同时是普通英文单词」的键名**（2026-09-15 修正）：早期版本把 `control`/`tab`/`enter`/
    ///   `space`/`home`/`end`/`menu`/`windows` 等**兼具自然语义**的词也当键名排除，结果把真界面文案误杀——
    ///   实测 Craftimizer 的属性名 **`Control`**（加工精度）就因此永远采不到、界面一直英文。
    ///   这些词已从此表移除：它们若真是键位标签，也能靠「译文==原文」收尾（同文现在算已翻译），
    ///   而若是普通文案（如本处的 `Control`），移除后才能正常送翻。**误杀代价 > 多采代价**。 </summary>
    private static readonly HashSet<string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "alt", "shift", "escape", "esc", "backspace", "del", "ins",
        "pageup", "pagedown", "pgup", "pgdn",
        "arrowup", "arrowdown", "arrowleft", "arrowright",
        "capslock", "numlock", "scrolllock", "printscreen", "prtsc",
        "shift+tab", "alt+tab", "ctrl+tab",
        "鼠标左键", "鼠标右键", "鼠标中键",
    };

    /// <summary> F1–F24 功能键。 </summary>
    private static readonly Regex FunctionKey = new(@"^f([1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary> 颜色值：#RGB / #RRGGBB / #RRGGBBAA（LightlessSync 等插件堆里大量存在，非界面文案）。 </summary>
    private static readonly Regex HexColor = new(@"^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

    /// <summary>
    /// printf 风格格式模板：<c>%.2f</c>、<c>%d</c>、<c>%s</c>、<c>%d%%</c> 等——是代码模板不是可读文案。
    ///
    /// ⚠ **2026-09-18 全面审查③（静默丢覆盖率，中偏高）**：原判据 `%[-+ #0-9.]*[diouxXeEfgGsc%]`
    ///   **未锚定**且字符类里**含空格** → `% damage` 的 `% d` 就命中；也不要求转换符后不是字母，
    ///   于是**大量正常文案被误判成"技术噪音"**：`% damage`、`% of max`、`% chance`、`% faster`、
    ///   `Deals 50% damage`、`100% complete` ……
    ///   后果不只是"少翻几条"：`IsTechnicalNoise` → `IsTranslatable` → `NeedsTranslation` 整条链排除，
    ///   这些串**既不进待翻列表、也不送 AI、`GetTranslationProgress` 还把它们排除** →
    ///   **进度显示 100%、界面永远英文，而用户看不到任何缺口**（最难发现的一类失败）。
    /// 修正：转换符后**不能紧跟字母**。真格式串后面是空格/标点/串尾（`%d items`、`Remaining: %d`）；
    ///   而 `% damage` 的 `d` 后面接着 `a`（在拼单词）→ 是正常文案。
    ///   已用 18 个正反例验证（8 个真模板 + 10 个含百分号的正常文案）全部判定正确。
    /// </summary>
    private static readonly Regex FormatTemplate = new(@"%[-+ #0-9.]*[diouxXeEfgGsc%](?![A-Za-z])", RegexOptions.Compiled);

    /// <summary> 结构化片段：以 { [ ( 开头且以 } ] ) 结尾的**紧凑占位符**（{Cids}、[x]、(a)）。
    /// ⚠ 判据必须要求「括号内不含空白」：旧写法 `^[\{\[\(].*[\}\]\)]$` 会把**方括号包裹的正常文案**
    ///   一并排除——实测 `[Cone 1]`、`[Cycle Targets]`、`[Lowest Health Target]` 等 BTS 快捷键标签
    ///   全被误判成"技术噪音"，于是不计入待翻译集合 → 永远不送翻、进度还显示已完成。 </summary>
    private static readonly Regex StructFragment = new(@"^[\{\[\(][^\s]*[\}\]\)]$", RegexOptions.Compiled);

    /// <summary> 代码标识符风格：无空格、含下划线/驼峰混排的点号路径（如 lightless-file-cache-version）。 </summary>
    private static readonly Regex IdentLike = new(@"^[a-z0-9][a-z0-9._\-]*$", RegexOptions.Compiled);

    /// <summary>
    /// 是否为「聊天命令」：以 <c>/</c> 开头**且不含空格**（如 <c>/tp</c>、<c>/bts</c>）——是命令而非文案。
    /// 注意：含空格的（如 <c>/bts → Open the configuration window.</c>）是提示文本，**要翻**，不能排除。
    /// </summary>
    public static bool IsChatCommand(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length < 2 || t[0] != '/') return false;
        if (!t.Contains(' ')) return true;                          // /tp 纯命令
        if (t.Contains("→") || t.Contains("<-")) return false;      // 提示文本（带箭头说明）
        if (CommandSentence.IsMatch(t)) return false;                // 含常见句子词 → 提示文本要翻
        return CommandSyntax.IsMatch(t);                            // 否则是 命令+子命令/参数
    }

    /// <summary> 斜杠后仅 字母数字/下划线/连字符/方括号/空格（命令语法，无标点无句子成分）。 </summary>
    private static readonly Regex CommandSyntax = new(@"^/[a-zA-Z0-9_\-\[\] ]+$", RegexOptions.Compiled);

    /// <summary> 常见英文句子成分词（出现即视为提示文本而非命令）。 </summary>
    private static readonly Regex CommandSentence = new(
        @"\b(the|a|an|to|you|your|is|are|was|were|will|would|see|usage|for|with|on|in|at|this|that|and|or|of|it|if|when|open|click|press|use|using|more|info|information|here|please|can|cannot|must|should|does|do|not)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary> 是否像「非界面的技术字符串」（颜色/格式模板/结构化片段/纯标识符），应排除。 </summary>
    public static bool IsTechnicalNoise(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.Length <= 2) return true;
        if (IsChatCommand(t)) return true;                     // /tp 这类聊天命令
        if (HexColor.IsMatch(t)) return true;                 // #FFFFFF
        if (t.StartsWith("#lightless-")) return true;          // 缓存键前缀（插件特有，通用规则兜底）
        if (FormatTemplate.IsMatch(t)) return true;            // %.0f px
        if (StructFragment.IsMatch(t)) return true;            // {Cids} / [x]
        if (t.Contains("\\n") || t.Contains("\\t")) return true; // 转义残留
        if (t.Contains("://")) return true;                    // URL
        // 无空格 + 全小写/含连字符点号 → 标识符或键名（不含自然语言的空格与大小写混排）
        if (!t.Contains(' ') && t.Length > 8 && IdentLike.IsMatch(t) && !t.Any(char.IsUpper)) return true;
        // JSON 键值残片
        if (t.Contains("\":") || t.Contains("\":")) return true;
        return false;
    }

    /// <summary> 是否为「纯键位名」：整串就是一个按键（如 Ctrl / F5 / Tab）。 </summary>
    public static bool IsKeyName(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0 || t.Length > 16) return false;
        if (KeyNames.Contains(t)) return true;
        if (FunctionKey.IsMatch(t)) return true;
        // 组合键形式：Ctrl+A / Shift+Tab / Alt+F4（每段都是键名/单字母/功能键）
        if (t.Contains('+'))
        {
            var parts = t.Split('+', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length is >= 2 and <= 4 && Array.TrueForAll(parts, p =>
            {
                var q = p.Trim();
                return q.Length == 1 || KeyNames.Contains(q) || FunctionKey.IsMatch(q);
            });
        }
        return false;
    }

    /// <summary> 是否含中日韩字符（说明已是中文/日文，不需要汉化）。 </summary>
    public static bool HasCjk(string s)
    {
        foreach (var c in s)
        {
            if (c is >= (char)0x4E00 and <= (char)0x9FFF) return true; // 统一表意
            if (c is >= (char)0x3040 and <= (char)0x30FF) return true; // 假名
            if (c is >= (char)0xAC00 and <= (char)0xD7A3) return true; // 谚文
            if (c is >= (char)0x3000 and <= (char)0x303F) return true; // 中日韩标点
            if (c is >= (char)0xFF00 and <= (char)0xFFEF) return true; // 全角
        }
        return false;
    }

    /// <summary> 是否含 ASCII 字母。 </summary>
    public static bool HasAsciiLetter(string s)
    {
        foreach (var c in s)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        }
        return false;
    }

    /// <summary>
    /// 去掉 ImGui 内部 ID 后缀：<c>"显示文字##internal_id"</c> → <c>"显示文字"</c>。
    /// <c>##</c> 之后不是显示内容（仅用于控件去重），采集与替换都只应针对显示部分。
    /// </summary>
    public static string StripIdSuffix(string s)
    {
        var i = s.IndexOf("##", StringComparison.Ordinal);
        return i >= 0 ? s[..i] : s;
    }

    /// <summary>
    /// 是否为「值得汉化的文案」：含 ASCII 字母、不含中日韩字符、不是纯键位名、不是 ImGui 内部 ID、
    /// 且不是技术噪音（颜色值 / 格式模板 / 结构化片段 / 标识符）。
    /// </summary>
    public static bool IsTranslatable(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = StripIdSuffix(s.Trim());
        if (t.Length == 0) return false;
        if (HasCjk(t)) return false;            // 已是中文/日文
        if (IsKeyName(t)) return false;         // 纯键位名
        if (IsTechnicalNoise(t)) return false;  // 技术噪音（颜色/模板/标识符等）
        return HasAsciiLetter(t);
    }

    /// <summary>
    /// 英文「推理/元评论」特征词：模型没给成稿、反而输出思考过程时几乎必含这些英文片段。
    /// 正常中文译文以 CJK 为主，不会出现这些整词，故命中即判无效。
    /// </summary>
    private static readonly Regex MetaComment = new(
        @"(?i)\b(hmm+|let me think|let me|i think|i believe|i guess|likely|maybe|actually|" +
        @"keep as|could maybe|could translate|no\.\.\.|the arena|associated with|should be|not sure|" +
        @"let's|i'm|isn't|doesn't|aren't|won't|can't tell)\b",
        RegexOptions.Compiled);

    /// <summary> 子串在文本中的出现次数（大小写敏感，与原文精确匹配口径一致）。 </summary>
    private static int CountOccurrences(string text, string sub)
    {
        int c = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { c++; i += sub.Length; }
        return c;
    }

    /// <summary>
    /// **机翻结果有效性校验**：自动沉淀 / 写入词典前调用，拦住「模型没给成稿、返回英文推理 / 元评论 / 空值 / 原文照抄」。
    ///
    /// 起因（2026-09-23）：词典里混入
    ///   · <c>The Wreath of Snakes</c> 的大段英文犹豫独白（Hmm? Seiryu? Suzaku?…）；
    ///   · <c>djUSA.GI</c> 的英文 “likely username/code, keep as” 注释。
    /// 与旧项目「扁平格式导致机翻返回空值」是同一类问题的变种。
    ///
    /// 规则（任一不满足即判无效，不写入词典）：
    ///   ① 译文非空白、原文非空白，且译文不等于原文；
    ///   ② 译文**必须含 CJK**——纯英文返回要么没翻、要么是英文元评论
    ///     （「专名保留」应走黑名单 / 译文=原文，不会到这里）；
    ///   ③ 不含 ② 所列英文推理 / 元评论特征词；
    ///   ④ 原文（≥3 字符）不得在译文里重复 ≥2 次（推理独白会反复念叨原文）；
    ///   ⑤ 问号不超过 1 个（犹豫文本常连续问号）。
    /// </summary>
    public static bool IsValidMachineTranslation(string? en, string? zh)
    {
        var z = (zh ?? "").Trim();
        var e = (en ?? "").Trim();
        if (z.Length == 0 || e.Length == 0) return false;          // ① 空值
        if (z == e) return false;                                  // ① 原文照抄
        if (!HasCjk(z)) return false;                              // ② 纯英文
        if (MetaComment.IsMatch(z)) return false;                  // ③ 元评论
        if (e.Length >= 3 && CountOccurrences(z, e) >= 2) return false; // ④ 重复原文
        // ⑤ 犹豫问号：只统计「孤立的单个 ?」。连续的 ??/???? 是占位符（未解锁地点显示 "????"），不是犹豫。
        var isolatedQ = 0;
        for (var k = 0; k < z.Length; k++)
            if (z[k] == '?' && (k == 0 || z[k - 1] != '?') && (k == z.Length - 1 || z[k + 1] != '?'))
                isolatedQ++;
        return isolatedQ <= 1;
    }
}
