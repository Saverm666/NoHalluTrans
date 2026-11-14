using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class OpenAiProvider(HttpClient client, ModelSettings settings) : ITranslationProvider
{
    public static Uri Endpoint(string baseUrl)
    {
        string value = baseUrl.Trim().TrimEnd('/');
        return new Uri(value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? value : value + "/chat/completions");
    }
    public async Task<Dictionary<int, string>> TranslateAsync(IReadOnlyList<Fragment> fragments, string sourceLanguage,
        string targetLanguage, IReadOnlyList<TermEntry> terms, string prose, string? retryError, CancellationToken cancellationToken)
    {
        string terminology = JsonSerializer.Serialize(terms, JsonDefaults.Options);
        string system = ResourceData.Prompts.GetProperty("fragment").GetString()!;
        system += "\n\n" + ResourceData.Prompts.GetProperty("batch").GetString()!.Replace("several fragments", "the requested fragments", StringComparison.Ordinal);
        system += "\nThe JSON response format above overrides the plain-text format. Each fragment has its own placeholder set: copy only the placeholders present in that fragment, verbatim and in their original order. Never invent or copy placeholders from document context or another fragment.";
        string user = $"source_language: {sourceLanguage}\ntarget_language: {targetLanguage}\nTERMINOLOGY: {terminology}\nDOCUMENT_PROSE_JSON: {JsonSerializer.Serialize(prose)}\nCURRENT_LATEX_JSON: " +
            JsonSerializer.Serialize(new { fragments = fragments.Select(f => new { id = f.Id, text = f.Template }) });
        if (retryError is not null) user += "\nPrevious validation error (correct it and return all requested fragments): " + retryError;
        string content = await ChatAsync(system, user, cancellationToken);
        return Parse(content, fragments);
    }
    public async Task TestAsync(CancellationToken token)
    {
        settings.Validate();
        await ChatAsync("Return only OK.", "Connection test", token, 32);
    }
    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken token)
    {
        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("请填写有效的 HTTP 或 HTTPS 接口地址。");
        string root = settings.BaseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) root = root[..^17];
        var endpoint = new Uri(root.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? root : root + "/models");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrEmpty(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "HTTP 401：服务端拒绝了 API key，请检查密钥是否有效、是否属于当前接口。",
                HttpStatusCode.Forbidden => "HTTP 403：当前密钥没有读取模型列表的权限，可以手动输入模型 ID。",
                HttpStatusCode.NotFound => "HTTP 404：服务端不支持此模型列表地址，可以手动输入模型 ID。",
                _ => $"获取模型列表返回 HTTP {(int)response.StatusCode}，可以重试或手动输入模型 ID。"
            }, null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using MemoryStream buffer = new();
        await SourceAcquirer.CopyLimitedAsync(stream, buffer, 4 * 1024 * 1024, deadline.Token);
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new FormatException("接口未返回标准模型列表，可以手动输入模型 ID。");
            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString()!.Trim())
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { throw new FormatException("接口返回的模型列表不是有效 JSON，可以手动输入模型 ID。"); }
    }
    public async Task<List<TermEntry>> ExtractTermsAsync(string prose, string source, string target, CancellationToken token)
    {
        string prompt = ResourceData.Prompts.GetProperty("terminology").GetString()!;
        string value = await ChatAsync(prompt, $"source_language: {source}\ntarget_language: {target}\nDOCUMENT_PROSE_JSON: " + JsonSerializer.Serialize(prose), token);
        using var document = JsonDocument.Parse(StripFences(value));
        return document.RootElement.GetProperty("terms").Deserialize<List<TermEntry>>(JsonDefaults.Options)!
            .Where(t => t.Source.Length > 0 && t.Target.Length > 0 && prose.Contains(t.Source, t.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(128).ToList();
    }
    private async Task<string> ChatAsync(string system, string user, CancellationToken token, int? maxTokens = null)
    {
        settings.Validate();
        Dictionary<string, object> payload = new()
        {
            ["model"] = settings.Model, ["temperature"] = 0, ["max_tokens"] = maxTokens ?? settings.MaxOutputTokens,
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = user } }
        };
        if (settings.Thinking is "enabled" or "disabled") payload["thinking"] = new { type = settings.Thinking };
        for (int attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings.BaseUrl)) { Content = JsonContent.Create(payload) };
            if (!string.IsNullOrEmpty(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                {
                    TimeSpan delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, token); continue;
                }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"模型服务返回 HTTP {(int)response.StatusCode}，请检查地址、密钥和模型权限。", null, response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using MemoryStream buffer = new();
                await SourceAcquirer.CopyLimitedAsync(stream, buffer, 16 * 1024 * 1024, deadline.Token);
                using var document = JsonDocument.Parse(buffer.ToArray());
                var message = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
                if (message.ValueKind == JsonValueKind.String) return message.GetString()!;
                if (message.ValueKind == JsonValueKind.Array) return string.Concat(message.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()));
                throw new FormatException("模型回复缺少文本内容。");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); }
            catch (JsonException) { throw new FormatException("模型回复不是有效 JSON。"); }
            catch (KeyNotFoundException) { throw new FormatException("模型回复不包含 choices/message/content。"); }
        }
    }
    public static string StripFences(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            int start = text.IndexOf('\n'), end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
        }
        return text;
    }
    public static Dictionary<int, string> Parse(string content, IReadOnlyList<Fragment> fragments)
    {
        string value = StripFences(content);
        try
        {
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("fragments", out var rows))
            {
                if (fragments.Count == 1 && document.RootElement.TryGetProperty("translation", out var single))
                    return new() { [fragments[0].Index] = single.GetString()! };
                throw new FormatException("模型回复缺少 fragments 数组。");
            }
            Dictionary<int, string> result = [];
            foreach (var row in rows.EnumerateArray())
            {
                string id = row.GetProperty("id").GetString()!;
                if (!Regex.IsMatch(id, @"^f\d+$") || !int.TryParse(id[1..], out int index) || !fragments.Any(f => f.Index == index))
                    throw new FormatException("模型回复包含未知片段编号。");
                string? translation = row.GetProperty("translation").GetString();
                if (translation is null) throw new FormatException("模型回复包含空的译文字段。");
                if (!result.TryAdd(index, translation)) throw new FormatException("模型回复包含重复片段编号。");
            }
            return result;
        }
        catch (JsonException)
        {
            if (fragments.Count == 1 && !value.StartsWith('{')) return new() { [fragments[0].Index] = content };
            throw new FormatException("模型没有按约定返回译文 JSON。");
        }
        catch (KeyNotFoundException) { throw new FormatException("模型回复缺少片段字段。"); }
        catch (InvalidOperationException) { throw new FormatException("模型回复的片段字段类型不正确。"); }
    }
}

public sealed class FileTranslationProvider : ITranslationProvider
{
    private readonly Dictionary<string, string> translations;
    public FileTranslationProvider(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        translations = document.RootElement.GetProperty("fragments").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("translation").GetString()!);
    }
    public Task<Dictionary<int, string>> TranslateAsync(IReadOnlyList<Fragment> fragments, string sourceLanguage, string targetLanguage,
        IReadOnlyList<TermEntry> terms, string prose, string? retryError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(fragments.Where(f => translations.ContainsKey(f.Id)).ToDictionary(f => f.Index, f => translations[f.Id]));
    }
}
public sealed class MockTranslationProvider(string mode = "identity") : ITranslationProvider
{
    public Task<Dictionary<int, string>> TranslateAsync(IReadOnlyList<Fragment> fragments, string sourceLanguage, string targetLanguage,
        IReadOnlyList<TermEntry> terms, string prose, string? retryError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == "error") throw new HttpRequestException("mock endpoint failure");
        return Task.FromResult(fragments.ToDictionary(f => f.Index, f => mode == "bad-markers" ? MarkerProtocol.Pattern.Replace(f.Template, "") : f.Template));
    }
}
