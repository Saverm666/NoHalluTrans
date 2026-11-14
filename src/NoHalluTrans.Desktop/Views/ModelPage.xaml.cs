using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace NoHalluTrans.Desktop.Views;
public sealed partial class ModelPage : UserControl
{
    private bool fieldsInitialized;
    private string? lastBaseUrl;
    public ModelPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (!fieldsInitialized)
            {
                ApiKeyBox.Password = vm.Settings.Model.ApiKey;
                lastBaseUrl = vm.Settings.Model.BaseUrl;
                fieldsInitialized = true;
            }
            if (ModelSelector.Text != vm.ModelId) ModelSelector.Text = vm.ModelId;
        };
        Unloaded += (_, _) => SyncCurrentFields();
    }
    private void SyncCurrentFields()
    {
        if (!fieldsInitialized || DataContext is not MainViewModel vm) return;
        // Read the controls before commands: PasswordChanged/TextChanged can still be queued.
        string selectedModel = ModelSelector.Text;
        if (vm.Settings.Model.ApiKey != ApiKeyBox.Password)
        { vm.Settings.Model.ApiKey = ApiKeyBox.Password; vm.ResetAvailableModels(); }
        vm.Settings.Model.BaseUrl = BaseUrlBox.Text;
        vm.ModelId = selectedModel;
    }
    public void FetchCurrentModels() { SyncCurrentFields(); if (DataContext is MainViewModel vm) vm.FetchModelsCommand.Execute(null); }
    public void SaveCurrentSettings() { SyncCurrentFields(); if (DataContext is MainViewModel vm) vm.SaveSettingsCommand.Execute(null); }
    private void FetchModels_Click(object sender, RoutedEventArgs e) => FetchCurrentModels();
    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveCurrentSettings();
    private void TestConnection_Click(object sender, RoutedEventArgs e)
    { SyncCurrentFields(); if (DataContext is MainViewModel vm) vm.TestConnectionCommand.Execute(null); }
    private void ApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!fieldsInitialized || DataContext is not MainViewModel vm) return;
        if (vm.Settings.Model.ApiKey != ApiKeyBox.Password)
        {
            string selected = ModelSelector.Text;
            vm.Settings.Model.ApiKey = ApiKeyBox.Password;
            vm.ResetAvailableModels();
            if (!string.IsNullOrEmpty(selected) && ModelSelector.Text != selected) ModelSelector.Text = selected;
        }
    }
    private void ConnectionField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!fieldsInitialized || DataContext is not MainViewModel vm) return;
        if (string.Equals(BaseUrlBox.Text, lastBaseUrl, StringComparison.Ordinal)) return;
        lastBaseUrl = BaseUrlBox.Text;
        string selected = ModelSelector.Text;
        vm.ResetAvailableModels();
        if (!string.IsNullOrEmpty(selected) && ModelSelector.Text != selected) ModelSelector.Text = selected;
    }
    private void ConnectionResult_Closed(InfoBar sender, InfoBarClosedEventArgs args) { if (DataContext is MainViewModel vm) vm.ClearModelConnectionResult(); }
    private async void Advanced_Click(object sender, RoutedEventArgs e) => await ShowAdvancedAsync();
    public async Task ShowAdvancedAsync()
    {
        var content = new ModelParameters { DataContext = DataContext };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "请求与翻译参数", Content = content, CloseButtonText = "完成", DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
        if (DataContext is MainViewModel vm) vm.ClearModelConnectionResult();
    }
}
