using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace NoHalluTrans.Desktop.Views;
public sealed partial class TasksPage : UserControl
{
    public TasksPage() => InitializeComponent();
    private async void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "翻译选项", Content = new TaskParameters { DataContext = DataContext }, CloseButtonText = "完成", DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        var logs = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 350, MinWidth = 420, FontSize = 12, Text = "" };
        logs.SetBinding(TextBox.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = DataContext, Path = new PropertyPath("TaskLog"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "运行详情", Content = logs, CloseButtonText = "关闭" };
        await dialog.ShowAsync();
    }
    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedTask is not { } task) return;
        if (!vm.DeleteTaskCommand.CanExecute(null)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除任务",
            Content = $"确定要删除任务“{task.Title}”及其工作目录吗？\n此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            vm.DeleteTaskCommand.Execute(null);
    }
}
