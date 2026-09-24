using System;
using System.Collections.Generic;
using System.IO;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// TranslationFile 读写回归（通用母本 B.7 原子写红线 + 三格式兼容）。
/// 覆盖：原子写无 .tmp 残留且内容完整；成对数组/纯字典/裸数组三格式兼容；允许「译文==原文」。
/// </summary>
public class TranslationFileTests
{
    [Fact]
    public void WriteAtomic_LeavesNoTemp_AndContentIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_tf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "d.json");
        try
        {
            TranslationFile.WriteAtomic(path, "{\"a\":1}");
            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));   // 原子写标志：无残留临时文件
        }
        finally { Directory.Delete(dir, true); }
    }

    // 三格式兼容：成对数组 / 纯字典 / 裸数组
    [Fact]
    public void Load_ThreeFormats_AllParsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_tf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p1 = Path.Combine(dir, "a.json");
            TranslationFile.WriteAtomic(p1, "{\"entries\":[{\"原文\":\"Retry\",\"译文\":\"重试\"}]}");
            Assert.Equal("重试", TranslationFile.Load(p1)["Retry"]);

            var p2 = Path.Combine(dir, "b.json");
            TranslationFile.WriteAtomic(p2, "{\"Hello\":\"你好\"}");
            Assert.Equal("你好", TranslationFile.Load(p2)["Hello"]);

            var p3 = Path.Combine(dir, "c.json");
            TranslationFile.WriteAtomic(p3, "[{\"原文\":\"Save\",\"译文\":\"保存\"}]");
            Assert.Equal("保存", TranslationFile.Load(p3)["Save"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // 允许「译文==原文」（保持原样，避免机翻死循环）
    [Fact]
    public void Save_KeepsSameAsSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpl_tf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s.json");
        try
        {
            TranslationFile.Save(path, new Dictionary<string, string> { ["URL"] = "URL", ["Attack"] = "攻击" });
            var d = TranslationFile.Load(path);
            Assert.Equal("URL", d["URL"]);
            Assert.Equal("攻击", d["Attack"]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
