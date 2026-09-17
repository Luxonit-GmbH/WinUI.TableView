using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestPlatform.TestExecutor;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System;
using System.IO;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUI.TableView.Tests;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class UnitTestApp : Application
{
    private UnitTestAppWindow? _window;

    internal static new UnitTestApp Current => (UnitTestApp)Application.Current;

    internal UnitTestAppWindow MainWindow
    {
        get
        {
            _window ??= new UnitTestAppWindow();
            return _window;
        }
    }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public UnitTestApp()
    {
        InitializeComponent();

        // A test host that dies of a stowed exception leaves nothing behind but an event-log entry naming
        // Microsoft.UI.Xaml.dll: an exception thrown from a timer, a dispatcher callback or an async void handler
        // ends the process outside any test's try/catch. Write what it was, and where the dispatcher stood, to
        // %TEMP%\WinUI.TableView.Tests.crash.log before that happens. A null dereference that a test does catch is
        // logged too, at its throw site, since a Release stack carries no line numbers.
        UnhandledException += (_, e) => LogCrash("UNHANDLED", e.Message, e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is NullReferenceException)
            {
                LogCrash("FIRST-CHANCE", e.Exception.Message, e.Exception);
            }
        };
    }

    private static void LogCrash(string kind, string message, Exception? exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "WinUI.TableView.Tests.crash.log");
            File.AppendAllText(path,
                $"{DateTime.Now:O} {kind}: {message}{Environment.NewLine}{exception}{Environment.NewLine}" +
                $"--- dispatcher stack:{Environment.NewLine}{Environment.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never be the second crash
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow.Activate();

        UITestMethodAttribute.DispatcherQueue = MainWindow.DispatcherQueue;

        UnitTestClient.CreateDefaultUI();

        UnitTestClient.Run(Environment.CommandLine);
    }
}
