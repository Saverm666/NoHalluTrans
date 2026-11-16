using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace NoHalluTrans.Desktop;
/// <summary>
/// bool/int/object → Visibility. Parameter "inverse" negates; "is:a,b" is visible when the value's text is one of a, b.
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string? option = parameter as string;
        bool visible = option is not null && option.StartsWith("is:", StringComparison.Ordinal)
            ? option[3..].Split(',').Contains(value?.ToString())
            : value is bool b ? b : value is int n ? n > 0 : value is string s ? s.Length > 0 : value is not null;
        if (option == "inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed class LabelConverter : IValueConverter
{
    public static string Label(string? value) => value switch
    {
        "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", "en" => "English", "ja" => "日本語", "ko" => "한국어",
        "fr" => "Français", "de" => "Deutsch", "es" => "Español", "auto" => "自动选择（推荐）",
        "target-only" => "仅目标语言", "alternating" => "双语交替", "side-by-side" => "双栏并排（原文｜译文）",
        "default" => "服务默认", "disabled" => "关闭", "enabled" => "开启", _ => value ?? ""
    };
    public object Convert(object value, Type targetType, object parameter, string language) => value is string text ? Label(text) : value;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
