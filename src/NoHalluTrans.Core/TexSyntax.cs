using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public static class ResourceData
{
    public static string Read(string suffix)
    {
        var assembly = typeof(ResourceData).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static readonly JsonElement Catalog = JsonDocument.Parse(Read("scanner-catalog.json")).RootElement;
    public static readonly JsonElement Prompts = JsonDocument.Parse(Read("prompts.json")).RootElement;
    public static HashSet<string> Set(string key) => Catalog.GetProperty(key).EnumerateArray()
        .Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
}

/// <summary>All offsets are UTF-16 indices into the original .NET string.</summary>
public static class TexSyntax
{
    public sealed record Node(string Kind, int Start, int End, string Text, IReadOnlyList<Node> Children);
    public static readonly HashSet<string> MathEnvironments = ResourceData.Set("MATH_ENVIRONMENTS");
    public static readonly HashSet<string> VerbatimEnvironments = ResourceData.Set("VERBATIM_ENVIRONMENTS");
    public static bool CommentStart(string s, int i) => s[i] == '%' && (i == 0 || s[i - 1] != '\\');
    public static int LineEnd(string s, int i, int end) { var j = s.IndexOf('\n', i, end - i); return j < 0 ? end : j; }
    public static (string Name, int End) Command(string s, int i, int end)
    {
        int j = i + 1;
        if (j >= end) return ("", Math.Min(j, end));
        if (!char.IsAsciiLetter(s[j])) return (s[j].ToString(), j + 1);
        int k = j + 1;
        while (k < end && char.IsAsciiLetter(s[k])) k++;
        return (s[j..k], k);
    }
    public static (string Name, int End)? EnvironmentName(string s, int i, int end)
    {
        while (i < end && s[i] is ' ' or '\t') i++;
        if (i >= end || s[i] != '{') return null;
        int close = s.IndexOf('}', i + 1, end - i - 1);
        if (close < 0) return null;
        string name = s[(i + 1)..close].Trim();
        return name.Length == 0 || name.IndexOfAny(['{', '\n', '\r']) >= 0 ? null : (name, close + 1);
    }
    public static (int End, bool Terminated) Group(string s, int i, int end, bool optional = false)
    {
        if (i >= end || s[i] != (optional ? '[' : '{')) return (Math.Min(i, end), false);
        int depth = 0, braces = 0;
        while (i < end)
        {
            char c = s[i];
            if (CommentStart(s, i)) { i = LineEnd(s, i, end); continue; }
            if (c == '\\')
            {
                var (name, after) = Command(s, i, end);
                if (name is "verb" or "lstinline") { i = InlineVerbatim(s, i, end); continue; }
                if (name == "begin" && EnvironmentName(s, after, end) is { } env && VerbatimEnvironments.Contains(env.Name))
                { i = FindEnvironment(s, env.Name, env.End, end)?.End ?? end; continue; }
                i = after; continue;
            }
            if (optional)
            {
                if (c == '{') braces++;
                else if (c == '}') braces = Math.Max(0, braces - 1);
                else if (c == '[' && braces == 0) depth++;
                else if (c == ']' && braces == 0 && --depth == 0) return (i + 1, true);
            }
            else
            {
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return (i + 1, true);
            }
            i++;
        }
        return (end, false);
    }
    public static int InlineVerbatim(string s, int i, int end)
    {
        var (name, j) = Command(s, i, end);
        if (j < end && s[j] == '*') j++;
        if (name == "lstinline" && j < end && s[j] == '[') j = Group(s, j, end, true).End;
        if (j >= end || s[j] is '\r' or '\n') return j;
        char delimiter = s[j++];
        while (j < end)
        {
            if (s[j] == delimiter) return j + 1;
            if (s[j] is '\r' or '\n') return j;
            j++;
        }
        return end;
    }
    public static (int End, bool Terminated) MathEnd(string s, int i, int end)
    {
        if (i >= end) return (end, false);
        if (s[i] == '$')
        {
            bool doubleDollar = i + 1 < end && s[i + 1] == '$';
            int j = i + (doubleDollar ? 2 : 1);
            while (j < end)
            {
                if (s[j] == '\\') { j += 2; continue; }
                if (s[j] == '$' && (!doubleDollar || j + 1 < end && s[j + 1] == '$'))
                    return (j + (doubleDollar ? 2 : 1), true);
                j++;
            }
            return (end, false);
        }
        string closing = s.AsSpan(i).StartsWith("\\[") ? "\\]" : "\\)";
        int close = s.IndexOf(closing, Math.Min(i + 2, end), end - Math.Min(i + 2, end), StringComparison.Ordinal);
        return close < 0 ? (end, false) : (close + 2, true);
    }
    public static (int Start, int End)? FindEnvironment(string s, string name, int start, int end)
    {
        int depth = 1, i = start;
        while (i < end)
        {
            char c = s[i];
            if (CommentStart(s, i)) { i = LineEnd(s, i, end); continue; }
            if (c == '\\')
            {
                var (command, after) = Command(s, i, end);
                if (command is "verb" or "lstinline") { i = InlineVerbatim(s, i, end); continue; }
                if (command is "begin" or "end" && EnvironmentName(s, after, end) is { } env)
                {
                    if (command == "begin" && VerbatimEnvironments.Contains(env.Name))
                    { i = FindEnvironment(s, env.Name, env.End, end)?.End ?? end; continue; }
                    if (env.Name == name)
                    {
                        depth += command == "begin" ? 1 : -1;
                        if (depth == 0) return (i, env.End);
                    }
                    i = env.End; continue;
                }
                if (command is "[" or "(") { i = MathEnd(s, i, end).End; continue; }
                i = after; continue;
            }
            if (c == '{') { i = Group(s, i, end).End; continue; }
            if (c == '$') { i = MathEnd(s, i, end).End; continue; }
            i++;
        }
        return null;
    }
    public static int Arguments(string s, int i, int end, bool spaces = false)
    {
        if (i < end && s[i] == '*') i++;
        while (i < end)
        {
            if (s[i] is '{' or '[') i = Group(s, i, end, s[i] == '[').End;
            else if (spaces && char.IsWhiteSpace(s[i]))
            {
                int j = i; while (j < end && char.IsWhiteSpace(s[j])) j++;
                if (j < end && s[j] is '{' or '[') i = j; else break;
            }
            else break;
        }
        return i;
    }
    public static bool HasProse(string s)
    {
        for (int i = 0; i < s.Length;)
        {
            if (s[i] == '\\') { i = Command(s, i, s.Length).End; continue; }
            if (CommentStart(s, i)) { i = LineEnd(s, i, s.Length); continue; }
            if (s[i] == '$') { i = MathEnd(s, i, s.Length).End; continue; }
            if (Rune.IsLetter(Rune.GetRuneAt(s, i))) return true;
            i += char.IsSurrogatePair(s, i) ? 2 : 1;
        }
        return false;
    }
    public static string WithoutComments(string s) => Regex.Replace(s, @"(?m)(^|[^\\])%[^\r\n]*", "$1");
    public static List<Node> Tokenize(string text, int start = 0, int? limit = null, int nesting = 0)
    {
        if (nesting > 128) throw new FormatException("LaTeX 分组嵌套过深。");
        int end = Math.Min(limit ?? text.Length, text.Length), i = Math.Max(0, start);
        List<Node> nodes = [];
        void Add(string kind, int j, IReadOnlyList<Node>? children = null)
        { nodes.Add(new(kind, i, j, text[i..j], children ?? [])); i = j; }
        while (i < end)
        {
            char ch = text[i];
            if (" \t\r\n\f\v".Contains(ch))
            { int j = i + 1; while (j < end && " \t\r\n\f\v".Contains(text[j])) j++; Add("space", j); continue; }
            if (CommentStart(text, i)) { Add("comment", LineEnd(text, i, end)); continue; }
            if (ch == '\\')
            {
                var (name, after) = Command(text, i, end);
                if (name is "verb" or "lstinline") { Add("verbatim", InlineVerbatim(text, i, end)); continue; }
                if (name == "begin" && EnvironmentName(text, after, end) is { } env)
                {
                    var found = FindEnvironment(text, env.Name, env.End, end);
                    int afterEnd = found?.End ?? end;
                    if (VerbatimEnvironments.Contains(env.Name)) Add("verbatim", afterEnd);
                    else if (MathEnvironments.Contains(env.Name)) Add("math", afterEnd);
                    else Add("env", afterEnd, Tokenize(text, env.End, found?.Start ?? end, nesting + 1));
                    continue;
                }
                if (name is "[" or "(") { Add("math", MathEnd(text, i, end).End); continue; }
                Add("command", after); continue;
            }
            if (ch == '$') { Add("math", MathEnd(text, i, end).End); continue; }
            if (ch == '{')
            {
                var group = Group(text, i, end);
                Add("group", group.End, Tokenize(text, i + 1, group.Terminated ? group.End - 1 : group.End, nesting + 1)); continue;
            }
            int next = i;
            while (next < end && !"\\${}".Contains(text[next]) && !CommentStart(text, next)) next++;
            if (next == i) next++;
            Add("text", next);
        }
        return nodes;
    }
}

public static class MarkerProtocol
{
    public const string Unknown = "translation contains an unknown placeholder";
    public const string InMath = "translation wrapped a placeholder in math delimiters";
    public static readonly Regex Pattern = new(@"⟦TEX_(\d+)⟧", RegexOptions.Compiled);
    private static readonly Regex MathPattern = new(@"\$\$.+?\$\$|\$[^$]+?\$|\\\[.+?\\\]|\\\(.+?\\\)", RegexOptions.Singleline);
    public static string Make(int index) => $"⟦TEX_{index}⟧";
    public static void Validate(int count, string text, bool allowOrderSwap = true)
    {
        if (MathPattern.Matches(text).Any(m => Pattern.IsMatch(m.Value))) throw new FormatException(InMath);
        var actual = Pattern.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        if (allowOrderSwap) Array.Sort(actual);
        if (!actual.SequenceEqual(Enumerable.Range(0, count))) throw new FormatException(Unknown);
    }
    public static string Expand(string template, IReadOnlyList<string> values) => Pattern.Replace(template, m =>
        int.TryParse(m.Groups[1].Value, out int i) && i < values.Count ? values[i] : throw new FormatException(Unknown));
}
