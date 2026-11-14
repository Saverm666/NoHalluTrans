using System.Collections.Concurrent;

namespace NoHalluTrans.Core;

public sealed class TranslationScheduler
{
    public async Task<TranslationResult> TranslateAsync(IReadOnlyList<Fragment> fragments, ITranslationProvider provider,
        ITranslationCache cache, ModelSettings settings, string sourceLanguage, string targetLanguage,
        IReadOnlyList<TermEntry> terms, IProgress<PipelineEvent>? progress, CancellationToken token)
    {
        settings.ValidateParameters();
        ConcurrentDictionary<int, string> translated = new();
        ConcurrentBag<TranslationIssue> issues = [];
        List<Fragment> pending = []; int cached = 0, successful = 0, fallback = 0, completed = 0;
        foreach (var f in fragments)
        {
            token.ThrowIfCancellationRequested();
            string? value = cache.Get(sourceLanguage, targetLanguage, f.Template);
            if (value is not null)
            {
                try { TranslationValidation.Validate(f, value, targetLanguage); translated[f.Index] = value; cached++; continue; }
                catch (FormatException) { /* Re-query invalid historic entries. */ }
            }
            pending.Add(f);
        }
        completed = cached;
        object progressGate = new();
        void Report(string message)
        {
            progress?.Report(new("progress", "translate", message, completed, fragments.Count,
                Fragments: fragments.Count, Translated: successful, Cached: cached, Fallback: fallback));
        }
        lock (progressGate) Report($"已处理 {completed}/{fragments.Count} 块，缓存复用 {cached} 块");
        string prose = string.Join("\n\n", fragments.Take(40).Select(f => f.Source));
        prose = prose[..Math.Min(prose.Length, 12000)];
        using SemaphoreSlim gate = new(Math.Clamp(settings.Concurrency, 1, 16));
        var tasks = pending.Chunk(settings.BatchSize).Select(async (batch, batchIndex) =>
        {
            await gate.WaitAsync(token);
            try
            {
                List<Fragment> remaining = [.. batch]; string? error = null;
                for (int round = 1; round <= settings.MaxRounds && remaining.Count > 0; round++)
                {
                    token.ThrowIfCancellationRequested();
                    lock (progressGate) Report($"正在请求模型 · 第 {batchIndex + 1} 批（{remaining.Count} 块） · 第 {round}/{settings.MaxRounds} 轮");
                    Dictionary<int, string> reply;
                    try { reply = await provider.TranslateAsync(remaining, sourceLanguage, targetLanguage, terms, prose, error, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or FormatException or TaskCanceledException)
                    {
                        error = ex is TaskCanceledException ? "模型请求超时" : ex.Message;
                        progress?.Report(new("issue", "translate", $"第 {round} 轮请求失败：{error}", Code: "translation-endpoint"));
                        continue;
                    }
                    List<Fragment> retry = [];
                    foreach (var f in remaining)
                    {
                        if (!reply.TryGetValue(f.Index, out var value)) { error = "回复缺少片段 " + f.Id; retry.Add(f); continue; }
                        try
                        {
                            TranslationValidation.Validate(f, value, targetLanguage);
                            lock (cache) cache.Put(sourceLanguage, targetLanguage, f.Template, value);
                            translated[f.Index] = value;
                            lock (progressGate)
                            {
                                successful++; completed++;
                                Report($"已处理 {completed}/{fragments.Count} 块");
                            }
                        }
                        catch (FormatException ex) { error = ex.Message; retry.Add(f); }
                    }
                    if (retry.Count > 0) progress?.Report(new("issue", "translate", $"第 {round} 轮译文校验未通过，将重试：{error}", Code: "translation-validation"));
                    remaining = retry;
                }
                foreach (var f in remaining)
                {
                    // A fallback must be left byte-identical during reassembly.
                    issues.Add(new("translation-fallback", error ?? "片段翻译失败，保留原文。", f.Id));
                    lock (progressGate)
                    {
                        fallback++; completed++;
                        Report($"已处理 {completed}/{fragments.Count} 块，部分片段保留原文");
                    }
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return new(new(translated), cached, successful, fallback, issues.OrderBy(i => i.Fragment).ToList());
    }
}
