using System;
using System.IO;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// OldDictionaryService「联动旧项目单词黑名单」回归测试（2026-09-25 司令官报 bug）：
/// 旧项目（FFXIV 模组汉化工具）已把 MOD 专名（Lavabod/YAB/TBSE…）拉进自己的 单词黑名单.json 保持英文，
/// 本插件翻 Penumbra 时若不联动就会把这些专名机翻/替换成中文。
/// 这里验证：联动文件按**并集**并入、去重、可关闭、重载清空重并、文件缺失不拖垮主流程。
/// </summary>
public class OldDictionaryLinkedBlacklistTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_bl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteBlacklist(string dir, params string[] words)
        => File.WriteAllText(Path.Combine(dir, OldDictionaryService.BlacklistFileName),
                             string.Join('\n', words));

    [Fact]
    public void LinkedBlacklist_UnionWithOwn()
    {
        var own = MakeTempDir();
        var old = MakeTempDir();
        try
        {
            WriteBlacklist(own, "URL", "DPS");
            WriteBlacklist(old, "Lavabod", "YAB");

            var svc = new OldDictionaryService(new AppLog());
            svc.SetLinkedBlacklistFile(Path.Combine(old, OldDictionaryService.BlacklistFileName));
            svc.Load(own);

            // 并集：本项目与旧项目的词都生效
            Assert.True(svc.IsBlacklisted("URL"));
            Assert.True(svc.IsBlacklisted("DPS"));
            Assert.True(svc.IsBlacklisted("Lavabod"));
            Assert.True(svc.IsBlacklisted("YAB"));
            Assert.False(svc.IsBlacklisted("SomeOtherWord"));
            // 计数：总 = 并集；联动新增 = 2
            Assert.Equal(4, svc.BlacklistCount);
            Assert.Equal(2, svc.LinkedBlacklistCount);
            // 大小写不敏感（与本项目黑名单口径一致）
            Assert.True(svc.IsBlacklisted("lavabod"));
        }
        finally { Directory.Delete(own, true); Directory.Delete(old, true); }
    }

    [Fact]
    public void LinkedBlacklist_DuplicateCountedOnce()
    {
        var own = MakeTempDir();
        var old = MakeTempDir();
        try
        {
            WriteBlacklist(own, "URL", "Heliosphere");
            WriteBlacklist(old, "URL", "TBSE");   // URL 与本项目重复

            var svc = new OldDictionaryService(new AppLog());
            svc.SetLinkedBlacklistFile(Path.Combine(old, OldDictionaryService.BlacklistFileName));
            svc.Load(own);

            Assert.Equal(3, svc.BlacklistCount);        // URL/Heliosphere/TBSE
            Assert.Equal(1, svc.LinkedBlacklistCount);  // 只新增 TBSE，URL 不算
        }
        finally { Directory.Delete(own, true); Directory.Delete(old, true); }
    }

    [Fact]
    public void LinkedBlacklist_DisabledWhenNull()
    {
        var own = MakeTempDir();
        var old = MakeTempDir();
        try
        {
            WriteBlacklist(own, "URL");
            WriteBlacklist(old, "Lavabod");

            var svc = new OldDictionaryService(new AppLog());
            svc.SetLinkedBlacklistFile(null);   // 开关关 → 不联动
            svc.Load(own);

            Assert.True(svc.IsBlacklisted("URL"));
            Assert.False(svc.IsBlacklisted("Lavabod"));
            Assert.Equal(1, svc.BlacklistCount);
            Assert.Equal(0, svc.LinkedBlacklistCount);
        }
        finally { Directory.Delete(own, true); Directory.Delete(old, true); }
    }

    [Fact]
    public void LinkedBlacklist_ReloadClearsStaleWords()
    {
        var own = MakeTempDir();
        var old = MakeTempDir();
        try
        {
            WriteBlacklist(own, "URL");
            WriteBlacklist(old, "Lavabod");

            var svc = new OldDictionaryService(new AppLog());
            svc.SetLinkedBlacklistFile(Path.Combine(old, OldDictionaryService.BlacklistFileName));
            svc.Load(own);
            Assert.True(svc.IsBlacklisted("Lavabod"));   // 第一次联动生效

            // 关闭联动后重载：Load 先清空再并（此时无联动源）→ 旧联动词不得残留
            svc.SetLinkedBlacklistFile(null);
            svc.Load(own);
            Assert.False(svc.IsBlacklisted("Lavabod"));
            Assert.Equal(1, svc.BlacklistCount);
            Assert.Equal(0, svc.LinkedBlacklistCount);
        }
        finally { Directory.Delete(own, true); Directory.Delete(old, true); }
    }

    [Fact]
    public void LinkedBlacklist_MissingFileDoesNotThrow()
    {
        var own = MakeTempDir();
        try
        {
            WriteBlacklist(own, "URL");

            var svc = new OldDictionaryService(new AppLog());
            // 指向一个不存在的联动文件（旧项目未装/未配目录的场景）
            svc.SetLinkedBlacklistFile(Path.Combine(own, "不存在的目录", "单词黑名单.json"));
            var ex = Record.Exception(() => svc.Load(own));

            Assert.Null(ex);                            // 不抛异常
            Assert.True(svc.IsBlacklisted("URL"));      // 本项目词仍生效
            Assert.Equal(1, svc.BlacklistCount);
            Assert.Equal(0, svc.LinkedBlacklistCount);
        }
        finally { Directory.Delete(own, true); }
    }
}
