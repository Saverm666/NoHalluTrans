using System.Text;
using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public sealed class FragmentScanner
{
    private static readonly HashSet<string> Headings = ResourceData.Set("HEADING_COMMANDS");
    private static readonly HashSet<string> Captions = ResourceData.Set("CAPTION_COMMANDS");
    private static readonly HashSet<string> Tables = ResourceData.Set("TABULAR_ENVIRONMENTS");
    private static readonly HashSet<string> Rows = ResourceData.Set("ROW_COMMANDS");
    private static readonly HashSet<string> TextCommands = ResourceData.Set("TEXT_COMMANDS");
    private static readonly HashSet<string> Opaque = ResourceData.Set("OPAQUE_COMMANDS");
    private static readonly HashSet<string> Definitions = ResourceData.Set("DEFINITION_COMMANDS");
    private static readonly Dictionary<string, HashSet<int>> ProseArgs = ResourceData.Catalog.GetProperty("PROSE_ARG_COMMANDS")
        .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetInt32()).ToHashSet());
    private static readonly Dictionary<string, int> EnvArgs = ResourceData.Catalog.GetProperty("ENV_BRACE_ARGS")
        .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
    private static readonly Regex Blank = new(@"(?:\r\n|\r|\n)[ \t]*(?:\r\n|\r|\n)");
    private readonly string text;
    private readonly string file;
    private readonly List<Fragment> fragments;
    private readonly List<string> warnings;
    private sealed record Span(int Start, int End, bool Skip);
    private FragmentScanner(string text, string file, List<Fragment> fragments, List<string> warnings)
        => (this.text, this.file, this.fragments, this.warnings) = (text, file, fragments, warnings);

    public static List<Fragment> Scan(string text, string file = "main.tex", int firstIndex = 0, List<string>? warnings = null)
    {
        var result = new List<Fragment>();
        var scanner = new FragmentScanner(text, file, result, warnings ?? []);
        int start = 0, end = text.Length, preambleEnd = -1;
        foreach (Match m in Regex.Matches(text, @"\\begin\s*\{document\}"))
        {
            int line = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            bool comment = false;
            for (int k = line; k < m.Index; k++) if (TexSyntax.CommentStart(text, k)) { comment = true; break; }
            if (comment) continue;
            start = m.Index + m.Length;
            preambleEnd = m.Index;
            end = TexSyntax.FindEnvironment(text, "document", start, end)?.Start ?? end;
            break;
        }
        if (preambleEnd >= 0)
        {
            foreach (Match title in Regex.Matches(text[..preambleEnd], @"\\(?:title|subtitle)\*?\s*"))
            {
                int argument = title.Index + title.Length;
                while (argument < preambleEnd && text[argument] == '[')
                    argument = TexSyntax.Group(text, argument, preambleEnd, true).End;
                if (argument >= preambleEnd || text[argument] != '{') continue;
                var group = TexSyntax.Group(text, argument, preambleEnd);
                if (group.Terminated) scanner.Region(argument + 1, group.End - 1, "title");
            }
        }
        scanner.Region(start, end);
        return result.Select((f, i) => f with { Index = firstIndex + i }).ToList();
    }
    private List<Span> Spans(int start, int end)
    {
        List<Span> spans = [];
        int i = start;
        while (i < end)
        {
            char ch = text[i];
            if (TexSyntax.CommentStart(text, i))
            { int j = TexSyntax.LineEnd(text, i, end); spans.Add(new(i, j, true)); i = j; continue; }
            if (ch == '\\')
            {
                var (name, after) = TexSyntax.Command(text, i, end);
                if (name is "verb" or "lstinline")
                { int j = TexSyntax.InlineVerbatim(text, i, end); spans.Add(new(i, j, true)); i = j; continue; }
                if (name == "begin")
                {
                    if (TexSyntax.EnvironmentName(text, after, end) is { } env &&
                        (TexSyntax.VerbatimEnvironments.Contains(env.Name) || TexSyntax.MathEnvironments.Contains(env.Name)))
                    {
                        int j = TexSyntax.FindEnvironment(text, env.Name, env.End, end)?.End ?? end;
                        spans.Add(new(i, j, TexSyntax.VerbatimEnvironments.Contains(env.Name))); i = j; continue;
                    }
                    i = after; continue;
                }
                if (name is "[" or "(")
                { int j = TexSyntax.MathEnd(text, i, end).End; spans.Add(new(i, j, false)); i = j; continue; }
                if (Definitions.Contains(name))
                {
                    int j = after;
                    while (j < end && text[j] is ' ' or '\t') j++;
                    if (j < end && text[j] == '\\')
                    {
                        j = TexSyntax.Command(text, j, end).End;
                        while (j < end && text[j] is not ('{' or '\r' or '\n' or '%'))
                            j = text[j] == '\\' ? TexSyntax.Command(text, j, end).End : j + 1;
                    }
                    j = TexSyntax.Arguments(text, j, end, true);
                    spans.Add(new(i, j, false)); i = j; continue;
                }
                if (ProseArgs.TryGetValue(name, out var prose))
                {
                    int j = after, arg = 0;
                    if (j < end && text[j] == '*') j++;
                    while (j < end && text[j] is '{' or '[')
                    {
                        bool optional = text[j] == '[';
                        var group = TexSyntax.Group(text, j, end, optional);
                        if (!optional && prose.Contains(arg)) spans.AddRange(Spans(j + 1, group.Terminated ? group.End - 1 : group.End));
                        else spans.Add(new(j, group.End, false));
                        if (!optional) arg++;
                        j = group.End;
                    }
                    i = j; continue;
                }
                if (Opaque.Contains(name))
                { int j = TexSyntax.Arguments(text, after, end); spans.Add(new(i, j, false)); i = j; continue; }
                if (name.Length == 1 || Headings.Contains(name) || Captions.Contains(name) || Rows.Contains(name) ||
                    TextCommands.Contains(name) || name is "item" or "par") { i = after; continue; }
                if (after < end && text[after] is '{' or '[')
                {
                    if (TryProseWrapper(after, end, out int bodyStart, out int bodyEnd, out int groupEnd, out _))
                    { spans.AddRange(Spans(bodyStart, bodyEnd)); i = groupEnd; continue; }
                    int j = TexSyntax.Arguments(text, after, end); spans.Add(new(i, j, false)); i = j; continue;
                }
                i = after; continue;
            }
            if (ch == '$')
            { int j = TexSyntax.MathEnd(text, i, end).End; spans.Add(new(i, j, false)); i = j; continue; }
            i++;
        }
        return spans;
    }
    private void Emit(int start, int end, string kind, List<Span> spans)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        List<Span> inside = [];
        foreach (var span in spans)
        {
            if (span.End <= start) continue;
            if (span.Start >= end) break;
            if (span.Start < start) { start = span.End; continue; }
            if (span.End > end) { end = span.Start; break; }
            inside.Add(span);
        }
        if (start >= end) return;
        StringBuilder template = new(), prose = new();
        List<string> values = [];
        int position = start;
        foreach (var span in inside)
        {
            string plain = text[position..span.Start];
            template.Append(plain).Append(MarkerProtocol.Make(values.Count)); prose.Append(plain);
            values.Add(text[span.Start..span.End]); position = span.End;
        }
        template.Append(text[position..end]); prose.Append(text[position..end]);
        if (!TexSyntax.HasProse(prose.ToString())) return;
        string source = text[start..end];
        if (MarkerProtocol.Expand(template.ToString(), values) != source) throw new InvalidOperationException("片段扫描不能无损还原。");
        fragments.Add(new(fragments.Count, file, start, end, source, template.ToString(), kind, values));
    }
    private void Region(int start, int end, string kind = "paragraph", bool cells = false, int nesting = 0)
    {
        if (nesting > 128) throw new FormatException("LaTeX 环境嵌套过深。");
        var spans = Spans(start, end);
        int sp = 0, i = start, chunk = start, depth = 0;
        string currentKind = kind;
        while (i < end)
        {
            while (sp < spans.Count && spans[sp].End <= i) sp++;
            if (sp < spans.Count && spans[sp].Start <= i)
            {
                var span = spans[sp++];
                if (span.Start < i) { i = span.End; continue; }
                if (span.Skip) { Emit(chunk, i, currentKind, spans); chunk = span.End; }
                i = span.End; continue;
            }
            if (text[i] is '\r' or '\n')
            {
                var blank = Blank.Match(text, i);
                if (blank.Success && blank.Index == i && blank.Index + blank.Length <= end)
                { Emit(chunk, i, currentKind, spans); i += blank.Length; chunk = i; currentKind = kind; continue; }
            }
            char ch = text[i];
            if (ch == '{') { depth++; i++; continue; }
            if (ch == '}') { depth = Math.Max(0, depth - 1); i++; continue; }
            if (ch == '\\')
            {
                var (name, after) = TexSyntax.Command(text, i, end);
                if (name == "begin" && TexSyntax.EnvironmentName(text, after, end) is { } env)
                {
                    int body = env.End;
                    while (body < end && text[body] == '[') body = TexSyntax.Group(text, body, end, true).End;
                    for (int a = 0; a < EnvArgs.GetValueOrDefault(env.Name); a++)
                        if (body < end && text[body] == '{') body = TexSyntax.Group(text, body, end).End; else break;
                    var found = TexSyntax.FindEnvironment(text, env.Name, body, end);
                    if (!TexSyntax.MathEnvironments.Contains(env.Name) && !TexSyntax.VerbatimEnvironments.Contains(env.Name))
                        Region(body, found?.Start ?? end, Tables.Contains(env.Name) ? "cell" : "paragraph", Tables.Contains(env.Name), nesting + 1);
                    // The reference emits recursive environment fragments before the pending outer chunk.
                    Emit(chunk, i, currentKind, spans);
                    if (found is null) warnings.Add($"{file}: unterminated \\begin{{{env.Name}}}");
                    i = found?.End ?? end; chunk = i; currentKind = kind; continue;
                }
                if (Headings.Contains(name) || Captions.Contains(name))
                {
                    Emit(chunk, i, currentKind, spans);
                    int j = after;
                    while (j < end && text[j] == '*') j++;
                    while (j < end && text[j] == '[') j = TexSyntax.Group(text, j, end, true).End;
                    if (name == "captionof")
                    {
                        if (j < end && text[j] == '{') j = TexSyntax.Group(text, j, end).End;
                        while (j < end && text[j] == '[') j = TexSyntax.Group(text, j, end, true).End;
                    }
                    if (j < end && text[j] == '{')
                    {
                        var group = TexSyntax.Group(text, j, end);
                        Emit(j + 1, group.Terminated ? group.End - 1 : group.End, Headings.Contains(name) ? "heading" : "caption", spans);
                        j = group.End;
                    }
                    i = j; chunk = i; currentKind = kind; continue;
                }
                if (name == "item" && !cells)
                {
                    Emit(chunk, i, currentKind, spans); i = after;
                    while (i < end && text[i] == '*') i++;
                    while (i < end && text[i] is '[' or '<')
                    {
                        if (text[i] == '[') i = TexSyntax.Group(text, i, end, true).End;
                        else { int close = text.IndexOf('>', i + 1, end - i - 1); i = close < 0 ? end : close + 1; }
                    }
                    chunk = i; currentKind = "item"; continue;
                }
                if (name == "par" || cells && (name == "\\" || Rows.Contains(name)))
                {
                    Emit(chunk, i, cells ? "cell" : currentKind, spans);
                    i = TexSyntax.Arguments(text, after, end); chunk = i; currentKind = kind; continue;
                }
                if (!Opaque.Contains(name) && !Definitions.Contains(name) && !ProseArgs.ContainsKey(name) &&
                    !TextCommands.Contains(name) && name.Length > 1 && after < end && text[after] is '{' or '[' &&
                    TryProseWrapper(after, end, out int bodyStart, out int bodyEnd, out int groupEnd, out bool isBlock))
                {
                    if (isBlock || chunk == i)
                    {
                        Emit(chunk, i, currentKind, spans);
                        Region(bodyStart, bodyEnd, currentKind, cells, nesting + 1);
                        i = groupEnd; chunk = i; currentKind = kind; continue;
                    }
                    i = groupEnd; continue;
                }
                i = after; continue;
            }
            if (cells && ch == '&' && depth == 0)
            { Emit(chunk, i, "cell", spans); i++; chunk = i; continue; }
            i++;
        }
        Emit(chunk, end, currentKind, spans);
    }
    private bool TryProseWrapper(int after, int end, out int bodyStart, out int bodyEnd, out int groupEnd, out bool isBlock)
    {
        bodyStart = bodyEnd = groupEnd = 0;
        isBlock = false;
        int j = after;
        while (j < end && text[j] == '[') j = TexSyntax.Group(text, j, end, true).End;
        if (j >= end || text[j] != '{') return false;
        var group = TexSyntax.Group(text, j, end);
        if (!group.Terminated || (group.End < end && text[group.End] == '{')) return false;
        bodyStart = j + 1;
        bodyEnd = group.End - 1;
        groupEnd = group.End;
        string body = text[bodyStart..bodyEnd];
        if (!TexSyntax.HasProse(body)) return false;
        isBlock = Blank.IsMatch(body) || Regex.IsMatch(body, @"\\(?:par|section|subsection|subsubsection|paragraph|subparagraph|begin|item)\b");
        return isBlock || Regex.Matches(body, @"\b[A-Za-z]{2,}\b").Count >= 3;
    }
    public static string Reassemble(string text, IEnumerable<Fragment> fragments, IReadOnlyDictionary<int, string> translations,
        string targetLanguage = "zh-Hans", bool normalizeQuotes = true)
    {
        foreach (var f in fragments.OrderByDescending(f => f.Start))
        {
            if (f.End > text.Length || text[f.Start..f.End] != f.Source) throw new InvalidOperationException("源码已变化，无法安全回写译文。");
            if (!translations.TryGetValue(f.Index, out var value)) continue;
            MarkerProtocol.Validate(f.Values.Count, value);
            if (normalizeQuotes) value = QuoteNormalizer.Normalize(value, targetLanguage);
            string expanded = QuoteNormalizer.GuardControlWords(MarkerProtocol.Expand(value, f.Values));
            text = text[..f.Start] + expanded + text[f.End..];
        }
        return text;
    }
}
