using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
namespace NoHalluTrans.Desktop.Views;
public sealed partial class TermsPage : UserControl
{
    public TermsPage()
    {
        InitializeComponent();
        TermList.AddHandler(KeyDownEvent, new KeyEventHandler(async (_, e) => { if (e.Key == VirtualKey.Delete) { e.Handled = true; await ConfirmDeleteAsync(); } }), true);
    }
    private void TermField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || DataContext is not MainViewModel vm) return;
        e.Handled = true;
        // Push the text box value before the command reads it.
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        vm.AddTermCommand.Execute(null);
        if (vm.TermSource.Length == 0) TermSourceBox.Focus(FocusState.Keyboard);
    }
    private async void DeleteTerm_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();
    private async Task ConfirmDeleteAsync()
    {
        if (DataContext is not MainViewModel { SelectedTerm: { } term } vm) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除术语", Content = $"确定删除“{term.Source} → {term.Target}”吗？",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) vm.DeleteTermCommand.Execute(null);
    }
}
