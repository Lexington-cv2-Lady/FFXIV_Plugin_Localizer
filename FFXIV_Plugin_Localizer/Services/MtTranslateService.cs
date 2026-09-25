using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        ("DeepSeek", "deepseek-flash", "https://api.deepseek.com/v1", "深度求索（OpenAI 兼容，国内可直连，性价比高；2026-09-10 起 deepseek-flash 正式发布，旧 deepseek-chat 已下线）"),
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

    /// <summary> 当前后台任务的句柄（供 `Dispose` **等待它真正退出**，见该方法说明）。 </summary>
    private volatile Task? _task;
    // 2026-09-19 运行时发现自动翻：钩子抓到未翻译英文 → 入此队列 → 空闲时自动送翻，翻完写词典
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _missedQueue = new();
    private readonly HashSet<string> _missedQueued = new(StringComparer.Ordinal);
    private readonly object _missedLock = new();
    /// <summary> 一批运行时发现文案翻完（en→zh），由 Plugin 订阅写进词典。 </summary>
    public event Action<Dictionary<string,string>>? MissedBatchTranslated;

    /// <summary> 已卸载：此后不再启动新任务（见 `Dispose`）。 </summary>
    private volatile bool _disposed;

    /// <summary> 进度/结果描述（窗口轮询显示）。 </summary>
    public string Status { get; private set; } = "";

    /// <summary> 翻译进度（供界面画进度条）：已完成 / 总数。Total = 0 表示当前无进度可显示。
    /// 只做整数值的**单向写入 + 轮询读取**（与旧项目"后台任务只做整串赋值"的约定一致）。 </summary>
    public volatile int ProgressDone;
    public volatile int ProgressTotal;

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

    /// <summary> 钩子抓到一条未翻译英文 → 入队；若当前空闲且已填 Key，则自动启动后台翻（2026-09-19 全自动）。 </summary>
    public void EnqueueMissed(string en)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg))) return;
        lock (_missedLock)
        {
            if (!_missedQueued.Add(en)) return;   // 已在队列/已入过队
        }
        _missedQueue.Enqueue(en);
        if (!Running) StartMissedLoop();
    }

    /// <summary> 后台循环：取 missed 队列攒批 → TranslateAll → 翻完事件抛给 Plugin 写词典。队列空即退出。 </summary>
    private void StartMissedLoop()
    {
        if (_disposed || Running) return;
        Running = true;
        ProgressDone = 0; ProgressTotal = 0;
        Status = "自动翻译运行时发现的新文案…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _task = Task.Run(async () =>
        {
            var done = 0;
            try
            {
                while (_missedQueue.TryDequeue(out var en))
                {
                    lock (_missedLock) _missedQueued.Remove(en);
                    if (en.Length > MaxTextLen) continue;
                    var batch = new List<string> { en };
                    while (batch.Count < 8 && _missedQueue.TryDequeue(out var more))
                    {
                        lock (_missedLock) _missedQueued.Remove(more);
                        if (more.Length <= MaxTextLen) batch.Add(more);
                    }
                    await TranslateAll(batch, t => { MissedBatchTranslated?.Invoke(t); return 0; }, cts.Token);
                    done += batch.Count;
                }
                if (done > 0) _appLog.Info("[自动翻] 运行时发现文案共翻 " + done + " 条并写词典");
            }
            catch (Exception ex)
            {
                Status = "自动翻失败：" + ex.Message;
                _appLog.Error("[自动翻] " + Status);
            }
            finally
            {
                Running = false;
                _cts = null;
                cts.Dispose();
                ProgressDone = 0; ProgressTotal = 0;
            }
        });
    }

    /// <summary>
    /// 单请求输出上限 max_tokens——**按平台给安全值**（搬自旧项目 `AiTranslateService.MaxTokensForModel`）。
    ///
    /// ⚠ 为什么要按平台分：不传 max_tokens 时各家用自家默认值，批量大时**响应会被中途截断**，
    ///   JSON 不完整 → 整批解析失败（用户看到的现象是"总有一批翻不出来"）。而各平台上限差异巨大
    ///   （DeepSeek 可到 38 万，其余多为 1.6 万左右），一刀切要么被拒、要么被截。
    ///   判断必须用**解析后的生效端点/模型**（选了预设服务商时 AiBaseUrl/AiModel 覆盖字段是空的）。
    /// </summary>
    public static long MaxTokensForModel(Configuration cfg)
    {
        var (url, model) = ResolveEndpoint(cfg);
        var m = (model ?? "").ToLowerInvariant();
        var b = (url ?? "").ToLowerInvariant();
        // ⚠ 这些值是**保守估计**，不是权威值：2026-09-15 实测智谱 glm-4-flash-250414 的真实上限是
        //   **16384**（旧项目写的 64000 是错的，照抄导致每批 400、全部翻译失败）。
        //   故这里一律取"不会出错"的档位，并配 EffectiveMaxTokens + ParseMaxTokensCap 自愈：
        //   万一某平台更小，第一次被拒后会自动降到它允许的值重试。
        if (b.Contains("deepseek") || m.Contains("deepseek")) return 8192;    // DeepSeek 上限高，但小值足够
        if (b.Contains("bigmodel") || m.Contains("bigmodel") || b.Contains("moonshot")) return 16384; // 智谱实测上限
        if (b.Contains("dashscope") || b.Contains("aliyuncs")) return 8192;
        return 4096;                                                          // 其它平台保守值
    }

    /// <summary> 运行中**学到的** max_tokens 上限（按端点 host 记）。被拒过一次后收敛到平台允许值。 </summary>
    private static readonly ConcurrentDictionary<string, long> _maxTokensLearned = new(StringComparer.OrdinalIgnoreCase);

    private static string HostKey(string baseUrl)
    {
        try { return new Uri(baseUrl).Host; } catch { return baseUrl ?? ""; }
    }

    /// <summary> 实际使用的 max_tokens：取"按平台估算值"与"学到的上限"中较小者。 </summary>
    private static long EffectiveMaxTokens(Configuration cfg, string baseUrl)
    {
        var want = MaxTokensForModel(cfg);
        if (_maxTokensLearned.TryGetValue(HostKey(baseUrl), out var cap) && cap > 0 && cap < want) return cap;
        return want;
    }

    /// <summary>
    /// 从平台报错里解析它允许的 max_tokens 上限，如智谱返回
    /// <c>max_tokens参数非法：限制数值范围[1,16384]</c> → 16384。
    /// 目的是**自愈**：任何平台的上限写错，最多只浪费一次请求就能自动纠正。
    /// </summary>
    private static long ParseMaxTokensCap(string errorBody)
    {
        try
        {
            var m = Regex.Match(errorBody, @"\[\s*\d+\s*,\s*(\d{2,9})\s*\]");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var cap) && cap > 0) return cap;
        }
        catch { /* 解析失败则返回 0，走原错误路径 */ }
        return 0;
    }

    /// <summary>
    /// 单批**输入字符**上限——与"条数上限"**双重生效**（搬自旧项目 `MaxBatchChars`）。
    ///
    /// ⚠ 只按条数分批是不够的：200 条长句子可能几万字符，照样超上下文被拒。
    ///   故分批时同时看"条数"和"累计字符数"，任一超限就拆批。
    /// </summary>
    public static int MaxBatchChars(Configuration cfg)
    {
        var (url, model) = ResolveEndpoint(cfg);
        var m = (model ?? "").ToLowerInvariant();
        var b = (url ?? "").ToLowerInvariant();
        if (b.Contains("deepseek") || m.Contains("deepseek")) return 30000;
        if (b.Contains("bigmodel") || b.Contains("moonshot") || b.Contains("dashscope") || b.Contains("aliyuncs")) return 20000;
        return 12000;
    }

    /// <summary>
    /// 组装对话补全端点。⚠ 自定义 BaseUrl 可能**已经带** `/chat/completions`
    /// （2026-09-15 代码审查 M4）：原实现无脑 `TrimEnd('/') + "/chat/completions"`，
    /// 对完整端点会拼成 `.../chat/completions/chat/completions` → 404。
    /// 这里先判断结尾，兼容三种填法：根地址 / 带 v1 / 已经是完整端点。
    /// </summary>
    private static string Endpoint(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b + "/chat/completions";
    }

    public MtTranslateService(AppLog appLog, ReplacementService replacement, Configuration cfg)    {        _appLog = appLog;
        _replacement = replacement;
        _cfg = cfg;
    }

    /// <summary> 启动后台翻译任务：安装器介绍缺口（同一时间只允许一个翻译任务）。 </summary>
    public void Start()
    {
        if (_disposed || Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        ProgressDone = 0; ProgressTotal = 0;   // 新一轮开始，清掉旧进度
        Status = "正在扫描缺失文案…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _task = Task.Run(async () =>
        {
            try
            {
                var missing = _replacement.CollectMissing();
                var texts = missing.Values.Distinct().Where(t => t.Length <= MaxTextLen).ToList();
                _appLog.Info($"[机翻] 开始：{missing.Count} 个插件缺口、{texts.Count} 条待翻文案（介绍类）");
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
                // 任务结束：清掉进度条（避免下一轮开始前显示上一轮的旧进度）
                ProgressDone = 0;
                ProgressTotal = 0;
            }
        });
    }

    /// <summary> 后台翻译某插件的窗口文字缺口（结果并入该插件窗口表）。 </summary>
    public void StartWindowPlugin(string plugin)
    {
        if (_disposed || Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        ProgressDone = 0; ProgressTotal = 0;   // 新一轮开始，清掉旧进度
        Status = $"[{plugin}] 正在读取缺口…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _task = Task.Run(async () =>
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
                // 任务结束：清掉进度条（避免下一轮开始前显示上一轮的旧进度）
                ProgressDone = 0;
                ProgressTotal = 0;
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
        if (_disposed || Running) return;
        if (string.IsNullOrWhiteSpace(GetApiKey(_cfg)))
        {
            Status = "请先在「AI 设置」填写当前服务商的 API Key";
            return;
        }
        Running = true;
        ProgressDone = 0; ProgressTotal = 0;   // 新一轮开始，清掉旧进度
        Status = "正在汇总各插件的缺口…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _task = Task.Run(async () =>
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
                _appLog.Info($"[机翻] 一键翻译窗口文字：{perPlugin.Count} 个插件、{uniq.Count} 条待翻");

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
                // 任务结束：清掉进度条（避免下一轮开始前显示上一轮的旧进度）
                ProgressDone = 0;
                ProgressTotal = 0;
            }
        });
    }

    /// <summary> 单词黑名单（命中词不送翻、也不替换）。由 Plugin 在词典加载后注入。 </summary>
    private HashSet<string>? _blacklist;

    /// <summary> 注入单词黑名单集合（大小写不敏感）。 </summary>
    public void SetBlacklist(IEnumerable<string>? words)
        => _blacklist = words == null ? null : new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

    /// <summary> 是否值得翻译：含 ASCII 字母、不含中日韩字符（已是中文的不送翻）、且不是纯键位名。 </summary>
    private static bool IsTranslatable(string s) => TextHeuristics.IsTranslatable(s);

    /// <summary> 插件卸载时中断进行中的翻译（对齐旧项目 `TranslatePipelineWindow.Dispose`）。
    /// 否则卸载后后台任务仍会继续跑并向已失效的服务里写数据。 </summary>
    public void Dispose()
    {
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* 已释放则忽略 */ }
        // ⚠ 2026-09-18 全面审查（**中高**）：原实现**只 Cancel、不等它退出**。而 `Plugin.Dispose`
        //   紧接着就调 `Replacement.Dispose()`（清空并释放全部表与中文指针）——后台任务此刻可能正停在
        //   `await` 之后，醒来继续 `MergeTranslations` → **写入已 Dispose 的服务**（重则把新指针塞进
        //   已清空的弃用区 → 永不释放的泄漏）。这里等它退出：token 已取消，在飞的请求会立刻抛
        //   OperationCanceledException，正常几十毫秒内结束；最多等 2 秒以免卡住卸载流程。
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 任务自身异常与卸载无关 */ }
        // ⚠ 2026-09-18 全面审查⑩（**中**）：`_http` 原先**从不释放** → 每次插件重载泄漏一个 HttpClient
        //   及其 handler/Socket 池（对频繁重载插件的用户是可累积的句柄/内存泄漏）。
        try { _http.Dispose(); } catch { /* 忽略 */ }
    }

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
        // 单词黑名单不送翻：这类词要求"永远保持英文"，送翻既浪费额度、回来还会被替换层拦掉。
        if (_blacklist is { Count: > 0 })
            texts = texts.Where(t => !_blacklist.Contains(t)).ToList();
        if (texts.Count == 0)
        {
            Status = "没有需要翻译的英文文案（缺口均为非英文，已跳过）";
            _appLog.Info("[机翻] 缺口过滤后为空");
            return;
        }
        var batchSize = Math.Clamp(_cfg.AiBatchSize, 1, 200);
        var maxBatchChars = MaxBatchChars(_cfg);   // ⚠ 与条数**双重**限制：只看条数会被超长批次打爆
        var total0 = texts.Count;
        var saved = 0;              // 已**落盘**的条数（进度条据此显示真实进度）
        var failed = 0;
        var cancelled = false;
        var queue = new List<string>(texts);
        ProgressTotal = total0;     // 供界面画进度条
        ProgressDone = 0;
        while (queue.Count > 0)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            // 按「条数」与「累计字符数」取本批（任一超限即截断）
            var take = 0;
            var chars = 0;
            while (take < queue.Count && take < batchSize)
            {
                var len = queue[take].Length;
                if (take > 0 && chars + len > maxBatchChars) break;   // 至少留一条，避免死循环
                chars += len;
                take++;
            }
            var batch = queue.Take(take).ToDictionary(t => t, _ => "", StringComparer.Ordinal);
            var doneNow = total0 - queue.Count;
            ProgressDone = doneNow;
            Status = $"翻译中 {doneNow}/{total0} 条（本批 {take} 条 / {chars} 字符）…";
            // 日志自带上下文：这一批在翻哪段文案（首条前 40 字），出问题不用再来回转述
            _appLog.Info($"[机翻] 进度 {doneNow}/{total0}：本批 {take} 条，首条 \"{Truncate(batch.Keys.First(), 40)}\"");
            try
            {
                // ⚠ **失败自动拆批重试**（2026-09-15 实测事故）：有一批 80 条因 AI **把输入原样回显**
                //   （zh 全空）→ 解析失败 → **整批 80 条真文案全丢**。单次失败常与「批次过大/内容同质」
                //   有关，**对半拆开再试通常就过了**；拆到单条仍失败才放弃（最多损失 1 条而非几十条）。
                var got = await TranslateBatchWithSplit(batch, token);
                // ⚠ **每批完成立即落盘**（2026-09-15 改进）：原实现把全部批次结果攒在内存、**最后才 merge**，
                //   于是中途崩溃 / 强退游戏 → 整轮成果**全丢**（用户看不到任何文件）。
                //   改为每批即写：最多损失"正在飞的这一批"，且进度条上的数字是**真实已落盘**的条数。
                //   `MergeWindowEntries` 本身是增量的（只写该批），重复调用不覆盖已有译文，安全。
                if (got.Count > 0) saved += merge(got);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // ⚠ 用户点「停止」导致的取消**不是失败**：不能记进 failed，也不该继续下一批。
                //    （放在通用 catch 之前，否则会被当成"一批失败"计入失败数、还白等一次 Delay。）
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                _appLog.Warn($"[机翻] 一批 {batch.Count} 条失败（首条 \"{Truncate(batch.Keys.First(), 40)}\"）：{Truncate(ex.Message, 120)}");
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

        // ⚠ 即使被停止也**保留已完成的部分**：那些批次已随批落盘（见上），不会丢。
        ProgressDone = total0 - queue.Count;
        Status = cancelled
            ? $"已停止：保留已完成 {saved} 条（未翻的仍可在缺口里重来；失败 {failed} 条）"
            : $"完成：新增 {saved} 条中文（失败 {failed} 条），已保存";
        _appLog.Info($"[机翻] {Status}");
    }

    /// <summary> 注入 wiki 术语表（送翻时附带相关术语作对照，保证游戏专有名词译名一致）。 </summary>
    public void SetWiki(WikiGlossaryService? wiki) => _wiki = wiki;

    /// <summary> 送一批（≤单批条数）翻译。**用「原文/译文」成对数组协议**（与本地文件格式一致，抄旧项目风格）：
    /// 窗口文案常含 <c>* [ ] : ' " ,</c> 等字符，若让其当 JSON **键**回写必然大量转义失败
    /// （实测 40 条失败 30 条）；成对数组里原文是 JSON 的**值**，序列化天然安全。
    /// 发送 <c>[{原文:"...",译文:""}]</c>，要求 AI 只回填译文，按**下标**对应回原文（不依赖 AI 复述原文）。
    /// </summary>
    /// <summary>
    /// **带拆批重试的批次翻译**：整批失败时对半拆分再试，直到单条。
    ///
    /// ⚠ 为什么需要（2026-09-15 实测）：有一批 80 条 AI 把输入**原样回显**（zh 全空）→ 解析失败
    ///   → **整批 80 条真文案全丢**。这类失败常与“批次过大 / 内容过于同质”有关，
    ///   对半拆开往往就正常了。拆到单条仍失败才真正放弃（最多损失 1 条）。
    /// 取消（用户点停止）不在此处吞掉，直接向上抛给主循环处理。
    /// </summary>
    private async Task<Dictionary<string, string>> TranslateBatchWithSplit(
        Dictionary<string, string> batch, CancellationToken token, int depth = 0)
    {
        try
        {
            return await TranslateBatch(batch, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }   // 用户主动停止：不重试；HttpClient 超时（token 未取消）会落到下面拆批
        catch (Exception ex) when (batch.Count > 1 && depth < 8)
        {
            var keys = batch.Keys.ToList();
            var half = keys.Count / 2;
            var a = keys.Take(half).ToDictionary(k => k, _ => "", StringComparer.Ordinal);
            var b = keys.Skip(half).ToDictionary(k => k, _ => "", StringComparer.Ordinal);
            _appLog.Warn($"[机翻] 一批 {batch.Count} 条失败（{Truncate(ex.Message, 80)}）——拆成 {a.Count}+{b.Count} 条重试");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in new[] { a, b })
            {
                try
                {
                    foreach (var (k, v) in await TranslateBatchWithSplit(part, token, depth + 1)) result[k] = v;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex2)
                {
                    _appLog.Warn($"[机翻] 拆出的 {part.Count} 条仍失败（首条 \"{Truncate(part.Keys.First(), 40)}\"）：{Truncate(ex2.Message, 100)}");
                }
                try { await Task.Delay(400, token); } catch (OperationCanceledException) { throw; }
            }
            return result;
        }
    }

    private async Task<Dictionary<string, string>> TranslateBatch(Dictionary<string, string> batch, CancellationToken token = default)
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

        // 【方案② · 2026-09-25】黑名单专名提示：这些词是 MOD 作者名 / 品牌名 / 固定标识符，
        // 应**永远保持英文**。整串精确匹配（IsBlacklisted）只能拦住"整标签恰等于该词"的情况，
        // 拦不住夹在更长短语/括号里的内嵌词（如 "Rue-YAB"、"Lavabod Omoi Rue"），
        // 故在机翻侧把整份黑名单作为"保持英文"提示注入，让模型对内嵌专名也按英文保留。
        if (_blacklist is { Count: > 0 })
        {
            var terms = _blacklist
                .Where(t => t.Length is > 0 and <= 40)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (terms.Count > 0)
            {
                system += "\n\n以下单词/缩写是 MOD 作者名、品牌名或固定标识符（如 Rue、Lavabod、YAB、TBSE、Uranus、Bibo 等），" +
                          "出现时请一律保持**英文原样**、不要翻译——即使它们夹在更长的短语、括号或句子里也要保留英文：\n" +
                          string.Join("、", terms);
            }
        }
        // 语境免责：游戏/MOD 语境里某些普通英文词有专门含义，不能按字面死译（司令官 2026-09-25 提示）。
        system += "\n\n语境提示：本批文案来自《最终幻想14》插件与 MOD 界面。" +
                  "① \"vanilla\" 在游戏/MOD 语境指「原版 / 无 mod」而非植物「香草」，遇到请译为「原版」或直接保留 vanilla；" +
                  "② 某些看似普通英文单词实为创作者专名（例如 rue 可能是作者名而非芸香草），请结合上下文判断，不确定时保留英文。";

        // 组装并发送；若平台拒了 max_tokens，**自动学习其上限后重试一次**（见下方自愈逻辑）
        var attempt = 0;
        JsonDocument json;
        while (true)
        {
            attempt++;
            var payload = JsonSerializer.Serialize(new
            {
                model,
                temperature = Math.Clamp(_cfg.AiTemperature, 0f, 2f),
                // ⚠ **必须显式给 max_tokens**（搬自旧项目）：不传就用平台默认值，批量稍大时
                //    返回会被**中途截断** → JSON 不完整 → 整批解析失败（表现为"总有一批翻不出来"）。
                //    各平台上限差异很大，按平台给安全值；被拒过一次后按学到的上限收敛。
                max_tokens = EffectiveMaxTokens(_cfg, baseUrl),
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = userJson }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl));
            req.Headers.Add("Authorization", "Bearer " + GetApiKey(_cfg).Trim());
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            // ⚠ 把 token 传进 SendAsync（对齐旧项目做法）：点「停止翻译」时**在飞的请求也会被立即中断**，
            //    而不是傻等它自然返回（本机 HttpClient 超时是 90 秒，用户会以为按钮没反应）。
            using var resp = await _http.SendAsync(req, token);
            var bodyText = await resp.Content.ReadAsStringAsync(token);
            if (!resp.IsSuccessStatusCode)
            {
                // ⚠ **自愈：max_tokens 被平台拒绝（2026-09-15 实测智谱 400「限制数值范围[1,16384]」）**
                //    我那批"按平台写死的上限"常量是照抄旧项目的，与平台实际限制不符 → **每一批都 400、全部白跑**。
                //    只改数字仍会重蹈覆辙（换平台/换模型又可能不对），故改为：**解析平台自报的合法范围、
                //    记下来并立即用正确值重试本批**。这样任何平台的上限写错最多只浪费一次请求。
                if (attempt == 1 && (int)resp.StatusCode == 400 &&
                    bodyText.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    var cap = ParseMaxTokensCap(bodyText);
                    if (cap > 0)
                    {
                        _maxTokensLearned[HostKey(baseUrl)] = cap;
                        _appLog.Warn($"[机翻] 平台拒绝 max_tokens（{Truncate(bodyText, 120)}）——" +
                                     $"已自动下调到 {cap} 并重试本批");
                        continue;
                    }
                }
                throw new Exception($"HTTP {(int)resp.StatusCode}：{Truncate(bodyText, 200)}");
            }
            json = JsonDocument.Parse(bodyText);
            break;
        }

        // ⚠ 审查：`JsonDocument` 持有池化内存 → 用 using 包住整个使用范围（原先未释放）
        using (json)
        {
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
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl));
            req.Headers.Add("Authorization", "Bearer " + apiKey.Trim());
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return $"连接失败（HTTP {(int)resp.StatusCode}）：{Truncate(body, 200)}";
            // ⚠ 审查：`JsonDocument` 持有池化内存，必须 using（原先未释放，机翻长任务下每批泄漏一份）
            using var json = JsonDocument.Parse(body);
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

    /// <summary> 当前服务商的名字（内置预设返回其名；自定义模式返回用户起的名，没起名则「自定义」）。 </summary>
    public static string CurrentProviderName(Configuration cfg)
    {
        var named = FindByName(cfg);
        if (named != null) return named.Value.Name;
        if (string.IsNullOrWhiteSpace(cfg.AiBaseUrl) || string.IsNullOrWhiteSpace(cfg.AiModel))
            return "未配置";
        var custom = (cfg.AiCustomName ?? "").Trim();
        return custom.Length > 0 ? custom : "自定义";
    }

    /// <summary> 当前是否为自定义端点模式（AiProviderName 空或不在内置预设表中）。 </summary>
    public static bool IsCustomProvider(Configuration cfg) => FindByName(cfg) == null;

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
