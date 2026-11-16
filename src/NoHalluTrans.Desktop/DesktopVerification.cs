using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using NoHalluTrans.Core;
using NoHalluTrans.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NoHalluTrans.Desktop;

internal static class DesktopVerification
{
    public static async Task RunAsync(App app, MainWindow window, string sample, string reportDirectory, bool coreOnly = false)
    {
        List<string> checks = []; List<string> errors = []; int exitCode = 1;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        async Task CheckDropdown(ComboBox box, string name)
        {
            box.IsDropDownOpen = true; await Task.Delay(300);
            // Items are realized asynchronously; poll rather than rely on a fixed delay.
            Popup? popup = null;
            for (var deadline = DateTime.UtcNow.AddSeconds(2); popup is null; await Task.Delay(50))
            {
                popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Surface.XamlRoot)
                    .LastOrDefault(p => p.IsOpen && p.Child is FrameworkElement child && Descendants(child).OfType<ComboBoxItem>().Any());
                if (popup is null && DateTime.UtcNow > deadline) throw new InvalidOperationException($"dropdown/{name}-did-not-open (IsDropDownOpen={box.IsDropDownOpen})");
            }
            await Task.Delay(100);
            var list = (FrameworkElement)popup!.Child;
            var fieldTop = box.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point());
            var listTop = list.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point());
            Check(list.ActualHeight > 20 && listTop.Y >= 0 && listTop.Y + list.ActualHeight <= window.Surface.ActualHeight + 2 && (listTop.Y >= fieldTop.Y + box.ActualHeight - 2 || listTop.Y + list.ActualHeight <= fieldTop.Y + 2),
                $"dropdown/{name}-outside-field ({listTop.Y:F1}+{list.ActualHeight:F1} / {fieldTop.Y:F1}-{fieldTop.Y + box.ActualHeight:F1})");
            if (!box.IsEditable && box.SelectedItem is not null)
                Check(Descendants(box).OfType<ContentPresenter>().Any(c => c.Visibility == Visibility.Visible && Equals(c.Content, box.SelectedItem)), "dropdown/" + name + "-selection-remains-visible");
            await SaveImageAsync(window.Surface, reportDirectory, name + "-dropdown.png");
            await SaveImageAsync(list, reportDirectory, name + "-menu.png");
            box.IsDropDownOpen = false; await Task.Delay(100);
        }
        try
        {
            Directory.CreateDirectory(reportDirectory);
            Check(DisplayScaling.IsPerMonitorV2(window), "display/per-monitor-v2");
            Check(Math.Abs(window.Surface.XamlRoot.RasterizationScale - DisplayScaling.Scale(window)) < 0.01, "display/native-rasterization-scale");
            var progressProbe = new TaskItem { Status = "运行中" };
            progressProbe.ApplyProgress(new("stage", "parse", "开始切分"));
            progressProbe.ApplyProgress(new("progress", "parse", "切分中", 1, 2, Fragments: 4));
            Check(progressProbe.BlockSummary.Contains("1/2") && progressProbe.BlockSummary.Contains("4 块"), "progress/scanning-counts");
            progressProbe.ApplyProgress(new("stage", "translate", "翻译中"));
            progressProbe.ApplyProgress(new("progress", "translate", "已处理", 3, 4, Fragments: 4, Translated: 2, Cached: 1, Fallback: 0));
            Check(progressProbe.CompletedBlocks == 3 && progressProbe.Percent == 75 && !progressProbe.IsIndeterminate, "progress/translation-counts");
            progressProbe.ApplyProgress(new("stage", "compile_translated", "构建中"));
            Check(progressProbe.IsIndeterminate && progressProbe.BlockSummary.Contains("3/4") && progressProbe.StageLabel.Contains("构建"), "progress/build-keeps-fragment-counts");
            progressProbe.ResetProgress();
            Check(progressProbe.FragmentCount == -1 && progressProbe.CompletedBlocks == 0, "progress/retry-resets-counts");
            var vm = window.Model;
            Check(vm.Settings.WorkRoot == Path.Combine(reportDirectory, "work"), "portable/default-work-directory");
            Check(vm.OutputDirectory == Path.Combine(reportDirectory, "output"), "portable/default-output-directory");
            foreach (string key in new[] { "tasks", "model", "terms", "environment" })
            {
                window.NavigateTo(key); await Task.Delay(400);
                Check(window.CurrentPage == key && window.Surface.ActualWidth > 0, "navigation/" + key);
                await SaveImageAsync(window.Surface, reportDirectory, key + ".png");
                if (key == "terms")
                {
                    var emptyTitle = Descendants(window.Surface).OfType<TextBlock>().Single(t => t.Text == "暂无术语");
                    var emptyPanel = (FrameworkElement)VisualTreeHelper.GetParent(emptyTitle);
                    var titlePoint = emptyTitle.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point());
                    var panelPoint = emptyPanel.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point());
                    Check(Math.Abs(titlePoint.X + emptyTitle.ActualWidth / 2 - panelPoint.X - emptyPanel.ActualWidth / 2) < 1, "layout/terms-empty-text-centered");
                }
                window.Surface.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(200); await SaveImageAsync(window.Surface, reportDirectory, key + "-dark.png");
                window.Surface.RequestedTheme = ElementTheme.Light;
                await Task.Delay(100);
            }
            window.NavigateTo("model");
            await Task.Delay(250);
            var modelSelector = Descendants(window.Surface).OfType<ComboBox>().Single(c => c.Name == "ModelSelector");
            var modelPosition = modelSelector.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point());
            var originalBaseUrl = vm.Settings.Model.BaseUrl;
            vm.Settings.Model.BaseUrl = "http://127.0.0.1:1/v1";
            vm.TestConnectionCommand.Execute(null);
            var connectionDeadline = DateTime.UtcNow.AddSeconds(15);
            while (vm.IsTestingModelConnection && DateTime.UtcNow < connectionDeadline) await Task.Delay(50);
            Check(!vm.IsTestingModelConnection && vm.CanEditModelConnection && vm.IsModelConnectionResultVisible
                && vm.ModelConnectionSeverity == InfoBarSeverity.Error, "model/failed-connection-feedback");
            await Task.Delay(100);
            Check(Math.Abs(modelSelector.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point()).Y - modelPosition.Y) < 1,
                "layout/model-alert-does-not-shift-form");
            await Task.Delay(100); await SaveImageAsync(window.Surface, reportDirectory, "model-test-error.png");
            vm.Settings.Model.BaseUrl = originalBaseUrl;
            vm.ClearModelConnectionResult();
            Check(!vm.IsModelConnectionResultVisible, "model/clear-stale-connection-result");
            vm.Notice = "准备就绪。";
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var modelServerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var modelServer = ServeModelListAsync(listener, modelServerDeadline.Token);
            var apiKeyField = Descendants(window.Surface).OfType<PasswordBox>().Single();
            var modelPage = Descendants(window.Surface).OfType<Views.ModelPage>().Single();
            apiKeyField.Password = "saved-key-sentinel";
            modelPage.SaveCurrentSettings();
            Check(new SettingsStore(vm.DataDirectory).Load().Model.ApiKey == "saved-key-sentinel", "model/immediate-save-key-roundtrip");
            try
            {
                var baseUrlField = Descendants(window.Surface).OfType<TextBox>().Single(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "ModelBaseUrl");
                baseUrlField.Text = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/v1";
                apiKeyField.Password = "unsaved-key-sentinel";
                // Let the queued TextChanged/PasswordChanged handlers run first; they reset the list, and the
                // local server answers fast enough to otherwise race them.
                await Task.Delay(150);
                modelPage.FetchCurrentModels();
                while (vm.IsFetchingModels && !modelServerDeadline.IsCancellationRequested) await Task.Delay(50);
                await modelServer;
                Check(true, "model/unsaved-v1-url-and-key-used-in-model-request");
                Check(vm.AvailableModels.SequenceEqual(new[] { "example/model-a", "example/model-b" }), $"model/fetch-list ([{string.Join(", ", vm.AvailableModels)}], model='{vm.ModelId}', result='{vm.ModelConnectionTitle}: {vm.ModelConnectionMessage}')");
                modelSelector.SelectedItem = "example/model-b";
                await Task.Delay(100);
                Check(vm.ModelId == "example/model-b", "model/dropdown-selection-binding");
                vm.ClearModelConnectionResult();
                modelSelector.IsDropDownOpen = true;
                await Task.Delay(200);
                Check(Math.Abs(modelSelector.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point()).Y - modelPosition.Y) < 1,
                    "layout/model-dropdown-overlays-form");
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Surface.XamlRoot).FirstOrDefault();
                if (popup?.Child is FrameworkElement menu) await SaveImageAsync(menu, reportDirectory, "model-dropdown-menu.png");
                modelSelector.IsDropDownOpen = false;
                modelSelector.Text = "custom/manual-model";
                await Task.Delay(100);
                Check(vm.ModelId == "custom/manual-model", "model/manual-id-binding");
                await SaveImageAsync(window.Surface, reportDirectory, "model-populated.png");
                await CheckDropdown(modelSelector, "model-selector");
            }
            finally { listener.Stop(); apiKeyField.Password = ""; vm.Settings.Model.BaseUrl = originalBaseUrl; vm.ClearModelConnectionResult(); }
            // A freshly built model page (as after reopening the app) must show, and keep, the saved model ID.
            List<string> modelHistory = [];
            void TrackModel(object? _, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.ModelId)) modelHistory.Add(vm.ModelId); }
            vm.PropertyChanged += TrackModel;
            window.NavigateTo("tasks"); window.DropPage("model");
            window.NavigateTo("model"); await Task.Delay(600);
            vm.PropertyChanged -= TrackModel;
            var reopenedSelector = Descendants(window.Surface).OfType<ComboBox>().Single(c => c.Name == "ModelSelector");
            Check(vm.ModelId == "custom/manual-model" && reopenedSelector.Text == "custom/manual-model",
                $"model/reopened-page-shows-saved-model (vm='{vm.ModelId}', text='{reopenedSelector.Text}', changes=[{string.Join(" > ", modelHistory.Select(m => "'" + m + "'"))}])");
            await SaveImageAsync(window.Surface, reportDirectory, "model-reopened.png");
            var parameters = new Views.ModelParameters { DataContext = vm };
            var dialog = new ContentDialog { XamlRoot = window.Surface.XamlRoot, Title = "请求与翻译参数", Content = parameters, CloseButtonText = "完成" };
            var showing = dialog.ShowAsync();
            await Task.Delay(250);
            await CheckDropdown(Descendants(parameters).OfType<ComboBox>().Single(), "thinking-mode");
            var numbers = Descendants(parameters).OfType<NumberBox>().ToArray();
            Check(numbers.Length == 5, "settings/native-number-controls");
            Check(numbers.All(n => n.SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Inline), "settings/spinners-inline");
            double parametersWidth = parameters.ActualWidth, parametersHeight = parameters.ActualHeight;
            int parameterPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Surface.XamlRoot).Count;
            foreach (var number in numbers)
            {
                number.Focus(FocusState.Keyboard); await Task.Delay(100);
                Check(Math.Abs(parameters.ActualWidth - parametersWidth) < 1 && Math.Abs(parameters.ActualHeight - parametersHeight) < 1
                    && VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Surface.XamlRoot).Count == parameterPopups,
                    "settings/focus-stable-" + number.Header);
            }
            numbers[0].Value = 7;
            numbers[0].GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource();
            Check(vm.Settings.Model.BatchSize == 7, "settings/number-binding");
            await SaveImageAsync(parameters, reportDirectory, "model-advanced.png");
            dialog.Hide(); await showing;
            window.NavigateTo("terms");
            vm.TermSource = "attention"; vm.TermTarget = "注意力"; vm.AddTermCommand.Execute(null);
            Check(vm.Terms.Count == 1, "terms/save");
            await Task.Delay(150); await SaveImageAsync(window.Surface, reportDirectory, "terms-filled.png");
            vm.SelectedTerm = vm.Terms[0];
            Check(vm.TermSource == "attention" && vm.TermTarget == "注意力", "terms/selection-populates-editor");
            vm.SelectedTerm = null;
            Check(vm.TermSource == "attention" && vm.TermActionLabel == "添加术语" && !vm.DeleteTermCommand.CanExecute(null), "terms/deselect-keeps-editor-and-disables-delete");
            vm.TermSource = "Attention"; vm.TermTarget = "注意力"; vm.AddTermCommand.Execute(null);
            Check(vm.Terms.Count == 1 && vm.Terms[0].Source == "Attention" && vm.TermSource == "", "terms/update-existing-source");
            vm.TermSearch = "no-such-term";
            Check(vm.FilteredTerms.Count == 0 && vm.NoTermMatches, "terms/search-filter");
            vm.TermSearch = "";
            Check(vm.FilteredTerms.Count == 1, "terms/search-reset");
            vm.TargetLanguage = "ja"; vm.SaveSettingsCommand.Execute(null);
            Check(new SettingsStore(vm.DataDirectory).Load().TargetLanguage == "ja", "settings/target-language-persisted");
            vm.TargetLanguage = "zh-Hans"; vm.SaveSettingsCommand.Execute(null);
            window.NavigateTo("tasks");
            await Task.Delay(250);
            vm.Source = "1706.03762";
            Check(vm.SourceHint.Contains("arXiv") && !vm.SourceHintIsWarning, "tasks/source-hint-arxiv");
            vm.Source = Path.Combine(reportDirectory, "missing-source.zip");
            Check(vm.SourceHintIsWarning, "tasks/source-hint-unknown");
            vm.Source = "";
            vm.Engine = "xelatex";
            Check(vm.AdvancedSummary == "高级选项 · 1", "tasks/advanced-summary-counts");
            vm.Engine = "auto";
            Check(vm.AdvancedSummary == "高级选项", "tasks/advanced-summary-reset");
            Check(vm.HasSetupHint == (vm.ModelNeedsSetup && vm.TranslationsFile == "" || vm.EnvironmentNeedsSetup), "tasks/setup-hint-state");
            foreach (var box in Descendants(window.Surface).OfType<ComboBox>().Where(c => c.IsLoaded && c.ActualHeight > 0).ToArray())
                await CheckDropdown(box, "home-" + (box.Header?.ToString() ?? "select"));
            var taskParameters = new Views.TaskParameters { DataContext = vm };
            var taskDialog = new ContentDialog { XamlRoot = window.Surface.XamlRoot, Title = "翻译选项", Content = taskParameters, CloseButtonText = "完成" };
            var taskShowing = taskDialog.ShowAsync(); await Task.Delay(250);
            foreach (var box in Descendants(taskParameters).OfType<ComboBox>().ToArray()) await CheckDropdown(box, "advanced-" + box.Header);
            Check(Descendants(taskParameters).OfType<ComboBox>().Count() == 2 && Descendants(taskParameters).OfType<CheckBox>().Count() == 2, "tasks/advanced-options-dialog");
            await SaveImageAsync(taskParameters, reportDirectory, "task-options.png");
            taskDialog.Hide(); await taskShowing;
            if (coreOnly)
            {
                Check(!TexRuntimeBundle.IsAvailable(vm.RuntimeRoot), "core-only/no-tex-in-distribution");
                Check(vm.SetupEnvironmentCommand.CanExecute(null) && vm.EnvironmentSetupLabel == "下载环境", "core-only/download-entry-available");
                vm.CheckEnvironmentCommand.Execute(null); await Task.Delay(100);
                Check(!Directory.Exists(vm.RuntimeRoot) && vm.EnvironmentReport.Contains("下载"), "core-only/check-does-not-download");
            }
            else
            {
            vm.Source = sample; vm.TranslationsFile = Path.Combine(sample, "translations.zh-Hans.json");
            vm.OutputDirectory = Path.Combine(reportDirectory, "output"); vm.AddTaskCommand.Execute(null);
            // AsyncCommand remains busy until its dispatcher continuation returns.
            await Task.Delay(100);
            var firstTask = vm.Tasks[0];
            Check(firstTask.Status == "运行中" && vm.AddTaskCommand.CanExecute(null) && vm.AddTaskLabel == "加入队列", "queue/can-add-while-running");
            List<string> executionOrder = [firstTask.Id];
            vm.Tasks.CollectionChanged += (_, args) =>
            {
                if (args.NewItems is null) return;
                foreach (TaskItem item in args.NewItems)
                    item.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(TaskItem.Status) && item.Status == "运行中") executionOrder.Add(item.Id); };
            };
            string secondSource = Path.Combine(reportDirectory, "second-paper");
            Directory.CreateDirectory(secondSource);
            foreach (string file in Directory.GetFiles(sample, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(secondSource, Path.GetRelativePath(sample, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            vm.Source = secondSource;
            vm.PdfLayout = "alternating";
            vm.OutputDirectory = Path.Combine(reportDirectory, "output"); vm.AddTaskCommand.Execute(null);
            await Task.Delay(100);
            var secondTask = vm.Tasks[0];
            Check(secondTask.Status == "排队中" && firstTask.Status == "运行中" && firstTask.Options.Source == sample && firstTask.Options.PdfLayout == "target-only", "queue/new-paper-isolated-from-running-task");
            vm.Source = sample;
            vm.PdfLayout = "side-by-side"; vm.AddTaskCommand.Execute(null);
            await Task.Delay(100);
            Check(vm.Tasks.Count == 3, "queue/three-formats");
            var expectedOrder = vm.Tasks.Reverse().Select(t => t.Id).ToArray();
            Check(vm.Tasks.Count(t => t.Status == "运行中") == 1 && vm.Tasks.Count(t => t.Status == "排队中") == 2, "queue/one-running-two-waiting");
            await SaveImageAsync(window.Surface, reportDirectory, "queue-running.png");
            var deadline = DateTime.UtcNow.AddMinutes(6);
            while (vm.HasActiveTask && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check(!vm.HasActiveTask && vm.AddTaskLabel == "开始翻译", "queue/completion");
            Check(executionOrder.SequenceEqual(expectedOrder), "queue/first-in-first-out");
            foreach (var task in vm.Tasks) Check(task.Status == "已完成" && task.Result?.Fallback == 0 && File.Exists(task.Options.OutputPdf), "pipeline/" + Path.GetFileName(task.Options.OutputPdf));
            Check(Directory.GetFiles(vm.OutputDirectory).Length == 3 && Directory.GetFiles(vm.OutputDirectory).All(p => Path.GetExtension(p) == ".pdf") && !Directory.GetDirectories(vm.OutputDirectory).Any(), "output/final-pdfs-only");
            Check(vm.Tasks.Select(t => t.Options.PdfLayout).Distinct().Count() == 3, "output/all-layouts");
            Check(vm.Tasks[0].Result!.Cached == 10, "cache/all-ten-fragments");
            using var restored = new MainViewModel(new DesktopServices(window), Path.Combine(reportDirectory, "data"), vm.RuntimeRoot);
            Check(restored.Tasks.Count == 3 && restored.Terms.Count == 1, "history/restart");
            Check(vm.TaskCount == 3 && vm.CompletedCount == 3, "overview/live-counts");
            Check(vm.Tasks.All(t => t.SubLabel != t.Status && t.StatusKind == "done" && t.HasOutput && !t.IsActive), "tasks/status-not-repeated");
            Check(vm.ActiveTaskCount == 0 && !vm.HasSetupHint, "tasks/idle-and-ready");
            var keep = vm.SelectedTask;
            vm.TaskFilter = "failed";
            Check(vm.FilteredTasks.Count == 0 && vm.FilteredEmpty, "tasks/filter-failed-empty");
            vm.TaskFilter = "done";
            Check(vm.FilteredTasks.Count == 3, "tasks/filter-done");
            vm.TaskFilter = "all"; vm.TaskSearch = "second";
            Check(vm.FilteredTasks.Count == 1 && vm.FilteredTasks[0].Options.Source == secondSource, "tasks/search");
            vm.TaskSearch = "";
            Check(vm.FilteredTasks.SequenceEqual(vm.Tasks) && vm.SelectedTask == keep, "tasks/filter-reset-keeps-selection");
            }
            await Task.Delay(250);
            await SaveImageAsync(window.Surface, reportDirectory, "desktop.png");
            window.Surface.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(300); await SaveImageAsync(window.Surface, reportDirectory, "desktop-dark.png");
            window.Surface.RequestedTheme = ElementTheme.Light;
            window.AppWindow.Resize(DisplayScaling.Pixels(window, 1040, 720));
            await Task.Delay(250); await SaveImageAsync(window.Surface, reportDirectory, "desktop-compact.png");
            Check(window.Surface.ActualWidth > 0, "layout/compact-window");
            foreach (string key in new[] { "tasks", "model", "terms", "environment" })
            {
                window.NavigateTo(key); await Task.Delay(400);
                var buttons = Descendants(window.Surface).OfType<Button>().Where(b => b.IsLoaded && b.ActualHeight > 0 && b.ActualWidth > 0 && IsShown(b, window.Surface)).ToArray();
                var clipped = buttons.Select(b => (Button: b, Point: b.TransformToVisual(window.Surface).TransformPoint(new Windows.Foundation.Point())))
                    .Where(x => x.Point.Y < 0 || x.Point.Y + x.Button.ActualHeight > window.Surface.ActualHeight + 1 || x.Point.X < 0 || x.Point.X + x.Button.ActualWidth > window.Surface.ActualWidth - 8)
                    .Select(x => $"{x.Button.GetType().Name}#{x.Button.Name}/{Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(x.Button)}@{x.Point.X:F0},{x.Point.Y:F0} {x.Button.ActualWidth:F0}x{x.Button.ActualHeight:F0}").ToArray();
                Check(clipped.Length == 0, "layout/visible-controls-" + key + (clipped.Length == 0 ? "" : $" (surface {window.Surface.ActualWidth:F0}x{window.Surface.ActualHeight:F0}): " + string.Join("; ", clipped)));
                await SaveImageAsync(window.Surface, reportDirectory, key + "-compact.png");
                if (key == "tasks")
                    Check(Descendants(window.Surface).OfType<ListView>().Single(l => l.Name == "TaskList").ActualHeight >= 200, "layout/task-list-min-height-compact");
                if (key == "tasks")
                    foreach (var box in Descendants(window.Surface).OfType<ComboBox>().Where(c => c.IsLoaded && c.ActualHeight > 0 && c.Visibility == Visibility.Visible).ToArray())
                        await CheckDropdown(box, "compact-" + box.Header);
            }
            window.AppWindow.Resize(DisplayScaling.Pixels(window, 1600, 1000));
            foreach (string key in new[] { "tasks", "model", "terms", "environment" })
            {
                window.NavigateTo(key); await Task.Delay(200);
                await SaveImageAsync(window.Surface, reportDirectory, key + "-wide.png");
            }
            Check(app.BindingErrors.Count == 0, "winui/no-binding-errors");
            exitCode = 0;
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally
        {
            errors.AddRange(app.BindingErrors);
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(Path.Combine(reportDirectory, "desktop-results.json"), JsonSerializer.Serialize(new { passed = exitCode == 0, framework = "WinUI 3 / Windows App SDK 2.6.5", coreOnly, checks, errors, os = Environment.OSVersion.ToString() }, JsonDefaults.Options));
            window.Model.CancelAll();
            while (window.Model.HasActiveTask || window.Model.IsEnvironmentBusy) await Task.Delay(50);
            Environment.ExitCode = exitCode;
            window.Close(); app.Exit();
        }
    }
    /// <summary>True when the element and every ancestor up to <paramref name="root"/> are visible and not transparent.</summary>
    private static bool IsShown(UIElement element, UIElement root)
    {
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
            if (node is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity == 0)) return false;
        return true;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task ServeModelListAsync(System.Net.Sockets.TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        List<string> requestLines = [];
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line) requestLines.Add(line);
        if (!requestLines[0].StartsWith("GET /v1/models ", StringComparison.Ordinal) || !requestLines.Any(l => l.Equals("Authorization: Bearer unsaved-key-sentinel", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Model request did not use the current URL and key.");
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"example/model-b\"},{\"id\":\"example/model-a\"}]}");
        byte[] headers = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token); await stream.WriteAsync(body, token);
    }
    private static async Task SaveImageAsync(FrameworkElement element, string directory, string name)
    {
        // RenderTargetBitmap excludes DWM's Mica backdrop. Supply an opaque base
        // only while exporting, so transparent pixels do not appear black in PNG viewers.
        var panel = element as Panel ?? (element as UserControl)?.Content as Panel;
        var background = panel?.Background;
        if (panel is not null) panel.Background = new SolidColorBrush(element.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(element); } finally { if (panel is not null) panel.Background = background; }
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
