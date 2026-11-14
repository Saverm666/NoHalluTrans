using System.Text;
using NoHalluTrans.Core;

namespace NoHalluTrans.Infrastructure;

// Add a Chinese backend without making it the owner of existing text or math fonts.
internal static class UnicodeFontPreparation
{
    internal const string Loader = """
        % hjfy: prepare_unicode -- preserve author fonts; provide Chinese fallback
        \makeatletter
        \@ifpackageloaded{ctex}{}{\@ifpackageloaded{xeCJK}{}{\@ifpackageloaded{luatexja}{}{\@ifpackageloaded{polyglossia}{}{
        \let\hjfyOwnChineseBackend\relax
        \@ifpackageloaded{fontspec}{}{\PassOptionsToPackage{no-math}{fontspec}}
        \let\hjfySavedEncoding\encodingdefault
        \let\hjfySavedRM\rmdefault\let\hjfySavedSF\sfdefault\let\hjfySavedTT\ttdefault\let\hjfySavedFamily\familydefault
        \edef\hjfyRestoreTextFont{\noexpand\fontencoding{\f@encoding}\noexpand\fontfamily{\f@family}\noexpand\fontseries{\f@series}\noexpand\fontshape{\f@shape}\noexpand\fontsize{\f@size}{\f@baselineskip}\noexpand\selectfont}
        \usepackage[UTF8,fontset=none,heading=false,scheme=plain]{ctex}
        \let\encodingdefault\hjfySavedEncoding
        \let\rmdefault\hjfySavedRM\let\sfdefault\hjfySavedSF\let\ttdefault\hjfySavedTT\let\familydefault\hjfySavedFamily
        \hjfyRestoreTextFont
        }}}}
        \makeatother

        """;

    // fontspec's recorded name and options preserve explicit font files, variants,
    // scale and other features. Guard the metadata lookup for non-fontspec fonts.
    internal static string Selection(PreparedProject project)
    {
        string text = "摘要参考文献图表" + string.Join("\n", project.BodyFiles.Where(File.Exists).Select(path => RepairRules.ExecutableText(FileStore.ReadTex(path))));
        string coverage = string.Join("\n", text.EnumerateRunes().Select(r => r.Value).Distinct().Order()
            .Where(c => c is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af)
            .Select(c => "\\iffontchar\\font\"" + c.ToString("X") + " \\bool_set_true:N \\l_tmpa_bool \\fi"));
        return SelectionTemplate.Replace("% HJFY-COVERAGE", coverage, StringComparison.Ordinal);
    }
    private const string SelectionTemplate = """
        % hjfy: choose Chinese fonts after author font declarations
        \ifdefined\hjfyOwnChineseBackend
        \makeatletter\ExplSyntaxOn
        \cs_new_protected:Npn \hjfy_choose_cjk:NNnn #1#2#3#4
          {
            \group_begin:
            \cs_if_exist:cT {g__fontspec_fontinfo_#1_prop} {\fontencoding{\g_fontspec_encoding_tl}}
            \fontfamily{#1}\selectfont
            \bool_set_false:N \l_tmpa_bool
            % HJFY-COVERAGE
            \bool_if:NTF \l_tmpa_bool
              {
                \cs_if_exist:cTF {g__fontspec_fontinfo_\f@family _prop}
                  {
                \prop_get:cnNTF {g__fontspec_fontinfo_\f@family _prop} {fontname} \l_tmpa_tl
                  {
                    \typeout{HJFY-AUTHOR-CJK-REUSED: \fontname\font}
                    \prop_get:cnN {g__fontspec_fontinfo_\f@family _prop} {options} \l_tmpb_tl
                    \use:x {\group_end: \exp_not:N #2 [\exp_not:V \l_tmpb_tl] {\exp_not:V \l_tmpa_tl}}
                  }
                  {\group_end: #2 [#3] {#4}}
                  }
                  {\group_end: #2 [#3] {#4}}
              }
              {\group_end: #2 [#3] {#4}}
          }
        \hjfy_choose_cjk:NNnn \rmdefault \setCJKmainfont
          {BoldFont=FandolSong-Bold.otf,ItalicFont=FandolKai-Regular.otf}{FandolSong-Regular.otf}
        \hjfy_choose_cjk:NNnn \sfdefault \setCJKsansfont
          {BoldFont=FandolHei-Bold.otf}{FandolHei-Regular.otf}
        \hjfy_choose_cjk:NNnn \ttdefault \setCJKmonofont {}{FandolFang-Regular.otf}
        \ExplSyntaxOff\makeatother
        \ifdefined\setCJKfallbackfamilyfont
          \setCJKfallbackfamilyfont{\CJKrmdefault}[BoldFont=FandolSong-Bold.otf,ItalicFont=FandolKai-Regular.otf]{FandolSong-Regular.otf}
          \setCJKfallbackfamilyfont{\CJKsfdefault}[BoldFont=FandolHei-Bold.otf]{FandolHei-Regular.otf}
          \setCJKfallbackfamilyfont{\CJKttdefault}{FandolFang-Regular.otf}
          \xeCJKenablefallback
        \fi
        \fi

        """;
}
