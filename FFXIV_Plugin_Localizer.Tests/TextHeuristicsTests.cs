using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// TextHeuristics 判据回归测试（最易回归，优先覆盖）。
/// 覆盖：机翻有效性校验（同文/空值/纯英文/元评论/重复原文/犹豫问号）、CJK 判定、
/// ImGui 内部 ID 去后缀、IsTranslatable 边界（含 09-18 百分号误判修复）。
/// </summary>
public class TextHeuristicsTests
{
    // 「译文 == 原文」按设计判为无效：IsValidMachineTranslation 是机翻结果校验闸门，
    // 模型原样返回=没翻，应拒收（与 TranslationFile.Save 允许「保持原样」条目是两回事）。
    [Theory]
    [InlineData("URL", "URL")]
    [InlineData("DPS", "DPS")]
    [InlineData("HP", "HP")]
    public void IsValidMachineTranslation_SameAsSource_IsInvalid(string en, string zh)
        => Assert.False(TextHeuristics.IsValidMachineTranslation(en, zh));

    // 空译文必须判为无效（要留着重试；曾把空值当成功导致进度卡 n-1/n）
    [Theory]
    [InlineData("Attack", "")]
    [InlineData("", "攻击")]
    [InlineData(null, "攻击")]
    public void IsValidMachineTranslation_Empty_IsInvalid(string? en, string? zh)
        => Assert.False(TextHeuristics.IsValidMachineTranslation(en, zh));

    // 纯英文译文（无 CJK）判为无效（没翻 / 英文元评论）
    [Fact]
    public void IsValidMachineTranslation_PureEnglish_IsInvalid()
        => Assert.False(TextHeuristics.IsValidMachineTranslation("Attack", "Attack damage"));

    // 英文推理/元评论特征词命中即无效
    [Fact]
    public void IsValidMachineTranslation_MetaComment_IsInvalid()
        => Assert.False(TextHeuristics.IsValidMachineTranslation("Some term", "likely username, keep as"));

    // 原文在译文里重复 ≥2 次（独白）判为无效
    [Fact]
    public void IsValidMachineTranslation_RepeatedOriginal_IsInvalid()
        => Assert.False(TextHeuristics.IsValidMachineTranslation("Snakes", "The Wreath of Snakes Snakes again"));

    // 孤立问号 >1 才无效（连续 ?? 是未解锁地点占位符，不算犹豫）
    [Fact]
    public void IsValidMachineTranslation_IsolatedQuestions_IsInvalid()
        => Assert.False(TextHeuristics.IsValidMachineTranslation("Term", "这是什么? maybe this?"));

    // 正常中文译文有效
    [Fact]
    public void IsValidMachineTranslation_ValidChinese_IsValid()
        => Assert.True(TextHeuristics.IsValidMachineTranslation("Attack", "攻击"));

    // CJK 判定：中文/假名有，英文无
    [Theory]
    [InlineData("攻击", true)]
    [InlineData("コマンド", true)]   // 片假名
    [InlineData("Attack", false)]
    public void HasCjk_Various(string s, bool expected)
        => Assert.Equal(expected, TextHeuristics.HasCjk(s));

    // 去 ImGui 内部 ID 后缀
    [Theory]
    [InlineData("显示文字##internal_id", "显示文字")]
    [InlineData("Normal", "Normal")]
    [InlineData("##only_id", "")]
    public void StripIdSuffix_RemovesInternalId(string input, string expected)
        => Assert.Equal(expected, TextHeuristics.StripIdSuffix(input));

    // IsTranslatable：英文可翻；中文/键名/功能键/颜色值/聊天命令不可翻
    [Theory]
    [InlineData("Attack", true)]
    [InlineData("攻击", false)]          // 已中文
    [InlineData("Ctrl", false)]          // 键名
    [InlineData("F5", false)]            // 功能键
    [InlineData("#FFFFFF", false)]       // 颜色值
    [InlineData("/tp", false)]           // 聊天命令
    public void IsTranslatable_Boundaries(string s, bool expected)
        => Assert.Equal(expected, TextHeuristics.IsTranslatable(s));

    // 09-18 修复回归：含百分号的普通文案（% 后接字母）不应误判为技术噪音
    [Fact]
    public void IsTranslatable_PercentFollowedByLetter_IsTranslatable()
        => Assert.True(TextHeuristics.IsTranslatable("% damage"));

    // 09-18 修复对照：真 printf 模板（% 后接空格/串尾）仍判为技术噪音
    [Fact]
    public void IsTranslatable_RealFormatTemplate_IsNotTranslatable()
        => Assert.False(TextHeuristics.IsTranslatable("Remaining: %d"));
}
