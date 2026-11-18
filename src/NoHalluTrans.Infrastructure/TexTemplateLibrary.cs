using System.Text.RegularExpressions;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

internal sealed class TexTemplateLibrary(string? root)
{
    internal enum Resolution { Unavailable, Restored, Ambiguous }

    private static readonly Regex DependencyPath = new(@"^(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*\.(?:cls|sty|bst|def|fd|cfg|clo|bbx|cbx|lbx|ldf|tex|ltx|enc|map|tfm|vf|otf|ttf|ttc|pfb|ist)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private Dictionary<string, string[]>? filesByName;

    internal bool HasCandidate(string requested, CancellationToken token) => Find(requested, token).Length > 0;

    internal Resolution Restore(PreparedProject project, string requested, IProgress<PipelineEvent>? progress, CancellationToken token)
    {
        string[] matches = Find(requested, token);
        if (matches.Length == 0) return Resolution.Unavailable;
        if (matches.Length > 1)
        {
            string candidates = string.Join("、", matches.Take(5).Select(path => Path.GetRelativePath(root!, path)));
            if (matches.Length > 5) candidates += $" 等 {matches.Length} 个文件";
            progress?.Report(new("issue", "packages",
                $"模板库中有多个 {requested}：{candidates}。请将正确版本放入论文源码包，或在模板库中只保留所需版本后重试。",
                Code: "template-library-ambiguous"));
            return Resolution.Ambiguous;
        }

        string relative = Normalize(requested);
        string target = Path.Combine(project.Root, relative);
        string parent = project.Root;
        foreach (string folder in relative.Split('/').SkipLast(1))
        {
            parent = Path.Combine(parent, folder);
            if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                return Resolution.Unavailable;
        }
        if (File.Exists(target) || Directory.Exists(target)) return Resolution.Unavailable;
        Directory.CreateDirectory(parent);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(matches[0], temporary);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        progress?.Report(new("stage", "packages", $"已从本地模板库补齐 {requested}（{Path.GetRelativePath(root!, matches[0])}）"));
        return Resolution.Restored;
    }

    private string[] Find(string requested, CancellationToken token)
    {
        string relative = Normalize(requested);
        if (root is null || !DependencyPath.IsMatch(relative) || relative.Split('/').Any(part => part.EndsWith('.'))) return [];
        filesByName ??= Index(root, token);
        return filesByName.GetValueOrDefault(Path.GetFileName(relative)) ?? [];
    }

    internal static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static Dictionary<string, string[]> Index(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return new(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        List<string> candidates = [];
        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            token.ThrowIfCancellationRequested();
            if (DependencyPath.IsMatch(Path.GetFileName(path))) candidates.Add(path);
        }
        return candidates.GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
