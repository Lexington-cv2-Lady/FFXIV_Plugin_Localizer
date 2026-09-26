using System;
using System.IO;
using System.Linq;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// OldDictionaryService「译文==原文（专名固定项）必须保留」回归测试（2026-09-26 修复）：
/// 原先 <c>Add</c> 用 <c>k == v</c> 规则把「译文==原文」的条目（如 [HS] Rue+、Konekomods）丢弃，
/// 导致它们被当成「未命中」反复送机翻 → 专名被错翻（rue→芸香）。
/// 这里验证：身份条目在 <c>Load</c> 后保留于词表，查词命中返回原文（no-op 锚点，永不送翻）。
/// 与 ReplacementService「不能排除 k==v」的设计意图一致。
/// </summary>
public class OldDictionaryIdentityPinTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_idpin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteDict(string dir, string json)
        => File.WriteAllText(Path.Combine(dir, OldDictionaryService.FileName), json);

    /// <summary> 一条专名固定项（译文==原文）+ 一条正常译文。 </summary>
    private const string Sample =
        "{\"terms\":[" +
        "{\"原文\":\"[HS] Rue+\",\"译文\":\"[HS] Rue+\"}," +
        "{\"原文\":\"Hello\",\"译文\":\"你好\"}" +
        "]}";

    [Fact]
    public void IdentityEntry_RetainedAfterLoad_AndPinsToSelf()
    {
        var dir = MakeTempDir();
        try
        {
            WriteDict(dir, Sample);
            var svc = new OldDictionaryService(new AppLog());
            svc.Load(dir);

            // 身份条目必须保留（不再被 k==v 丢弃）
            Assert.True(svc.TryGet("[HS] Rue+", out var zh), "专名固定项应保留在词表中");
            Assert.Equal("[HS] Rue+", zh);                 // 命中即返回原文（no-op 锚点）
            Assert.True(svc.TryGet("Hello", out var zh2));
            Assert.Equal("你好", zh2);
            Assert.Equal(2, svc.Count);                    // 两条都应计入
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IdentityEntry_FlowsIntoPrefill_AsNoOp()
    {
        var dir = MakeTempDir();
        try
        {
            WriteDict(dir, Sample);
            var svc = new OldDictionaryService(new AppLog());
            svc.Load(dir);

            var (hit, pairs) = svc.Prefill(new[] { "[HS] Rue+", "Hello" });
            Assert.Equal(2, hit);
            // 身份项在预翻译里应产出「原文→原文」的 no-op 锚点（而非被当作未翻译丢弃）
            var rue = pairs.First(p => p.En == "[HS] Rue+");
            Assert.Equal("[HS] Rue+", rue.Zh);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NonIdentityGarbage_StillSkipped()
    {
        // 空译文 / 过短键仍应被丢弃（只放开 k==v）
        var dir = MakeTempDir();
        try
        {
            WriteDict(dir,
                "{\"terms\":[" +
                "{\"原文\":\"[HS] Rue+\",\"译文\":\"[HS] Rue+\"}," +   // 保留
                "{\"原文\":\"abc\",\"译文\":\"\"}" +                    // 空译文 → 丢弃
                "]}");
            var svc = new OldDictionaryService(new AppLog());
            svc.Load(dir);

            Assert.True(svc.TryGet("[HS] Rue+", out _));
            Assert.Equal(1, svc.Count);                            // 仅身份项保留
        }
        finally { Directory.Delete(dir, true); }
    }
}
