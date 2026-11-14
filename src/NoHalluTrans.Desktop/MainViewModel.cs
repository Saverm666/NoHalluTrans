using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using NoHalluTrans.Core;
using NoHalluTrans.Infrastructure;

namespace NoHalluTrans.Desktop;

public sealed class TaskItem : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public PipelineOptions Options { get; set; } = new();
    public ModelSettings Model { get; set; } = new();
    public List<TermEntry> Terms { get; set; } = [];
    public string WorkDirectory { get; set; } = "";
    private string status = "排队中";
    public string Status { get => status; set { if (Set(ref status, value)) { Changed(nameof(StageLabel)); Changed(nameof(IsIndeterminate)); } } }
    private string detail = "等待开始";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private double percent;
    public double Percent { get => percent; set => Set(ref percent, value); }
    public PipelineResult? Result { get; set; }
    public string[] MainCandidates { get; set; } = [];
    public string StageId { get; set; } = "";
    public int FragmentCount { get; set; } = -1;
    public int CompletedBlocks { get; set; }
    public int TranslatedBlocks { get; set; }
    public int CachedBlocks { get; set; }
    public int FallbackBlocks { get; set; }
    public int ScannedFiles { get; set; }
    public int TotalFiles { get; set; }
    [JsonIgnore] public string StageLabel => Status != "运行中" ? Status : StageId switch
    {
        "prepare_cache" or "prepare_source" => "1/5 · 获取源码",
        "compile_original" => "2/5 · 检查原文与编译环境",
        "parse" => "3/5 · 提取与切分",
        "terminology" => "3/5 · 提取术语",
        "translate" => "4/5 · 翻译正文",
        "reassemble" or "compile_translated" or "compose_pdf" or "write_output" => "5/5 · 构建 PDF",
        _ => "准备任务"
    };
    [JsonIgnore] public string BlockSummary => FragmentCount < 0 ? "正文尚未切分" : StageId == "parse"
        ? $"已扫描 {ScannedFiles}/{TotalFiles} 个文件 · 已切分 {FragmentCount} 块"
        : $"已切分 {FragmentCount} 块 · 已处理 {CompletedBlocks}/{FragmentCount}\n新译 {TranslatedBlocks} · 缓存 {CachedBlocks} · 保留原文 {FallbackBlocks}";
    [JsonIgnore] public bool IsIndeterminate => Status == "运行中" && !(StageId == "translate" && FragmentCount > 0 || StageId == "parse" && TotalFiles > 0);
    public void ApplyProgress(PipelineEvent value)
    {
        if (value.Type == "stage") { StageId = value.StageId; Percent = 0; }
        if (value.Fragments is { } count) FragmentCount = count;
        if (value.Translated is { } translated) TranslatedBlocks = translated;
        if (value.Cached is { } cached) CachedBlocks = cached;
        if (value.Fallback is { } fallback) FallbackBlocks = fallback;
        if (value.Type == "progress" && value.Completed is { } completed && value.Total is { } total)
        {
            if (value.StageId == "translate") CompletedBlocks = completed;
            if (value.StageId == "parse") { ScannedFiles = completed; TotalFiles = total; }
            Percent = total > 0 ? 100d * completed / total : 0;
        }
        Detail = value.Message;
        Changed(nameof(StageLabel)); Changed(nameof(BlockSummary)); Changed(nameof(IsIndeterminate));
    }
    public void ResetProgress()
    {
        StageId = ""; FragmentCount = -1; CompletedBlocks = TranslatedBlocks = CachedBlocks = FallbackBlocks = ScannedFiles = TotalFiles = 0;
        Percent = 0;
        Changed(nameof(StageLabel)); Changed(nameof(BlockSummary)); Changed(nameof(IsIndeterminate));
    }
    [JsonIgnore] public string Title => Path.GetFileName(Options.Source.TrimEnd('/', '\\')) is { Length: > 0 } title ? title : Options.Source;
    [JsonIgnore] public string CreatedLabel => Created.ToLocalTime().ToString("MM-dd HH:mm");
    [JsonIgnore] public string OutputLabel => Options.OutputPdf;
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly SettingsStore store;
    private readonly IDesktopServices dialogs;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ProcessRunner runner = new();
    private readonly string runtimeRoot;
    private CancellationTokenSource? activeCancellation;
    private CancellationTokenSource? environmentCancellation;
    private bool isEnvironmentBusy;
    private string environmentStage = "";
    private double environmentPercent;
    private bool isEnvironmentIndeterminate = true;
    public bool IsEnvironmentBusy { get => isEnvironmentBusy; private set { if (Set(ref isEnvironmentBusy, value)) { Changed(nameof(CanManageEnvironment)); CommandHub.Refresh(); } } }
    public bool CanManageEnvironment => !IsEnvironmentBusy && !HasActiveTask;
    public bool IsEnvironmentIndeterminate { get => isEnvironmentIndeterminate; private set => Set(ref isEnvironmentIndeterminate, value); }
    public double EnvironmentPercent { get => environmentPercent; private set => Set(ref environmentPercent, value); }
    public string EnvironmentStage { get => environmentStage; private set => Set(ref environmentStage, value); }
    public bool IsEnvironmentReady => TexRuntimeBundle.IsReady(runtimeRoot);
    public string EnvironmentSetupLabel => Directory.Exists(runtimeRoot) ? "修复环境" : "下载环境";
    public string EnvironmentSummary => IsEnvironmentReady ? "已安装" : "未安装";
    private bool pumping;
    public AppSettings Settings { get; }
    public ObservableCollection<TaskItem> Tasks { get; }
    public ObservableCollection<TermEntry> Terms { get; }
    public string[] Languages { get; } = ["zh-Hans", "zh-Hant", "en", "ja", "ko", "fr", "de", "es"];
    public string[] Engines { get; } = ["auto", "xelatex", "lualatex", "pdflatex"];
    public string[] ThinkingModes { get; } = ["default", "disabled", "enabled"];
    public string RuntimeRoot => runtimeRoot;
    public event Action<string>? NavigationRequested;
    public string DataDirectory => store.DataDirectory;
    private string source = "", sourceLanguage = "en", targetLanguage = "zh-Hans", engine = "auto", mainHint = "", translationsFile = "";
    public string Source { get => source; set => Set(ref source, value); }
    public string OutputDirectory { get => Settings.OutputDirectory; set { if (Settings.OutputDirectory == value) return; Settings.OutputDirectory = value; Changed(); } }
    public string[] PdfLayouts { get; } = ["target-only", "alternating", "side-by-side"];
    public string PdfLayout { get => Settings.PdfLayout; set { if (Settings.PdfLayout == value) return; Settings.PdfLayout = value; Changed(); } }
    public string TargetLanguage { get => targetLanguage; set => Set(ref targetLanguage, value); }
    public string SourceLanguage { get => sourceLanguage; set => Set(ref sourceLanguage, value); }
    public string Engine { get => engine; set => Set(ref engine, value); }
    public string MainHint { get => mainHint; set => Set(ref mainHint, value); }
    public string TranslationsFile { get => translationsFile; set => Set(ref translationsFile, value); }
    public bool AllowShellEscape { get; set; }
    public bool AutoTerminology { get; set; }
    private string notice = "";
    public string Notice { get => notice; set { if (Set(ref notice, value)) NoticeIsError = false; } }
    private bool noticeIsError;
    public bool NoticeIsError { get => noticeIsError; set => Set(ref noticeIsError, value); }
    private bool isTestingModelConnection, isModelConnectionResultVisible, isFetchingModels;
    public ObservableCollection<string> AvailableModels { get; } = [];
    public string ModelId
    {
        get => Settings.Model.Model;
        set { if (Settings.Model.Model != value) { Settings.Model.Model = value; Changed(); ClearModelConnectionResult(); } }
    }
    public bool IsFetchingModels { get => isFetchingModels; private set { if (Set(ref isFetchingModels, value)) Changed(nameof(CanEditModelConnection)); } }
    private string modelConnectionTitle = "", modelConnectionMessage = "";
    private Microsoft.UI.Xaml.Controls.InfoBarSeverity modelConnectionSeverity;
    public bool IsTestingModelConnection { get => isTestingModelConnection; private set { if (Set(ref isTestingModelConnection, value)) Changed(nameof(CanEditModelConnection)); } }
    public bool CanEditModelConnection => !IsTestingModelConnection && !IsFetchingModels;
    public bool IsModelConnectionResultVisible { get => isModelConnectionResultVisible; private set => Set(ref isModelConnectionResultVisible, value); }
    public string ModelConnectionTitle { get => modelConnectionTitle; private set => Set(ref modelConnectionTitle, value); }
    public string ModelConnectionMessage { get => modelConnectionMessage; private set => Set(ref modelConnectionMessage, value); }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity ModelConnectionSeverity { get => modelConnectionSeverity; private set => Set(ref modelConnectionSeverity, value); }
    private string environmentReport = "尚未检查";
    public string EnvironmentReport { get => environmentReport; set => Set(ref environmentReport, value); }
    private string taskLog = "选择一个任务查看详情。";
    public string TaskLog { get => taskLog; set => Set(ref taskLog, value); }
    private TaskItem? selectedTask;
    public TaskItem? SelectedTask
    {
        get => selectedTask;
        set { if (Set(ref selectedTask, value)) { RefreshDetails(); Changed(nameof(MainCandidates)); Changed(nameof(HasSelectedTask)); CommandHub.Refresh(); } }
    }
    public string[] MainCandidates => SelectedTask?.MainCandidates ?? [];
    public bool CanDeleteSelectedTask => SelectedTask is not null && SelectedTask.Status is not ("运行中" or "排队中");
    private TermEntry? selectedTerm;
    public TermEntry? SelectedTerm
    {
        get => selectedTerm;
        set
        {
            if (!Set(ref selectedTerm, value) || value is null) return;
            TermSource = value.Source; TermTarget = value.Target; TermCaseSensitive = value.CaseSensitive;
        }
    }
    private string termSource = "", termTarget = "";
    private bool termCaseSensitive;
    public string TermSource { get => termSource; set => Set(ref termSource, value); }
    public string TermTarget { get => termTarget; set => Set(ref termTarget, value); }
    public bool TermCaseSensitive { get => termCaseSensitive; set => Set(ref termCaseSensitive, value); }
    public string SelectedMainCandidate { get; set; } = "";
    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseTranslationsCommand { get; }
    public ICommand AddTaskCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand OpenPdfCommand { get; }
    public ICommand OpenOriginalCommand { get; }
    public ICommand OpenWorkCommand { get; }
    public ICommand ExportSourceCommand { get; }
    public ICommand ExportLogsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand FetchModelsCommand { get; }
    public ICommand CheckEnvironmentCommand { get; }
    public ICommand SetupEnvironmentCommand { get; }
    public ICommand CancelEnvironmentCommand { get; }
    public ICommand BrowseWorkCommand { get; }
    public ICommand AddTermCommand { get; }
    public ICommand DeleteTermCommand { get; }
    public ICommand ImportTermsCommand { get; }
    public ICommand ExportTermsCommand { get; }
    public bool HasActiveTask => pumping;
    public string AddTaskLabel => HasActiveTask ? "加入队列" : "开始翻译";
    public MainViewModel(IDesktopServices dialogs, string? dataDirectory = null, string? texRoot = null)
    {
        this.dialogs = dialogs;
        store = new(dataDirectory);
        var portableRoot = PortablePaths.Root;
        runtimeRoot = texRoot ?? Path.Combine(portableRoot, "runtime", "texlive");
        Settings = store.Load();
        if (!string.IsNullOrWhiteSpace(Settings.Model.Model)) AvailableModels.Add(Settings.Model.Model);
        Tasks = new(store.LoadHistory<List<TaskItem>>()); Terms = new(Settings.Terms);
        foreach (var task in Tasks.Where(t => t.Status is "运行中" or "排队中")) { task.Status = "已中断"; task.Detail = "上次运行已中断，可重新运行并复用翻译缓存。"; }
        SaveHistory();
        BrowseSourceCommand = new AsyncCommand(async () => { if (await dialogs.PickSourceAsync() is { } path) Source = path; }, Error);
        BrowseFolderCommand = new AsyncCommand(async () => { if (await dialogs.PickFolderAsync("选择工程") is { } path) Source = path; }, Error);
        BrowseOutputCommand = new AsyncCommand(async () => { if (await dialogs.PickFolderAsync("选择 PDF 输出目录") is { } path) OutputDirectory = path; }, Error);
        BrowseTranslationsCommand = new AsyncCommand(async () => { if (await dialogs.PickJsonAsync() is { } path) TranslationsFile = path; }, Error);
        AddTaskCommand = new AsyncCommand(AddTaskAsync, Error, () => !IsEnvironmentBusy);
        CancelCommand = new RelayCommand(CancelSelected, () => SelectedTask?.Status is "运行中" or "排队中");
        RetryCommand = new RelayCommand(() => Guard(RetrySelected), () => !IsEnvironmentBusy && SelectedTask is not null && SelectedTask.Status is not ("运行中" or "排队中"));
        DeleteTaskCommand = new RelayCommand(() => Guard(DeleteSelected), () => SelectedTask is not null && SelectedTask.Status is not ("运行中" or "排队中"));
        OpenPdfCommand = new RelayCommand(() => Guard(() => Open(SelectedTask!.Options.OutputPdf)), () => SelectedTask is not null && File.Exists(SelectedTask.Options.OutputPdf));
        OpenOriginalCommand = new RelayCommand(() => Guard(() => Open(Path.Combine(SelectedTask!.WorkDirectory, "original.pdf"))), () => SelectedTask is not null && File.Exists(Path.Combine(SelectedTask.WorkDirectory, "original.pdf")));
        OpenWorkCommand = new RelayCommand(() => Guard(() => Open(SelectedTask!.WorkDirectory)), () => SelectedTask is not null && Directory.Exists(SelectedTask.WorkDirectory));
        ExportSourceCommand = new AsyncCommand(() => ExportArchiveAsync(false), Error, () => SelectedTask is not null && Directory.Exists(Path.Combine(SelectedTask.WorkDirectory, "source")) && SelectedTask.Status != "运行中");
        ExportLogsCommand = new AsyncCommand(() => ExportArchiveAsync(true), Error, () => SelectedTask is not null && Directory.Exists(SelectedTask.WorkDirectory) && SelectedTask.Status != "运行中");
        SaveSettingsCommand = new RelayCommand(() => Guard(SaveSettings));
        TestConnectionCommand = new AsyncCommand(TestModelConnectionAsync, Error, () => !IsFetchingModels);
        FetchModelsCommand = new AsyncCommand(FetchModelsAsync, Error, () => !IsTestingModelConnection);
        CheckEnvironmentCommand = new AsyncCommand(CheckEnvironmentAsync, Error, () => CanManageEnvironment);
        SetupEnvironmentCommand = new AsyncCommand(async () =>
        {
            if (Directory.Exists(runtimeRoot) && !await dialogs.ConfirmAsync("修复编译环境", "将重新下载基础环境，检查成功后替换当前环境。额外补装的宏包可能需要重新下载；下载失败时保留原环境。")) return;
            await InstallEnvironmentAsync();
        }, Error, () => CanManageEnvironment);
        CancelEnvironmentCommand = new RelayCommand(() => environmentCancellation?.Cancel(), () => IsEnvironmentBusy);
        BrowseWorkCommand = new AsyncCommand(async () => { if (await dialogs.PickFolderAsync("选择工作目录") is { } path) { Settings.WorkRoot = path; Changed(nameof(Settings)); } }, Error);
        AddTermCommand = new RelayCommand(() => Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(TermSource) || string.IsNullOrWhiteSpace(TermTarget)) throw new ArgumentException("请填写原词与译词。");
            if (SelectedTerm is not null) Terms.Remove(SelectedTerm);
            Terms.Add(new(TermSource.Trim(), TermTarget.Trim(), TermCaseSensitive)); SaveSettings();
        }));
        DeleteTermCommand = new RelayCommand(() => Guard(() => { if (SelectedTerm is not null) Terms.Remove(SelectedTerm); SaveSettings(); }));
        ImportTermsCommand = new AsyncCommand(ImportTermsAsync, Error);
        ExportTermsCommand = new AsyncCommand(async () =>
        {
            if (await dialogs.SaveAsync(".json", "terminology") is { } path) FileStore.WriteText(path, JsonSerializer.Serialize(Terms, JsonDefaults.Options));
        }, Error);
        Tasks.CollectionChanged += (_, _) => RefreshOverview();
        foreach (var task in Tasks) ObserveTask(task);
        if (!IsEnvironmentReady) EnvironmentReport = "点击“下载环境”开始安装。";
    }
    public void SetDroppedSource(string path) { Source = path; Notice = "已导入：" + Path.GetFileName(path); }
    private async Task AddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(Source)) throw new ArgumentException("请输入 arXiv 编号、链接，或导入 TeX 源码包。");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("请选择 PDF 输出目录。");
        if (!PdfLayouts.Contains(PdfLayout)) throw new ArgumentException("请选择 PDF 格式。");
        if (TranslationsFile == "") Settings.Model.Validate();
        else if (!File.Exists(TranslationsFile)) throw new FileNotFoundException("外部译文文件不存在。");
        if (!TexRuntimeBundle.IsAvailable(runtimeRoot))
        {
            if (HasActiveTask) throw new IOException("编译环境缺失，请等待当前任务结束后在“编译环境”页下载或修复。");
            if (!await dialogs.ConfirmAsync("首次准备编译环境", "轻便版尚未安装编译环境。现在联网下载编译器、中文字体与基础宏包吗？全部文件保存在便携目录，准备完成后继续此任务。")) return;
            NavigationRequested?.Invoke("environment");
            if (!await InstallEnvironmentAsync()) return;
            NavigationRequested?.Invoke("tasks");
        }
        ValidatePaths();
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputDirectory.Trim()));
        string workRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Settings.WorkRoot));
        if (directory.Equals(workRoot, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(Path.EndsInDirectorySeparator(workRoot) ? workRoot : workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || workRoot.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出目录与任务工作目录不能相同或互相包含，请单独选择目录。");
        string stem = Source.Trim().TrimEnd('/', '\\');
        stem = Path.GetFileName(stem);
        if (File.Exists(Source)) stem = Path.GetFileNameWithoutExtension(stem);
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        if (stem.Length > 100) stem = stem[..100];
        if (string.IsNullOrWhiteSpace(stem)) stem = "translated";
        string outputPdf = Path.Combine(directory, $"{stem}.{TargetLanguage}.{PdfLayout}.pdf");
        int suffix = 2;
        string baseOutput = outputPdf;
        while (File.Exists(outputPdf) || Tasks.Any(t => string.Equals(t.Options.OutputPdf, outputPdf, StringComparison.OrdinalIgnoreCase)))
            outputPdf = Path.Combine(directory, Path.GetFileNameWithoutExtension(baseOutput) + $"-{suffix++}.pdf");
        SaveSettings();
        var options = new PipelineOptions { Source = Source, OutputPdf = outputPdf, PdfLayout = PdfLayout, SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage, Engine = Engine,
            MainHint = string.IsNullOrWhiteSpace(MainHint) ? null : MainHint, TranslationsFile = string.IsNullOrWhiteSpace(TranslationsFile) ? null : TranslationsFile,
            AllowShellEscape = AllowShellEscape, AutoTerminology = AutoTerminology };
        var task = new TaskItem { Options = options, Model = Settings.Model.Snapshot(), Terms = [.. Terms] };
        task.WorkDirectory = Path.Combine(Settings.WorkRoot, task.Id);
        ObserveTask(task); Tasks.Insert(0, task); SelectedTask = task; SaveHistory(); _ = PumpAsync();
    }
    private void ValidatePaths()
    {
        if (!FileStore.Ascii(runtimeRoot)) throw new ArgumentException("请将完整软件文件夹移至纯英文路径，例如 D:\\HJFY，然后重新启动。");
        if (!TexRuntimeBundle.IsAvailable(runtimeRoot)) throw new DirectoryNotFoundException("编译环境尚未就绪，请在“编译环境”页下载或修复环境。");
        if (!FileStore.Ascii(Settings.WorkRoot)) throw new ArgumentException("请在编译环境设置中选择纯英文工作目录。");
        Directory.CreateDirectory(Settings.WorkRoot);
        string probe = Path.Combine(Settings.WorkRoot, ".hjfy-write-test"); File.WriteAllText(probe, ""); File.Delete(probe);
    }
    private async Task PumpAsync()
    {
        if (pumping) return;
        pumping = true; Changed(nameof(HasActiveTask)); RefreshOverview();
        try
        {
            while (Tasks.LastOrDefault(t => t.Status == "排队中") is { } task)
            {
                activeCancellation = new(); task.ResetProgress(); task.Status = "运行中"; task.Detail = "准备任务"; SaveHistory();
                try
                {
                    ValidatePaths();
                    await TexRuntimeBundle.EnsureAsync(runtimeRoot, new Progress<int>(percent =>
                    { task.Detail = $"首次准备编译环境 {percent}%"; task.Percent = percent; }), activeCancellation.Token);
                    task.Percent = 0;
                    task.Model.ApiKey = Settings.Model.ApiKey;
                    var runtime = new TexRuntime(runtimeRoot, runner) { AutoInstallPackages = Settings.AutoInstallPackages };
                    var progress = new Progress<PipelineEvent>(e =>
                    {
                        if (task.Status != "运行中") return;
                        task.ApplyProgress(e);
                        if (SelectedTask == task && e.Type is "stage" or "issue") RefreshDetails();
                    });
                    task.Result = await Task.Run(() => new TranslationPipeline(http, runtime, runner).RunAsync(task.Options, task.Model,
                        task.Terms, task.WorkDirectory, store.CachePath, progress, activeCancellation.Token));
                    task.ApplyProgress(new("progress", "translate", "译文 PDF 已生成", task.Result.Fragments, task.Result.Fragments,
                        Fragments: task.Result.Fragments, Translated: task.Result.Translated, Cached: task.Result.Cached, Fallback: task.Result.Fallback));
                    task.Status = task.Result.Fallback > 0 || task.Result.Issues.Count > 0 ? "完成，需检查" : "已完成";
                    task.Detail = task.Result.Fallback > 0 ? "译文 PDF 已生成，部分片段保留原文，请检查。"
                        : task.Result.Issues.Count > 0 ? "译文 PDF 已生成，请查看诊断提示。" : "译文 PDF 已生成。";
                    task.Percent = 100;
                }
                catch (MainFileAmbiguousException ex)
                { task.Status = "请选择主文件"; task.Detail = ex.Message; task.MainCandidates = ex.Candidates.ToArray(); }
                catch (OperationCanceledException) { task.Status = "已取消"; task.Detail = "任务已取消，已完成译文保留在缓存中。"; }
                catch (Exception ex) { task.Status = "失败"; task.Detail = ex.Message; }
                finally
                {
                    activeCancellation.Dispose(); activeCancellation = null;
                    task.Model.ApiKey = ""; SaveHistory(); RefreshDetails(); Changed(nameof(MainCandidates)); Changed(nameof(HasSelectedTask)); CommandHub.Refresh();
                }
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { pumping = false; Changed(nameof(HasActiveTask)); RefreshOverview(); }
    }
    private void CancelSelected()
    {
        if (SelectedTask?.Status == "运行中") activeCancellation?.Cancel();
        else if (SelectedTask?.Status == "排队中") { SelectedTask.Status = "已取消"; SelectedTask.Detail = "已移出队列。"; SaveHistory(); }
    }
    private void RetrySelected()
    {
        if (SelectedTask is not { } task) return;
        if (task.Status == "请选择主文件")
        {
            if (!task.MainCandidates.Contains(SelectedMainCandidate)) throw new ArgumentException("请先从下方列表选择主 TeX 文件。");
            task.Options.MainHint = SelectedMainCandidate;
        }
        task.Model = Settings.Model.Snapshot(); task.Terms = [.. Terms];
        // Keep previous diagnostics and use a fresh work copy.
        task.WorkDirectory = Path.Combine(Settings.WorkRoot, task.Id + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
        task.ResetProgress(); task.Status = "排队中"; task.Detail = "等待重新运行"; SaveHistory(); _ = PumpAsync();
    }
    private void DeleteSelected()
    {
        if (SelectedTask is not { } task || task.Status is "运行中" or "排队中") return;
        int index = Tasks.IndexOf(task);
        Tasks.Remove(task);
        SelectedTask = Tasks.Count == 0 ? null : Tasks[Math.Min(index, Tasks.Count - 1)];
        SaveHistory();
        RefreshOverview();
        if (!string.IsNullOrWhiteSpace(task.WorkDirectory) && Directory.Exists(task.WorkDirectory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(task.WorkDirectory, recursive: true);
        }
        Notice = "已删除任务及其工作目录。";
    }
    private void RefreshDetails()
    {
        Changed(nameof(CanDeleteSelectedTask));
        if (SelectedTask is not { } task) { TaskLog = "选择一个任务查看详情。"; return; }
        string text = $"{task.Title}\n{task.Status} · {task.StageLabel}\n{task.BlockSummary}\n{task.Detail}\n\n来源：{task.Options.Source}\n输出：{task.Options.OutputPdf}\n工作目录：{task.WorkDirectory}\n";
        string events = Path.Combine(task.WorkDirectory, "events.jsonl");
        if (File.Exists(events))
        {
            try
            {
                using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text += "\n" + string.Join('\n', reader.ReadToEnd().Split('\n').Where(l => l.Length > 0).TakeLast(80).Select(line =>
                {
                    try { using var d = JsonDocument.Parse(line); return d.RootElement.TryGetProperty("message", out var m) ? m.GetString() : ""; }
                    catch (JsonException) { return ""; }
                }).Where(l => !string.IsNullOrEmpty(l)));
            }
            catch (IOException) { }
        }
        TaskLog = text;
    }
    public void ClearModelConnectionResult() { if (!IsTestingModelConnection && !IsFetchingModels) IsModelConnectionResultVisible = false; }
    public void ResetAvailableModels()
    {
        string selected = ModelId;
        AvailableModels.Clear();
        if (!string.IsNullOrWhiteSpace(selected)) AvailableModels.Add(selected);
        ModelId = selected;
        Changed(nameof(ModelId));
        ClearModelConnectionResult();
    }
    private async Task FetchModelsAsync()
    {
        IsFetchingModels = true;
        IsModelConnectionResultVisible = true;
        ModelConnectionSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        ModelConnectionTitle = "正在获取模型";
        ModelConnectionMessage = "正在读取服务商的模型列表…";
        try
        {
            var models = await new OpenAiProvider(http, Settings.Model.Snapshot()).GetModelsAsync(CancellationToken.None);
            string selected = ModelId;
            AvailableModels.Clear();
            foreach (string model in models) AvailableModels.Add(model);
            ModelId = selected;
            ModelConnectionSeverity = models.Count > 0 ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            ModelConnectionTitle = models.Count > 0 ? $"已获取 {models.Count} 个模型" : "列表为空";
            ModelConnectionMessage = models.Count > 0 ? "下拉选择，或手动输入。" : "服务商未返回模型，可以手动输入模型 ID。";
            Notice = ModelConnectionTitle;
        }
        catch (Exception ex)
        {
            ModelConnectionSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            ModelConnectionTitle = "获取模型失败";
            ModelConnectionMessage = ex is OperationCanceledException ? "请求超时，请重试或手动输入模型 ID。" : ex.Message;
            Notice = "获取模型失败，可以手动输入模型 ID。";
        }
        finally { IsFetchingModels = false; }
    }
    private async Task TestModelConnectionAsync()
    {
        var model = Settings.Model.Snapshot();
        IsTestingModelConnection = true;
        IsModelConnectionResultVisible = true;
        ModelConnectionSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        ModelConnectionTitle = "正在测试连接";
        ModelConnectionMessage = "使用当前填写的配置发送测试请求，请稍候。";
        Notice = "正在测试连接…";
        try
        {
            await new OpenAiProvider(http, model).TestAsync(CancellationToken.None);
            ModelConnectionSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
            ModelConnectionTitle = "连接成功";
            ModelConnectionMessage = $"模型 {model.Model} 已响应。确认后点击“保存设置”。";
            Notice = "模型连接成功。";
        }
        catch (Exception ex)
        {
            ModelConnectionSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            ModelConnectionTitle = "连接失败";
            ModelConnectionMessage = ex.Message;
            Notice = "模型连接失败，请查看页面中的检查结果。";
        }
        finally { IsTestingModelConnection = false; }
    }
    private void SaveSettings()
    {
        Settings.Terms = Terms.ToList(); Settings.PackagePolicyAcknowledged = true; store.Save(Settings);
        Notice = "设置已保存。";
    }
    private async Task CheckEnvironmentAsync()
    {
        if (!TexRuntimeBundle.IsAvailable(runtimeRoot)) { RefreshEnvironmentState(); EnvironmentReport = "环境未就绪，请下载或修复。"; return; }
        environmentCancellation = new(); IsEnvironmentBusy = true;
        EnvironmentStage = "检查编译环境"; IsEnvironmentIndeterminate = true;
        try
        {
            await TexRuntimeBundle.EnsureAsync(runtimeRoot, cancellationToken: environmentCancellation.Token);
            EnvironmentReport = "正在检查内置环境…";
            EnvironmentReport = await new TexRuntime(runtimeRoot, runner).CheckAsync(environmentCancellation.Token);
            Notice = "编译环境检查完成。";
        }
        catch (OperationCanceledException) { EnvironmentReport = "环境检查已取消。"; }
        finally { environmentCancellation.Dispose(); environmentCancellation = null; IsEnvironmentBusy = false; RefreshEnvironmentState(); }
    }
    private void RefreshEnvironmentState()
    { Changed(nameof(IsEnvironmentReady)); Changed(nameof(EnvironmentSetupLabel)); Changed(nameof(EnvironmentSummary)); }
    private async Task<bool> InstallEnvironmentAsync()
    {
        if (!CanManageEnvironment) return false;
        environmentCancellation = new(); IsEnvironmentBusy = true;
        EnvironmentStage = "准备下载环境"; EnvironmentPercent = 0; IsEnvironmentIndeterminate = true;
        EnvironmentReport = "正在连接 TeX Live 下载源…";
        var updates = new Progress<TexSetupProgress>(value =>
        {
            if (!IsEnvironmentBusy) return;
            EnvironmentStage = value.Stage; EnvironmentPercent = value.Percent ?? 0;
            IsEnvironmentIndeterminate = value.Percent is null;
            EnvironmentReport = value.Message;
            Notice = value.Stage + " · " + value.Message;
        });
        try
        {
            var token = environmentCancellation.Token;
            await Task.Run(() => new TexRuntimeInstaller(http).InstallAsync(runtimeRoot, repair: Directory.Exists(runtimeRoot), progress: updates, token: token));
            EnvironmentReport = await new TexRuntime(runtimeRoot, runner).CheckAsync(token);
            Notice = "编译环境已安装，可以开始翻译。";
            return true;
        }
        catch (OperationCanceledException) { EnvironmentReport = "环境安装已取消。未完成的安装不会启用，原环境保持不变，可重新下载。"; Notice = "环境安装已取消。"; return false; }
        catch (Exception ex) { EnvironmentReport = ex.Message + "\n可重试下载；详细日志位于便携目录 data/logs。"; Error(ex); return false; }
        finally { environmentCancellation.Dispose(); environmentCancellation = null; IsEnvironmentBusy = false; RefreshEnvironmentState(); }
    }
    private async Task ImportTermsAsync()
    {
        if (await dialogs.PickJsonAsync() is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object) root = root.GetProperty("terms");
        var entries = root.Deserialize<List<TermEntry>>(JsonDefaults.Options) ?? [];
        foreach (var term in entries.Where(t => !string.IsNullOrWhiteSpace(t.Source) && !string.IsNullOrWhiteSpace(t.Target))) if (!Terms.Contains(term)) Terms.Add(term);
        SaveSettings();
    }
    private async Task ExportArchiveAsync(bool logsOnly)
    {
        if (SelectedTask is not { } task) return;
        if (await dialogs.SaveAsync(".zip", logsOnly ? "hjfy-diagnostics" : "translated-source") is not { } path) return;
        using var output = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        string root = logsOnly ? task.WorkDirectory : Path.Combine(task.WorkDirectory, "source");
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (!logsOnly && (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "_download" or "_repro") ||
                Path.GetExtension(file) is ".aux" or ".log" or ".out" or ".blg" or ".fls" or ".fdb_latexmk" || file.EndsWith(".synctex.gz"))) continue;
            if (logsOnly && (Path.GetRelativePath(root, file).Contains(Path.DirectorySeparatorChar) || Path.GetExtension(file) is not (".log" or ".json" or ".jsonl"))) continue;
            archive.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        Notice = "已导出：" + path;
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void SaveHistory() => store.SaveHistory(Tasks.ToList());
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
    private void Error(Exception ex) { Notice = ex.Message; NoticeIsError = true; }
    public bool HasSelectedTask => SelectedTask is not null;
    public int TaskCount => Tasks.Count;
    public int CompletedCount => Tasks.Count(t => t.Status is "已完成" or "完成，需检查");
    public string QueueSummary => HasActiveTask ? "正在处理 · 其余任务依次排队" : Tasks.Any(t => t.Status == "排队中") ? "等待处理" : "准备就绪";
    private void ObserveTask(TaskItem task) => task.PropertyChanged += (_, _) => RefreshOverview();
    private void RefreshOverview() { Changed(nameof(TaskCount)); Changed(nameof(CompletedCount)); Changed(nameof(QueueSummary)); Changed(nameof(AddTaskLabel)); Changed(nameof(CanManageEnvironment)); CommandHub.Refresh(); }
    public void CancelAll() { activeCancellation?.Cancel(); environmentCancellation?.Cancel(); foreach (var task in Tasks.Where(t => t.Status == "排队中")) task.Status = "已中断"; SaveHistory(); }
    public void Dispose() { activeCancellation?.Cancel(); environmentCancellation?.Cancel(); http.Dispose(); }
}
