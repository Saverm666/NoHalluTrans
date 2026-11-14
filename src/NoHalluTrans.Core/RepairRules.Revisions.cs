using System.Text.RegularExpressions;

namespace NoHalluTrans.Core;

public sealed partial class RepairRules
{
    private static readonly HashSet<string> RevisionHandlers = [
        "revision_kpfonts_classic", "revision_kpfonts_slanted", "revision_tabcaption",
        "revision_cjk_options", "revision_legacy_cjk", "revision_bm_letters",
        "revision_unicode_script", "revision_subfiles_ctex", "revision_frontmatter_font",
        "revision_prodint", "revision_half_tick", "revision_outputbox", "revision_sola", "revision_aas_script",
        "revision_missing_glyph", "revision_theorem_text", "revision_titlesec_anchor", "revision_appendix_dest", "revision_natbib_aux",
        "revision_soul_caption", "revision_soul_highlight", "revision_soul_colored", "revision_soul_title",
        "revision_jmlr", "revision_ismir", "revision_libertinus", "revision_bm_color",
        "revision_prime_href", "revision_profile", "revision_robo_font", "revision_physical_font", "revision_epsf"
    ];
    private static bool IsRevision(RepairRule rule) => rule.Handler is not null && RevisionHandlers.Contains(rule.Handler);
    private static bool RevisionNeedsProject(RepairRule rule) => rule.Handler is
        "revision_tabcaption" or "revision_legacy_cjk" or "revision_bm_letters" or "revision_cjk_options" or "revision_outputbox" or "revision_sola" or "revision_aas_script" or "revision_missing_glyph" or "revision_titlesec_anchor" or "revision_soul_caption" or "revision_soul_highlight" or "revision_soul_colored" or "revision_bm_color" or "revision_profile" or "revision_physical_font" or "revision_jmlr" or "revision_ismir";
    private static bool RevisionPerFile(RepairRule rule) => IsRevision(rule) && rule.Handler is not "revision_tabcaption" and not "revision_legacy_cjk" and not "revision_sola" and not "revision_missing_glyph" and not "revision_titlesec_anchor" and not "revision_jmlr" and not "revision_ismir" and not "revision_physical_font";

    private static IEnumerable<Match> RevisionPackageLines(string text, string package) => Regex.Matches(text,
        @"(?m)^[ \t]*\\(?:usepackage|RequirePackage)[ \t]*(?:\[(?<options>[^\]\r\n]*)\][ \t]*)?\{[ \t]*" + Regex.Escape(package) + @"[ \t]*\}[ \t]*\r?$").Cast<Match>();

    private static bool UnknownKpOption(string log, string option) => Regex.IsMatch(Regex.Replace(log, @"\s+", " "),
        @"Unknown option [`']" + option + @"' for package [`']kpfonts-otf'");

    private static readonly string BoldAtoms = @"(?:[A-Za-z0-9]|\\(?:alpha|beta|gamma|delta|epsilon|varepsilon|zeta|eta|theta|vartheta|iota|kappa|lambda|mu|nu|xi|pi|varpi|rho|varrho|sigma|varsigma|tau|upsilon|phi|varphi|chi|psi|omega|Gamma|Delta|Theta|Lambda|Xi|Pi|Sigma|Upsilon|Phi|Psi|Omega))";
    private static readonly Regex BoldCalls = new(@"\\(?:bm|boldsymbol)(?:[ \t]*\{(?<atom>" + BoldAtoms + @")\}|[ \t]*(?<atom>" + BoldAtoms + @"))(?![A-Za-z])");

    private static bool RevisionApplies(RepairRule rule, string text, string log, string engine,
        string? sourcePath, string? projectRoot, string? projectSource)
    {
        string project = ExecutableText(projectSource ?? text);
        switch (rule.Handler)
        {
            case "revision_kpfonts_classic":
            case "revision_kpfonts_slanted":
                string option = rule.Handler == "revision_kpfonts_classic" ? "classicReIm" : "slantedGreeks";
                return engine is "xelatex" or "lualatex" && UnknownKpOption(log, option)
                    && RevisionPackageLines(text, "kpfonts-otf").Any(m => PackageOptions(m.Groups["options"].Value)?.Any(o => o.Trim() == option) == true)
                    && (option != "slantedGreeks" || !Regex.IsMatch(text, @"math-style\s*=|\\unimathsetup\b|\bfrenchstyle\b"));
            case "revision_tabcaption":
                return UndefinedCommand(log, "tabcaption") && !DefinesCommand(project, "tabcaption")
                    && StandaloneMinipageCaptions(project);
            case "revision_cjk_options":
                return engine == "xelatex" && log.Contains("Option clash for package xeCJK.", StringComparison.Ordinal)
                    && (Loads(project, "ctex") || Regex.IsMatch(project, @"\\documentclass(?:\[[^\]]*\])?\{ctex(?:art|book|rep)\}"))
                    && RuntimeCjkLines(text).Any();
            case "revision_legacy_cjk":
                var envs = Regex.Matches(project, @"\\begin\{CJK\}(?:\{(?<encoding>[^{}]+)\}\{(?<family>[^{}]+)\})?");
                return engine == "xelatex" && log.Contains("LaTeX Error: Environment CJK undefined.", StringComparison.Ordinal)
                    && envs.Count > 0 && envs.All(m => m.Groups["encoding"].Value == "UTF8" && m.Groups["family"].Value is "gbsn" or "gkai" or "bsmi" or "bkai")
                    && !Loads(project, "CJKutf8") && !Regex.IsMatch(project, @"\\(?:newenvironment|renewenvironment)\{CJK\}");
            case "revision_bm_letters":
                return engine is "xelatex" or "lualatex" && Loads(project, "unicode-math") && Loads(project, "bm")
                    && Regex.Matches(log, @"(?:Extended math\s*char used as mathchar|Improper alphabetic constant)[\s\S]{0,700}")
                        .Any(m => Regex.IsMatch(m.Value, @"\\(?:bm(?:@|\b)|boldsymbol\b)")) && BoldCalls.IsMatch(text)
                    && !DefinesCommand(WithoutOwnedIsmirRepair(project, projectRoot), "bm") && !DefinesCommand(project, "boldsymbol");
            case "revision_unicode_script":
                return engine is "xelatex" or "lualatex" && ReferencesFamily(project, log, "unicode-math")
                    && log.Contains("Missing { inserted.", StringComparison.Ordinal) && log.Contains(@"\__um_group_begin:", StringComparison.Ordinal)
                    && DiagnosedScript(text, log, sourcePath, projectRoot) is not null;
            case "revision_subfiles_ctex":
                return log.Contains("LaTeX Error: Can be used only in preamble.", StringComparison.Ordinal)
                    && Regex.IsMatch(text, @"\\documentclass\[[^\]\r\n]+\]\{subfiles\}")
                    && LateCtex(text, log, sourcePath, projectRoot) is not null;
            case "revision_frontmatter_font":
                return engine == "xelatex" && log.Contains("Package fontspec Error:", StringComparison.Ordinal)
                    && Regex.IsMatch(log, @"cannot be found|not loadable") && MissingCjkFont(text, log) is not null;
            case "revision_prodint":
                return engine is "xelatex" or "lualatex" && Loads(text, "prodint") && Loads(text, "fontspec") && !Loads(text, "unicode-math")
                    && Regex.IsMatch(Regex.Replace(log, @"\s+", " "), @"Command [`']\\Pi' already defined\.")
                    && DefinesCommand(text, "Pi") && !text.Contains(@"\PassOptionsToPackage", StringComparison.Ordinal);
            case "revision_half_tick":
                return engine == "xelatex" && Regex.IsMatch(Regex.Replace(log, @"\s+", ""), @"Illegalunitofmeasure\(ptinserted\)")
                    && Regex.IsMatch(log, @"(?m)^l\.\d+[^\r\n]*\\halfT[ \t]*\r?$") && HalfTickPixel(text) is not null;
            case "revision_outputbox":
                bool firstDefinition = Regex.IsMatch(log, @"Command \\outputbox already defined\.")
                    && Regex.IsMatch(project, @"\\newtcolorbox(?:\[[^\]]*\])?\{outputbox\}") && !project.Contains("hjfyoutputbox", StringComparison.Ordinal);
                bool laterReference = (projectSource ?? "").Contains("% hjfy: ported-outputbox-name repair", StringComparison.Ordinal)
                    && Regex.Matches(project, @"\\newtcolorbox(?:\[[^\]]*\])?\{hjfyoutputbox\}").Count == 1
                    && Regex.Matches(text, @"\\(?:begin|end)\{outputbox\}").Any(m =>
                        AtDiagnosedLine(text, m, log, "Missing number, treated as zero.", sourcePath, projectRoot)
                        || AtDiagnosedLine(text, m, log, "LaTeX Error: Environment outputbox undefined.", sourcePath, projectRoot));
                return engine == "lualatex" && !DefinesCommand(project, "outputbox") && (firstDefinition || laterReference)
                    && Regex.IsMatch(text, @"\\(?:newtcolorbox(?:\[[^\]]*\])?|begin|end)\{outputbox\}");
            case "revision_sola":
                return Regex.IsMatch(log, @"Extra \}, or forgotten \$\.") && log.Contains(@"\end{tabular}", StringComparison.Ordinal)
                    && ReferencesFamily(project, log, "spr-sola") && !DefinesCommand(project, "endtabular")
                    && ProjectResourceHash(projectRoot, "spr-sola.cls", "786F350A13A0350EE7FA281E0442C83003DADE07754D438CAD92A6DBBC4FA849");
            case "revision_aas_script":
                return engine == "xelatex" && ReferencesFamily(project, log, "xeCJK") && log.Contains("Missing { inserted.", StringComparison.Ordinal)
                    && log.Contains(@"\__kernel_tl_set:Nx", StringComparison.Ordinal)
                    && DiagnosedScript(text, log, sourcePath, projectRoot) is { } script && script.Groups["content"].Value.StartsWith(@"\textrm{", StringComparison.Ordinal);
            case "revision_missing_glyph":
                return CanRepairMathCheckmark(project, log, engine) || CanRepairMissingCjk(project, log, engine);
            case "revision_theorem_text":
                return engine == "xelatex" && ReferencesFamily(project, log, "xeCJK") && !HasOtherTeXError(log) && DiagnosedTheoremBox(text, log) is not null;
            case "revision_titlesec_anchor":
                return !HasOtherTeXError(log) && Loads(text, "titlesec") && Loads(text, "hyperref")
                    && Regex.IsMatch(Regex.Replace(log.Replace("(hyperref)", "", StringComparison.Ordinal), @"\s+", " "), @"The anchor of a bookmark and its parent's must not be the same")
                    && !DefinesCommand(project, "phantomsection") && TitlesecAnchorCalls(text).Count > 0;
            case "revision_appendix_dest":
                Match appendix = Regex.Match(text, @"\\appendix\b");
                var resets = Regex.Matches(text, @"\\setcounter\{equation\}\{([^{}]+)\}");
                return !HasOtherTeXError(log) && Loads(text, "hyperref") && appendix.Success && Regex.Matches(text, @"\\appendix\b").Count == 1
                    && resets.Count == 1 && resets[0].Index > appendix.Index && resets[0].Groups[1].Value == "0"
                    && (Regex.IsMatch(log, @"(?:name\{equation\.[^}]+\}|Object @equation\.[^ \r\n]+ already defined|duplicate destination with the name 'equation\.)")
                        || DuplicateAppendixEquationTargets(text, projectRoot, sourcePath))
                    && !Regex.IsMatch(text, @"\\(?:numberwithin|counterwithin)\{equation\}|\\@addtoreset\{equation\}")
                    && (Regex.IsMatch(text, @"\\documentclass(?:\[[^\]]*\])?\{article\}") || text.Contains(@"\renewcommand{\theHequation}{\arabic{equation}}", StringComparison.Ordinal))
                    && !DefinesCommand(text, "hjfyAppendixHEquation")
                    && (!DefinesCommand(text, "theHequation") || text.Contains(@"\renewcommand{\theHequation}{\arabic{equation}}", StringComparison.Ordinal));
            case "revision_natbib_aux":
                return Loads(text, "natbib") && text.Contains(@"\NAT@cmprs\z@", StringComparison.Ordinal)
                    && text.Contains(@"\hjfy@orig@enddocument", StringComparison.Ordinal)
                    && log.Contains("Missing number, treated as zero.", StringComparison.Ordinal) && Regex.IsMatch(log, @"(?m)^l\.\d+[ \t]+\\end\{document\}");
            case "revision_soul_caption":
            case "revision_soul_highlight":
            case "revision_soul_colored":
                return engine == "xelatex" && ReferencesFamily(project, log, "xeCJK") && Loads(project, "soul") && text.Contains(@"\documentclass", StringComparison.Ordinal)
                    && SoulCandidate(rule, text, log, sourcePath, projectRoot) is not null
                    && (rule.Handler == "revision_soul_colored" || !DefinesCommand(project, rule.Handler == "revision_soul_highlight" ? "hjfyCjkHighlight" : "hjfyCjkUnderline"));
            case "revision_soul_title":
                return Loads(text, "soul") && Loads(text, "ulem") && !Regex.IsMatch(text, @"\\setul(?:color)?\b")
                    && SoulTitle(text, log, sourcePath, projectRoot) is not null;
            case "revision_jmlr":
                return Regex.IsMatch(Regex.Replace(log, @"\s+", " "), @"Argument of \\@tempa has an extra \}|Paragraph ended before \\@tempa was complete|File ended while scanning use of \\@tempa")
                    && ReferencesFamily(project, log, "jmlr") && (ProjectResourceHash(projectRoot, "jmlr.cls", "105BA77A8D57D9E991E34D4B9C9DA60B6FA0334C80CAE6FC21C31E23AE6C1BAA")
                        || ProjectResourceHash(projectRoot, "jmlr.cls", "9BF9275A2CA2144CD09AA8E5A377D9ABD9D53D5C870C15AA1DCD50189AFDFAE4"));
            case "revision_ismir":
                return (Regex.IsMatch(log, @"Command \\setstretch already defined\.") || Loads(project, "bm") && Regex.IsMatch(log, @"Command \\bm already defined\."))
                    && Loads(project, "ismir") && Loads(project, "setspace")
                    && ProjectResourceHash(projectRoot, "ismir.sty", "7A9A3E5DCBC699B42A967D38CFBD2C6D7361E827D617A95AC422A84B5276B919");
            case "revision_libertinus":
                return engine is "xelatex" or "lualatex" && Regex.IsMatch(Regex.Replace(log, @"\s+", ""), @"Packagelibertinus-type1Error:.*Can'tusexelatexorlualatex")
                    && LibertinusLines(text).Any();
            case "revision_bm_color":
                return engine is "xelatex" or "lualatex" && Loads(project, "unicode-math") && Loads(project, "bm") && Loads(project, "xcolor")
                    && !DefinesCommand(project, "textcolor") && !DefinesCommand(WithoutOwnedIsmirRepair(project, projectRoot), "bm")
                    && Regex.IsMatch(project, @"\\newcommand\{\\diff\}\[1\]\{\\textcolor\[rgb\]\{0\.70,\s*0\.29,\s*0\.27\}\{#1\}\}")
                    && Regex.Matches(project, @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let)\s*\{?\\diff\b").Count == 1
                    && Regex.IsMatch(log, @"(?m)^l\.\d+[^\r\n]*\\bm\{\\diff\{")
                    && ColoredBoldCalls(text).Any(m => AtDiagnosedLine(text, m, log, "Extended mathchar used as mathchar", sourcePath, projectRoot)
                        || AtDiagnosedLine(text, m, log, "Missing number, treated as zero.", sourcePath, projectRoot)
                        || AtDiagnosedLine(text, m, log, @"Argument of \__tl_tmp:w has an extra }.", sourcePath, projectRoot));
            case "revision_prime_href":
                return Regex.IsMatch(log, @"Command \\theHassumption already defined\.") && Regex.IsMatch(text, @"\\newtheorem\{assumption\}")
                    && PrimeHrefDeclaration(text) is not null;
            case "revision_profile":
                return log.Contains("File `profiles/anonymous.tex' not found", StringComparison.Ordinal)
                    && Regex.IsMatch(text, @"\\ifx[ \t]+匿名公众") && project.Contains(@"\def\HoffManPaperProfile{public}", StringComparison.Ordinal)
                    && project.Contains(@"\def\HoffManPublicProfileName{public}", StringComparison.Ordinal)
                    && projectRoot is not null && File.Exists(Path.Combine(projectRoot, "profiles", "public", "profile.tex"));
            case "revision_robo_font":
                return engine == "xelatex" && log.Contains("Package fontspec Error:", StringComparison.Ordinal)
                    && FontspecMissingName(log) == "Source Sans Pro"
                    && SourceSansDeclaration(text) is not null && SourceSansFiles(projectRoot);
            case "revision_physical_font":
                return engine == "xelatex" && log.Contains("Cannot proceed without .vf or \"physical\" font for PDF output", StringComparison.Ordinal)
                    && PhysicalFontMap(project, projectRoot) is not null;
            default: return false;
        }
    }

    private static string RewriteRevision(RepairRule rule, string text, RepairContext context, string marker)
    {
        string active = ExecutableText(text);
        switch (rule.Handler)
        {
            case "revision_kpfonts_classic":
            case "revision_kpfonts_slanted":
                string option = rule.Handler == "revision_kpfonts_classic" ? "classicReIm" : "slantedGreeks";
                foreach (Match line in RevisionPackageLines(active, "kpfonts-otf").Reverse())
                {
                    Group group = line.Groups["options"];
                    string[] parts = PackageOptions(group.Value)!;
                    if (!parts.Any(o => o.Trim() == option)) continue;
                    string kept = string.Join(',', parts.Select(o => o.Trim() == option ? option == "classicReIm" ? "fancyReIm" : "" : o).Where(o => o.Length > 0));
                    text = kept.Length == 0 ? text.Remove(group.Index - 1, group.Length + 2) : text.Remove(group.Index, group.Length).Insert(group.Index, kept);
                }
                return marker + (option == "slantedGreeks" ? "\\PassOptionsToPackage{math-style=ISO}{unicode-math}\n" : "") + text;
            case "revision_tabcaption":
                return Inject(text, marker + "\\makeatletter\n\\providecommand{\\tabcaption}{\\def\\@captype{table}\\caption}\n\\makeatother\n", afterClass: true);
            case "revision_cjk_options":
                foreach (Match line in RuntimeCjkLines(active).Reverse())
                {
                    Group options = line.Groups["options"];
                    int start = line.Index + line.Value.IndexOf('\\');
                    int end = line.Index + line.Value.LastIndexOf('}') + 1;
                    text = text.Remove(start, end - start).Insert(start, "\\xeCJKsetup{" + options.Value + "}");
                }
                return marker + text;
            case "revision_legacy_cjk":
                string source = ExecutableText(context.ProjectSource ?? text);
                string snippet = PreserveLatinCjkLoader(source, context.Log);
                var families = Regex.Matches(source, @"\\begin\{CJK\}\{UTF8\}\{(gbsn|gkai|bsmi|bkai)\}").Select(m => m.Groups[1].Value).Distinct();
                foreach (string family in families)
                    snippet += "\\setCJKfamilyfont{hjfy" + family + "}{" + (family is "gkai" or "bkai" ? "FandolKai-Regular.otf" : "FandolSong-Regular.otf") + "}\n";
                return Inject(text, marker + snippet + "\\newenvironment{CJK}[2]{\\CJKfamily{hjfy#2}}{}\n");
            case "revision_bm_letters":
                return marker + BoldCalls.Replace(text, m =>
                {
                    string atom = m.Groups["atom"].Value;
                    // unicode-math chooses upright/italic glyphs according to
                    // the author's math-style and bold-style settings.
                    return @"\symbf{" + atom + "}";
                });
            case "revision_unicode_script":
            case "revision_aas_script":
                Match script = DiagnosedScript(active, context.Log, context.SourcePath, context.ProjectRoot)!;
                Group content = script.Groups["content"];
                return marker + text.Insert(content.Index + content.Length, "}").Insert(content.Index, "{");
            case "revision_subfiles_ctex":
                Match package = LateCtex(active, context.Log, context.SourcePath, context.ProjectRoot)!;
                string moved = text.Substring(package.Index, package.Length);
                // Leave its line comment and the document boundary in place.
                return Inject(text.Remove(package.Index, package.Length), marker + moved + "\n", afterClass: true);
            case "revision_frontmatter_font":
                Match font = MissingCjkFont(active, context.Log)!;
                string name = font.Groups["font"].Value;
                string fallback = name.Contains("Hei", StringComparison.Ordinal) ? "FandolHei-Regular.otf" : name.Contains("Kai", StringComparison.Ordinal) ? "FandolKai-Regular.otf" : "FandolSong-Regular.otf";
                string original = text.Substring(font.Index, font.Length);
                string replacement = original.Replace("{" + name + "}", "{" + fallback + "}", StringComparison.Ordinal);
                return marker + text.Remove(font.Index, font.Length).Insert(font.Index, "\\IfFontExistsTF{" + name + "}{" + original + "}{" + replacement + "}");
            case "revision_prodint":
                return marker + "\\PassOptionsToPackage{no-math}{fontspec}\n" + text;
            case "revision_half_tick":
                Group pixel = HalfTickPixel(active)!.Groups["pixel"];
                string number = Regex.Match(pixel.Value, @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)").Value;
                return marker + text.Remove(pixel.Index, pixel.Length).Insert(pixel.Index, @"\dimexpr" + number + @"\dimexpr65782sp\relax\relax");
            case "revision_outputbox":
                return marker + Regex.Replace(text, @"(\\(?:newtcolorbox(?:\[[^\]]*\])?|begin|end)\{)outputbox(\})", "${1}hjfyoutputbox${2}");
            case "revision_sola":
                return Inject(text, marker + "\\RequirePackage{etoolbox}\n\\makeatletter\n"
                    + "\\AddToHook{begindocument/end}{\\patchcmd{\\endtabular}{\\crcr\\egroup\\egroup$\\egroup}{\\crcr\\egroup\\egroup\\egroup}{}{\\PackageError{hjfy}{Audited sola tabular closure changed}{}}}\n\\makeatother\n", afterClass: true);
            case "revision_missing_glyph":
                string glyphProject = ExecutableText(context.ProjectSource ?? text);
                string glyphPatch = "";
                if (CanRepairMissingCjk(glyphProject, context.Log, context.Engine))
                {
                    string checks = string.Join("", MissingHanGlyphs(context.Log).Where(c => c != 0x2713).Distinct().Select(c => "\\iffontchar\\font\"" + c.ToString("X") + "\\else\\PackageError{hjfy}{Fallback font lacks a diagnosed glyph}{}\\fi\n"));
                    glyphPatch += PreserveLatinCjkLoader(glyphProject, context.Log) + "\\setCJKmainfont{FandolSong-Regular.otf}\n"
                        + "\\newfontfamily\\hjfyCjkGlyphProbe{FandolSong-Regular.otf}\n\\begingroup\\hjfyCjkGlyphProbe\n" + checks + "\\endgroup\n";
                }
                if (CanRepairMathCheckmark(glyphProject, context.Log, context.Engine))
                    glyphPatch += "\\newfontface\\hjfyCheckmarkGlyphProbe{latinmodern-math.otf}\n"
                        + "\\begingroup\\hjfyCheckmarkGlyphProbe\\iffontchar\\font\"2713\\else\\PackageError{hjfy}{Checkmark fallback lacks U+2713}{}\\fi\\endgroup\n"
                        + "\\AddToHook{begindocument/end}{\\protected\\def\\checkmark{\\mathord{\\text{\\hjfyCheckmarkGlyphProbe\\symbol{\"2713}}}}}\n";
                return Inject(text, marker + glyphPatch);
            case "revision_theorem_text":
                Match box = DiagnosedTheoremBox(active, context.Log)!;
                return marker + text.Remove(box.Index, @"\mbox".Length);
            case "revision_titlesec_anchor":
                return marker + TitlesecAnchorCalls(text).Cast<Match>().Reverse().Aggregate(text,
                    (result, call) => result.Insert(call.Groups["toc"].Index, @"\phantomsection"));
            case "revision_appendix_dest":
                Match appendix = Regex.Match(active, @"\\appendix\b");
                return text.Insert(appendix.Index + appendix.Length, "\n" + marker
                    + "\\let\\hjfyAppendixHEquation\\theHequation\n\\renewcommand{\\theHequation}{hjfy.appendix.\\arabic{section}.\\hjfyAppendixHEquation}\n");
            case "revision_natbib_aux":
                return marker + text.Replace(@"\NAT@cmprs\z@", @"\def\NAT@cmprs{0}", StringComparison.Ordinal);
            case "revision_soul_title":
                Group titleCommand = SoulTitle(active, context.Log, context.SourcePath, context.ProjectRoot)!.Groups["command"];
                return marker + text.Remove(titleCommand.Index, titleCommand.Length).Insert(titleCommand.Index, @"\uline");
            case "revision_soul_caption":
            case "revision_soul_highlight":
            case "revision_soul_colored":
                Match candidate = SoulCandidate(rule, active, context.Log, context.SourcePath, context.ProjectRoot)!;
                string load = Loads(ExecutableText(context.ProjectSource ?? text), "xeCJKfntef") ? "" : "\\RequirePackage{xeCJKfntef}\n";
                if (rule.Handler == "revision_soul_colored")
                {
                    var names = SoulCalls(rule, active).Where(m => Regex.IsMatch(m.Groups["literal"].Value, @"[\u4e00-\u9fa5]"))
                        .Select(m => m.Groups["name"].Value).ToHashSet();
                    foreach (Match definition in ColoredSoulDefinitions(active).Where(m => names.Contains(m.Groups["name"].Value)).Reverse())
                    {
                        Group command = definition.Groups["underline"];
                        string coloredReplacement = "\\CJKunderanyline{" + definition.Groups["depth"].Value + "}{\\color{" + definition.Groups["color"].Value
                            + "}\\vrule width1pt height" + definition.Groups["thickness"].Value + "}{#1}";
                        text = text.Remove(command.Index, command.Length).Insert(command.Index, coloredReplacement);
                    }
                    return Inject(text, marker + load, afterClass: true);
                }
                bool highlight = rule.Handler == "revision_soul_highlight";
                string helper = highlight ? "hjfyCjkHighlight" : "hjfyCjkUnderline";
                string body = highlight ? "\\CJKunderanyline{0pt}{\\SOUL@hlcolor{\\vrule width1pt height.8em depth.2em}}{#1}"
                    : "\\CJKunderanyline{\\SOUL@uldepth}{\\SOUL@ulcolor{\\vrule width1pt height\\SOUL@ulthickness}}{#1}";
                foreach (Match literal in SoulCalls(rule, active).Where(m => Regex.IsMatch(m.Groups["literal"].Value, @"[\u4e00-\u9fa5]")).Reverse())
                {
                    Group call = literal.Groups["command"];
                    text = text.Remove(call.Index, call.Length).Insert(call.Index, "\\" + helper);
                }
                return Inject(text, marker + load
                    + "\\makeatletter\n\\DeclareRobustCommand{\\" + helper + "}[1]{" + body + "}\n\\makeatother\n", afterClass: true);
            case "revision_jmlr":
                return Inject(text, marker + "\\RequirePackage{etoolbox}\n\\makeatletter\n"
                    + "\\patchcmd{\\Ginclude@graphics}{\\Gin@getbase{\\Gin@sepdefault\\filename@ext}}{\\edef\\hjfyJmlrExt{\\Gin@sepdefault\\filename@ext}\\edef\\hjfyJmlrExt{\\expandafter\\detokenize\\expandafter{\\hjfyJmlrExt}}\\expandafter\\Gin@getbase\\expandafter{\\hjfyJmlrExt}}{}{}\n"
                    + "\\patchcmd{\\p@subfigure}{\\expandafter\\@p@subfigure}{\\@p@subfigure}{}{}\n\\makeatother\n", afterClass: true);
            case "revision_ismir":
                return marker + IsmirCompatibilityPrelude + text;
            case "revision_libertinus":
                foreach (Match libPackage in LibertinusLines(active).Reverse())
                {
                    Group libName = libPackage.Groups["package"];
                    text = text.Remove(libName.Index, libName.Length).Insert(libName.Index, "libertinus-otf");
                    Group options = libPackage.Groups["options"];
                    text = options.Success ? text.Insert(options.Index, "nomath,") : text.Insert(libPackage.Index + libPackage.Value.IndexOf('{'), "[nomath]");
                }
                bool preserveMath = !Loads(context.ProjectSource ?? active, "fontspec") && !Loads(context.ProjectSource ?? active, "unicode-math");
                return marker + (preserveMath ? "\\PassOptionsToPackage{no-math}{fontspec}\n" : "") + text;
            case "revision_bm_color":
                return marker + ColoredBoldCalls(active).Reverse().Aggregate(text, (current, call) =>
                {
                    string atom = call.Groups["atom"].Value;
                    return current.Remove(call.Index, call.Length).Insert(call.Index, "\\diff{\\symbf{" + atom + "}}");
                });
            case "revision_prime_href":
                Match declaration = PrimeHrefDeclaration(active)!;
                return marker + text.Insert(declaration.Index + 1, "re");
            case "revision_profile":
                return marker + Regex.Replace(text, @"\\ifx[ \t]+匿名公众", @"\ifx\HoffManPaperProfile\HoffManPublicProfileName");
            case "revision_robo_font":
                Match sourceFamily = SourceSansDeclaration(active)!;
                string role = sourceFamily.Groups["role"].Value;
                return marker + text.Remove(sourceFamily.Index, sourceFamily.Length).Insert(sourceFamily.Index, "\\set" + role
                    + "font[Extension=.otf,UprightFont=*-Regular,ItalicFont=*-RegularIt,BoldFont=*-Bold,BoldItalicFont=*-BoldIt]{SourceSansPro}");
            case "revision_physical_font":
                string mapping = PhysicalFontMap(ExecutableText(context.ProjectSource ?? text), context.ProjectRoot)!;
                return Inject(text, marker + "\\AtBeginDvi{\\special{pdf:mapline " + mapping + "}}\n", afterClass: true);
            default: return text;
        }
    }

    private static bool DefinesCommand(string text, string command) => Regex.IsMatch(text,
        @"\\(?:[gex]?def|(?:re)?newcommand|providecommand|let|DeclareRobustCommand|(?:New|Renew|Declare)DocumentCommand)[ \t]*\*?[ \t]*\{?[ \t]*\\" + Regex.Escape(command) + @"\b");
    private static IEnumerable<Match> RuntimeCjkLines(string text) => RevisionPackageLines(text, "xeCJK").Where(m =>
        PackageOptions(m.Groups["options"].Value) is { Length: > 0 } parts && parts.All(o => Regex.IsMatch(o.Trim(),
            @"^(?:CJKmath\s*=\s*(?:true|false)|PunctStyle\s*=\s*(?:plain|quanjiao|banjiao|kaiming)|AutoFakeBold\s*=\s*(?:true|false|[0-9]+(?:\.[0-9]+)?))$")));

    private static bool AtDiagnosedLine(string text, Match candidate, string log, string diagnostic, string? path, string? root)
    {
        Match error = Regex.Match(log, @"(?m)^([^\r\n]+\.tex):(\d+): " + Regex.Escape(diagnostic));
        if (!error.Success || !int.TryParse(error.Groups[2].Value, out int line) || text[..candidate.Index].Count(c => c == '\n') + 1 != line) return false;
        if (path is null) return true;
        string reported = Path.GetFullPath(Path.Combine(root ?? Path.GetDirectoryName(path)!, error.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
        return reported.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }
    private static Match? DiagnosedScript(string text, string log, string? path, string? root) => Regex.Matches(text,
        @"[\^_][ \t]*(?<content>\\(?:mathcal|mathbb|mathscr|mathrm|mathbf|symcal|symbf|symit|symup|textrm)\{[A-Za-z0-9]+\})").Cast<Match>()
        .FirstOrDefault(m => AtDiagnosedLine(text, m, log, "Missing { inserted.", path, root) && ScriptPointerTargets(m, log));
    private static bool ScriptPointerTargets(Match script, string log)
    {
        string content = script.Groups["content"].Value;
        string command = Regex.Match(content, @"^\\[A-Za-z]+").Value;
        return Regex.Matches(log, @"(?m)^l\.\d+[ \t]+([^\r\n]+)").Any(p => p.Groups[1].Value.TrimEnd().EndsWith(command, StringComparison.Ordinal)
            || p.Groups[1].Value.TrimEnd().EndsWith(content, StringComparison.Ordinal));
    }
    private static Match? LateCtex(string text, string log, string? path, string? root)
    {
        int begin = text.IndexOf(@"\begin{document}", StringComparison.Ordinal);
        return Regex.Matches(text, @"\\usepackage[ \t]*(?:\[[^\]\r\n]*\][ \t]*)?\{ctex\}").Cast<Match>()
            .FirstOrDefault(m => begin >= 0 && m.Index > begin && AtDiagnosedLine(text, m, log, "LaTeX Error: Can be used only in preamble.", path, root));
    }
    private static Match? MissingCjkFont(string text, string log) => Regex.Matches(text,
        @"\\setCJKmainfont\{(?<font>SimSun|Simsun|SimHei|KaiTi|STSong|AR PL SungtiL GB|AR PL KaitiM GB|WenQuanYi Micro Hei)\}").Cast<Match>()
        .FirstOrDefault(m => FontspecMissingName(log) == m.Groups["font"].Value);
    private static string? FontspecMissingName(string log)
    {
        string normalized = Regex.Replace(log.Replace("(fontspec)", "", StringComparison.Ordinal), @"\s+", " ");
        var missing = Regex.Match(normalized, "Package fontspec Error: The font \"([^\"]+)\" cannot be found");
        return missing.Success ? missing.Groups[1].Value : null;
    }
    private static Match? UniqueMatch(IEnumerable<Match> matches)
    {
        var first = matches.Take(2).ToArray();
        return first.Length == 1 ? first[0] : null;
    }
    private static Match? HalfTickPixel(string text) => UniqueMatch(Regex.Matches(text,
        @"\\(?:newcommand|renewcommand)\{\\halfT\}\{\\kern(?<pixel>[+-]?(?:\d+(?:\.\d*)?|\.\d+)px)\([^\r\n]*\}").Cast<Match>());
    private static bool ProjectResourceHash(string? root, string file, string hash)
    {
        if (root is null) return false;
        string path = Path.Combine(root, file);
        try { return File.Exists(path) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) == hash; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static bool HasOtherTeXError(string log, bool allowMissingGlyph = false) => Regex.Matches(log,
        @"(?m)^(?:[^\r\n]+\.(?:tex|sty|cls):\d+: |! )(?<message>[^\r\n]+)").Any(m =>
            !m.Groups["message"].Value.Contains("Warning", StringComparison.Ordinal) && !(allowMissingGlyph && m.Groups["message"].Value.StartsWith("Missing character:", StringComparison.Ordinal)));
    private static int[] MissingHanGlyphs(string log) => Regex.Matches(log,
        @"Missing character: There is no [^\r\n]+? \((?:U\+|"")([A-Fa-f0-9]{1,6})\)")
        .Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToArray();
    private static bool CanRepairMathCheckmark(string project, string log, string engine) =>
        engine is "xelatex" or "lualatex" && ReferencesFamily(project, log, "unicode-math")
        && Regex.IsMatch(log, @"(?m)^Missing character: There is no ✓ \((?:U\+|"")2713\) in font [^\r\n]*LibertinusMath-Regular(?:\.otf|[^A-Za-z0-9_-])[^\r\n]*script=math[;:]")
        && Regex.IsMatch(project, @"\\checkmark\b") && !DefinesCommand(project, "checkmark")
        && !project.Contains(@"\hjfyCheckmarkGlyphProbe", StringComparison.Ordinal)
        && !HasOtherTeXError(log, allowMissingGlyph: true);
    private static bool CanRepairMissingCjk(string project, string log, string engine)
    {
        bool mathFallback = CanRepairMathCheckmark(project, log, engine);
        int[] glyphs = MissingHanGlyphs(log).Where(c => c != 0x2713 || !mathFallback).ToArray();
        return engine == "xelatex" && glyphs.Length > 0 && project.Any(c => c is >= '\u4e00' and <= '\u9fa5')
            && glyphs.All(c => (c is >= 0x4e00 and <= 0x9fa5 or >= 0x3000 and <= 0x303f or >= 0xff00 and <= 0xffef) && project.Contains((char)c))
            && !ReferencesFamily(project, log, "xeCJK") && !ReferencesFamily(project, log, "ctex") && !Regex.IsMatch(project, @"\\documentclass(?:\[[^\]]*\])?\{ctex")
            && !project.Contains(@"\hjfyCjkGlyphProbe", StringComparison.Ordinal)
            && !HasOtherTeXError(log, allowMissingGlyph: true);
    }
    private static Match? DiagnosedTheoremBox(string text, string log)
    {
        if (Regex.IsMatch(text, @"\\(?:input|include|subfile)\b") || !text.Contains(@"\documentclass", StringComparison.Ordinal)) return null;
        foreach (Match warning in Regex.Matches(log, @"Overfull \\hbox[^\r\n]*in paragraph at lines (\d+)--(\d+)"))
        {
            int first = int.Parse(warning.Groups[1].Value), last = int.Parse(warning.Groups[2].Value);
            foreach (Match theorem in Regex.Matches(text, @"\\begin\{theorem\}[\s\S]*?\\end\{theorem\}"))
                foreach (Match box in Regex.Matches(text, @"\\mbox\{[\u4e00-\u9fa5，。；：、！？ ]{12,}\}"))
                {
                    int line = text[..box.Index].Count(c => c == '\n') + 1;
                    if (box.Index > theorem.Index && box.Index + box.Length < theorem.Index + theorem.Length && line >= first && line <= last) return box;
                }
        }
        return null;
    }
    private const string SoulLiteral = @"[A-Za-z0-9\u4e00-\u9fa5，。；：、！？ ,.!?;:'-]+";
    private static IEnumerable<Match> ColoredSoulDefinitions(string text) => Regex.Matches(text,
        @"\\newcommand\{\\(?<name>[A-Za-z]+)\}\[1\]\{\\setul\{(?<depth>(?:\d+(?:\.\d*)?|\.\d+)pt)\}\{(?<thickness>(?:\d+(?:\.\d*)?|\.\d+)pt)\}\\setulcolor\{(?<color>[A-Za-z][A-Za-z0-9]*)\}(?<underline>\\ul\{#1\})\}").Cast<Match>();
    private static Match? SoulCandidate(RepairRule rule, string text, string log, string? path, string? root)
    {
        const string diagnostic = "Package soul Error: Reconstruction failed.";
        if (!log.Contains(diagnostic, StringComparison.Ordinal)) return null;
        return SoulCalls(rule, text).FirstOrDefault(m => Regex.IsMatch(m.Groups["literal"].Value, @"[\u4e00-\u9fa5]")
            && AtDiagnosedLine(text, m, log, diagnostic, path, root)
            && (rule.Handler != "revision_soul_colored" || ColoredSoulDefinitions(text).Count(d => d.Groups["name"].Value == m.Groups["name"].Value) == 1));
    }
    private static IEnumerable<Match> SoulCalls(RepairRule rule, string text)
    {
        string pattern = rule.Handler switch
        {
            "revision_soul_caption" => @"\\caption(?:\[[^\]\r\n]*\])?\{[^{}\r\n]*(?<command>\\ul)\{(?<literal>" + SoulLiteral + @")\}[^{}\r\n]*\}",
            "revision_soul_highlight" => @"(?<command>\\hl)\{(?<literal>" + SoulLiteral + @")\}",
            _ => @"\\(?<name>[A-Za-z]+)\{(?<literal>" + SoulLiteral + @")\}"
        };
        return Regex.Matches(text, pattern).Cast<Match>();
    }
    private static Match? SoulTitle(string text, string log, string? path, string? root) => Regex.Matches(text,
        @"\\twocolumn\[\{(?<command>\\ul)\{\\uline\{[A-Za-z0-9 ,.!?-]+\}\}\}\]").Cast<Match>()
        .FirstOrDefault(m => AtDiagnosedLine(text, m, log, @"Argument of \UL@on has an extra }.", path, root));
    private static IEnumerable<Match> LibertinusLines(string text) => Regex.Matches(text,
        @"\\(?:usepackage|RequirePackage)(?:\[(?<options>[^\]\r\n]*)\])?\{(?<package>libertinus-type1)\}").Cast<Match>().Where(m =>
            !m.Groups["options"].Success || PackageOptions(m.Groups["options"].Value) is { } opts && opts.All(o => Regex.IsMatch(o.Trim(),
                @"^(?:lining|nf|lf|oldstyle|osf|tabular|t|proportional|p|semibold|sb|bold|mono)(?:=(?:true|false))?$")));
    private static IEnumerable<Match> ColoredBoldCalls(string text) => Regex.Matches(text,
        @"\\bm\{\\diff\{(?<atom>" + BoldAtoms + @")\}\}").Cast<Match>();
    private static Match? PrimeHrefDeclaration(string text) => UniqueMatch(Regex.Matches(text,
        @"\\newcommand\{\\theHassumption\}\{(?:[A-Za-z0-9.:-]+|\\arabic\{assumption\})+\}").Cast<Match>());
    private static Match? SourceSansDeclaration(string text) => UniqueMatch(Regex.Matches(text,
        @"\\set(?<role>main|sans|mono)font\{Source Sans Pro\}").Cast<Match>());
    private static bool SourceSansFiles(string? root) =>
        ProjectResourceHash(root, "SourceSansPro-Regular.otf", "7134D229B15CDD0827376D8A24F6F531F616EB1B3FECD16E1CF8A86D0BF6BC51")
        && ProjectResourceHash(root, "SourceSansPro-RegularIt.otf", "D38E0D70CD4F4AA072457C5AE647B987F10C3CEA34BFF581EE358A7C2A526053")
        && ProjectResourceHash(root, "SourceSansPro-Bold.otf", "DACCDDBE3DD60FE10F6E8A785EDA187925DA6B611141024DFFA43626998DFC7C")
        && ProjectResourceHash(root, "SourceSansPro-BoldIt.otf", "8449C8884AE97AE11B00581CB61EBA7141462FFC03000030E6DE13CE297F9304");
    private static string? PhysicalFontMap(string text, string? root)
    {
        var fonts = Regex.Matches(text, @"\\font\\[A-Za-z]+[ \t]*=[ \t]*(?<font>[A-Za-z][A-Za-z0-9_-]*)\b");
        if (root is null || fonts.Count != 1 || !Regex.IsMatch(text, @"\\documentclass(?:\[[^\]]*\])?\{article\}")) return null;
        string font = fonts[0].Groups["font"].Value;
        if (!File.Exists(Path.Combine(root, font + ".tfm"))) return null;
        List<string> candidates = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.map"))
            foreach (string line in File.ReadLines(file))
            {
                Match entry = Regex.Match(line, @"^" + Regex.Escape(font) + @"[ \t]+(?<ps>[A-Za-z0-9_-]+)[ \t]+<(?<pfb>[A-Za-z0-9_./-]+\.pfb)[ \t]*$");
                string name = entry.Groups["pfb"].Value;
                if (!entry.Success || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal)) continue;
                string physical = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(physical) && System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(physical))
                    .Contains("/FontName /" + entry.Groups["ps"].Value + " ", StringComparison.Ordinal)) candidates.Add(line.Trim());
            }
        return candidates.Distinct().Count() == 1 ? candidates[0] : null;
    }
    private static Match? EpsfPercentFault(string text)
    {
        var matches = Regex.Matches(text, @"(?m)\\catcode`\\%(?<extra>%)=12[ \t]+\\global\\let\\epsfpercent=%[ \t]*\r?$").Cast<Match>().ToArray();
        if (matches.Length != 1) return null;
        Match fault = matches[0];
        return ExecutableText(text).AsSpan(fault.Index).StartsWith(@"\catcode`\%", StringComparison.Ordinal) ? fault : null;
    }
    private static bool RevisionEpsfApplies(string text, string log, string? path, string? root)
    {
        Match? fault = EpsfPercentFault(text);
        if (fault is null || !log.Contains("Missing number, treated as zero.", StringComparison.Ordinal)) return false;
        Match next = Regex.Match(text, @"\\endgroup", RegexOptions.None, TimeSpan.FromSeconds(3));
        return next.Success && next.Index > fault.Index && AtDiagnosedLine(text, next, log, "Missing number, treated as zero.", path, root);
    }
    private static bool StandaloneMinipageCaptions(string text)
    {
        var calls = Regex.Matches(text, @"\\tabcaption(?=\[|\{)");
        if (calls.Count == 0) return false;
        foreach (Match call in calls)
        {
            Stack<string> stack = new();
            foreach (Match token in Regex.Matches(text[..call.Index], @"\\(?<kind>begin|end)\{(?<env>minipage|table\*?|figure\*?)\}"))
            {
                string env = token.Groups["env"].Value;
                if (token.Groups["kind"].Value == "begin") stack.Push(env);
                else if (stack.Count == 0 || stack.Pop() != env) return false;
            }
            if (!stack.Contains("minipage") || stack.Any(env => env != "minipage")) return false;
        }
        return true;
    }
    // Explicit directory entries after starred headings need their own targets.
    // Restrict this repair to literal titles and the matching heading level.
    private static string IsmirCompatibilityPrelude => "\\RequirePackage{etoolbox}\n\\makeatletter\n" + IsmirNumberlinePatch + "\\AddToHook{package/ismir/before}{%\n"
                    + "\\let\\hjfySetstretch\\setstretch\\let\\hjfyRestoreSpacing\\restore@spacing\\let\\hjfySpacing\\spacing\\let\\hjfyEndspacing\\endspacing\n"
                    + "\\@ifpackageloaded{bm}{\\let\\hjfyIsmirBM\\bm\\let\\bm\\@undefined}{}\n"
                    + "\\let\\setstretch\\@undefined\\let\\restore@spacing\\@undefined\\let\\spacing\\@undefined\\let\\endspacing\\@undefined}\n"
                    + "\\AddToHook{package/ismir/after}{\\let\\setstretch\\hjfySetstretch\\let\\restore@spacing\\hjfyRestoreSpacing\\let\\spacing\\hjfySpacing\\let\\endspacing\\hjfyEndspacing"
                    + "\\@ifpackageloaded{bm}{\\let\\bm\\hjfyIsmirBM}{}\\hjfyIsmirHeadingPatch}\n\\makeatother\n";
    private static string WithoutOwnedIsmirRepair(string project, string? root) =>
        project.Contains(@"\hjfyIsmirHeadingPatch", StringComparison.Ordinal) && ProjectResourceHash(root, "ismir.sty", "7A9A3E5DCBC699B42A967D38CFBD2C6D7361E827D617A95AC422A84B5276B919")
            ? project.Replace(ExecutableText(IsmirCompatibilityPrelude), "", StringComparison.Ordinal) : project;
    // etoolbox nested patching requires literal (category-12) parameter marks.
    private const string IsmirNumberlinePatch = """
        \begingroup\catcode`\#=12
        \gdef\hjfyIsmirHeadingPatch{\makeatletter
        \patchcmd{\@sect}{\protect\numberline
          \if@ismirsection
            {\csname the#1\endcsname.}
          \else
            {\csname the#1\endcsname}
          \fi
        }{\protect\numberline{\csname the#1\endcsname\if@ismirsection.\fi}}{}{\PackageError{hjfy}{Audited ISMIR heading changed}{}}\makeatother}
        \endgroup

        """;
    private static MatchCollection TitlesecAnchorCalls(string text) => Regex.Matches(text,
        @"\\(?<level>section|subsection|subsubsection)\*\{[^{}\r\n]*\}\s*(?<toc>\\addcontentsline\{toc\}\{\k<level>\}\{[^{}\r\n]*\})");
    public static bool WarningDiagnosticResolved(RepairRule rule, string before, string after, string? source = null, string? root = null, string? main = null) => rule.Handler switch
    {
        "revision_missing_glyph" => !MissingHanGlyphs(after).Intersect(MissingHanGlyphs(before)).Any(),
        "revision_theorem_text" => Regex.Matches(after, @"Overfull \\hbox").Count < Regex.Matches(before, @"Overfull \\hbox").Count,
        "revision_titlesec_anchor" => !Regex.Replace(after.Replace("(hyperref)", "", StringComparison.Ordinal), @"\s+", " ")
            .Contains("The anchor of a bookmark and its parent's must not be the same", StringComparison.Ordinal),
        "revision_appendix_dest" => !Regex.IsMatch(after, @"name\{equation\.[^}]+\}|Object @equation\.[^ \r\n]+ already defined|duplicate destination with the name 'equation\.")
            && (source is null || !DuplicateAppendixEquationTargets(ExecutableText(source), root, main)),
        _ => true
    };
    private static string PreserveLatinCjkLoader(string source, string log)
    {
        return "\\makeatletter\n\\@ifpackageloaded{xeCJK}{}{%\n"
            + "\\@ifpackageloaded{fontspec}{}{\\PassOptionsToPackage{no-math}{fontspec}}\n"
            + "\\let\\hjfyCjkSavedEncoding\\encodingdefault\\let\\hjfyCjkSavedRM\\rmdefault\\let\\hjfyCjkSavedSF\\sfdefault\\let\\hjfyCjkSavedTT\\ttdefault\\let\\hjfyCjkSavedFamily\\familydefault\n"
            + "\\expandafter\\let\\expandafter\\hjfyCjkActualFont\\the\\font\n"
            + "\\edef\\hjfyCjkRestoreFont{\\noexpand\\fontencoding{\\f@encoding}\\noexpand\\fontfamily{\\f@family}\\noexpand\\fontseries{\\f@series}\\noexpand\\fontshape{\\f@shape}\\noexpand\\fontsize{\\f@size}{\\f@baselineskip}\\noexpand\\selectfont}\n"
            + "\\RequirePackage{xeCJK}\n"
            + "\\let\\encodingdefault\\hjfyCjkSavedEncoding\\let\\rmdefault\\hjfyCjkSavedRM\\let\\sfdefault\\hjfyCjkSavedSF\\let\\ttdefault\\hjfyCjkSavedTT\\let\\familydefault\\hjfyCjkSavedFamily\n"
            + "\\hjfyCjkRestoreFont\\hjfyCjkActualFont\n}\\makeatother\n";
    }
    private static bool DuplicateAppendixEquationTargets(string text, string? root, string? main)
    {
        if (root is null || main is null) return false;
        int appendix = text.IndexOf(@"\appendix", StringComparison.Ordinal);
        string aux = Path.Combine(root, Path.GetFileNameWithoutExtension(main) + ".aux");
        if (appendix < 0 || !File.Exists(aux)) return false;
        var labels = Regex.Matches(FileStoreAuxText(aux), @"\\newlabel\{(?<label>[^}\r\n]+)\}\{[^\r\n]*\{(?<target>equation\.[^{}\r\n]+)\}\{[^\r\n]*\}")
            .GroupBy(m => m.Groups["label"].Value).ToDictionary(g => g.Key, g => g.Last().Groups["target"].Value);
        HashSet<string> mainTargets = [], appendixTargets = [];
        foreach (Match equation in Regex.Matches(text, @"\\begin\{equation\}[\s\S]*?\\end\{equation\}"))
            foreach (Match label in Regex.Matches(equation.Value, @"\\label\{([^{}]+)\}"))
                if (labels.TryGetValue(label.Groups[1].Value, out string? target))
                    (equation.Index < appendix ? mainTargets : appendixTargets).Add(target);
        return mainTargets.Overlaps(appendixTargets);
    }
    private static string FileStoreAuxText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
