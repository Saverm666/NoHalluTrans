using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
namespace NoHalluTrans.Desktop.Views;
public sealed partial class TasksPage : UserControl
{
    public TasksPage()
    {
        InitializeComponent();
        // ListView consumes Enter/Delete itself; listen to handled events as well.
        TaskList.AddHandler(KeyDownEvent, new KeyEventHandler(TaskList_KeyDown), true);
    }
    private MainViewModel? Model => DataContext as MainViewModel;
    public void FocusSource() { SourceBox.Focus(FocusState.Keyboard); SourceBox.SelectAll(); }
    private async void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "翻译选项", Content = new TaskParameters { DataContext = DataContext }, CloseButtonText = "完成", DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }
    private void SetupHint_Click(object sender, RoutedEventArgs e) { if (Model is { SetupHintTarget.Length: > 0 } vm) vm.RequestNavigation(vm.SetupHintTarget); }
    private void Source_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Model is not { } vm) return;
        e.Handled = true;
        vm.AddTaskCommand.Execute(null);
    }
    private void Filter_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    { if (Model is { } vm && sender.SelectedItem?.Tag is string filter) vm.TaskFilter = filter; }
    private void TaskList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TaskItem && Model is { } vm) Run(vm.OpenPdfCommand);
    }
    private async void TaskList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Model is not { SelectedTask: not null } vm) return;
        if (e.Key == VirtualKey.Enter) { e.Handled = true; Run(vm.OpenPdfCommand); }
        else if (e.Key == VirtualKey.Delete) { e.Handled = true; await ConfirmDeleteAsync(); }
    }
    private void TaskList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Model is not { } vm) return;
        // Right-clicking an item selects it first; all commands act on the selection.
        if ((args.OriginalSource as FrameworkElement)?.DataContext is TaskItem task) vm.SelectedTask = task;
        if (vm.SelectedTask is null) return;
        args.Handled = true;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, ICommand command) =>
            new() { Text = text, Icon = new FontIcon { Glyph = glyph }, Command = command };
        menu.Items.Add(Item("打开译文", "", vm.OpenPdfCommand));
        menu.Items.Add(Item("在文件夹中显示", "", vm.RevealPdfCommand));
        menu.Items.Add(Item("复制输出路径", "", vm.CopyOutputPathCommand));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (vm.SelectedTask.IsActive) menu.Items.Add(Item("取消任务", "", vm.CancelCommand));
        else
        {
            menu.Items.Add(Item("重新运行", "", vm.RetryCommand));
            var delete = new MenuFlyoutItem { Text = "删除任务…", Icon = new FontIcon { Glyph = "" }, IsEnabled = vm.CanDeleteSelectedTask };
            delete.Click += async (_, _) => await ConfirmDeleteAsync();
            menu.Items.Add(delete);
        }
        var target = args.OriginalSource as FrameworkElement ?? TaskList;
        if (args.TryGetPosition(target, out var point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
    }
    private static void Run(ICommand command) { if (command.CanExecute(null)) command.Execute(null); }
    private async void DeleteTask_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();
    private async Task ConfirmDeleteAsync()
    {
        if (Model is not { SelectedTask: { } task } vm || !vm.DeleteTaskCommand.CanExecute(null)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除任务",
            Content = $"确定要删除任务“{task.Title}”及其工作目录吗？\n已生成的译文 PDF 不会被删除。此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) vm.DeleteTaskCommand.Execute(null);
    }
    private async void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } vm || !vm.ClearFinishedCommand.CanExecute(null)) return;
        int count = vm.Tasks.Count(t => !t.IsActive);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除已结束任务",
            Content = $"将删除 {count} 个已完成、失败、已取消或已中断的任务记录及其工作目录。\n输出目录中的译文 PDF 会保留，翻译缓存不受影响。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) vm.ClearFinishedCommand.Execute(null);
    }
}
