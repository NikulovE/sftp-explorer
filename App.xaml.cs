using Microsoft.UI.Xaml;
using System;
using System.Linq;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI;

public partial class App : Application
{
    private Window? m_window;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        try
        {
            Log.Debug("App constructor started");
            InitializeComponent();
            Log.Debug("App InitializeComponent completed");
            
            this.UnhandledException += App_UnhandledException;
        }
        catch (Exception ex)
        {
            Log.Error("Error in App constructor", ex);
            throw;
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.Exception);

        // A formatting failure in a XAML/DispatcherQueue callback is recoverable:
        // the affected status text is not updated, but the window and active
        // connections remain valid. Keep all other exception types fail-fast.
        if (e.Exception is FormatException)
        {
            e.Handled = true;
            Log.Warning("Recovered from an invalid UI text format");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log.Debug("OnLaunched started");
            _ = DragCacheService.CleanupExpiredSessionsAsync();
            m_window = new MainWindow();
            MainWindow = m_window;
            Log.Debug("MainWindow created");

            // Activate first so protocol-validation errors always have a live
            // XamlRoot for their ContentDialog.
            m_window.Activate();
            Log.Debug("Window activated");

            // Check for sftp:// protocol URL
            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Length > 1 &&
                cmdArgs[1].StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
            {
                var url = cmdArgs[1];
                // Parse and open connection (will be handled by MainWindow)
                ((MainWindow)m_window).OpenSftpUrl(url);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in OnLaunched", ex);
            throw;
        }
    }
}
