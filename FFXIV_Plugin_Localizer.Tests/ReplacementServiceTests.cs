using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// ReplacementService 高危逻辑回归测试（真实 API，不 mock 核心判定）：
/// ① F4 闭环第四源 _dictTerms（AutoClosureEnabled 默认关 / 注入与清除）；
/// ② 四层替换源优先级（窗口 &gt; 安装器 &gt; wiki &gt; 词典，逐层降级 + 跨插件排序）；
/// ③ TryReplace 子串边界：##ID 保留、首尾空白保留、前缀模板匹配；
/// ④ 黑名单硬保证、Enabled 开关、Count/未命中不抛异常。
/// 对应司令官指令第 2/3/5 类高危逻辑（igButton/igSelectable 标签替换的「替换决策」核心即 TryReplace）。
/// </summary>
public class ReplacementServiceTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);   // 始终创建，保证 finally 清理时目录存在
        return dir;
    }

    /// <summary> 用临时配置目录构造服务：installer/window 源走真实文件（TranslationFile），wiki/dict 走公开 API。 </summary>
    private static ReplacementService BuildService(string dir,
        Dictionary<string, string>? installer = null,
        Dictionary<string, string>? window = null)
    {
        if (installer != null)
            TranslationFile.Save(Path.Combine(dir, ReplacementService.TableFileName), installer);
        if (window != null)
        {
            var wdir = Path.Combine(dir, ReplacementService.WindowTableDirName);
            Directory.CreateDirectory(wdir);
            TranslationFile.Save(Path.Combine(wdir, "TestPlugin.json"), window);
        }

        var svc = new ReplacementService(new AppLog(), () => dir, new Configuration());
        svc.Enabled = true;
        return svc;
    }

    /// <summary> 调真实 TryReplace(byte*, int)，返回中文串（未命中返回 null）。 </summary>
    private static string? Replace(ReplacementService svc, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                var ptr = svc.TryReplace(p, bytes.Length);
                return ptr == 0 ? null : Marshal.PtrToStringUTF8(ptr);
            }
        }
    }

    // F4 闭环：AutoClosureEnabled 默认关（开关关时第四源不被注入生效表）
    [Fact]
    public void F4_AutoClosureEnabled_DefaultsOff()
        => Assert.False(new Configuration().AutoClosureEnabled);

    // 第四源 _dictTerms：SetDictTerms 注入后兜底生效；置 null 后清除
    [Fact]
    public void F4_DictTerms_InjectedThenCleared()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir);                 // 无其它源
            Assert.Null(Replace(svc, "Key"));            // 空表 → 未命中
            svc.SetDictTerms(new Dictionary<string, string> { ["Key"] = "词典" });
            Assert.Equal("词典", Replace(svc, "Key"));
            svc.SetDictTerms(null);
            Assert.Null(Replace(svc, "Key"));            // 清除后未命中
        }
        finally { Directory.Delete(dir, true); }
    }

    // 第四源为空/空字典时不注入（F4 v2：null 或空 = 不启用）
    [Fact]
    public void F4_DictTerms_EmptyOrNull_NotInjected()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir);
            svc.SetDictTerms(new Dictionary<string, string>());   // 空
            Assert.Null(Replace(svc, "Anything"));
            svc.SetDictTerms(null);
            Assert.Null(Replace(svc, "Anything"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 四层替换源优先级：窗口 > 安装器 > wiki > 词典（逐层降级）
    [Fact]
    public void Priority_WindowOverInstallerOverWikiOverDict()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir,
                installer: new Dictionary<string, string> { ["Key"] = "安装器" },
                window: new Dictionary<string, string> { ["Key"] = "窗口" });
            svc.SetWikiTerms(new Dictionary<string, string> { ["Key"] = "wiki" });
            svc.SetDictTerms(new Dictionary<string, string> { ["Key"] = "词典" });

            Assert.Equal("窗口", Replace(svc, "Key"));                                   // ① 窗口最高

            File.Delete(Path.Combine(dir, ReplacementService.WindowTableDirName, "TestPlugin.json"));
            svc.LoadWindowTables();
            Assert.Equal("安装器", Replace(svc, "Key"));                                 // ② 仅安装器

            TranslationFile.Save(Path.Combine(dir, ReplacementService.TableFileName), new Dictionary<string, string>());
            svc.Load();
            Assert.Equal("wiki", Replace(svc, "Key"));                                   // ③ 仅 wiki

            svc.SetWikiTerms(null);
            Assert.Equal("词典", Replace(svc, "Key"));                                   // ④ 仅词典兜底

            svc.SetDictTerms(null);
            Assert.Null(Replace(svc, "Key"));                                            // 全清 → 未命中
        }
        finally { Directory.Delete(dir, true); }
    }

    // 同键跨插件：插件名排序靠前者优先（RebuildMerged 排序语义，审查 M2）
    [Fact]
    public void Priority_CrossPlugin_SortedByName()
    {
        var dir = MakeTempDir();
        try
        {
            var wdir = Path.Combine(dir, ReplacementService.WindowTableDirName);
            Directory.CreateDirectory(wdir);
            TranslationFile.Save(Path.Combine(wdir, "BBB.json"), new Dictionary<string, string> { ["XX"] = "来自B" });
            TranslationFile.Save(Path.Combine(wdir, "AAA.json"), new Dictionary<string, string> { ["XX"] = "来自A" });
            var svc = new ReplacementService(new AppLog(), () => dir, new Configuration()) { Enabled = true };
            Assert.Equal("来自A", Replace(svc, "XX"));   // AAA 靠前优先
        }
        finally { Directory.Delete(dir, true); }
    }

    // 黑名单硬保证：命中黑名单永不替换（即使表里有译文）
    [Fact]
    public void Blacklist_BlocksReplacement()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string> { ["Hello"] = "你好" });
            svc.SetBlacklist(s => s == "Hello");
            Assert.Null(Replace(svc, "Hello"));   // 被拉黑
            svc.SetBlacklist(null);
            Assert.Equal("你好", Replace(svc, "Hello"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 开关关闭时 TryReplace 一律不替换
    [Fact]
    public void EnabledFalse_NoReplacement()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string> { ["Hello"] = "你好" });
            svc.Enabled = false;
            Assert.Null(Replace(svc, "Hello"));
            svc.Enabled = true;
            Assert.Equal("你好", Replace(svc, "Hello"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 控件标签 ##ID 必须原样保留（igButton/igSelectable 标签替换，09-16 实机结论：丢 ID 会撞控件/丢状态）
    [Fact]
    public void TryReplace_PreservesInternalIdSuffix()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string> { ["OK"] = "确定" });
            Assert.Equal("确定##close_btn", Replace(svc, "OK##close_btn"));
            // 字节/字符下标不一致时（含非 ASCII）也要正确切分（09-16 修复点）
            Assert.Equal("确定##café_id", Replace(svc, "OK##café_id"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 首尾空白容错：保留原始排版间距（源码缩进/换行文案）
    [Fact]
    public void TryReplace_PreservesLeadingTrailingWhitespace()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string> { ["Hello"] = "你好" });
            Assert.Equal("  你好  ", Replace(svc, "  Hello  "));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 前缀模板匹配：动态插值串（C# 插值）命中静态前缀 → 译文 + 剩余原样（仅窗口/安装器源参与）
    [Fact]
    public void TryReplace_PrefixTemplateMatch()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string>
            {
                ["The logged in character is"] = "已登录角色是"
            });
            var input = "The logged in character is All on Moen";
            Assert.Equal("已登录角色是 All on Moen", Replace(svc, input));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Count 反映合并表规模；未命中不抛异常（仅累积到运行时发现队列）
    [Fact]
    public void Count_ReflectsMergedTable_AndMissDoesNotThrow()
    {
        var dir = MakeTempDir();
        try
        {
            var svc = BuildService(dir, installer: new Dictionary<string, string>
            {
                ["AA"] = "甲", ["BB"] = "乙"
            });
            Assert.Equal(2, svc.Count);
            Assert.Null(Replace(svc, "Unknown"));   // 触发未命中（不抛异常）
        }
        finally { Directory.Delete(dir, true); }
    }
}
