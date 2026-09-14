using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 文案判断规则（扫描、翻译、替换共用一处，便于统一调整口径）。
/// 目的：只把**真正需要汉化的界面文案**挑出来，排除三类噪音——
/// ①已是中文（部分插件字符串堆含中文）；②ImGui 内部 ID（<c>##</c> 开头）；③纯键位名（Ctrl / F1 / Tab 等）。
/// </summary>
public static class TextHeuristics
{
    /// <summary> 单个键位名（整串恰好是其一，大小写不敏感）。短语类如 "Delete mod" 不算——那是真文案。 </summary>
    private static readonly HashSet<string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "control", "alt", "shift", "tab", "enter", "return", "escape", "esc", "space", "spacebar",
        "backspace", "delete", "del", "insert", "ins", "home", "end", "pageup", "pagedown", "pgup", "pgdn",
        "up", "down", "left", "right", "arrowup", "arrowdown", "arrowleft", "arrowright",
        "capslock", "numlock", "scrolllock", "printscreen", "prtsc", "pause", "break", "win", "windows",
        "super", "meta", "command", "cmd", "option", "menu", "apps", "shift+tab", "alt+tab", "ctrl+tab",
        "鼠标左键", "鼠标右键", "鼠标中键",
    };

    /// <summary> F1–F24 功能键。 </summary>
    private static readonly Regex FunctionKey = new(@"^f([1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// 是否为「值得汉化的文案」：含 ASCII 字母、不含中日韩字符、不是纯键位名、不是 ImGui 内部 ID。
    /// </summary>
    public static bool IsTranslatable(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = StripIdSuffix(s.Trim());
        if (t.Length == 0) return false;
        if (HasCjk(t)) return false;            // 已是中文/日文
        if (IsKeyName(t)) return false;         // 纯键位名
        return HasAsciiLetter(t);
    }
}
