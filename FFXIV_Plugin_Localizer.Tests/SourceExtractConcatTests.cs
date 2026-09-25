using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// 「C# 字符串拼接链合并」回归测试（2026-09-25，Heliosphere 技术信息段落实证）。
///
/// 背景：C# 的 `"A " + "B " + "C"` 在**编译期**合并成一整串，运行时传给 ImGui 的是完整长句。
/// 提取器若按单个字面量捕获，就会把每条**半截片段**当独立文案存进表 →
/// ① 片段永远命不中（纯废条目、白耗机翻额度）；② 真正的完整长句漏采 → 界面整段英文。
/// 因此提取前必须先做拼接合并，让下游正则看到"编译器最终看到的那一串"。
/// </summary>
public class SourceExtractConcatTests
{
    // 基本形态：两段拼接 → 合成一条
    [Fact]
    public void Merge_TwoPieces_Merges()
    {
        var src = "ImGui.Text(\"Hello \" + \"world\");";
        var outp = SourceExtractService.MergeConcatenatedLiterals(src);
        Assert.Contains("\"Hello world\"", outp);
        Assert.DoesNotContain("+", outp);
    }

    // 跨行 12 段（Heliosphere「技术信息」的真实形态）→ 合成完整一句
    [Fact]
    public void Merge_TwelvePiecesAcrossLines_MergesIntoOne()
    {
        var src =
            "ImGui.TextUnformatted(\n" +
            "    \"One-click installs use a password system. The button \" +\n" +
            "    \"above will generate a password, hash it, then copy the \" +\n" +
            "    \"password to your clipboard. The plugin doesn't store \" +\n" +
            "    \"the password. \" +\n" +
            "    \"This is to prevent other, unauthorised programs from \" +\n" +
            "    \"issuing install requests that will automatically go \" +\n" +
            "    \"through.\"\n" +
            ");";
        var outp = SourceExtractService.MergeConcatenatedLiterals(src);
        Assert.Contains(
            "\"One-click installs use a password system. The button above will generate a password, " +
            "hash it, then copy the password to your clipboard. The plugin doesn't store the password. " +
            "This is to prevent other, unauthorised programs from issuing install requests that will " +
            "automatically go through.\"",
            outp);
        // 原始的半截片段不应再单独存在
        Assert.DoesNotContain("\"One-click installs use a password system. The button \"", outp);
    }

    // 单条字面量（无拼接）必须原样保留
    [Fact]
    public void Merge_SingleLiteral_Unchanged()
    {
        var src = "ImGui.Text(\"Commands\");";
        Assert.Equal(src, SourceExtractService.MergeConcatenatedLiterals(src));
    }

    // 插值串 `$"…{x}…"` 与变量拼接**不得**合并（不可静态求值）
    [Fact]
    public void Merge_InterpolatedOrVariable_NotMerged()
    {
        var src1 = "ImGui.Text($\"Count: {n}\" + \" items\");";
        Assert.Contains("+\"", SourceExtractService.MergeConcatenatedLiterals(src1).Replace(" ", ""));

        var src2 = "ImGui.Text(\"Prefix \" + suffix + \" suffix\");";
        // 只应合并到变量前为止（"Prefix " 后紧跟变量，右侧不是字面量 → 不构成链）
        Assert.Equal(src2, SourceExtractService.MergeConcatenatedLiterals(src2));
    }

    // 转义序列必须原样保留（交由下游 Unescape 处理，合并阶段不得改写语义）
    [Fact]
    public void Merge_EscapesPreserved()
    {
        var src = "ImGui.Text(\"line1\\n\" + \"line2\\t\" + \"end\");";
        var outp = SourceExtractService.MergeConcatenatedLiterals(src);
        Assert.Contains("\"line1\\nline2\\tend\"", outp);
    }

    // 相邻但**不属于同一条链**的字面量（逗号分隔的实参）不得被误粘
    [Fact]
    public void Merge_SeparateArguments_NotGlued()
    {
        var src = "DrawOption(\"Enable Synthesis Helper\", \"Adds a helper\");";
        Assert.Equal(src, SourceExtractService.MergeConcatenatedLiterals(src));
    }

    // 无 `+` 的文件走零成本预筛分支（内容不变）
    [Fact]
    public void Merge_NoPlusSign_ReturnedAsIs()
    {
        var src = "ImGui.Text(\"Commands\");\nImGui.Text(\"Nerd info\");";
        Assert.Equal(src, SourceExtractService.MergeConcatenatedLiterals(src));
    }

    // ★ 关键回归：Heliosphere「技术信息」长段落（577 字符）必须能被采到。
    //   旧上限 300 会把合并后的完整长句整条丢掉 → 长句既不在候选表、也没译文 → 界面整段英文。
    //   上限已放宽到 600（替换层能力上限 1024 字节之内）。此处直接验证"合并结果长度落在可采区间"。
    [Fact]
    public void Merge_LongParagraph_Exceeds300ButUnder600()
    {
        var src =
            "ImGui.TextUnformatted(\n" +
            "    \"One-click installs use a password system. The button \" +\n" +
            "    \"above will generate a password, hash it, then copy the \" +\n" +
            "    \"password to your clipboard. The plugin doesn't store \" +\n" +
            "    \"the password. By pasting the password into the \" +\n" +
            "    \"Heliosphere website, the website can provide it to the \" +\n" +
            "    \"plugin during install requests. If the hash of the \" +\n" +
            "    \"provided password is the same as the stored hash in \" +\n" +
            "    \"the plugin, one-click installs will proceed. \" +\n" +
            "    \"Otherwise, the normal download prompt will be shown. \" +\n" +
            "    \"This is to prevent other, unauthorised programs from \" +\n" +
            "    \"issuing install requests that will automatically go \" +\n" +
            "    \"through.\"\n" +
            ");";
        var outp = SourceExtractService.MergeConcatenatedLiterals(src);
        // 取出合并后的字面量内容
        var first = outp.IndexOf('"') + 1;
        var last = outp.LastIndexOf('"');
        var merged = outp.Substring(first, last - first);
        Assert.True(merged.Length > 300, $"合并后应超过旧上限 300，实际 {merged.Length}");
        Assert.True(merged.Length <= 600, $"合并后应在新上限 600 之内，实际 {merged.Length}");
        Assert.StartsWith("One-click installs use a password system.", merged);
        Assert.EndsWith("automatically go through.", merged);
    }
}
