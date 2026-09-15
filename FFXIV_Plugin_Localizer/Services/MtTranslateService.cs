using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    private WikiGlossaryService? _wiki;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary> 翻译任务是否在跑（窗口据此禁用按钮）。 </summary>
    public volatile bool Running;

    /// <summary> 当前任务的取消源（「停止翻译」用）。null = 没有任务在跑。 </summary>
    private volatile CancellationTokenSource? _cts;

    /// <summary> 进度/结果描述（窗口轮询显示）。 </summary>
    public string Status { get; private set; } = "";

    /// <summary> 外部（如启动检查）写入提示，不改运行状态。 </summary>
    public void Notify(string message) => Status = message;

    /// <summary>
    /// **中止当前翻译任务**：不再发起新的批次请求；**已经翻完的批次会保留**（那是花了额度的成果，
    /// 丢弃等于白花钱）。正在飞行中的那一批请求无法取消，但它返回后不会继续下一批。
    /// </summary>
    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 已释放则忽略 */ }
        Status = "正在停止…（已完成的批次会保留）";
        _appLog.Info("[机翻] 用户请求停止翻译");
    }

    public MtTranslateService(AppLog appLog, ReplacementService replacement, Configuration cfg)    {
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
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var missing = _replacement.CollectMissing();
                var texts = missing.Values.Distinct().Where(t => t.Length <= MaxTextLen).ToList();
                await TranslateAll(texts, t => _replacement.MergeTranslations(t), cts.Token);
            }
            catch (Exception ex)
            {
                Status = "翻译失败：" + ex.Message;
                _appLog.Error("[机翻] " + Status);
            }
            finally
            {
                Running = false;
                _cts = null;
                cts.Dispose();
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
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, untranslated) = _replacement.GetWindowEntries(plugin);
                var texts = untranslated.Where(t => t.Length <= MaxTextLen).ToList();
                await TranslateAll(texts, t => _replacement.MergeWindowEntries(plugin, t), cts.Token);
            }
            catch (Exception ex)
            {
                Status = "翻译失败：" + ex.Message;
                _appLog.Error("[机翻] " + Status);
            }
            finally
            {
                Running = false;
                _cts = null;
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// **一键翻译：把所有插件的窗口文字缺口一次翻完**（插件翻译窗口的「一键翻译」）。
    /// 逐个插件收集缺口 → 合并成一批送翻（不是每个插件各发一次请求，省额度也更快）→
    /// 结果按插件分别并入各自的窗口表（译文表保持**按插件**组织，不做跨插件串味）。
    /// </summary>
    public void StartAllWindowPlugins()
    {
        if (Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        Status = "正在汇总各插件的缺口…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                // ① 逐插件收集缺口，记录"这句原文属于哪些插件"（同一句可能被多个插件用到）
                var perPlugin = new List<(string Plugin, List<string> Missing)>();
                var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // 原文 → 插件列表
                var all = new List<string>();
                foreach (var (name, _, _) in _replacement.GetWindowPlugins())
                {
                    var (_, untranslated) = _replacement.GetWindowEntries(name);
                    var texts = untranslated.Where(t => t.Length <= MaxTextLen).ToList();
                    if (texts.Count == 0) continue;
                    perPlugin.Add((name, texts));
                    foreach (var t in texts)
                    {
                        if (!owners.TryGetValue(t, out var list)) owners[t] = list = new List<string>();
                        if (!list.Contains(name)) list.Add(name);
                        all.Add(t);
                    }
                }
                var uniq = all.Distinct().ToList();
                if (uniq.Count == 0)
                {
                    Status = "没有需要翻译的窗口文字（各插件都已翻完）。";
                    _appLog.Info("[机翻] 一键翻译：无缺口");
                    return;
                }
                Status = $"共 {perPlugin.Count} 个插件、{uniq.Count} 条待翻，正在翻译…";

                // ② 整批送翻，再按"归属插件"分发回各自的窗口表
                await TranslateAll(uniq, translated =>
                {
                    var total = 0;
                    var byPlugin = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    foreach (var (en, zh) in translated)
                    {
                        if (!owners.TryGetValue(en, out var list)) continue;
                        foreach (var p in list)
                        {
                            if (!byPlugin.TryGetValue(p, out var d))
                                byPlugin[p] = d = new Dictionary<string, string>(StringComparer.Ordinal);
                            d[en] = zh;
                        }
                    }
                    foreach (var (p, d) in byPlugin)
                    {
                        total += _replacement.MergeWindowEntries(p, d);
                    }
                    return total;
                }, cts.Token);
            }
            catch (Exception ex)
            {
                Status = "翻译失败：" + ex.Message;
                _appLog.Error("[机翻] " + Status);
            }
            finally
            {
                Running = false;
                _cts = null;
                cts.Dispose();
            }
        });
    }

    /// <summary> 是否值得翻译：含 ASCII 字母、不含中日韩字符（已是中文的不送翻）、且不是纯键位名。 </summary>
    private static bool IsTranslatable(string s) => TextHeuristics.IsTranslatable(s);

    /// <summary>
    /// 在当前译文表里筛出「仍未翻译」的那些（用于翻译过程中重算队列）。
    /// ⚠ 不做**跨插件**判断：这里只关心"这些串现在是否已被任何插件的译文表覆盖"——
    ///   只要有一处已有译文，本次就不必再送翻（同一句译名相同，省额度）。
    /// </summary>
    private List<string> GetCurrentMissing(IEnumerable<string> candidates)
        => _replacement.FilterStillMissing(candidates);

    /// <summary> 批量循环：分批送翻 → 汇总 → merge 落盘。 </summary>
    private async Task TranslateAll(List<string> texts, Func<Dictionary<string, string>, int> merge, CancellationToken token)
    {
        if (texts.Count == 0)
        {
            Status = "没有缺失的翻译（已全部覆盖）";
            _appLog.Info("[机翻] 没有缺失条目");
            return;
        }
        // 只送**英文**文案：某些插件字符串堆含中文（或已部分汉化），把它们送翻只会得到回声/解析失败。
        texts = texts.Where(IsTranslatable).ToList();
        if (texts.Count == 0)
        {
            Status = "没有需要翻译的英文文案（缺口均为非英文，已跳过）";
            _appLog.Info("[机翻] 缺口过滤后为空");
            return;
        }
        var batchSize = Math.Clamp(_cfg.AiBatchSize, 1, 200);
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = 0;
        var cancelled = false;
        var queue = new List<string>(texts);
        while (queue.Count > 0)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            var batch = queue.Take(batchSize).ToDictionary(t => t, _ => "", StringComparer.Ordinal);
            var done = Math.Min(batch.Count, queue.Count);
            Status = $"翻译中 {done}/{queue.Count}…";
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
            queue.RemoveRange(0, batch.Count);

            try { await Task.Delay(400, token); }   // 免费/低配额档限速，留出间隔；停止时立即抛出让循环结束
            catch (OperationCanceledException) { cancelled = true; break; }

            // ⚠ 每批结束后**把已被别人解决掉的条目移出队列**（2026-09-15 并发修复）：
            //    机翻是长任务，期间用户可能点「全部预翻译」把词典里更权威的现成译名套了进来。
            //    那些条目若还留在队列里就会被**白翻一遍**（花掉额度，结果还会因 MergeWindowEntries
            //    的"已有译文优先"被丢弃）。故按"当前实际缺口"重算队列。
            //    ⚠ 只在**批次边界**做（不是每批都全量读盘），且失败不影响主流程。
            if (queue.Count > 0)
            {
                try
                {
                    var still = GetCurrentMissing(queue);
                    if (still.Count < queue.Count)
                    {
                        _appLog.Info($"[机翻] 跳过 {queue.Count - still.Count} 条（期间已被预翻译/手动译完），剩余 {still.Count} 条");
                        queue = still;
                    }
                }
                catch { /* 重算失败就按原队列继续 */ }
            }
        }

        // ⚠ 即使被停止也**保留已完成的部分**：这些批次已经花掉额度了，丢弃等于白花钱。
        var added = translated.Count > 0 ? merge(translated) : 0;
        Status = cancelled
            ? $"已停止：保留已完成 {added} 条（未翻的仍可在缺口里重来；失败 {failed} 条）"
            : $"完成：新增 {added} 条中文（失败 {failed} 条），已保存";
        _appLog.Info($"[机翻] {Status}");
    }

    /// <summary> 注入 wiki 术语表（送翻时附带相关术语作对照，保证游戏专有名词译名一致）。 </summary>
    public void SetWiki(WikiGlossaryService? wiki) => _wiki = wiki;

    /// <summary> 送一批（≤单批条数）翻译。**用「原文/译文」成对数组协议**（与本地文件格式一致，抄旧项目风格）：
    /// 窗口文案常含 <c>* [ ] : ' " ,</c> 等字符，若让其当 JSON **键**回写必然大量转义失败
    /// （实测 40 条失败 30 条）；成对数组里原文是 JSON 的**值**，序列化天然安全。
    /// 发送 <c>[{原文:"...",译文:""}]</c>，要求 AI 只回填译文，按**下标**对应回原文（不依赖 AI 复述原文）。
    /// </summary>
    private async Task<Dictionary<string, string>> TranslateBatch(Dictionary<string, string> batch)
    {
        var (baseUrl, model) = ResolveEndpoint(_cfg);
        var items = batch.Keys.ToList(); // 保持插入顺序
        var userJson = TranslationFile.ToPairJson(items);

        var system = "你是游戏插件界面的翻译引擎。用户会给出一个 JSON 数组，每个元素形如 " +
                     "{\"en\": \"英文原文\", \"zh\": \"\"}。请把每条的 \"en\" 翻译成简洁自然的简体中文（游戏/UI 语境），" +
                     "填入同一条的 \"zh\" 字段。保持数组长度、顺序、每条的 \"en\" 完全不变，只填 \"zh\"。" +
                     "只输出这个 JSON 数组本身，不要任何解释或代码块标记。";
        // 附带相关官方术语（词汇出现在本批文案中的），确保游戏专有名词译名与官方一致
        if (_wiki is { Loaded: true })
        {
            var relevant = _wiki.FindRelevant(items, 80);
            if (relevant.Count > 0)
            {
                system += "\n\n以下是《最终幻想14》官方译名对照，涉及这些词时必须使用官方译法（不要自行意译）：\n" +
                          string.Join("\n", relevant.Select(t => $"{t.En} = {t.Zh}"));
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = Math.Clamp(_cfg.AiTemperature, 0f, 2f),
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userJson }
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
        // 推理模型（glm-4.7-flash / glm-4.5-flash 等）把思考过程放 reasoning_content：content 空时兜底取用
        if (string.IsNullOrWhiteSpace(content) && reasoning.Length > 0) content = reasoning;

        // 按成对数组下标解析；失败再退回「编号行」解析（兼容 AI 不听话改了格式）
        var result = TranslationFile.FromPairJson(content, items);
        if (result.Count == 0) result = ParseNumberedLines(content, items);
        if (result.Count == 0) throw new Exception("返回内容无法解析：" + Truncate(content, 200));
        return result;
    }

    /// <summary> 解析「编号. 译文」行，按编号映射回原英文。兼容全角句点、多种分隔与多余前后缀。 </summary>
    private static Dictionary<string, string> ParseNumberedLines(string content, List<string> items)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content)) return result;
        foreach (var rawLine in content.Replace('。', '.').Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('`', '*', '-', ' ');
            if (line.Length == 0) continue;
            // 找开头的编号
            var dot = -1;
            for (var i = 0; i < line.Length && i < 6; i++)
            {
                if (line[i] == '.' || line[i] == '、' || line[i] == ')') { dot = i; break; }
                if (!char.IsDigit(line[i])) break;
            }
            if (dot <= 0) continue;
            if (!int.TryParse(line[..dot], out var num)) continue;
            if (num < 1 || num > items.Count) continue;
            var zh = line[(dot + 1)..].Trim();
            if (zh.Length == 0) continue;
            result[items[num - 1]] = zh;
        }
        return result;
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
