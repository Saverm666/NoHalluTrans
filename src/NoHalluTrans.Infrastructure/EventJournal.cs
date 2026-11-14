using System.Text.Json;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class EventJournal : IProgress<PipelineEvent>, IDisposable
{
    private readonly StreamWriter writer;
    private readonly IProgress<PipelineEvent>? downstream;
    private readonly object gate = new();
    private int sequence;
    public EventJournal(string path, IProgress<PipelineEvent>? downstream)
    { writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true }; this.downstream = downstream; }
    public void Emit(string type, IDictionary<string, object?>? fields = null)
    {
        lock (gate)
        {
            Dictionary<string, object?> record = new()
            {
                ["protocol_version"] = 1, ["seq"] = ++sequence, ["type"] = type,
                ["product"] = "hjfy-latex-repro", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d
            };
            if (fields is not null) foreach (var pair in fields) if (pair.Value is not null) record[pair.Key] = pair.Value;
            writer.WriteLine(JsonSerializer.Serialize(record));
        }
    }
    public void Report(PipelineEvent value)
    {
        Emit(value.Type, new Dictionary<string, object?>
        {
            ["stage_id"] = value.StageId, ["message"] = value.Message, ["completed"] = value.Completed,
            ["total"] = value.Total, ["code"] = value.Code, ["fragment"] = value.Fragment,
            ["fragments"] = value.Fragments, ["translated"] = value.Translated, ["cached"] = value.Cached, ["fallback"] = value.Fallback,
            ["severity"] = value.Type == "issue" ? "warning" : null, ["actionable"] = value.Type == "issue" ? true : null
        });
        downstream?.Report(value);
    }
    public void Dispose() => writer.Dispose();
}
