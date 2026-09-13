using System.Text.Encodings.Web;
using System.Text.Json;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 统一的 JSON 写出选项：缩进 + 不转义非 ASCII 字符（搬自旧项目）。
/// System.Text.Json 默认把中文全部转成 \uXXXX 转义（合法 JSON 但人眼不可读），
/// 必须用 UnsafeRelaxedJsonEscaping 让中文按 UTF-8 原样写出。
/// </summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
