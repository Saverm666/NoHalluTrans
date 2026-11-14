using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public sealed record RepairContext(string Engine, string Log, string TargetLanguage, string? ProjectSource = null,
    string? SourcePath = null, string? ProjectRoot = null);

public sealed record RepairRule(string Name, string Description, string[] Triggers, string? Tex,
    string? Find = null, string? Replace = null, string TriggerKind = "log", string? RequiresClass = null,
    bool Manual = false, bool Enabled = true, string? DisabledReason = null, bool Retired = false, string? Handler = null);

public sealed partial class RepairRules
{
    public IReadOnlyList<RepairRule> Rules { get; }
    public IReadOnlyList<RepairRule> AllRules { get; }
    public static bool CanRepeat(RepairRule rule) => rule.Name == "rewrite-tcolorbox-leading-break" || rule.Handler is "px_dimension" or "revision_natbib_aux" or "revision_unicode_script" or "revision_aas_script" or "revision_outputbox";
    public static bool RepairsLocalStyles(RepairRule rule) => rule.Handler is "revision_kpfonts_classic" or "revision_kpfonts_slanted" or "revision_cjk_options" or "revision_frontmatter_font" or "revision_libertinus" or "revision_robo_font" or "revision_outputbox";
    public static bool AllowsSuccessfulCompile(RepairRule rule) => rule.Handler is "revision_missing_glyph" or "revision_theorem_text" or "revision_titlesec_anchor" or "revision_appendix_dest";
    public static bool RequiresSuccessfulCompile(RepairRule rule) => AllowsSuccessfulCompile(rule) && rule.Handler != "revision_missing_glyph";
    public static bool RequiresProjectSourceEvaluation(RepairRule rule) =>
        RevisionNeedsProject(rule) || rule.Name == "rewrite-missing-textls" || rule.Handler is "pdfinfo" or "graphics_valign" or "xypdf_luatex85" or "state_delimiter" or "bbl_language" or "px_dimension";
    public static bool RequiresPerFileEvaluation(RepairRule rule) =>
        RevisionPerFile(rule) || rule.Handler == "px_dimension" || !RequiresProjectSourceEvaluation(rule) && (rule.TriggerKind == "source" || rule.Find is not null ||
        rule.Handler is "braced_definition" or "quantumarticle_pdfoutput" or "microtype_expansion" or "pdftex_primitive" or "text_underscore" or "pict2e_driver" or "tikz_polar_brace" or "hyphenation_tail" ||
        rule.Name is "subfiles-ctex" or "mdpi-pict2e-driver" or "cjk-ctex-package-conflict");

    public RepairRules()
    {
        List<RepairRule> rules = [];
        foreach (var e in JsonDocument.Parse(ResourceData.Read("manual-rules.json")).RootElement.EnumerateArray())
            rules.Add(new(e.GetProperty("name").GetString()!, e.GetProperty("description").GetString()!, Strings(e, "triggers"), String(e, "tex"),
                Manual: true, Enabled: Boolean(e, "enabled", true), DisabledReason: String(e, "disabled_reason"), Retired: Boolean(e, "retired", false), Handler: String(e, "handler")));
        var entries = JsonDocument.Parse(ResourceData.Read("ported-rules.json")).RootElement.EnumerateArray().ToArray();
        foreach (var e in entries.OrderBy(e => String(e, "kind") == "source_rewrite" ? 1 : 0))
        {
            bool rewrite = String(e, "kind") == "source_rewrite";
            string module = e.GetProperty("module").GetString()!;
            rules.Add(new((rewrite ? "rewrite-" : "ported-") + module.Replace('_', '-'), String(e, "description") ?? module,
                Strings(e, "triggers"), String(e, "tex"), rewrite ? String(e, "find") : null,
                rewrite ? String(e, "replace") : null, String(e, "trigger_kind") ?? "log", String(e, "requires_class"),
                Enabled: Boolean(e, "enabled", true), DisabledReason: String(e, "disabled_reason"), Retired: Boolean(e, "retired", false), Handler: String(e, "handler")));
        }
        if (rules.Any(r => r.Handler is not null && !IsRevision(r) && r.Handler is not "braced_definition" and not "quantumarticle_pdfoutput"
            and not "microtype_expansion" and not "pdfinfo" and not "pdftex_primitive" and not "text_underscore" and not "graphics_valign"
            and not "xypdf_luatex85" and not "state_delimiter" and not "bbl_language" and not "pict2e_driver"
            and not "tikz_polar_brace" and not "hyphenation_tail" and not "px_dimension"))
            throw new InvalidDataException("未知的源码修复处理器。");
        AllRules = rules;
        Rules = rules.Where(r => !r.Retired).ToArray();
        if (AllRules.Count != 91 || AllRules.Select(r => r.Name).Distinct().Count() != 91)
            throw new InvalidDataException("修复规则清单不完整。");
    }
    private static string? String(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool Boolean(JsonElement e, string key, bool fallback) =>
        e.TryGetProperty(key, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False) ? p.GetBoolean() : fallback;
    private static string[] Strings(JsonElement e, string key) => e.TryGetProperty(key, out var p) ? p.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
    public static bool Trigger(string trigger, string haystack)
    {
        if (trigger.StartsWith("(?m)") || trigger.StartsWith('^')) return Pattern(trigger).IsMatch(haystack);
        return haystack.Contains(trigger, StringComparison.Ordinal);
    }
    public static Regex Pattern(string pattern) => new(pattern.Replace("(?P<", "(?<"), RegexOptions.Multiline, TimeSpan.FromSeconds(3));
    public bool Applies(RepairRule rule, string text, string log, string engine, string targetLanguage,
        string? sourcePath = null, string? projectRoot = null, string? projectSource = null)
    {
        if (!rule.Enabled || rule.Retired) return false;
        if (!CanRepeat(rule) && (text.Contains("% hjfy: " + rule.Name + " ") || text.Contains("% hjfy: " + rule.Name + "\n"))) return false;
        if (rule.Handler == "revision_epsf") return RevisionEpsfApplies(text, log, sourcePath, projectRoot);
        text = ExecutableText(text);
        if (IsRevision(rule)) return RevisionApplies(rule, text, log, engine, sourcePath, projectRoot, projectSource);
        if (rule.Handler == "px_dimension")
            return engine == "xelatex"
                && !Regex.IsMatch(ExecutableText(projectSource ?? text), @"\\(?:pdfpxdimen|pxdimen)\b")
                && DiagnosedPixelDimension(text, log, sourcePath, projectRoot) is not null;
        if (rule.Handler == "bbl_language")
        {
            var calls = Regex.Matches(text, @"\\selectlanguage\b");
            return DiagnosticCommand(log, "No multilingual package has been loaded.", "selectlanguage")
                && !Loads(text, "babel") && !Loads(text, "polyglossia")
                && !Regex.IsMatch(text, @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let)[ \t]*\{?[ \t]*\\selectlanguage\b")
                && calls.Count > 0 && calls.All(m => Regex.IsMatch(text[m.Index..], @"^\\selectlanguage[ \t]*\{(?:en|english)\}"));
        }
        if (rule.Handler == "pict2e_driver")
            return engine == "xelatex" && log.Contains("Package pict2e Error: No suitable driver specified.", StringComparison.Ordinal)
                && PictDriverLines(text).Any();
        if (rule.Handler == "tikz_polar_brace")
            return log.Contains(@"File ended while scanning use of \pgffor@collectargument.", StringComparison.Ordinal)
                && ReferencesFamily(text, log, "tikz") && PolarCoordinates(text).Count == 1;
        if (rule.Handler == "hyphenation_tail")
        {
            var candidates = HyphenationTails(text);
            if (candidates.Count != 1 || !log.Contains(@"LaTeX Error: Missing \begin{document}.", StringComparison.Ordinal)) return false;
            var pointer = Regex.Match(log, @"(?m)^l\.\d+[ \t]+(\\hyphenation\{[\u3400-\u9fff]+\}[\u3400-\u9fff]+)[ \t]*\r?$");
            return pointer.Success && candidates[0].Value.TrimStart().StartsWith(pointer.Groups[1].Value, StringComparison.Ordinal);
        }
        if (rule.Name == "rewrite-missing-textls")
            return engine is "pdflatex" or "lualatex"
                && !Loads(text, "microtype") && !Loads(text, "letterspace")
                && Regex.IsMatch(text, @"\\textls\b")
                && UndefinedCommand(log, "textls");
        if (rule.Name == "rewrite-hyperref-driver")
            return engine == "xelatex" && HyperrefDriverLines(text).Count() == 1
                && (HyperrefDriverError(log) || log.Contains("Option clash for package hyperref.", StringComparison.Ordinal)
                    && RequestedClashOptions(log) is { } request && PackageOptions(request)?.Any(o => o.Trim() is "pdftex" or "hypertex") == true);
        if (rule.Handler == "microtype_expansion")
            return engine == "xelatex" && log.Contains("Package microtype Error: Font expansion does not work with xetex", StringComparison.Ordinal)
                && ExpansionPackageLines(text).Any();
        if (rule.Handler == "pdfinfo")
            return engine is "xelatex" or "lualatex" && UndefinedCommand(log, "pdfinfo")
                && Regex.IsMatch(text, @"\\pdfinfo[ \t]*\{");
        if (rule.Handler == "state_delimiter")
        {
            if (!UndefinedCommand(log, "state") || Regex.IsMatch(text,
                @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let|DeclareRobustCommand|(?:New|Renew|Declare)DocumentCommand)[ \t]*\{?[ \t]*\\state\b")) return false;
            var calls = Regex.Matches(text, @"\\state\b");
            return calls.Count > 0 && calls.All(m => Regex.IsMatch(text[m.Index..],
                @"^\\state[ \t]+[A-Za-z0-9][A-Za-z0-9 -]{0,100}\.[ \t\r\n]+"));
        }
        if (rule.Handler == "xypdf_luatex85")
            return engine == "lualatex" && !Loads(text, "luatex85")
                && Regex.Replace(log, @"\s+", "").Contains("PackagexypdfError:pdfTeXversion1.40.0orhigherisneededforthexypdfpackagewithPDFoutput.", StringComparison.Ordinal)
                // This exact adapter has the same LuaTeX backend as luatex85's
                // alias. Other author definitions remain a hard boundary.
                && !Regex.IsMatch(text.Replace(@"\providecommand{\pdfinfo}[1]{\pdfextension info{#1}}", "", StringComparison.Ordinal),
                    @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let)[ \t]*\{?[ \t]*\\pdf[A-Za-z]+\b")
                && ReferencesFamily(text, log, "xy") && log.Contains("xypdf.tex", StringComparison.Ordinal);
        if (rule.Handler == "graphics_valign")
            return (log.Contains("Package keyval Error: valign undefined.", StringComparison.Ordinal)
                    || log.Contains("Package xkeyval Error: `valign' undefined in families `Gin'.", StringComparison.Ordinal))
                && Regex.IsMatch(log, @"(?m)^l\.\d+[^\r\n]*\\includegraphics[ \t]*\[[^\r\n]*\bvalign[ \t]*=[ \t]*c\b")
                && !Loads(text, "adjustbox")
                && !Regex.IsMatch(text, @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let|RenewDocumentCommand|NewDocumentCommand|DeclareRobustCommand)[ \t]*\{?[ \t]*\\includegraphics\b")
                && Regex.Matches(text, @"\\includegraphics[ \t]*\[([^\]\r\n]*)\]").Any(m =>
                    PackageOptions(m.Groups[1].Value)?.Any(o => Regex.IsMatch(o, @"^\s*valign\s*=\s*c\s*$")) == true);
        if (rule.Handler == "pdftex_primitive")
            return engine is "xelatex" or "lualatex" && UndefinedCommand(log, "pdfoutput")
                && Regex.IsMatch(log, @"(?m)^l\.\d+[ \t]+\\pdfoutput[ \t]*\r?$") && PdfOutputAssignments(text).Any();
        if (rule.Handler == "text_underscore")
            return log.Contains("Missing $ inserted.", StringComparison.Ordinal)
                && Regex.IsMatch(log, @"(?m)^l\.\d+[^\r\n]*\$[ \t]*\\(?:text|textit)[ \t]*\{[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\}[ \t]*\r?$")
                && TextIdentifiers(text).Any();
        if (rule.Name == "ported-tikz-text-degree")
            return DiagnosticCommand(log, "Missing $ inserted.", "degree")
                && HasMathDegree(text);
        if (rule.Handler == "quantumarticle_pdfoutput")
            return engine == "xelatex" && log.Contains("quantumarticle.cls", StringComparison.Ordinal)
                && log.Contains(@"\iftoggle{@nopdfoutputerror}", StringComparison.Ordinal)
                && UndefinedCommand(log, "pdfoutput") && QuantumClassLine(text).Success;
        if (rule.Handler == "braced_definition")
            return log.Contains("Missing control sequence inserted.", StringComparison.Ordinal)
                && Regex.IsMatch(log, @"(?m)^l\.\d+[^\r\n]*\\(?:[gex]?def)[ \t]*\{[ \t]*\r?$")
                && BracedDefinitions(text).Any();
        if (rule.Manual) return ManualApplies(rule.Name, text, log, engine, targetLanguage);
        if (rule.RequiresClass is { } cls && !ReferencesFamily(text, log, cls)) return false;
        if (rule.Name == "rewrite-arydshln-order")
            return log.Contains("Undefined control sequence", StringComparison.Ordinal)
                && (Regex.IsMatch(log, @"<argument>\s*\\dashline(?:dash|gap)\b")
                    || Regex.IsMatch(log, @"(?m)^\\adl@hline\b[^\r\n]*\\adl@rowsL[ \t]*\r?$"))
                && NeedsArrayBeforeArydshln(text);
        if (rule.Name == "rewrite-hyperref-option-clash")
            return log.Contains("Option clash for package hyperref.", StringComparison.Ordinal)
                && HasSafeHyperrefOptions(text, log);
        if (rule.Name == "rewrite-tcolorbox-leading-break")
            return log.Contains("LaTeX Error: There's no line here to end.", StringComparison.Ordinal)
                && DiagnosedTcolorboxBreak(text, log, sourcePath, projectRoot) is not null;
        if (rule.Find is { Length: > 0 } find)
            return (rule.Triggers.Length == 0 || rule.Triggers.Any(t => Trigger(t, log))) && Pattern(find).IsMatch(text);
        return !string.IsNullOrEmpty(rule.Tex) && rule.Triggers.Any(t => Trigger(t, rule.TriggerKind == "source" ? text : log));
    }
    public static bool Loads(string text, string package) => Regex.Matches(text, @"\\(?:usepackage|RequirePackage)\s*(?:\[[^\]]*\])?\s*\{([^}]+)\}")
        .Any(m => m.Groups[1].Value.Split(',').Any(p => p.Trim().Equals(package, StringComparison.OrdinalIgnoreCase)));
    private static bool ManualApplies(string name, string text, string log, string engine, string language) => name switch
    {
        "cjk-ctex-package-conflict" => (log.Contains("already defined") || log.Contains(@"Command \CJK")) &&
            (log.Contains("xeCJK") || text.Contains("xeCJK")) && (log.Contains("ctex") || text.Contains("ctex")) && Loads(text, "xeCJK"),
        "legacy-font-character-mapping" => log.Contains("Missing character:") && engine != "pdflatex" && !Loads(text, "ctex"),
        "legacy-frontmatter-cjk-font" => engine != "pdflatex" && Regex.IsMatch(log, "Font\\s+\\\\?\\S+\\s+not loadable|The font\\s+\"\\S+\"\\s+cannot be found"),
        "utf8-bibliography" => engine is "xelatex" or "lualatex" && UndefinedCommand(log, "IeC")
            && Regex.IsMatch(log, @"(?m)^[^\r\n]+\.bbl:\d+: Undefined control sequence\."),
        "missing-bbl" => Regex.IsMatch(log, @"(?:I couldn't open file|No file)\s+\S*?\.bbl"),
        "natbib-final-aux" => new[] { "Label(s) may have changed", "Citation(s) may have changed", "aa:natbib-final-aux", "Rerun to get cross-references right" }.Any(log.Contains) &&
            (Loads(text, "natbib") || log.Contains("natbib.sty") || log.Contains("aa.cls")),
        "cjk-theorem-break" => log.Contains(@"Overfull \hbox") && Regex.IsMatch(text, @"\\begin\s*\{(?:theorem|lemma|proposition|corollary|definition|remark|example|proof)\}") &&
            (QuoteNormalizer.LooksCjk(text) || language.StartsWith("zh") || language.StartsWith("ja") || language.StartsWith("ko")),
        "titlesec-unnumbered-anchor" => log.Contains("The anchor of a bookmark and its parent's must not be the same") && (Loads(text, "titlesec") || log.Contains("titlesec.sty")),
        "xecjk-boundary" => (log.Contains(@"\__xeCJK_boundary_inline_stream_") || log.Contains("xeCJK boundary")) && (log.Contains("xeCJK") || Loads(text, "xecjk")),
        "microtype-xetex" => engine != "pdflatex" && (Regex.IsMatch(log, @"Cannot use \\?XeTeXglyph\b") || log.Contains("Font expansion does not work with xetex")),
        "mdpi-pict2e-driver" => (log.Contains(@"\set@color") && log.Contains(@"\pdfcolorstack") || log.Contains("xcolor.sty") && log.Contains("mdpi")) && Regex.IsMatch(text, @"\\usepackage\s*\[[^\]]*\]\s*\{\s*pict2e\s*\}"),
        "subfiles-ctex" => log.Contains("Can be used only in preamble") && Regex.IsMatch(text, @"\\documentclass\s*(?:\[[^\]]*\])?\s*\{subfiles\}"),
        "appendix-equation-destinations" => (log.Contains("destination with the same identifier", StringComparison.OrdinalIgnoreCase) || log.Contains("appendix:colliding-equation-destination")) &&
            text.Contains(@"\appendix") && Regex.IsMatch(text, @"\\begin\s*\{(?:equation|align|gather|multline|eqnarray)\*?\}"),
        "soul-twocolumn" => (log.Contains("Package soul Error") || log.Contains("Reconstruction failed")) && Regex.IsMatch(text, @"\\twocolumn\s*\[\s*\{") && (Loads(text, "soul") || Loads(text, "ulem") || log.Contains("soul.sty")),
        "pdfinfo" => new[] { "pdfinfo", "pdftrailerid", "pdfsuppressptexinfo" }.Any(t => log.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
            (Regex.IsMatch(text, @"(?m)^[ \t]*\\pdfinfo\s*\{") || text.Contains(@"\pdfinfoomitdate") ||
             text.Contains(@"\pdftrailerid") || text.Contains(@"\pdfsuppressptexinfo")),
        "enumitem-xecjk-multiline" => engine == "xelatex" && Regex.IsMatch(log, @"Argument of \\enit@postlabel@i has an extra \}") &&
            Loads(text, "enumitem") && (Loads(text, "xeCJK") || Loads(text, "ctex") || ReferencesFamily(text, log, "xeCJK"))
            && !Regex.IsMatch(text, @"\\(?:[gex]?def|(?:re)?newcommand|let)[ \t]*\{?[ \t]*\\enit@postlabel")
            && Regex.IsMatch(text, @"\\begin\s*\{description\}\s*\[[^\]\r\n]*\bstyle\s*=\s*multiline\b"),
        "legacy-cjk-environment" => engine != "pdflatex" && log.Contains("Environment CJK undefined") && Regex.IsMatch(text, @"\\begin\s*\{CJK\}"),
        "state-delimiter" => log.Contains("Undefined control sequence") && log.Contains(@"\state") &&
            Regex.IsMatch(text, @"\\state\b") && !Regex.IsMatch(text, @"\\(?:re)?newcommand\s*\{?\\state\}?"),
        "literal-syntax-guard" => false,
        _ => throw new InvalidOperationException("未知手工规则 " + name)
    };
    public string Rewrite(RepairRule rule, string text, RepairContext? context = null)
    {
        if (!rule.Enabled || rule.Retired || !CanRepeat(rule) && text.Contains("% hjfy: " + rule.Name + " ", StringComparison.Ordinal)) return text;
        if (rule.Handler == "revision_epsf")
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage, context.SourcePath, context.ProjectRoot)) return text;
            Group extra = EpsfPercentFault(text)!.Groups["extra"];
            return "% hjfy: " + rule.Name + " repair\n" + text.Remove(extra.Index, extra.Length);
        }
        string active = ExecutableText(text);
        List<(string Token, string Original)> protectedText = [];
        string prefix = "HJFYPROTECTED";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "X";
        StringBuilder shielded = new();
        for (int i = 0; i < text.Length;)
        {
            if (active[i] == text[i]) { shielded.Append(text[i++]); continue; }
            int start = i;
            // A protected region can contain whitespace already equal to its mask.
            // Stop at an executable non-whitespace character or a line ending.
            while (i < text.Length && text[i] is not '\r' and not '\n'
                && (active[i] != text[i] || char.IsWhiteSpace(text[i]))) i++;
            string token = "%" + prefix + "_" + protectedText.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_END";
            protectedText.Add((token, text[start..i]));
            shielded.Append(token);
        }
        string rewritten = RewriteCore(rule, shielded.ToString(), context);
        foreach (var (placeholder, original) in protectedText.AsEnumerable().Reverse())
        {
            // Never accept a rewrite that consumes protected material.
            int first = rewritten.IndexOf(placeholder, StringComparison.Ordinal);
            if (first < 0 || rewritten.IndexOf(placeholder, first + placeholder.Length, StringComparison.Ordinal) >= 0) return text;
            rewritten = rewritten.Replace(placeholder, original, StringComparison.Ordinal);
        }
        return rewritten;
    }

    private string RewriteCore(RepairRule rule, string text, RepairContext? context)
    {
        if (!rule.Enabled || rule.Retired) return text;
        if (!CanRepeat(rule) && text.Contains("% hjfy: " + rule.Name + " ", StringComparison.Ordinal)) return text;
        string marker = "% hjfy: " + rule.Name + " repair\n";
        if (IsRevision(rule))
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage,
                context.SourcePath, context.ProjectRoot, context.ProjectSource)) return text;
            return RewriteRevision(rule, text, context, marker);
        }
        if (rule.Handler == "px_dimension")
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage,
                context.SourcePath, context.ProjectRoot, context.ProjectSource)) return text;
            var dimension = DiagnosedPixelDimension(ExecutableText(text), context.Log, context.SourcePath, context.ProjectRoot)!;
            // pdfTeX's default px register is 65782sp, rounded once per pixel.
            // An internal dimension preserves TeX's decimal multiplication too.
            string value = dimension.Groups["number"].Value;
            return marker + text.Remove(dimension.Index, dimension.Length)
                .Insert(dimension.Index, @"\dimexpr" + value + @"\dimexpr65782sp\relax\relax");
        }
        if (rule.Handler is "bbl_language" or "pict2e_driver" or "tikz_polar_brace" or "hyphenation_tail")
        {
            if (context is null || !Applies(rule, context.ProjectSource ?? text, context.Log, context.Engine, context.TargetLanguage)) return text;
            if (rule.Handler == "bbl_language")
                return Inject(text, marker + "\\RequirePackage[english]{babel}\n\\makeatletter\\@ifundefined{l@en}{\\adddialect\\l@en\\l@english}{}\\makeatother\n", afterClass: true);
            if (rule.Handler == "pict2e_driver")
            {
                string changed = text;
                foreach (Match line in PictDriverLines(ExecutableText(text)).Reverse())
                {
                    Group options = line.Groups["options"];
                    string kept = string.Join(',', PackageOptions(options.Value)!.Where(o => o.Trim() != "pdftex"));
                    int at = options.Index, length = options.Length;
                    if (string.IsNullOrWhiteSpace(kept)) { at--; length += 2; kept = ""; }
                    changed = changed.Remove(at, length).Insert(at, kept);
                }
                return marker + changed;
            }
            if (rule.Handler == "tikz_polar_brace")
            {
                Group angle = PolarCoordinates(ExecutableText(text))[0].Groups["angle"];
                return marker + text.Insert(angle.Index + angle.Length, "}");
            }
            var bad = HyphenationTails(ExecutableText(text))[0];
            int close = bad.Groups[2].Index + bad.Groups[2].Length;
            int end = bad.Groups[3].Index + bad.Groups[3].Length;
            return marker + text.Remove(close, 1).Insert(end - 1, "}");
        }
        if (rule.Handler == "state_delimiter")
        {
            if (context is null || !Applies(rule, context.ProjectSource ?? text, context.Log, context.Engine, context.TargetLanguage)) return text;
            return Inject(text, marker + "\\makeatletter\n\\@ifundefined{state}{\\def\\state #1. {\\noindent{\\bfseries #1.\\enspace}}}{}\n\\makeatother\n", afterClass: true);
        }
        if (rule.Handler == "xypdf_luatex85")
        {
            if (context is null || !Applies(rule, context.ProjectSource ?? text, context.Log, context.Engine, context.TargetLanguage)) return text;
            return Inject(text, marker + "\\RequirePackage{luatex85}\n", atTop: true);
        }
        if (rule.Handler == "graphics_valign")
        {
            if (context is null || !Applies(rule, context.ProjectSource ?? text, context.Log, context.Engine, context.TargetLanguage)) return text;
            return Inject(text, marker + "\\PassOptionsToPackage{export}{adjustbox}\n\\makeatletter\n"
                + "\\AddToHook{package/graphicx/after}{\\@ifpackageloaded{adjustbox}{}{\\RequirePackage{adjustbox}}}\n"
                + "\\makeatother\n", atTop: true);
        }
        if (rule.Handler is "microtype_expansion" or "pdfinfo" or "pdftex_primitive" or "text_underscore")
        {
            if (context is null || !Applies(rule, context.ProjectSource ?? text, context.Log, context.Engine, context.TargetLanguage)) return text;
            if (rule.Handler == "pdfinfo")
            {
                string adapter = context.Engine == "xelatex"
                    ? @"\providecommand{\pdfinfo}[1]{\special{pdf:docinfo <<#1>>}}"
                    : @"\providecommand{\pdfinfo}[1]{\pdfextension info{#1}}";
                return Inject(text, marker + adapter + "\n", afterClass: true);
            }
            string changed = text;
            if (rule.Handler == "microtype_expansion")
            {
                foreach (Match line in ExpansionPackageLines(ExecutableText(text)).Reverse())
                {
                    Group options = line.Groups["options"];
                    string kept = string.Join(',', PackageOptions(options.Value)!.Where(o => o.Trim().Split('=')[0].Trim() != "expansion"));
                    string replacement = (string.IsNullOrWhiteSpace(kept) ? "" : kept + ",") + "expansion=false";
                    changed = options.Success ? changed.Remove(options.Index, options.Length).Insert(options.Index, replacement)
                        : changed.Insert(line.Groups["command"].Index + line.Groups["command"].Length, "[" + replacement + "]");
                }
            }
            else if (rule.Handler == "pdftex_primitive")
            {
                foreach (Match assignment in PdfOutputAssignments(ExecutableText(text)).Reverse())
                {
                    Group value = assignment.Groups["assignment"];
                    string replacement = context.Engine == "xelatex" ? @"\ifdefined\pdfoutput\pdfoutput=1\fi" : @"\outputmode=1";
                    changed = changed.Remove(value.Index, value.Length).Insert(value.Index, replacement);
                }
            }
            else
                foreach (Match identifier in TextIdentifiers(ExecutableText(text)).Reverse())
                {
                    Group value = identifier.Groups["identifier"];
                    changed = changed.Remove(value.Index, value.Length).Insert(value.Index, value.Value.Replace("_", @"\_"));
                }
            return changed == text ? text : marker + changed;
        }
        if (rule.Handler == "quantumarticle_pdfoutput")
        {
            Match declaration = QuantumClassLine(ExecutableText(text));
            if (!declaration.Success) return text;
            Group options = declaration.Groups["options"];
            int position = options.Success ? options.Index + options.Length
                : declaration.Groups["command"].Index + declaration.Groups["command"].Length;
            string added = options.Success ? (string.IsNullOrWhiteSpace(options.Value) ? "" : ",") + "nopdfoutputerror"
                : "[nopdfoutputerror]";
            return marker + text.Insert(position, added);
        }
        if (rule.Handler == "braced_definition")
        {
            string repaired = text;
            foreach (Match declaration in BracedDefinitions(ExecutableText(text)).Reverse())
            {
                Group wrapper = declaration.Groups["wrapper"];
                repaired = repaired.Remove(wrapper.Index, wrapper.Length).Insert(wrapper.Index, declaration.Groups["command"].Value);
            }
            return repaired == text ? text : marker + repaired;
        }
        if (rule.Name == "rewrite-missing-textls")
        {
            string active = ExecutableText(context?.ProjectSource ?? text);
            if (!Regex.IsMatch(active, @"\\textls\b") || Loads(active, "microtype") || Loads(active, "letterspace")) return text;
            return Inject(text, marker + rule.Tex + "\n");
        }
        if (rule.Name == "rewrite-hyperref-driver")
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage)) return text;
            string repairedDriver = RemoveHyperrefDriver(text);
            return repairedDriver == text ? text : marker + repairedDriver;
        }
        if (rule.Name == "rewrite-arydshln-order")
        {
            string reordered = MoveArrayBeforeArydshln(text);
            return reordered == text ? text : marker + reordered;
        }
        if (rule.Name == "rewrite-hyperref-option-clash")
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage)) return text;
            string configured = MoveHyperrefOptionsToSetup(text, context.Log);
            return configured == text ? text : marker + configured;
        }
        if (rule.Name == "rewrite-tcolorbox-leading-break")
        {
            if (context is null || !Applies(rule, text, context.Log, context.Engine, context.TargetLanguage, context.SourcePath, context.ProjectRoot)) return text;
            var command = DiagnosedTcolorboxBreak(ExecutableText(text), context.Log, context.SourcePath, context.ProjectRoot);
            return command is { } value ? marker + text.Remove(value.Index, value.Length) : text;
        }
        if (rule.Find is { Length: > 0 } find)
        {
            string changed = Pattern(find).Replace(text, m => PythonReplacement(rule.Replace ?? "", m));
            return changed == text ? text : marker + changed;
        }
        switch (rule.Name)
        {
            case "cjk-ctex-package-conflict":
                return Regex.Replace(text, @"\\usepackage\s*(?:\[[^\]]*\])?\s*\{\s*xeCJK\s*\}", _ => marker, RegexOptions.None, TimeSpan.FromSeconds(3));
            case "mdpi-pict2e-driver":
                string changed = Regex.Replace(text, @"\\usepackage\s*\[([^\]]*)\]\s*\{\s*pict2e\s*\}", m =>
                {
                    string[] kept = m.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 &&
                        !new[] { "pdftex", "dvips", "dvipdfm", "dvipdfmx", "xetex", "luatex", "vtex" }.Contains(s.ToLowerInvariant())).ToArray();
                    return "\\usepackage" + (kept.Length > 0 ? "[" + string.Join(',', kept) + "]" : "") + "{pict2e}";
                });
                return changed == text ? text : marker + changed;
            case "soul-twocolumn":
                return new Regex(@"\\twocolumn\s*\[\s*\{").Replace(text, _ => "\\twocolumn[{\\renewcommand\\twocolumn[1][]{#1}%\n" + marker, 1);
            case "subfiles-ctex": return RewriteSubfile(text, marker);
        }
        string? snippet = rule.Tex;
        if (rule.Name == "cjk-theorem-break")
        {
            var environments = new[] { "theorem", "lemma", "proposition", "corollary", "definition", "remark", "example", "proof" }
                .Where(e => Regex.IsMatch(text, @"\\begin\s*\{\s*" + e + @"\s*\}"));
            snippet = "\\usepackage{etoolbox}\n" + string.Join('\n', environments.Select(e => @"\AtBeginEnvironment{" + e + @"}{\emergencystretch=3em\relax}"));
        }
        if (string.IsNullOrEmpty(snippet)) return text;
        return Inject(text, marker + snippet + "\n", rule.Name is "pdfinfo" or "state-delimiter" || rule.TriggerKind == "inject_after_class", rule.TriggerKind == "inject_at_top");
    }
    // Keep offsets stable so a diagnostic/source match cannot come from a comment
    // or a displayed TeX example. TeX control symbols are consumed as a unit.
    public static string ExecutableText(string text)
    {
        char[] visible = text.ToCharArray();
        void Hide(int start, int end)
        {
            for (int at = start; at < end; at++)
                if (visible[at] is not '\r' and not '\n') visible[at] = ' ';
        }
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '%')
            {
                int end = TexSyntax.LineEnd(text, i, text.Length);
                Hide(i, end); i = end; continue;
            }
            if (text[i] != '\\') { i++; continue; }
            var (name, after) = TexSyntax.Command(text, i, text.Length);
            if (name is "verb" or "lstinline")
            {
                int end = TexSyntax.InlineVerbatim(text, i, text.Length);
                Hide(i, end); i = end; continue;
            }
            if (name == "begin" && TexSyntax.EnvironmentName(text, after, text.Length) is { } env
                && (TexSyntax.VerbatimEnvironments.Contains(env.Name) || env.Name == "comment"))
            {
                string closing = @"\end{" + env.Name + "}";
                int close = text.IndexOf(closing, env.End, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + closing.Length;
                Hide(i, end); i = end; continue;
            }
            i = after;
        }
        return new string(visible);
    }

    private static IEnumerable<Match> ExpansionPackageLines(string text) =>
        Regex.Matches(text, @"(?m)^[ \t]*(?<command>\\(?:usepackage|RequirePackage))[ \t]*(?:\[(?<options>[^\]\r\n]*)\][ \t]*)?\{[ \t]*microtype[ \t]*\}[ \t]*\r?$")
            .Where(m => PackageOptions(m.Groups["options"].Value) is { } options
                && options.Any(o => Regex.IsMatch(o.Trim(), @"^expansion(?:[ \t]*=[ \t]*true)?$"))
                && options.Where(o => o.Trim().Split('=')[0].Trim() == "expansion")
                    .All(o => Regex.IsMatch(o.Trim(), @"^expansion(?:[ \t]*=[ \t]*true)?$")));

    private static IEnumerable<Match> PdfOutputAssignments(string text) =>
        Regex.Matches(text, @"(?m)^[ \t]*(?<assignment>\\pdfoutput[ \t]*=[ \t]*1)[ \t]*\r?$").Cast<Match>();

    private static bool HasMathDegree(string text)
    {
        if (Regex.IsMatch(text, @"\\(?:[gex]?def|let|(?:re)?newcommand|providecommand|DeclareRobustCommand)[ \t]*\*?[ \t]*\{?[ \t]*\\degree\b")) return false;
        MatchCollection definitions = Regex.Matches(text, @"(?m)^[ \t]*\\mathchardef[ \t]*\\degree[ \t]*=[ \t]*(?:""[0-9A-Fa-f]+|[0-9]+)[ \t]*\r?$");
        Match document = Regex.Match(text, @"\\begin[ \t]*\{document\}");
        return definitions.Count == 1 && document.Success && definitions[0].Index < document.Index;
    }

    private static IEnumerable<Match> TextIdentifiers(string text)
    {
        if (Regex.IsMatch(text, @"\\(?:[gex]?def|let|(?:re)?newcommand|providecommand|DeclareRobustCommand)[ \t]*\*?[ \t]*\{?[ \t]*\\(?:text|textit)\b")) return [];
        return Regex.Matches(text, @"(?<!\\)\$[ \t]*\\(?:text|textit)[ \t]*\{(?<identifier>[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+)\}[ \t]*\$").Cast<Match>();
    }

    private static Match QuantumClassLine(string text)
    {
        Match line = Regex.Match(text, @"(?m)^[ \t]*(?<command>\\documentclass[ \t]*)(?:\[(?<options>[^\]\r\n]*)\][ \t]*)?\{[ \t]*quantumarticle[ \t]*\}[ \t]*\r?$");
        if (!line.Success) return Match.Empty;
        string[]? options = PackageOptions(line.Groups["options"].Value);
        return options is null || options.Any(o => o.Trim().Split('=')[0] == "nopdfoutputerror") ? Match.Empty : line;
    }

    private static IEnumerable<Match> BracedDefinitions(string text)
    {
        foreach (Match match in Regex.Matches(text, @"(?m)^[ \t]*\\(?:[gex]?def)[ \t]*(?<wrapper>\{[ \t]*(?<command>\\[A-Za-z]+)[ \t]*\})[ \t]*(?=\{)"))
        {
            int start = match.Index + match.Length;
            var group = TexSyntax.Group(text, start, text.Length);
            if (!group.Terminated || text[start..group.End].IndexOfAny(['\r', '\n', '#']) >= 0) continue;
            int lineEnd = TexSyntax.LineEnd(text, group.End, text.Length);
            if (text[group.End..lineEnd].Trim().Length != 0) continue;
            yield return match;
        }
    }

    private static bool ReferencesFamily(string text, string log, string family)
    {
        string namePattern = Regex.Escape(family) + @"(?:[0-9][A-Za-z0-9-]*|-[A-Za-z0-9-]+)?";
        foreach (Match declaration in Regex.Matches(text, @"\\(?:documentclass|usepackage|RequirePackage)\s*(?:\[[^\]]*\])?\s*\{([^}]+)\}"))
            foreach (string name in declaration.Groups[1].Value.Split(','))
                if (Regex.IsMatch(name.Trim(), "^(?:[A-Za-z0-9_./-]*/)?" + namePattern + "$", RegexOptions.IgnoreCase)) return true;
        return Regex.IsMatch(log, @"(?<![A-Za-z0-9_-])" + namePattern + @"\.(?:cls|sty)\b", RegexOptions.IgnoreCase);
    }

    private static bool UndefinedCommand(string log, string command) => DiagnosticCommand(log, "Undefined control sequence.", command);

    private static bool DiagnosticCommand(string log, string diagnostic, string command)
    {
        foreach (Match error in Regex.Matches(log, Regex.Escape(diagnostic) + @"[^\r\n]*\r?\n(?:(?!.*(?:Error:|Undefined control sequence))[^\r\n]*\r?\n){0,8}"))
            if (Regex.IsMatch(error.Value, @"(?m)^(?:l\.\d+|<recently read>|<argument>|<to be read again>)[^\r\n]*\\"
                + Regex.Escape(command) + @"[ \t]*\r?$", RegexOptions.None, TimeSpan.FromSeconds(3))) return true;
        return false;
    }

    private static bool HyperrefDriverError(string log) =>
        Regex.IsMatch(log, @"Package hyperref Error: Wrong (?:DVI mode )?driver option [`'](?:pdftex|hypertex)'", RegexOptions.None, TimeSpan.FromSeconds(3));

    private static string[]? PackageOptions(string options)
    {
        List<string> parts = []; int depth = 0, start = 0;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == '\\') { i++; continue; }
            if (options[i] == '{') depth++;
            else if (options[i] == '}' && --depth < 0) return null;
            else if (options[i] == ',' && depth == 0) { parts.Add(options[start..i]); start = i + 1; }
        }
        if (depth != 0) return null;
        parts.Add(options[start..]);
        return parts.ToArray();
    }

    private static IEnumerable<Match> PictDriverLines(string text) =>
        Regex.Matches(text, @"(?m)^[ \t]*\\(?:usepackage|RequirePackage)[ \t]*\[(?<options>[^\]\r\n]*)\][ \t]*\{[ \t]*pict2e[ \t]*\}[ \t]*\r?$")
            .Where(m => PackageOptions(m.Groups["options"].Value) is { } options
                && options.Count(o => o.Trim() == "pdftex") == 1
                && options.All(o => o.Trim() is "pdftex" or "ltxarrows" or "pstarrows"));

    private static MatchCollection PolarCoordinates(string text) => Regex.Matches(text,
        @"\\(?:node|coordinate)\b[^;\r\n]*?\bat[ \t]*\(\{(?<angle>\\[A-Za-z]+[ \t]*\*[ \t]*\([ \t]*360[ \t]*/[ \t]*(?:[1-9][0-9]*|\\[A-Za-z]+)[ \t]*\))[ \t]*:[ \t]*[0-9]+(?:\.[0-9]+)?[ \t]*(?:cm|mm|pt|bp|in)[ \t]*\)");

    private static MatchCollection HyphenationTails(string text)
    {
        int end = text.IndexOf(@"\begin{document}", StringComparison.Ordinal);
        return Regex.Matches(end < 0 ? "" : text[..end], @"(?m)^([ \t]*)\\hyphenation\{([\u3400-\u9fff]+)\}([\u3400-\u9fff]+)[ \t]*\r?$");
    }

    private static IEnumerable<Match> HyperrefDriverLines(string text) =>
        Regex.Matches(text, @"(?m)^[ \t]*\\(?:usepackage|RequirePackage)[ \t]*\[(?<options>[^\]\r\n]*)\][ \t]*\{[ \t]*hyperref[ \t]*\}[ \t]*\r?$")
            .Where(m => PackageOptions(m.Groups["options"].Value) is { } options
                && options.Any(o => o.Trim() is "pdftex" or "hypertex")
                && !options.Any(o => o.Trim() is "xetex" or "luatex" or "dvips" or "dvipdfm" or "dvipdfmx"));

    private static string RemoveHyperrefDriver(string text)
    {
        foreach (Match line in HyperrefDriverLines(ExecutableText(text)).Reverse())
        {
            Group options = line.Groups["options"];
            string kept = string.Join(',', PackageOptions(options.Value)!.Where(o => o.Trim() is not "pdftex" and not "hypertex"));
            int start = options.Index, length = options.Length;
            if (string.IsNullOrWhiteSpace(kept)) { start--; length += 2; kept = ""; }
            text = text.Remove(start, length).Insert(start, kept);
        }
        return text;
    }

    private static Match PackageLine(string text, string package) =>
        Regex.Match(ExecutableText(text), @"(?m)^[ \t]*\\(?:usepackage|RequirePackage)[ \t]*(?:\[[^\]\r\n]*\][ \t]*)?\{[ \t]*"
            + Regex.Escape(package) + @"[ \t]*\}[ \t]*(?:%[^\r\n]*)?\r?$");

    private static bool NeedsArrayBeforeArydshln(string text)
    {
        Match array = PackageLine(text, "array");
        Match arydshln = PackageLine(text, "arydshln");
        return array.Success && arydshln.Success && array.Index > arydshln.Index;
    }

    private static string MoveArrayBeforeArydshln(string text)
    {
        Match array = PackageLine(text, "array");
        Match arydshln = PackageLine(text, "arydshln");
        if (!array.Success || !arydshln.Success || array.Index < arydshln.Index) return text;
        int arrayStart = text.LastIndexOf('\n', array.Index);
        arrayStart = arrayStart < 0 ? 0 : arrayStart + 1;
        int arrayEnd = text.IndexOf('\n', array.Index);
        arrayEnd = arrayEnd < 0 ? text.Length : arrayEnd + 1;
        int arydshlnStart = text.LastIndexOf('\n', arydshln.Index);
        arydshlnStart = arydshlnStart < 0 ? 0 : arydshlnStart + 1;
        string arrayLine = text[arrayStart..arrayEnd];
        if (!arrayLine.EndsWith('\n')) arrayLine += "\n";
        string withoutArray = text.Remove(arrayStart, arrayEnd - arrayStart);
        if (arrayStart < arydshlnStart) arydshlnStart -= arrayEnd - arrayStart;
        return withoutArray.Insert(arydshlnStart, arrayLine);
    }

    private static string? RequestedClashOptions(string log)
    {
        Match request = Regex.Match(log, @"There has now been an attempt to load it with options\s*\[([^\]]*)\]");
        return request.Success ? request.Groups[1].Value : null;
    }
    private static Match HyperrefPackageLine(string text, string? log = null)
    {
        var matches = Regex.Matches(text, @"(?m)^(?<indent>[ \t]*)(?<command>\\usepackage[ \t]*\[(?<options>[^\]\r\n]*)\][ \t]*\{[ \t]*hyperref[ \t]*\})(?<tail>[ \t]*(?:%[^\r\n]*)?\r?)$").Cast<Match>().ToArray();
        if (log is not null && RequestedClashOptions(log) is { } request)
            return matches.FirstOrDefault(m => Regex.Replace(m.Groups["options"].Value, @"\s+", "") == Regex.Replace(request, @"\s+", "")) ?? Match.Empty;
        return matches.Length == 1 ? matches[0] : Match.Empty;
    }

    private static bool HasSafeHyperrefOptions(string text, string? log = null)
    {
        Match line = HyperrefPackageLine(text, log);
        if (!line.Success) return false;
        string[] options = line.Groups["options"].Value.Split(',');
        return options.Length > 0 && options.All(option =>
        {
            string value = option.Trim();
            return Regex.IsMatch(value, @"^(?:breaklinks|colorlinks)(?:=(?:true|false))?$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(value, @"^allcolors=[A-Za-z][A-Za-z0-9]*$", RegexOptions.IgnoreCase);
        });
    }

    private static string MoveHyperrefOptionsToSetup(string text, string log)
    {
        if (!HasSafeHyperrefOptions(text, log)) return text;
        Match line = HyperrefPackageLine(text, log);
        Group command = line.Groups["command"];
        return text[..command.Index] + @"\hypersetup{" + line.Groups["options"].Value + "}" + text[(command.Index + command.Length)..];
    }

    private static string TcolorboxBeginPattern => @"\\begin\s*\{tcolorbox\}\s*\[[^\]\r\n]*\]";
    private static string TcolorboxBreakBeforePattern =>
        @"(?m)^[ \t]*\\\\\*?(?:\[[^\]\r\n]*\])?[ \t]*(?:%[^\r\n]*)?\r?\n(?:[ \t]*\r?\n)*[ \t]*" + TcolorboxBeginPattern;
    private static string TcolorboxBreakAfterPattern =>
        TcolorboxBeginPattern + @"\r?\n(?:[ \t]*\r?\n)*[ \t]*\\\\\*?(?:\[[^\]\r\n]*\])?[ \t]*(?:%[^\r\n]*)?(?:\r?\n|$)";

    private static Match? DiagnosedPixelDimension(string text, string log, string? sourcePath, string? projectRoot)
    {
        var error = Regex.Match(log, @"(?m)^([^\r\n]+\.tex):(\d+): Illegal unit o\s*f measure \(pt inserted\)\.");
        if (!error.Success || !int.TryParse(error.Groups[2].Value, out int line)) return null;
        if (sourcePath is not null)
        {
            string reported = Path.GetFullPath(Path.Combine(projectRoot ?? Path.GetDirectoryName(sourcePath)!, error.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!reported.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)) return null;
        }
        var pointer = Regex.Match(log[error.Index..], @"(?m)^l\." + line + @"[ \t]+([^\r\n]+)");
        if (!pointer.Success) return null;
        var spaces = Regex.Matches(text, @"\\[hv]space\*?\{(?<dimension>[+-]?(?:\d+(?:\.\d*)?|\.\d+)[ \t]*px)\}").Cast<Match>();
        var graphics = Regex.Matches(text, @"\\includegraphics\*?\[(?<options>[^\]\r\n]*)\]\{[^{}\r\n]+\}").Cast<Match>()
            .Where(m => PackageOptions(m.Groups["options"].Value) is { } options
                && options.All(o => Regex.IsMatch(o.Trim(), @"^(?:(?:width|height)\s*=\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)\s*(?:px|pt|bp|cm|mm|in|sp)|keepaspectratio(?:\s*=\s*(?:true|false))?)$"))
                && Regex.Matches(m.Groups["options"].Value, @"[0-9][ \t]*px\b").Count == 1);
        foreach (Match call in spaces.Concat(graphics).OrderBy(m => m.Index))
        {
            int start = text.LastIndexOf('\n', call.Index) + 1;
            if (text[..call.Index].Count(c => c == '\n') + 1 != line) continue;
            string prefix = text[start..(call.Index + call.Length)];
            if (!pointer.Groups[1].Value.TrimEnd().Equals(prefix.TrimStart(), StringComparison.Ordinal)
                || Regex.IsMatch(prefix, @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let)\b")) continue;
            Group dimension = call.Groups["dimension"].Success ? call.Groups["dimension"] : call.Groups["options"];
            return new Regex(@"(?<number>[+-]?(?:\d+(?:\.\d*)?|\.\d+))[ \t]*px", RegexOptions.None, TimeSpan.FromSeconds(3)).Match(text, dimension.Index);
        }
        return null;
    }

    private static (int Index, int Length)? DiagnosedTcolorboxBreak(string text, string log, string? sourcePath, string? projectRoot)
    {
        Match pointer = Regex.Match(log, @"(?m)^l\.(\d+)[ \t]+([^\r\n]+)");
        if (!pointer.Success || !int.TryParse(pointer.Groups[1].Value, out int line)) return null;
        if (sourcePath is not null)
        {
            Match file = Regex.Match(log, @"(?m)^([^\r\n]+\.tex):(\d+): LaTeX Error: There's no line here to end\.");
            if (!file.Success || file.Groups[2].Value != pointer.Groups[1].Value) return null;
            string reported = Path.GetFullPath(Path.Combine(projectRoot ?? Path.GetDirectoryName(sourcePath)!, file.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!reported.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)) return null;
        }
        foreach (string pattern in new[] { TcolorboxBreakBeforePattern, TcolorboxBreakAfterPattern })
            foreach (Match match in Regex.Matches(text, pattern))
                foreach (Match local in Regex.Matches(match.Value, @"(?m)^[ \t]*(?<command>\\\\\*?(?:\[[^\]\r\n]*\])?)"))
                {
                    Group command = local.Groups["command"];
                    int start = match.Index + command.Index;
                    int next = start + command.Length;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next >= text.Length || text[..next].Count(c => c == '\n') + 1 != line) continue;
                    int end = text.IndexOf('\n', next); if (end < 0) end = text.Length;
                    if (!text[next..end].StartsWith(pointer.Groups[2].Value.TrimStart(), StringComparison.Ordinal)) continue;
                    return (start, command.Length);
                }
        return null;
    }

    private static string RewriteSubfile(string text, string marker)
    {
        var dc = Regex.Match(text, @"\\documentclass\s*(?:\[[^\]]*\])?\s*\{subfiles\}");
        if (!dc.Success) return text;
        var begin = Regex.Match(text, @"\\begin\s*\{document\}");
        string changed = text;
        if (!begin.Success) changed = text.Insert(dc.Index + dc.Length, "\n" + marker + "\\begin{document}\n") + "\n\\end{document}\n";
        else if (Regex.IsMatch(text[(dc.Index + dc.Length)..begin.Index], @"\\(?:section|chapter|begin|item)\b"))
            changed = text[..(dc.Index + dc.Length)] + "\n" + marker + "\\begin{document}\n" + text[(dc.Index + dc.Length)..begin.Index] + text[(begin.Index + begin.Length)..];
        var ctex = new Regex(@"\\usepackage\s*(?:\[[^\]]*\])?\s*\{ctex\}");
        return ctex.Replace(changed, _ => marker + @"\makeatletter\@ifpackageloaded{ctex}{}{\usepackage[fontset=fandol,UTF8]{ctex}}\makeatother", 1);
    }
    public static string Inject(string text, string snippet, bool afterClass = false, bool atTop = false)
    {
        if (atTop) return snippet + "\n" + text;
        string active = ExecutableText(text);
        Match location = afterClass ? Regex.Match(active, @"\\documentclass\s*(?:\[[^\]]*\])?\s*\{[^}]+\}") : Regex.Match(active, @"\\begin\s*\{document\}");
        if (!location.Success) return text;
        int at = afterClass ? location.Index + location.Length : location.Index;
        return text.Insert(at, "\n" + snippet + "\n");
    }
    public static string PythonReplacement(string replacement, Match match)
    {
        StringBuilder result = new();
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c != '\\' || i + 1 >= replacement.Length) { result.Append(c); continue; }
            char next = replacement[++i];
            if (next == '\\') { result.Append('\\'); continue; }
            if (next == 'g' && i + 1 < replacement.Length && replacement[i + 1] == '<')
            {
                int close = replacement.IndexOf('>', i + 2);
                result.Append(match.Groups[replacement[(i + 2)..close]].Value); i = close; continue;
            }
            if (char.IsAsciiDigit(next))
            {
                int start = i; while (i + 1 < replacement.Length && char.IsAsciiDigit(replacement[i + 1])) i++;
                result.Append(match.Groups[int.Parse(replacement[start..(i + 1)])].Value); continue;
            }
            result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
        }
        return result.ToString();
    }
}
