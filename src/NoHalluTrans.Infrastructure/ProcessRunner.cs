using System.Diagnostics;
using System.Text;

namespace NoHalluTrans.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken token);
}

public sealed class ProcessRunner(IProgress<string>? progress = null) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动编译工具。");
        Task<string> output = ReadOutputAsync(process.StandardOutput);
        Task<string> error = ReadOutputAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var stopTree = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        bool timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            timedOut = !token.IsCancellationRequested;
        }
        string outputText = await output;
        string errorText = await error;
        token.ThrowIfCancellationRequested();
        timedOut |= deadline.IsCancellationRequested;
        return new(timedOut ? -1 : process.ExitCode, outputText, errorText, timedOut);
    }
    private async Task<string> ReadOutputAsync(StreamReader reader)
    {
        StringBuilder text = new();
        while (await reader.ReadLineAsync() is { } line)
        {
            text.AppendLine(line);
            progress?.Report(line);
        }
        return text.ToString();
    }
}
