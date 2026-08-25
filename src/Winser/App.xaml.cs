using System.Diagnostics;
using Microsoft.UI.Xaml;
using Winser.Helpers;
using Winser.Services;
using XamlUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Winser;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Initialize();
        WindowManager.LastWindowClosed += OnLastWindowClosed;
        WindowManager.CreateWindow(initialUrl: UrlFromCommandLine());
    }

    /// <summary>Supports "winser.exe https://example.com" and file associations.</summary>
    private static string? UrlFromCommandLine()
    {
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-') || arg.StartsWith('/'))
            {
                continue;
            }

            return arg;
        }

        return null;
    }

    private static void OnLastWindowClosed(object? sender, EventArgs e) => AppServices.Shutdown();

    private static void OnUnhandledException(object sender, XamlUnhandledExceptionEventArgs e)
    {
        // Persist whatever is queued before the process goes down.
        Debug.WriteLine($"[Winser] Unhandled exception: {e.Exception}");
        AppServices.Shutdown();
    }
}
