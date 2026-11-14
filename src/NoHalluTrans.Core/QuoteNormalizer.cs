using System.Text;
using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public static class QuoteNormalizer
{
    private const string Cjk = @"[\u3000-\u303f\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af\uff00-\uffef]";
    public static bool LooksCjk(string text) => text.Length > 0 && Regex.Matches(text, Cjk).Count / (double)text.EnumerateRunes().Count() >= .05;
    public static string Normalize(string text, string language = "zh-Hans", string? script = null)
    {
        string working = Regex.Replace(text, "``(.+?)''", "“$1”", RegexOptions.Singleline);
        StringBuilder result = new(); bool opening = true;
        for (int i = 0; i < working.Length; i++)
        {
            char c = working[i];
            if (c == '\\') { result.Append(c); if (i + 1 < working.Length) result.Append(working[++i]); continue; }
            if (c == '"') { result.Append(opening ? '“' : '”'); opening = !opening; }
            else result.Append(c);
        }
        string value = result.ToString();
        bool cjk = script == "cjk" || script is null && new[] { "zh", "ja", "ko", "chinese", "japanese", "korean" }.Any(language.ToLowerInvariant().StartsWith);
        if (cjk || LooksCjk(value)) return value;
        return value.Replace("“", "``").Replace("”", "''");
    }
    public static string GuardControlWords(string text) => Regex.Replace(text, @"(\\[A-Za-z]+\*?)(?=" + Cjk + ")", "$1{}");
}
