using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

public sealed class AppSettings
{
    public ModelSettings Model { get; set; } = new();
    public List<TermEntry> Terms { get; set; } = [];
    public string OutputDirectory { get; set; } = "";
    public string PdfLayout { get; set; } = "target-only";
    public string WorkRoot { get; set; } = "";
    public bool AutoInstallPackages { get; set; } = true;
    public bool PackagePolicyAcknowledged { get; set; }
    public string? ProtectedApiKey { get; set; }
}

public sealed class SettingsStore
{
    public string DataDirectory { get; }
    private readonly string portableRoot;
    private static readonly HashSet<string> PathProperties = ["output_directory", "work_root", "work_directory", "output_pdf", "original_pdf", "source", "translations_file"];
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string HistoryPath => Path.Combine(DataDirectory, "tasks.json");
    public string CachePath => Path.Combine(DataDirectory, "cache", "translations.sqlite3");
    public SettingsStore(string? directory = null)
    {
        DataDirectory = Path.GetFullPath(directory ?? PortablePaths.Data);
        portableRoot = Path.GetDirectoryName(DataDirectory)!;
        Directory.CreateDirectory(DataDirectory);
    }
    public AppSettings Load()
    {
        AppSettings settings = File.Exists(SettingsPath)
            ? JsonSerializer.Deserialize<AppSettings>(ReadPaths(SettingsPath), JsonDefaults.Options) ?? new() : new();
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory)) settings.OutputDirectory = Path.Combine(portableRoot, "output");
        if (settings.WorkRoot == "")
            settings.WorkRoot = Path.Combine(portableRoot, "work");
        if (settings.ProtectedApiKey is not null && OperatingSystem.IsWindows())
        {
            try { settings.Model.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedApiKey), null, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { settings.Model.ApiKey = ""; }
        }
        return settings;
    }
    public void Save(AppSettings settings)
    {
        settings.ProtectedApiKey = string.IsNullOrEmpty(settings.Model.ApiKey) ? null : OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.Model.ApiKey), null, DataProtectionScope.CurrentUser))
            : throw new PlatformNotSupportedException("密钥存储仅支持 Windows。");
        WritePaths(SettingsPath, settings);
    }
    public T LoadHistory<T>() where T : new() => File.Exists(HistoryPath)
        ? JsonSerializer.Deserialize<T>(ReadPaths(HistoryPath), JsonDefaults.Options) ?? new T() : new T();
    public void SaveHistory<T>(T value) => WritePaths(HistoryPath, value);

    private string ReadPaths(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        TransformPaths(node, false);
        return node!.ToJsonString(JsonDefaults.Options);
    }
    private void WritePaths<T>(string path, T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
        TransformPaths(node, true);
        FileStore.WriteText(path, node!.ToJsonString(JsonDefaults.Options));
    }
    private void TransformPaths(JsonNode? node, bool saving)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) TransformPaths(item, saving);
        }
        else if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (PathProperties.Contains(property.Key) && property.Value is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    if (saving && Path.IsPathFullyQualified(path))
                    {
                        string relative = Path.GetRelativePath(portableRoot, path);
                        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative))
                            obj[property.Key] = "./" + relative.Replace(Path.DirectorySeparatorChar, '/');
                    }
                    else if (!saving && path.StartsWith("./", StringComparison.Ordinal))
                        obj[property.Key] = Path.GetFullPath(path, portableRoot);
                }
                else TransformPaths(property.Value, saving);
            }
        }
    }
}
