using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// 机翻补缺：智谱 GLM（OpenAI 兼容 /chat/completions，glm-4-flash 免费模型）。
/// 把「安装器缺失扫描」出的英文文案分批（每批 10 条，整批 JSON 进出）送翻，结果并入对照表并落盘。
/// API Key 由用户自己填（Configuration.ZhipuApiKey，只存本机 pluginConfigs，严禁入库）。
/// </summary>
public sealed class MtTranslateService
{
    private const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string Model = "glm-4-flash";
    private const int BatchSize = 10;
    private const int MaxTextLen = 1000;

    private readonly AppLog _appLog;
    private readonly ReplacementService _replacement;
    private readonly Func<string> _apiKey;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary> 翻译任务是否在跑（窗口据此禁用按钮）。 </summary>
    public volatile bool Running;

    /// <summary> 进度/结果描述（窗口轮询显示）。 </summary>
    public string Status { get; private set; } = "";

    public MtTranslateService(AppLog appLog, ReplacementService replacement, Func<string> apiKey)
    {
        _appLog = appLog;
        _replacement = replacement;
        _apiKey = apiKey;
    }

    /// <summary> 启动后台翻译任务（同一时间只允许一个）。 </summary>
    public void Start()
    {
        if (Running) return;
        if (string.IsNullOrWhiteSpace(_apiKey()))
        {
            Status = "请先填 API Key（bigmodel.cn 注册后控制台创建）";
            return;
        }
        Running = true;
        Status = "正在扫描缺失文案…";
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                Status = "翻译失败：" + ex.Message;
                _appLog.Error("[机翻] " + Status);
            }
            finally
            {
                Running = false;
            }
        });
    }

    private async Task RunAsync()
    {
        var missing = _replacement.CollectMissing();
        var texts = missing.Values.Distinct().Where(t => t.Length <= MaxTextLen).ToList();
        if (texts.Count == 0)
        {
            Status = "没有缺失的翻译（对照表已覆盖全部已装插件介绍）";
            _appLog.Info("[机翻] 没有缺失条目");
            return;
        }

        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = 0;
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            var batch = texts.Skip(i).Take(BatchSize)
                .ToDictionary(t => t, _ => "", StringComparer.Ordinal);
            var done = Math.Min(i + BatchSize, texts.Count);
            Status = $"翻译中 {done}/{texts.Count}…";
            try
            {
                foreach (var (en, zh) in await TranslateBatch(batch))
                {
                    translated[en] = zh;
                }
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                _appLog.Warn($"[机翻] 一批 {batch.Count} 条失败：{ex.Message}");
            }
            await Task.Delay(400); // 免费档限速，留出间隔
        }

        var added = _replacement.MergeTranslations(translated);
        Status = $"完成：新增 {added} 条中文（失败 {failed} 条，对照表共 {_replacement.Count} 条），已保存";
        _appLog.Info($"[机翻] {Status}");
    }

    /// <summary> 送一批（≤10 条）翻译：整批 JSON 进、整批 JSON 出，容错剥离 ``` 围栏。 </summary>
    private async Task<Dictionary<string, string>> TranslateBatch(Dictionary<string, string> batch)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            temperature = 0.1,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "你是游戏插件界面的翻译引擎。把用户给出的 JSON 对象中的每个英文值翻译成简洁自然的简体中文（游戏/UI 语境），键保持英文原文不变，值替换为译文。只返回 JSON 对象本身，不要输出任何解释或代码块标记。"
                },
                new { role = "user", content = JsonSerializer.Serialize(batch) }
            }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Add("Authorization", "Bearer " + _apiKey().Trim());
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {(int)resp.StatusCode}：{Truncate(body, 200)}");
        }

        var json = JsonDocument.Parse(body);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        content = StripFences(content);

        var result = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
        if (result == null) throw new Exception("返回内容不是 JSON：" + Truncate(content, 200));
        return result.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
    }

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
