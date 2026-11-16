using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace NoHalluTrans.Desktop;

public interface IDesktopServices
{
    Task<string?> PickSourceAsync();
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickJsonAsync();
    Task<string?> SaveAsync(string extension, string suggestedName);
    Task<bool> ConfirmAsync(string title, string message);
    void CopyText(string text);
    void RevealInExplorer(string path);
}
public sealed class DesktopServices(Window window) : IDesktopServices
{
    private T Initialize<T>(T picker)
    { WinRT.Interop.InitializeWithWindow.Initialize(picker!, WinRT.Interop.WindowNative.GetWindowHandle(window)); return picker; }
    public async Task<string?> PickSourceAsync()
    {
        var picker = Initialize(new FileOpenPicker { CommitButtonText = "导入源码" });
        foreach (string extension in new[] { ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".tex" }) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }
    public async Task<string?> PickFolderAsync(string title)
    {
        var picker = Initialize(new FolderPicker { CommitButtonText = title }); picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public async Task<string?> PickJsonAsync()
    { var picker = Initialize(new FileOpenPicker()); picker.FileTypeFilter.Add(".json"); return (await picker.PickSingleFileAsync())?.Path; }
    public async Task<string?> SaveAsync(string extension, string suggestedName)
    {
        var picker = Initialize(new FileSavePicker { SuggestedFileName = suggestedName });
        picker.FileTypeChoices.Add(extension.ToUpperInvariant().TrimStart('.') + " 文件", new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = title, Content = message, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
    public void RevealInExplorer(string path)
    {
        // Explorer expects the quoted path after the comma, not a fully quoted switch.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = false });
    }
}
