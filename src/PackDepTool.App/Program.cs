using System.Runtime.InteropServices;
using Avalonia;
using PackDepTool.App.Services;

namespace PackDepTool.App;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--apply-update", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = UpdateInstaller.RunApplyUpdate(args);
            return;
        }

        _singleInstance = new Mutex(true, "PackDependencyTool.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(_singleInstance);
    }

    private static void ActivateExistingWindow()
    {
        var handle = FindWindow(null, "Pack Dependency Tool");
        if (handle == IntPtr.Zero) return;
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

