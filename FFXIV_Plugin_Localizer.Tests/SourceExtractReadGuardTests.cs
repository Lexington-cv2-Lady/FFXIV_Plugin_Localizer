using System;
using System.IO;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// 「遗留发现项 F1：静默 catch」回归测试（2026-09-26，审查方整改件）。
///
/// 背景：SourceExtractService.ExtractFromDirectory 原先是
///     try { text = File.ReadAllText(file, Encoding.UTF8); } catch { continue; }
/// —— 吞掉一切异常、不留日志：文件被占用 / 无权限 / 枚举后被删时无声跳过，
///    连「代码 bug 导致的异常」也会被一并吞掉，排查时毫无线索。
///
/// 处置：抽出 internal TryReadSourceText，① 写明「此处异常属预期」的理由；
///      ② 只捕获 IO / 权限类异常（其它照旧上抛）；③ 由调用方汇总一条 Warn 留痕。
/// 本测试守住这三点的可观测行为。
/// </summary>
public class SourceExtractReadGuardTests : IDisposable
{
    private readonly string _dir;
    private FileStream? _lock;

    public SourceExtractReadGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ptp_src_read_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言结果 */ }
        GC.SuppressFinalize(this);
    }

    // 正常文件：读得到，内容按 UTF-8 正确解出（含中文）
    [Fact]
    public void Read_NormalFile_ReturnsTrueWithContent()
    {
        var p = Path.Combine(_dir, "a.cs");
        File.WriteAllText(p, "ImGui.Text(\"你好 World\");");

        var ok = SourceExtractService.TryReadSourceText(p, out var text);

        Assert.True(ok);
        Assert.Contains("你好 World", text);
    }

    // 被其它进程独占（FileShare.None）→ 属预期：返回 false 且不抛
    [Fact]
    public void Read_LockedByOtherProcess_ReturnsFalseWithoutThrowing()
    {
        var p = Path.Combine(_dir, "locked.cs");
        File.WriteAllText(p, "ImGui.Text(\"x\");");
        _lock = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ok = SourceExtractService.TryReadSourceText(p, out var text);

        Assert.False(ok);
        Assert.Equal(string.Empty, text);   // 失败路径必须给出确定的空串，不能留 null 隐患
    }

    // 文件不存在（枚举后被删的典型形态）→ 属预期：返回 false 且不抛
    [Fact]
    public void Read_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var p = Path.Combine(_dir, "gone.cs");

        var ok = SourceExtractService.TryReadSourceText(p, out var text);

        Assert.False(ok);
        Assert.Equal(string.Empty, text);
    }

    // 读取失败时不得把异常吞成「静默成功」：本用例与上面两条共同保证调用方能据此计数并打 Warn
    [Fact]
    public void Read_Failure_DoesNotReturnTrue()
    {
        var p = Path.Combine(_dir, "locked2.cs");
        File.WriteAllText(p, "ImGui.Text(\"y\");");
        using var l = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(SourceExtractService.TryReadSourceText(p, out _));
    }
}
