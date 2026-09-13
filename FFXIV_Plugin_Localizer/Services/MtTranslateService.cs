using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FFXIVPluginLocalizer.Services;

/// <summary>
/// AI 翻译补缺：OpenAI 兼容 /chat/completions（默认智谱 glm-4-flash 免费模型）。
/// 12 家供应商预设 + 自定义端点（抄自旧项目 AiTranslateService 的设置体系）。
/// 把「安装器缺失扫描」的英文文案分批送翻（条数/温度在 AI 设置），结果并入对照表并落盘。
/// API Key 按服务商分存（Configuration.AiApiKeys），用户自己填、只存本机 pluginConfigs，严禁入库。
/// </summary>
public sealed class MtTranslateService
{
    /// <summary> 预置供应商（国内可直连优先，海外在后；自定义模式见 AI 设置 Combo 末项）。 </summary>
    public static readonly (string Name, string Model, string BaseUrl, string Note)[] Providers =
    {
        ("智谱 GLM", "glm-4-flash-250414", "https://open.bigmodel.cn/api/paas/v4", "智谱 AI 开放平台（OpenAI 兼容；glm-4-flash-250414 免费、128K 长上下文、结构化 JSON 友好，实测比 glm-4-flash 快数倍）"),
        ("通义千问", "qwen-plus", "https://dashscope.aliyuncs.com/compatible-mode/v1", "阿里云百炼（OpenAI 兼容，需先开通百炼）"),
        ("腾讯混元", "hunyuan-turbos-latest", "https://api.hunyuan.cloud.tencent.com/v1", "腾讯云大模型（OpenAI 兼容）"),
        ("百度千帆", "ernie-4.5-turbo-32k", "https://qianfan.baidubce.com/v2", "百度智能云千帆（OpenAI 兼容）"),
        ("DeepSeek", "deepseek-chat", "https://api.deepseek.com/v1", "深度求索（OpenAI 兼容，国内可直连，性价比高）"),
        ("OpenRouter", "openai/gpt-4o-mini", "https://openrouter.ai/api/v1", "海外聚合中转，可调 GPT/Claude/Gemini"),
        ("Groq", "llama-3.3-70b-versatile", "https://api.groq.com/openai/v1", "开源模型超高速推理（海外）"),
        ("OpenAI（GPT）", "gpt-4o-mini", "https://api.openai.com/v1", "官方接口：国内不可直连，需代理或中转"),
        ("Google Gemini", "gemini-1.5-flash", "https://generativelanguage.googleapis.com/v1beta/openai", "谷歌官方 OpenAI 兼容端点：国内不可直连"),
        ("Anthropic Claude", "claude-3-5-sonnet-latest", "https://api.anthropic.com/v1", "Anthropic 官方：国内不可直连，需代理"),
        ("xAI Grok", "grok-2-latest", "https://api.x.ai/v1", "xAI 官方（OpenAI 兼容）：国内不可直连"),
        ("Mistral", "mistral-small-latest", "https://api.mistral.ai/v1", "Mistral 官方：国内不可直连"),
    };

    private const int MaxTextLen = 1000;

    private readonly AppLog _appLog;
    private readonly ReplacementService _replacement;
    private readonly Configuration _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary> 翻译任务是否在跑（窗口据此禁用按钮）。 </summary>
    public volatile bool Running;

    /// <summary> 进度/结果描述（窗口轮询显示）。 </summary>
    public string Status { get; private set; } = "";

    /// <summary> 外部（如启动检查）写入提示，不改运行状态。 </summary>
    public void Notify(string message) => Status = message;

    public MtTranslateService(AppLog appLog, ReplacementService replacement, Configuration cfg)
    {
        _appLog = appLog;
        _replacement = replacement;
        _cfg = cfg;
    }

    /// <summary> 启动后台翻译任务：安装器介绍缺口（同一时间只允许一个翻译任务）。 </summary>
    public void Start()
    {
        if (Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        Status = "正在扫描缺失文案…";
        _ = Task.Run(async () =>
        {
            try
            {
                var missing = _replacement.CollectMissing();
                var texts = missing.Values.Distinct().Where(t => t.Length <= MaxTextLen).ToList();
                await TranslateAll(texts, t => _replacement.MergeTranslations(t));
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

    /// <summary> 后台翻译某插件的窗口文字缺口（结果并入该插件窗口表）。 </summary>
    public void StartWindowPlugin(string plugin)
    {
        if (Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        Status = $"[{plugin}] 正在读取缺口…";
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, untranslated) = _replacement.GetWindowEntries(plugin);
                var texts = untranslated.Where(t => t.Length <= MaxTextLen).ToList();
                await TranslateAll(texts, t => _replacement.MergeWindowEntries(plugin, t));
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

    /// <summary> 批量循环：分批送翻 → 汇总 → merge 落盘。 </summary>
    private async Task TranslateAll(List<string> texts, Func<Dictionary<string, string>, int> merge)
    {
        if (texts.Count == 0)
        {
            Status = "没有缺失的翻译（已全部覆盖）";
            _appLog.Info("[机翻] 没有缺失条目");
            return;
        }
        var batchSize = Math.Clamp(_cfg.AiBatchSize, 1, 200);
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = 0;
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            var batch = texts.Skip(i).Take(batchSize)
                .ToDictionary(t => t, _ => "", StringComparer.Ordinal);
            var done = Math.Min(i + batchSize, texts.Count);
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
            await Task.Delay(400); // 免费/低配额档限速，留出间隔
        }

        var added = merge(translated);
        Status = $"完成：新增 {added} 条中文（失败 {failed} 条），已保存";
        _appLog.Info($"[机翻] {Status}");
    }

    /// <summary> 送一批（≤单批条数）翻译：整批 JSON 进、整批 JSON 出，容错剥离 ``` 围栏。 </summary>
    private async Task<Dictionary<string, string>> TranslateBatch(Dictionary<string, string> batch)
    {
        var (baseUrl, model) = ResolveEndpoint(_cfg);
        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = Math.Clamp(_cfg.AiTemperature, 0f, 2f),
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

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + GetApiKey(_cfg).Trim());
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {(int)resp.StatusCode}：{Truncate(body, 200)}");
        }

        var json = JsonDocument.Parse(body);
        var message = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        var content = message.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
        var reasoning = message.TryGetProperty("reasoning_content", out var rcEl) ? rcEl.GetString() ?? "" : "";
        // 推理模型（glm-4.7-flash / glm-4.5-flash 等）会把思考过程放 reasoning_content：
        // 若 content 里没有可用 JSON，从 reasoning 兜底提取（否则整批静默失败）。
        if (string.IsNullOrWhiteSpace(content) || !content.Contains('{') || reasoning.Length > 0)
        {
            content = PickJson(content, reasoning);
        }
        content = StripFences(content);

        var result = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
        if (result == null) throw new Exception("返回内容不是 JSON：" + Truncate(content, 200));
        return result.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
    }

    /// <summary> 从若干候选文本里挑出「能解析成 JSON 对象」的那段（优先 content，其次 reasoning）。 </summary>
    private static string PickJson(string content, string reasoning)
    {
        foreach (var candidate in new[] { content, reasoning })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var s = StripFences(candidate);
            var start = s.IndexOf('{');
            var end = s.LastIndexOf('}');
            if (start < 0 || end <= start) continue;
            var slice = s[start..(end + 1)];
            try
            {
                var probe = JsonSerializer.Deserialize<Dictionary<string, string>>(slice);
                if (probe != null && probe.Count > 0) return slice;
            }
            catch { /* 这段不是 JSON，试下一段 */ }
        }
        return content;
    }

    /// <summary> 测试连接（AI 设置窗口用，返回简短结果）。抄自旧项目。 </summary>
    public async Task<string> TestAsync()
    {
        try
        {
            var (baseUrl, model) = ResolveEndpoint(_cfg);
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model))
                return "自定义模式需填写 API 地址与模型";
            var apiKey = GetApiKey(_cfg);
            if (string.IsNullOrWhiteSpace(apiKey))
                return "未填写 API Key";
            var payload = JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.1,
                max_tokens = 50,
                messages = new[] { new { role = "user", content = "你好，请回复：连接成功" } }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
            req.Headers.Add("Authorization", "Bearer " + apiKey.Trim());
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return $"连接失败（HTTP {(int)resp.StatusCode}）：{Truncate(body, 200)}";
            var json = JsonDocument.Parse(body);
            var content = json.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";
            return "连接成功：" + Truncate(StripFences(content), 80);
        }
        catch (Exception ex)
        {
            return "连接失败：" + ex.Message;
        }
    }

    // ═══════════════════════ 供应商解析 / Key 分存（抄自旧项目 AiTranslateService） ═══════════════════════

    /// <summary> 按名称解析生效服务商（本版无自定义供应商列表，仅内置 12 家 + 手工自定义）。 </summary>
    private static (string Name, string Model, string BaseUrl, string Note)? FindByName(Configuration cfg)
    {
        var n = (cfg.AiProviderName ?? "").Trim();
        if (string.IsNullOrEmpty(n)) return null;
        foreach (var p in Providers)
        {
            if (p.Name == n) return p;
        }
        return null;
    }

    /// <summary> 获取生效的 BaseUrl / 模型名（覆盖字段留空 = 供应商预设；自定义模式必须手填）。 </summary>
    public static (string BaseUrl, string Model) ResolveEndpoint(Configuration cfg)
    {
        var named = FindByName(cfg);
        if (named != null)
        {
            var baseUrl = string.IsNullOrWhiteSpace(cfg.AiBaseUrl) ? named.Value.BaseUrl : cfg.AiBaseUrl.TrimEnd('/');
            var model = string.IsNullOrWhiteSpace(cfg.AiModel) ? named.Value.Model : cfg.AiModel.Trim();
            return (baseUrl, model);
        }
        var cb = (cfg.AiBaseUrl ?? "").Trim().TrimEnd('/');
        var cm = (cfg.AiModel ?? "").Trim();
        return string.IsNullOrEmpty(cb) || string.IsNullOrEmpty(cm) ? ("", "") : (cb, cm);
    }

    /// <summary> 当前服务商的名字（手工自定义模式返回「自定义」）。 </summary>
    public static string CurrentProviderName(Configuration cfg)
    {
        var named = FindByName(cfg);
        if (named != null) return named.Value.Name;
        return string.IsNullOrWhiteSpace(cfg.AiBaseUrl) || string.IsNullOrWhiteSpace(cfg.AiModel)
            ? "未配置"
            : "自定义";
    }

    /// <summary> 读取当前服务商保存的 API Key（兼容旧版单一 ZhipuApiKey 字段：仅迁移到智谱名下）。 </summary>
    public static string GetApiKey(Configuration cfg)
    {
        var name = CurrentProviderName(cfg);
        if (cfg.AiApiKeys != null && cfg.AiApiKeys.TryGetValue(name, out var k) && !string.IsNullOrWhiteSpace(k))
            return k;
        // 兼容旧版单一字段：仅在还没有任何分服务商 Key 时使用
        if (cfg.AiApiKeys == null || cfg.AiApiKeys.Count == 0)
            return cfg.ZhipuApiKey ?? "";
        return "";
    }

    /// <summary> 保存当前服务商的 API Key。 </summary>
    public static void SetApiKey(Configuration cfg, string key)
    {
        var name = CurrentProviderName(cfg);
        cfg.AiApiKeys ??= new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(key))
            cfg.AiApiKeys.Remove(name);
        else
            cfg.AiApiKeys[name] = key.Trim();
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
