namespace NoHalluTrans.Infrastructure;

public static class PortablePaths
{
    public static string RootFrom(string applicationDirectory)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        return Path.GetFileName(directory).Equals("app", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)! : directory;
    }

    public static string Root => RootFrom(AppContext.BaseDirectory);
    public static string Data => Path.Combine(Root, "data");
    public static string Work => Path.Combine(Root, "work");
    public static string Output => Path.Combine(Root, "output");
    public static string Logs => Path.Combine(Data, "logs");
}
