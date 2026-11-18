using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public sealed record TexDiagnostic(string Kind, string Message, string? File = null, int? Line = null);

public static class CompileDiagnostics
{
    private static readonly Regex FileLine = new(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+\.(?:tex|sty|cls|clo|def|cfg|bbl|aux|ltx|fd|bbx|cbx|ldf)):(?<line>\d+):\s*(?<message>.+)$");
    private static readonly Regex Warning = new(@"^(?:(?:LaTeX|LaTeX Font|Package\s+\S+|Class\s+\S+|pdfTeX|LuaTeX|XeTeX)\s+Warning:|(?:Overfull|Underfull)\s+\\[hv]box|Missing character:|\*\* WARNING \*\*)");
    public static List<TexDiagnostic> Parse(string log)
    {
        List<TexDiagnostic> diagnostics = [];
        string[] lines = log.Replace("\r\n", "\n").Split('\n');
        HashSet<string> errors = [];
        string Key(string value) => Regex.Replace(value.Trim().TrimEnd('.'), @"\s+", " ").ToLowerInvariant();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            var m = FileLine.Match(line);
            if (m.Success)
            {
                string message = m.Groups["message"].Value.Trim();
                string key = Key(message) + "|" + m.Groups["file"].Value + "|" + m.Groups["line"].Value;
                if (errors.Add(key)) diagnostics.Add(new("error", message, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value)));
                continue;
            }
            if (line.StartsWith("xdvipdfmx:warning: Could not locate a virtual/physical font for TFM ", StringComparison.Ordinal)
                && log.Contains("xdvipdfmx:fatal: Cannot proceed without .vf or \"physical\" font", StringComparison.Ordinal))
            {
                if (errors.Add(Key(line))) diagnostics.Add(new("error", line["xdvipdfmx:warning: ".Length..].Trim()));
                continue;
            }
            if (line.StartsWith("xdvipdfmx:fatal:", StringComparison.Ordinal))
            {
                if (errors.Add(Key(line))) diagnostics.Add(new("error", line.Trim()));
                continue;
            }
            if (line.StartsWith('!'))
            {
                string message = line[1..].Trim(); if (message.Length == 0 || errors.Any(k => k.StartsWith(Key(message) + "|"))) continue;
                int? number = null;
                foreach (string following in lines.Skip(i + 1).Take(6))
                {
                    var hint = Regex.Match(following, @"^l\.(\d+)");
                    if (hint.Success) { number = int.Parse(hint.Groups[1].Value); break; }
                    if (following.StartsWith('!') || FileLine.IsMatch(following)) break;
                }
                if (errors.Add(Key(message) + "||" + number)) diagnostics.Add(new("error", message, Line: number));
            }
            else if (Warning.IsMatch(line) && (diagnostics.Count == 0 || Key(diagnostics[^1].Message) != Key(line))) diagnostics.Add(new("warning", line.Trim()));
        }
        return diagnostics;
    }
    public static bool NeedsRerun(string log) => new[] { "Rerun to get cross-references right", "Rerun LaTeX", "Label(s) may have changed" }.Any(log.Contains);
}
