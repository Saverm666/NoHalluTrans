using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using NoHalluTrans.Desktop.Views;

namespace NoHalluTrans.Desktop;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    private readonly Dictionary<string, UserControl> pages = [];
    private bool allowClose;
    public FrameworkElement Surface => Root;
    public string CurrentPage { get; private set; } = "tasks";
    public MainWindow(string? dataDirectory = null, string? texRoot = null)
    {
        InitializeComponent();
        Model = new(new DesktopServices(this), dataDirectory, texRoot);
        Model.NavigationRequested += NavigateTo;
        Root.DataContext = Model;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var initialSize = DisplayScaling.Pixels(this, 1280, 880);
        AppWindow.Resize(new SizeInt32(Math.Min(initialSize.Width, workArea.Width), Math.Min(initialSize.Height, workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var minimumSize = DisplayScaling.Pixels(this, 1040, 720);
            presenter.PreferredMinimumWidth = Math.Min(minimumSize.Width, workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min(minimumSize.Height, workArea.Height);
        }
        AppWindow.Closing += AppWindow_Closing;
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First();
        NavigateTo("tasks");
        RegisterShortcuts();
    }
    /// <summary>Ctrl+1..4 switch pages; Ctrl+N focuses the paper source box.</summary>
    private void RegisterShortcuts()
    {
        string[] keys = ["tasks", "terms", "model", "environment"];
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number1 + i, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) => { e.Handled = true; NavigateTo(key); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        var newTask = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        newTask.Invoked += (_, e) => { e.Handled = true; NavigateTo("tasks"); (pages["tasks"] as TasksPage)?.FocusSource(); };
        Root.KeyboardAccelerators.Add(newTask);
        Root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }
    public void NavigateTo(string key)
    {
        if (!pages.TryGetValue(key, out var page))
        {
            page = key switch { "tasks" => new TasksPage(), "terms" => new TermsPage(), "model" => new ModelPage(), "environment" => new EnvironmentPage(), _ => throw new ArgumentOutOfRangeException(nameof(key)) };
            page.DataContext = Model; pages.Add(key, page);
        }
        CurrentPage = key; PageHost.Content = page;
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == key);
        if (!ReferenceEquals(Navigation.SelectedItem, item)) Navigation.SelectedItem = item;
    }
    /// <summary>Verification hook: forget a cached page so the next navigation builds it fresh, as after a restart.</summary>
    internal void DropPage(string key)
    {
        if (CurrentPage == key) PageHost.Content = null;
        pages.Remove(key);
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    { if (Model is not null && args.SelectedItemContainer?.Tag is string key) NavigateTo(key); }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "导入论文源码"; }
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try { var files = await e.DataView.GetStorageItemsAsync(); if (files.Count > 0) { Model.SetDroppedSource(files[0].Path); Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(); NavigateTo("tasks"); } }
        catch (Exception ex) { Model.Notice = ex.Message; Model.NoticeIsError = true; }
        finally { deferral.Complete(); }
    }
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        if (Model.HasActiveTask || Model.IsEnvironmentBusy)
        {
            args.Cancel = true;
            if (!await new DesktopServices(this).ConfirmAsync("关闭 NoHalluTrans", "关闭将取消正在运行的任务或环境安装。已完成译文保留在缓存中；未完成的环境不会启用。")) return;
            Model.CancelAll();
            while (Model.HasActiveTask || Model.IsEnvironmentBusy) await Task.Delay(50);
            allowClose = true; Model.Dispose(); Close();
        }
        else { Model.CancelAll(); Model.Dispose(); }
    }
}
