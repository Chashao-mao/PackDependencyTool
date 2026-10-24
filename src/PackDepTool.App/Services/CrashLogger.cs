using Avalonia.Threading;

namespace PackDepTool.App.Services;

/// <summary>Last-resort logging so a background exception never makes the app vanish silently.</summary>
public static class CrashLogger
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Install(string logDirectory)
    {
        _directory = logDirectory;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Write(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write(args.Exception, "TaskScheduler");
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Write(args.Exception, "Dispatcher");
            args.Handled = true;
        };
    }

    public static void Write(Exception exception, string source)
    {
        try
        {
            var directory = _directory;
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var text = $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, text);
            }
        }
        catch (Exception)
        {
            // Never let the crash logger itself crash the process.
        }
    }
}
