using Microsoft.UI.Xaml;
using NoHalluTrans.Infrastructure;
using System.Text;

namespace NoHalluTrans.Desktop;

public partial class App : Application
{
    private Window? window;
    private Mutex? singleInstance;
    internal List<string> BindingErrors { get; } = [];
    public App()
    {
        UnhandledException += (_, e) => { LogStartupFailure(e.Exception); LogStartupMessage(e.Message); };
        try { InitializeComponent(); }
        catch (Exception ex) { LogStartupFailure(ex); throw; }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DebugSettings.XamlResourceReferenceFailed += (_, e) => LogStartupMessage(e.Message);
        DebugSettings.BindingFailed += (_, e) => BindingErrors.Add(e.Message);
        UnhandledException += (_, e) =>
        {
            if (window is MainWindow main) { main.Model.Notice = e.Exception.Message; main.Model.NoticeIsError = true; e.Handled = true; }
        };
    }
    private static void LogStartupFailure(Exception ex)
    {
        LogStartupMessage(ex.ToString());
    }
    private static void LogStartupMessage(string message)
    {
        try
        {
            Directory.CreateDirectory(PortablePaths.Logs);
            File.AppendAllText(Path.Combine(PortablePaths.Logs, "startup.log"), message + Environment.NewLine);
        }
        catch { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Launch(); }
        catch (Exception ex) { LogStartupFailure(ex); throw; }
    }
    private void Launch()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Length >= 5 && arguments[1] is "--verify-desktop" or "--verify-core-desktop")
        {
            window = new MainWindow(Path.Combine(arguments[4], "data"), arguments[2] == "--bundled-runtime" ? null : arguments[2]);
            var main = (MainWindow)window;
            main.Surface.Loaded += async (_, _) => await DesktopVerification.RunAsync(this, main, arguments[3], arguments[4], arguments[1] == "--verify-core-desktop");
            window.Activate();
            return;
        }
        singleInstance = new Mutex(true, "Local\\HJFY.WinUI3", out bool first);
        if (!first) { Exit(); return; }
        window = new MainWindow();
        window.Closed += (_, _) => singleInstance.Dispose();
        window.Activate();
    }
}
