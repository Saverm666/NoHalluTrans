using System.Text;
using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public static class TranslationValidation
{
    private static readonly Regex Markdown = new(@"```|(?<!`)\*\*(?!\s)|(?<!\w)__(?!\s)|\[[^\]\n]{1,80}\]\([^)\n]{1,200}\)");
    private static readonly Regex Math = new(@"\$[^$]*\$|\\\(|\\\)|\\\[|\\\]");
    private static readonly Regex Placeholder = new(@"⟦TEX_[A-Za-z0-9_]+⟧");
    private static readonly Regex Dimension = new(@"(?<![\w.])([-+]?\d*\.?\d+)\s*(pt|bp|sp|mm|cm|in|em|ex)\b");
    private static readonly Regex CommandArg = new(@"\\[A-Za-z@]+(?:\s*\[[^\]]*\])?(?:\s*\{[^{}]*\}){1,3}");
    private static readonly Regex TexStructure = new(@"\\(?:[A-Za-z@]+|.)|[{}]");
    private static readonly Regex Refusal = new(@"\b(?:I(?:'m| am) sorry|I cannot|I can't|as an AI|无法(?:完成|协助|翻译)|抱歉)", RegexOptions.IgnoreCase);
    public static List<TranslationIssue> Check(string source, string translation, string targetLanguage = "zh-Hans",
        bool checkMarkdown = true, bool checkScripts = true)
    {
        List<TranslationIssue> problems = [];
        void Add(string code, string message) => problems.Add(new(code, message));
        if (string.IsNullOrWhiteSpace(translation)) { Add("omitted-too-much", "translation omitted too much content"); return problems; }
        if (checkMarkdown && Markdown.IsMatch(translation)) Add("markdown-syntax", "translation contains Markdown syntax");
        int src = source.Trim().EnumerateRunes().Count(), dst = translation.Trim().EnumerateRunes().Count();
        double floor = targetLanguage.StartsWith("zh") || targetLanguage.StartsWith("ja") || targetLanguage.StartsWith("ko") ? .10 : .25;
        if (src >= 40)
        {
            if (dst < src * floor) Add("omitted-too-much", "translation omitted too much content");
            else if (dst > src * 6) Add("implausibly-long", "translation is implausibly long");
        }
        if (checkScripts && targetLanguage.StartsWith("zh-Hans"))
        {
            if (Regex.IsMatch(translation, @"[\u3040-\u30ff]")) Add("japanese", "translation unexpectedly uses Japanese");
            if (Regex.IsMatch(translation, @"[\uac00-\ud7af]")) Add("korean", "translation unexpectedly uses Korean");
            if (translation.Count(c => "們這個說時會對學實現點為與從關發體樣問題".Contains(c)) >= 3)
                Add("traditional-chinese", "translation unexpectedly uses Traditional Chinese");
        }
        else if (checkScripts && targetLanguage.StartsWith("zh-Hant") && Regex.IsMatch(translation, @"[\u3040-\u30ff]"))
            Add("japanese", "translation unexpectedly uses Japanese");
        if (Math.Matches(translation).Count + Placeholder.Matches(translation).Count < Math.Matches(source).Count + Placeholder.Matches(source).Count)
            Add("fewer-math", "translation has fewer math expressions");
        if (translation.Count(c => c == '$') % 2 != 0) Add("invalid-math-delims", "translation introduced invalid math delimiters");
        int delta = BraceDelta(translation) - BraceDelta(source);
        if (delta != 0) Add("omitted-required-syntax", "translation omitted required syntax " + (delta > 0 ? "closing brace" : "opening brace"));
        var markers = Placeholder.Matches(source).Select(m => m.Value).ToArray();
        if (markers.Length > 0 && !markers.SequenceEqual(Placeholder.Matches(translation).Select(m => m.Value)))
            Add("unrestored-placeholder", "translation contains an unrestored placeholder");
        if (Refusal.IsMatch(translation)) Add("refusal", "translation looks like a refusal");
        if (CommandDimensions(translation) < CommandDimensions(source)) Add("translated-dimension", "translation may have translated a TeX dimension");
        if (translation.TrimStart().StartsWith('[') && !source.TrimStart().StartsWith('[')) Add("invalid-leading-option", "translation introduced an invalid leading option");
        return problems;
    }
    public static void Validate(Fragment fragment, string value, string language)
    {
        MarkerProtocol.Validate(fragment.Values.Count, value);
        var problems = Check(fragment.Template, value, language);
        // Slot identity is preserved even when the target grammar reorders slots.
        var first = problems.FirstOrDefault(p => p.Code != "unrestored-placeholder");
        if (first is not null) throw new FormatException(first.Message);
        if (!SameTexStructureMultiset(fragment.Template, value))
            throw new FormatException("译文改变了 TeX 命令或分组参数结构。");
    }
    private static bool SameTexStructureMultiset(string source, string translation)
    {
        var expected = TexStructure.Matches(source).Select(m => m.Value)
            .GroupBy(v => v, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var actual = TexStructure.Matches(translation).Select(m => m.Value)
            .GroupBy(v => v, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return expected.Count == actual.Count
            && expected.All(kv => actual.TryGetValue(kv.Key, out int count) && count == kv.Value);
    }
    private static int BraceDelta(string text)
    {
        int delta = 0;
        for (int i = 0; i < text.Length; i++)
        { if (text[i] == '\\') { i++; continue; } if (text[i] == '{') delta++; if (text[i] == '}') delta--; }
        return delta;
    }
    private static int CommandDimensions(string text) => CommandArg.Matches(text).Sum(m =>
        Dimension.Matches(m.Value).Count(d => RealDimension(d, m.Value)) + Placeholder.Matches(m.Value).Count);
    private static bool RealDimension(Match m, string text)
    {
        if (m.Groups[2].Value != "in" || !m.Value.Any(c => c is ' ' or '\t')) return true;
        int i = m.Index + m.Length; while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i >= text.Length || text[i] != '⟦' && !char.IsLetter(text[i]);
    }
}
