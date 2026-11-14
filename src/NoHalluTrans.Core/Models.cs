using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoHalluTrans.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed record TermEntry(string Source, string Target, bool CaseSensitive = false);
public sealed record Fragment(int Index, string File, int Start, int End, string Source,
    string Template, string Kind, IReadOnlyList<string> Values)
{
    public string Id => $"f{Index:D4}";
    public string[] Markers => Enumerable.Range(0, Values.Count).Select(MarkerProtocol.Make).ToArray();
}
public sealed record TranslationIssue(string Code, string Message, string? Fragment = null);
public sealed record TranslationResult(Dictionary<int, string> Translations, int Cached, int Translated,
    int Fallback, List<TranslationIssue> Issues);
public sealed record PreparedProject(string Root, string MainTex, string Engine, string DocumentClass,
    IReadOnlyList<string> BodyFiles, bool ShellEscape = false);
public sealed record TexPreflight(string Engine, string? Binary, bool Ok, IReadOnlyList<string> Missing,
    IReadOnlyDictionary<string, bool?> Available, string? SuggestedEngine = null);
public sealed record CompileResult(bool Ok, string? Pdf, string Log, int ReturnCode, int Runs,
    IReadOnlyList<string> MissingFiles)
{
    public IReadOnlyList<TexDiagnostic> Diagnostics => CompileDiagnostics.Parse(Log);
}
public sealed record RepairRecord(string Name, string Description, int Round);
public sealed record PipelineEvent(string Type, string StageId = "", string Message = "",
    int? Completed = null, int? Total = null, string? Code = null, string? Fragment = null,
    int? Fragments = null, int? Translated = null, int? Cached = null, int? Fallback = null);
public sealed record PipelineResult(string Outcome, string? OutputPdf, string? OriginalPdf,
    string WorkDirectory, int Fragments, int Cached, int Translated, int Fallback,
    IReadOnlyList<RepairRecord> Repairs, IReadOnlyList<TranslationIssue> Issues);

public sealed class ModelSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public int BatchSize { get; set; } = 8;
    public int Concurrency { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRounds { get; set; } = 3;
    public int MaxOutputTokens { get; set; } = 16384;
    public string Thinking { get; set; } = "default";
    public ModelSettings Snapshot() => (ModelSettings)MemberwiseClone();
    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("模型接口地址必须是 HTTP 或 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("请填写模型名称。");
        ValidateParameters();
    }
    public void ValidateParameters()
    {
        if (BatchSize is < 1 or > 64 || Concurrency is < 1 or > 16 || MaxRounds is < 1 or > 10 ||
            TimeoutSeconds is < 5 or > 3600 || MaxOutputTokens is < 256 or > 131072)
            throw new ArgumentException("高级模型参数超出支持范围。");
    }
}

public sealed class PipelineOptions
{
    public string Source { get; set; } = "";
    public string OutputPdf { get; set; } = "";
    public string PdfLayout { get; set; } = "target-only";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "zh-Hans";
    public string? MainHint { get; set; }
    public string Engine { get; set; } = "auto";
    public string? TranslationsFile { get; set; }
    public bool AllowShellEscape { get; set; }
    public bool AutoTerminology { get; set; }
    public bool Mock { get; set; }
    public int CompileTimeoutSeconds { get; set; } = 300;
    public int MaxRepairRounds { get; set; } = 8;
}

public interface ITranslationCache : IDisposable
{
    string? Get(string sourceLanguage, string targetLanguage, string text);
    void Put(string sourceLanguage, string targetLanguage, string text, string translated, string origin = "model");
}
public interface ITranslationProvider
{
    Task<Dictionary<int, string>> TranslateAsync(IReadOnlyList<Fragment> fragments, string sourceLanguage,
        string targetLanguage, IReadOnlyList<TermEntry> terms, string prose, string? retryError,
        CancellationToken cancellationToken);
}
