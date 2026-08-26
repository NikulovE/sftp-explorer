using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT;
using Renci.SshNet;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SftpExplorerWinUI.Controls;
using SftpExplorerWinUI.Services;
using SftpExplorerWinUI.Models;
using SftpExplorerWinUI.Helpers;

namespace SftpExplorerWinUI;

public sealed partial class MainWindow : Window
{
    private AppWindow m_appWindow;
    private MicaController? m_backdropController;
    private SystemBackdropConfiguration? m_configurationSource;
    private SftpClient? _sftpClient;
    private ActiveConnection? _currentConnection;
    private string _hostname = "";
    private ConnectionsPanel? _connectionsPanel;
    private ConnectionManager _connectionManager;
    private readonly SshClientFactory _sshClientFactory = new();
    private readonly Dictionary<string, ActiveConnection> _activeConnections = new(StringComparer.Ordinal);
    private readonly HashSet<ActiveConnection> _ownedConnections = new();
    private readonly HashSet<SshConnectionSession> _connectionSessions = new();
    private readonly Dictionary<SshConnectionSession, int> _sessionRetainCounts = new();
    private readonly Dictionary<SftpTabContent, ActiveConnection> _tabConnections = new();
    private readonly object _connectionOwnershipLock = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _windowLifetimeCts = new();
    private CancellationTokenSource? _connectionAttemptCts;
    private long _connectionAttemptGeneration;
    private bool _isPaneOpenState = false;
    private bool _connectionsPanelInitialized = false;
    private bool _isClosed;
    private UIElement? _currentTitleBar;
    private const int InvalidOperationHResult = unchecked((int)0x800710DD);

    public MainWindow()
    {
        try
        {
            Log.Debug("MainWindow constructor started");
            InitializeComponent();
            Log.Debug("InitializeComponent completed");
            
            // Initialize connection manager
            _connectionManager = new ConnectionManager();
            
            // Get AppWindow
            m_appWindow = GetAppWindowForCurrentWindow();
            Log.Debug("Got AppWindow");
            
            // Set window size
            m_appWindow.Resize(new SizeInt32(1400, 900));

            ((FrameworkElement)Content).ActualThemeChanged += Window_ThemeChanged;
            
            // Enable Mica background
            TrySetSystemBackdrop();

            this.Closed += Window_Closed;

            // Customize title bar
            CustomizeTitleBar();
            
            // Initialize UI
            this.Activated += MainWindow_FirstActivated;
            
            Log.Debug("MainWindow constructor completed");
        }
        catch (Exception ex)
        {
            Log.Error("Error in MainWindow constructor", ex);
            throw;
        }
    }

    private async void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs e)
    {
        // Unsubscribe to run only once
        this.Activated -= MainWindow_FirstActivated;

        try
        {
            // Wait a bit for XamlRoot to be ready, but do not resume against a
            // Window whose DispatcherQueue is already shutting down.
            await Task.Delay(100, _windowLifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
            return;
        }

        if (_isClosed)
        {
            return;
        }

        // Initialize connections panel
        InitializeConnectionsPanel();
    }

    private void InitializeConnectionsPanel()
    {
        if (_connectionsPanelInitialized)
        {
            return;
        }
        
        _connectionsPanel = new ConnectionsPanel(_connectionManager, _sshClientFactory);
        _connectionsPanel.ConnectionSelected += ConnectionsPanel_ConnectionSelected;
        _connectionsPanel.ConnectionTerminalRequested += ConnectionsPanel_ConnectionTerminalRequested;
        _connectionsPanel.NewConnectionRequested += ConnectionsPanel_NewConnectionRequested;
        _connectionsPanel.GroupExpansionChanged += ConnectionsPanel_GroupExpansionChanged;
        
        // Add to the left panel
        if (StartupPanel.Children[0] is Border leftPanel)
        {
            leftPanel.Child = _connectionsPanel;
        }
        
        _connectionsPanelInitialized = true;
    }

    private void ConnectionsPanel_GroupExpansionChanged(object? sender, EventArgs e)
    {
        LoadConnectionsList();
    }

    private async void ConnectionsPanel_ConnectionSelected(object? sender, SavedConnection connection)
    {
        await OpenSavedConnectionAsync(connection, openTerminalMaximized: false);
    }

    private async void ConnectionsPanel_ConnectionTerminalRequested(object? sender, SavedConnection connection)
    {
        await OpenSavedConnectionAsync(connection, openTerminalMaximized: true);
    }

    private async Task OpenSavedConnectionAsync(SavedConnection connection, bool openTerminalMaximized)
    {
        string? password;
        try
        {
            password = _connectionManager.GetPassword(connection.Id);
        }
        catch (Exception persistenceError)
        {
            await ShowPersistenceWarningAsync(persistenceError);
            return;
        }
        
        if (string.IsNullOrEmpty(password) &&
            (connection.AuthenticationMode == SftpAuthenticationMode.Password ||
             connection.PrivateKeyRequiresPassphrase))
        {
            // Если пароля нет - запрашиваем
            await ShowPasswordDialog(connection, openTerminalMaximized);
        }
        else
        {
            await ConnectToServer(connection, password ?? "", openTerminalMaximized);
        }
    }

    private async void ConnectionsPanel_NewConnectionRequested(object? sender, System.EventArgs e)
    {
        await ShowConnectionDialog();
    }

    private async Task ShowPasswordDialog(SavedConnection connection, bool openTerminalMaximized = false)
    {
        var passwordBox = new PasswordBox
        {
            Margin = new Thickness(0, 0, 0, 0),
            Height = 36,
            Width = 320
        };

        var dialog = new ContentDialog
        {
            Title = string.Format(LocalizationHelper.GetString("ConnectToProfileTitle") ?? "Connect to {0}", connection.Name),
            Content = new StackPanel
            {
                Spacing = 8,
                Padding = new Thickness(12, 12, 12, 8),
                Children =
                {
                    new TextBlock 
                    { 
                        Text = connection.AuthenticationMode == SftpAuthenticationMode.PrivateKey
                            ? LocalizationHelper.GetString("PassphrasePromptHelp") ?? "Enter the key passphrase, or leave it empty if the key is not encrypted."
                            : LocalizationHelper.GetString("PasswordRequired") ?? "Password required to connect",
                        Margin = new Thickness(0, 0, 0, 4),
                        FontSize = 13,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    },
                    new TextBlock
                    {
                        Text = $"{connection.Username}@{connection.Hostname}",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 16)
                    },
                    new TextBlock
                    {
                        Text = connection.AuthenticationMode == SftpAuthenticationMode.PrivateKey
                            ? LocalizationHelper.GetString("PrivateKeyPassphraseLabel") ?? "Key passphrase"
                            : LocalizationHelper.GetString("PasswordLabel") ?? "Password",
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 4)
                    },
                    passwordBox
                }
            },
            PrimaryButtonText = LocalizationHelper.GetString("Connect") ?? "Connect",
            CloseButtonText = LocalizationHelper.GetString("Cancel") ?? "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        
        if (result == ContentDialogResult.Primary)
        {
            await ConnectToServer(connection, passwordBox.Password, openTerminalMaximized);
        }
        else
        {
            // User cancelled the password dialog, return to startup mode
            ShowStartupMode();
        }
    }

    private async Task ConnectToServer(
        SavedConnection connection,
        string password,
        bool openTerminalMaximized = false)
    {
        var connected = await ConnectAsync(
            connection.Hostname,
            connection.Port,
            connection.Username,
            password,
            openTerminalMaximized: openTerminalMaximized,
            authenticationMode: connection.AuthenticationMode,
            privateKeyPath: connection.PrivateKeyPath,
            authenticationRevision: connection.AuthenticationRevision);
        
        if (connected)
        {
            try
            {
                _connectionManager.UpdateLastUsed(connection.Id);
            }
            catch (Exception persistenceError)
            {
                await ShowPersistenceWarningAsync(persistenceError);
            }
            
            // Show tab mode
            ShowTabMode();
            
            // Load navigation pane state and connections
            LoadNavigationPaneState();
            LoadConnectionsList();
        }
        else
        {
            // Connection failed, return to startup mode
            ShowStartupMode();
        }
    }

    private async Task ShowConnectionDialog()
    {
        // Wait for XamlRoot to be ready
        while (this.Content?.XamlRoot == null)
        {
            await Task.Delay(100);
        }
        
        IReadOnlyList<ConnectionGroupSettings> groups;
        try
        {
            groups = _connectionManager.LoadGroups();
        }
        catch (Exception persistenceError)
        {
            await ShowPersistenceWarningAsync(persistenceError);
            return;
        }

        var dialog = new SftpConnectionDialog(
            groups,
            sshClientFactory: _sshClientFactory)
        {
            XamlRoot = this.Content?.XamlRoot
        };
        dialog.ConnectionRequestedAsync = async inputState =>
        {
            Exception? connectionErrorForAttempt = null;
            var success = await ConnectAsync(
                inputState.Hostname,
                inputState.Port,
                inputState.Username,
                inputState.Secret,
                ex => connectionErrorForAttempt = ex,
                hostKeyConfirmation: dialog.ConfirmHostKeyAsync,
                authenticationMode: inputState.AuthenticationMode,
                privateKeyPath: inputState.PrivateKeyPath);
            return new SftpConnectionDialog.ConnectionRequestResult(
                success,
                success
                    ? null
                    : connectionErrorForAttempt?.Message ??
                      LocalizationHelper.GetString("ConnectionFailedMessage") ??
                      "Failed to connect.",
                connectionErrorForAttempt as HostKeyChangedException);
        };

        var terminalTab = TabContentArea.Children
            .OfType<SftpTabContent>()
            .SingleOrDefault(tab => tab.Visibility == Visibility.Visible);
        var restoreNativeTerminal = terminalTab?.SuspendNativeTerminalForXamlOverlay() == true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            terminalTab?.RestoreNativeTerminalAfterXamlOverlay(restoreNativeTerminal);
        }

        var connectedInputState = dialog.ConnectedInputState;
        if (connectedInputState == null)
        {
            return;
        }

        // Save the profile only after a successful connection.
        var connection = new SavedConnection
        {
            Name = string.IsNullOrWhiteSpace(connectedInputState.ConnectionName)
                ? $"{connectedInputState.Username}@{connectedInputState.Hostname}"
                : connectedInputState.ConnectionName,
            Hostname = connectedInputState.Hostname,
            Port = connectedInputState.Port,
            Username = connectedInputState.Username,
            AuthenticationMode = connectedInputState.AuthenticationMode,
            AuthenticationRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PrivateKeyPath = connectedInputState.PrivateKeyPath,
            PrivateKeyRequiresPassphrase = connectedInputState.AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                                           !string.IsNullOrEmpty(connectedInputState.Secret),
            Group = connectedInputState.GroupName,
            Notes = connectedInputState.Notes,
            Glyph = connectedInputState.ConnectionGlyph,
            Color = connectedInputState.ConnectionColor
        };

        try
        {
            _connectionManager.AddOrUpdateGroup(new ConnectionGroupSettings
            {
                Name = connectedInputState.GroupName,
                Glyph = connectedInputState.GroupGlyph,
                Color = connectedInputState.GroupColor
            });
            _connectionManager.AddOrUpdateConnection(
                connection,
                connectedInputState.SaveCredentials ? connectedInputState.Secret : null);
            _connectionsPanel?.LoadConnections();
        }
        catch (Exception persistenceError)
        {
            // The network session and tab are already valid. A profile-save
            // failure must not tear them down or masquerade as a connect error.
            await ShowPersistenceWarningAsync(persistenceError);
        }

        // Show tab mode
        ShowTabMode();

        // Load navigation pane state and connections
        LoadNavigationPaneState();
        LoadConnectionsList();
    }

    private async Task<bool> ConnectAsync(
        string hostname,
        int port,
        string username,
        string password,
        Action<Exception>? connectionFailureHandler = null,
        bool openTerminalMaximized = false,
        HostKeyConfirmationAsync? hostKeyConfirmation = null,
        SftpAuthenticationMode authenticationMode = SftpAuthenticationMode.Password,
        string? privateKeyPath = null,
        long authenticationRevision = 0)
    {
        var generation = Interlocked.Increment(ref _connectionAttemptGeneration);
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
            _windowLifetimeCts.Token);
        var previousAttempt = Interlocked.Exchange(ref _connectionAttemptCts, attemptCts);
        try
        {
            previousAttempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded attempt completed between the atomic exchange and
            // cancellation. It no longer owns any candidate connection.
        }

        var gateHeld = false;
        SshConnectionSession? session = null;
        ActiveConnection? candidate = null;
        var committed = false;

        try
        {
            await _connectionGate.WaitAsync(attemptCts.Token);
            gateHeld = true;
            attemptCts.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _connectionAttemptGeneration))
            {
                return false;
            }

            session = _sshClientFactory.CreateSession(
                hostname,
                port,
                username,
                authenticationMode,
                password ?? "",
                privateKeyPath,
                authenticationRevision);
            var client = await _sshClientFactory.ConnectSftpAsync(
                session,
                hostKeyConfirmation ?? ConfirmHostKeyAsync,
                attemptCts.Token);
            candidate = new ActiveConnection(client, session);

            attemptCts.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _connectionAttemptGeneration) ||
                !client.IsConnected)
            {
                return false;
            }

            RegisterConnection(candidate);
            _currentConnection = candidate;
            _sftpClient = client;
            _hostname = hostname;
            m_appWindow.Title = $"SFTP Explorer - {username}@{hostname}";
            await AddConnectedTabAsync(candidate, openTerminalMaximized);
            committed = true;
            return true;
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (connectionFailureHandler != null)
            {
                connectionFailureHandler(ex);
                return false;
            }

            if (ex is HostKeyChangedException changedHostKey)
            {
                await ShowChangedHostKeyDialogAsync(changedHostKey);
            }
            else
            {
                var errorDialog = new ContentDialog
                {
                    Title = LocalizationHelper.GetString("ConnectionFailedTitle") ?? "Connection Failed",
                    Content = $"{LocalizationHelper.GetString("ConnectionFailedMessage") ?? "Failed to connect to"} {hostname}:\n{ex.Message}",
                    CloseButtonText = LocalizationHelper.GetString("OK") ?? "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }

            return false;
        }
        finally
        {
            if (!committed)
            {
                if (candidate != null)
                {
                    UnregisterAndDisposeConnection(candidate);
                }
                else
                {
                    session?.Dispose();
                }
            }

            if (gateHeld)
            {
                _connectionGate.Release();
            }

            Interlocked.CompareExchange(ref _connectionAttemptCts, null, attemptCts);
            attemptCts.Dispose();
        }
    }

    private async Task ShowChangedHostKeyDialogAsync(HostKeyChangedException exception)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("HostKeyChangedTitle") ??
                    "SSH host key changed — connection blocked",
            Content = new TextBlock
            {
                Text = string.Format(
                           LocalizationHelper.GetString("HostKeyChangedDetailsMessage"),
                           exception.Hostname,
                           exception.Port,
                           exception.ExpectedAlgorithm,
                           exception.ExpectedFingerprint,
                           exception.ReceivedAlgorithm,
                           exception.ReceivedFingerprint) +
                       "\n\n" + LocalizationHelper.GetString("HostKeyChangedFollowUpMessage"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                MaxWidth = 560
            },
            PrimaryButtonText = LocalizationHelper.GetString("ForgetHostKeyButton") ??
                                "Forget saved host key",
            CloseButtonText = LocalizationHelper.GetString("Cancel") ?? "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _sshClientFactory.KnownHosts.RemoveAsync(
                exception.Hostname,
                exception.Port,
                _windowLifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception removeError)
        {
            var errorDialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("HostKeyRemoveFailedTitle") ??
                        "Could not remove saved host key",
                Content = removeError.Message,
                CloseButtonText = LocalizationHelper.GetString("OK") ?? "OK",
                XamlRoot = Content.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }

    private void TrySetSystemBackdrop()
    {
        if (MicaController.IsSupported())
        {
            m_backdropController = new MicaController
            {
                Kind = MicaKind.Base
            };

            m_configurationSource = new SystemBackdropConfiguration();
            this.Activated += Window_Activated;

            m_configurationSource.IsInputActive = true;
            SetConfigurationSourceTheme();

            m_backdropController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            m_backdropController.SetSystemBackdropConfiguration(m_configurationSource);
        }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (m_configurationSource != null)
        {
            m_configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        this.Activated -= MainWindow_FirstActivated;
        this.Activated -= Window_Activated;
        ((FrameworkElement)Content).ActualThemeChanged -= Window_ThemeChanged;

        // SDK 1.7+: Clear badge notification when app closes
        Services.BadgeNotificationService.ClearBadge();
        _windowLifetimeCts.Cancel();
        try
        {
            _connectionAttemptCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var closingTabs = new List<(SftpTabContent Tab, ActiveConnection Connection)>();
        foreach (var tab in MainTabView.TabItems.OfType<TabViewItem>())
        {
            if (tab.Tag is SftpTabContent tabContent)
            {
                tabContent.DisposeTerminal();
                if (_tabConnections.Remove(tabContent, out var connection))
                {
                    closingTabs.Add((tabContent, connection));
                }
            }
        }

        _ = ReleaseWindowConnectionsAfterCleanupAsync(closingTabs);
        _currentConnection = null;
        _sftpClient = null;

        if (m_backdropController != null)
        {
            m_backdropController.Dispose();
            m_backdropController = null;
        }
        if (m_configurationSource != null)
        {
            m_configurationSource = null;
        }
    }

    private void Window_ThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarTheme();

        if (m_configurationSource != null)
        {
            SetConfigurationSourceTheme();
        }
    }

    private void SetConfigurationSourceTheme()
    {
        if (m_configurationSource != null && Content is FrameworkElement rootElement)
        {
            m_configurationSource.Theme = rootElement.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default
            };
        }
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    private void CustomizeTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBarSafely(WindowTitleBar);
        m_appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        m_appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // SDK 1.7+: Set title bar theme to follow system
        m_appWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        UpdateTitleBarTheme();

        // SDK 1.7+: Set minimum window size using OverlappedPresenter
        if (m_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 600;
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (Content is not FrameworkElement rootElement)
        {
            return;
        }

        var captionButtonForeground = rootElement.ActualTheme == ElementTheme.Dark
            ? Colors.White
            : Colors.Black;

        m_appWindow.TitleBar.ButtonForegroundColor = captionButtonForeground;
        m_appWindow.TitleBar.ButtonInactiveForegroundColor = captionButtonForeground;
        m_appWindow.TitleBar.ButtonHoverForegroundColor = captionButtonForeground;
        m_appWindow.TitleBar.ButtonPressedForegroundColor = captionButtonForeground;
    }

    private async void MainTabView_AddTabButtonClick(TabView sender, object args)
    {
        await AddNewTabAsync();
    }

    private void ShowTabMode()
    {
        if (_isClosed)
        {
            return;
        }

        // Hide startup panel, show tabs
        StartupPanel.Visibility = Visibility.Collapsed;
        WindowTitleBar.Visibility = Visibility.Collapsed;
        TabViewContainer.Visibility = Visibility.Visible;
        TitleBarArea.Visibility = Visibility.Visible;
        SetTitleBarSafely(CustomDragRegion);
    }

    private void ShowStartupMode()
    {
        if (_isClosed)
        {
            return;
        }

        // Ensure connections panel is initialized when showing startup mode
        if (!_connectionsPanelInitialized)
        {
            InitializeConnectionsPanel();
        }

        // Show startup panel, hide tabs
        StartupPanel.Visibility = Visibility.Visible;
        WindowTitleBar.Visibility = Visibility.Visible;
        TabViewContainer.Visibility = Visibility.Collapsed;
        TitleBarArea.Visibility = Visibility.Collapsed;
        SetTitleBarSafely(WindowTitleBar);
    }

    private void SetTitleBarSafely(UIElement titleBar)
    {
        if (_isClosed || ReferenceEquals(_currentTitleBar, titleBar))
        {
            return;
        }

        try
        {
            SetTitleBar(titleBar);
            _currentTitleBar = titleBar;
        }
        catch (COMException ex) when (ex.HResult == InvalidOperationHResult)
        {
            // WinUI rejects a title-bar registration when its window operation
            // has already become invalid. The previous drag region remains usable;
            // a rare mode transition must not terminate the process.
            Log.Warning($"Title-bar switch was skipped because the window operation is no longer valid: {ex.Message}");
        }
    }

    private async Task AddNewTabAsync(bool openTerminalMaximized = false)
    {
        var source = _currentConnection;
        if (source?.Client.IsConnected != true)
        {
            Log.Debug("AddNewTabAsync: no connected session is selected");
            return;
        }

        ActiveConnection? connection = null;
        var committed = false;
        using var sessionLease = RetainSession(source.Session);
        try
        {
            var client = await _sshClientFactory.ConnectSftpAsync(
                source.Session,
                ConfirmHostKeyAsync,
                _windowLifetimeCts.Token);
            connection = new ActiveConnection(client, source.Session);
            _windowLifetimeCts.Token.ThrowIfCancellationRequested();
            RegisterConnection(connection);
            _currentConnection = connection;
            _sftpClient = client;
            await AddConnectedTabAsync(connection, openTerminalMaximized);
            committed = true;
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ex is HostKeyChangedException changedHostKey)
            {
                await ShowChangedHostKeyDialogAsync(changedHostKey);
            }
            else
            {
                await ShowConnectionErrorAsync(source.Session.Hostname, ex);
            }
        }
        finally
        {
            if (!committed && connection != null)
            {
                UnregisterAndDisposeConnection(connection);
            }
        }
    }

    private async Task AddConnectedTabAsync(
        ActiveConnection connection,
        bool openTerminalMaximized = false,
        int? insertionIndex = null,
        string? initialPath = null)
    {
        Log.Debug($"Creating tab for connected endpoint {connection.Session.EndpointKey}");
        SftpTabContent? tabContent = null;
        TabViewItem? newTab = null;
        Action<string>? headerHandler = null;
        try
        {
            tabContent = new SftpTabContent(
                connection.Client,
                connection.Session,
                _sshClientFactory,
                ConfirmHostKeyAsync);

            newTab = new TabViewItem
            {
                Header = tabContent.CurrentFolderName,
                IconSource = new SymbolIconSource { Symbol = Symbol.Folder },
                Tag = tabContent
            };
            newTab.ContextFlyout = CreateTabContextMenu(newTab);

            headerHandler = name => newTab.Header = name;
            tabContent.CurrentFolderChanged += headerHandler;
            _tabConnections.Add(tabContent, connection);

            if (insertionIndex is >= 0)
            {
                MainTabView.TabItems.Insert(
                    Math.Min(insertionIndex.Value, MainTabView.TabItems.Count),
                    newTab);
            }
            else
            {
                MainTabView.TabItems.Add(newTab);
            }
            MainTabView.SelectedItem = newTab;

            UpdateTabContent(newTab);

            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                await Task.Yield();
                tabContent.NavigateToPath(initialPath);
            }

            if (openTerminalMaximized)
            {
                try
                {
                    await tabContent.OpenTerminalMaximizedAsync();
                }
                catch (Exception terminalError)
                {
                    // The SFTP tab is valid even if the optional terminal could
                    // not start. Do not disconnect the file session beneath it.
                    Log.Error("Opening the SSH terminal failed", terminalError);
                }
            }

            if (MainTabView.TabItems.Count == 1)
            {
                MainTabView.SelectionChanged += MainTabView_SelectionChanged;
            }

            NavigationSplitView.IsPaneOpen = false;
            _isPaneOpenState = false;
            Log.Debug($"Tab added. Total tabs: {MainTabView.TabItems.Count}");
        }
        catch
        {
            if (tabContent != null)
            {
                if (headerHandler != null)
                {
                    tabContent.CurrentFolderChanged -= headerHandler;
                }
                _tabConnections.Remove(tabContent);
                TabContentArea.Children.Remove(tabContent);
                try
                {
                    tabContent.DisposeTerminal();
                    await tabContent.CloseCleanupTask;
                }
                catch (Exception cleanupError)
                {
                    Log.Warning($"Rolling back failed tab creation encountered a cleanup error: {cleanupError.Message}");
                }
            }

            if (newTab != null)
            {
                MainTabView.TabItems.Remove(newTab);
            }
            if (MainTabView.TabItems.Count == 0)
            {
                MainTabView.SelectionChanged -= MainTabView_SelectionChanged;
            }

            throw;
        }
    }

    private void RegisterConnection(ActiveConnection connection)
    {
        lock (_connectionOwnershipLock)
        {
            _ownedConnections.Add(connection);
            _connectionSessions.Add(connection.Session);
            _activeConnections[GetConnectionKey(connection.Session)] = connection;
        }
    }

    private void UnregisterAndDisposeConnection(ActiveConnection connection)
    {
        var disposeSession = false;
        lock (_connectionOwnershipLock)
        {
            _ownedConnections.Remove(connection);
            var connectionKey = GetConnectionKey(connection.Session);
            if (_activeConnections.TryGetValue(connectionKey, out var registered) &&
                ReferenceEquals(registered, connection))
            {
                var replacement = _ownedConnections.FirstOrDefault(existing =>
                    existing.Client.IsConnected &&
                    string.Equals(
                        GetConnectionKey(existing.Session),
                        connectionKey,
                        StringComparison.Ordinal));
                if (replacement == null)
                {
                    _activeConnections.Remove(connectionKey);
                }
                else
                {
                    _activeConnections[connectionKey] = replacement;
                }
            }

            if (ReferenceEquals(_currentConnection, connection))
            {
                _currentConnection = null;
                _sftpClient = null;
            }

            if (!_ownedConnections.Any(existing =>
                    ReferenceEquals(existing.Session, connection.Session)))
            {
                _connectionSessions.Remove(connection.Session);
                disposeSession = !_sessionRetainCounts.ContainsKey(connection.Session);
            }
        }

        connection.Dispose();
        if (disposeSession)
        {
            connection.Session.Dispose();
        }
    }

    private void ReleaseTabConnection(SftpTabContent tabContent)
    {
        if (_tabConnections.Remove(tabContent, out var connection))
        {
            _ = ReleaseConnectionAfterCleanupAsync(tabContent.CloseCleanupTask, connection);
        }
    }

    private async Task ReleaseConnectionAfterCleanupAsync(
        Task closeCleanupTask,
        ActiveConnection connection)
    {
        try
        {
            await closeCleanupTask.ConfigureAwait(false);
        }
        catch (Exception cleanupError)
        {
            Log.Warning($"Waiting for tab cleanup failed: {cleanupError.Message}");
        }
        finally
        {
            try
            {
                UnregisterAndDisposeConnection(connection);
            }
            catch (Exception releaseError)
            {
                Log.Error("Releasing a closed tab connection failed", releaseError);
            }
        }
    }

    private async Task ReleaseWindowConnectionsAfterCleanupAsync(
        IReadOnlyList<(SftpTabContent Tab, ActiveConnection Connection)> closingTabs)
    {
        await Task.WhenAll(closingTabs.Select(item =>
            ReleaseConnectionAfterCleanupAsync(item.Tab.CloseCleanupTask, item.Connection)))
            .ConfigureAwait(false);

        ActiveConnection[] remainingConnections;
        SshConnectionSession[] remainingSessions;
        lock (_connectionOwnershipLock)
        {
            remainingConnections = _ownedConnections.ToArray();
            remainingSessions = _connectionSessions
                .Where(session => !_sessionRetainCounts.ContainsKey(session))
                .ToArray();
            _ownedConnections.Clear();
            _activeConnections.Clear();
            _connectionSessions.Clear();
        }

        foreach (var connection in remainingConnections)
        {
            connection.Dispose();
        }
        foreach (var session in remainingSessions)
        {
            session.Dispose();
        }

        _windowLifetimeCts.Dispose();
    }

    private static string GetConnectionKey(SshConnectionSession session)
    {
        return $"{session.Username}@{session.EndpointKey}#{session.AuthenticationRevision}";
    }

    private async Task ShowConnectionErrorAsync(string hostname, Exception exception)
    {
        var errorDialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("ConnectionFailedTitle") ?? "Connection Failed",
            Content = $"{LocalizationHelper.GetString("ConnectionFailedMessage") ?? "Failed to connect to"} " +
                      $"{hostname}:\n{exception.Message}",
            CloseButtonText = LocalizationHelper.GetString("OK") ?? "OK",
            XamlRoot = Content.XamlRoot
        };
        await errorDialog.ShowAsync();
    }

    private async Task ShowPersistenceWarningAsync(Exception exception)
    {
        Log.Error("Saved connection data could not be read or written", exception);
        if (Content?.XamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("ConnectionDataErrorTitle") ??
                    "Saved connection data error",
            Content = "The active server connection was not closed, but profile or credential changes " +
                      $"could not be completed.\n\n{exception.Message}",
            CloseButtonText = LocalizationHelper.GetString("OK") ?? "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private Task<bool> ConfirmHostKeyAsync(
        HostKeyPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return ShowHostKeyConfirmationOnUiAsync(prompt, cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(
                        await ShowHostKeyConfirmationOnUiAsync(prompt, cancellationToken));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private async Task<bool> ShowHostKeyConfirmationOnUiAsync(
        HostKeyPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Content?.XamlRoot == null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("UnknownHostKeyTitle") ?? "Unknown SSH host key",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(
                            LocalizationHelper.GetString("HostKeyFirstUseMessage"),
                            prompt.Hostname,
                            prompt.Port,
                            prompt.Algorithm,
                            prompt.DisplayFingerprint),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        MaxWidth = 520
                    }
                }
            },
            PrimaryButtonText = LocalizationHelper.GetString("TrustHostKeyButton") ?? "Trust and connect",
            CloseButtonText = LocalizationHelper.GetString("Cancel") ?? "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            DispatcherQueue.TryEnqueue(dialog.Hide);
        });
        var result = await dialog.ShowAsync();
        return !cancellationToken.IsCancellationRequested &&
               result == ContentDialogResult.Primary;
    }

    private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabView.SelectedItem is TabViewItem selectedTab)
        {
            if (selectedTab.Tag is SftpTabContent tabContent &&
                _tabConnections.TryGetValue(tabContent, out var connection))
            {
                _currentConnection = connection;
                _sftpClient = connection.Client;
                _hostname = connection.Session.Hostname;
                m_appWindow.Title =
                    $"SFTP Explorer - {connection.Session.Username}@{connection.Session.Hostname}";
            }

            UpdateTabContent(selectedTab);
        }
    }

    private void UpdateTabContent(TabViewItem tab)
    {
        if (tab.Tag is not SftpTabContent content)
        {
            return;
        }

        // Keep every tab mounted. Detaching and reattaching a TerminalControl
        // destroys its child HWND, forcing a full scrollback replay and leaving
        // a blank frame during every switch. Visibility preserves both native
        // renderers and makes tab selection a constant-time show/hide operation.
        foreach (var existingContent in TabContentArea.Children.OfType<SftpTabContent>())
        {
            if (ReferenceEquals(existingContent, content))
            {
                continue;
            }

            existingContent.SuspendNativeTerminalForTabSwitch();
            existingContent.Visibility = Visibility.Collapsed;
        }

        if (!TabContentArea.Children.Contains(content))
        {
            TabContentArea.Children.Add(content);
        }

        content.Visibility = Visibility.Visible;
        content.RestoreNativeTerminalAfterTabSwitch();
        ApplyNavigationPaneOcclusionToCurrentTab();
    }

    private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // Remove content from area if it's the current tab
        if (args.Tab is TabViewItem tabViewItem && tabViewItem.Tag is SftpTabContent tabContent)
        {
            tabContent.DisposeTerminal();
            TabContentArea.Children.Remove(tabContent);
            ReleaseTabConnection(tabContent);
        }

        MainTabView.TabItems.Remove(args.Tab);

        // Show startup panel if all tabs closed
        if (MainTabView.TabItems.Count == 0)
        {
            MainTabView.SelectionChanged -= MainTabView_SelectionChanged;
            ShowStartupMode();

            // Save navigation pane state
            SaveNavigationPaneState();
        }
    }

    public async void OpenSftpUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.DnsSafeHost))
            {
                throw new FormatException("A valid absolute sftp:// URL with a hostname is required.");
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new FormatException("SFTP URL query strings and fragments are not supported.");
            }

            var host = uri.DnsSafeHost;
            var port = uri.Port is >= 1 and <= 65535 ? uri.Port : 22;
            var username = Uri.UnescapeDataString(uri.UserInfo);
            if (username.Contains(':', StringComparison.Ordinal))
            {
                throw new FormatException(
                    "Passwords in sftp:// URLs are blocked because URLs can be exposed in command-line history and process listings. " +
                    "Use sftp://username@host and enter the password securely.");
            }

            if (username.Any(char.IsControl))
            {
                throw new FormatException("The SFTP URL contains an invalid username.");
            }

            this.Activated -= MainWindow_FirstActivated;
            while (Content?.XamlRoot == null)
            {
                await Task.Delay(100, _windowLifetimeCts.Token);
            }

            SavedConnection? savedConnection = null;
            string? password = null;
            if (!string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    savedConnection = _connectionManager.LoadConnections().FirstOrDefault(connection =>
                    {
                        try
                        {
                            return string.Equals(connection.Username, username, StringComparison.Ordinal) &&
                                   string.Equals(
                                       KnownHostStore.GetEndpointKey(connection.Hostname, connection.Port),
                                       KnownHostStore.GetEndpointKey(host, port),
                                       StringComparison.Ordinal);
                        }
                        catch (ArgumentException)
                        {
                            return false;
                        }
                    });
                    if (savedConnection != null)
                    {
                        password = _connectionManager.GetPassword(savedConnection.Id);
                    }
                }
                catch (Exception persistenceError)
                {
                    savedConnection = null;
                    password = null;
                    await ShowPersistenceWarningAsync(persistenceError);
                }
            }

            if (string.IsNullOrWhiteSpace(username) || password == null)
            {
                var credentials = await PromptForUrlCredentialsAsync(host, username);
                if (!credentials.Accepted)
                {
                    ShowStartupMode();
                    return;
                }

                username = credentials.Username;
                password = credentials.Password;
            }

            var success = await ConnectAsync(host, port, username, password);

            if (success)
            {
                // Show tab mode
                ShowTabMode();

                // Load navigation pane state and connections
                LoadNavigationPaneState();
                LoadConnectionsList();
                if (savedConnection != null)
                {
                    try
                    {
                        _connectionManager.UpdateLastUsed(savedConnection.Id);
                    }
                    catch (Exception persistenceError)
                    {
                        await ShowPersistenceWarningAsync(persistenceError);
                    }
                }
            }
            else
            {
                // Connection failed, return to startup mode
                ShowStartupMode();
            }
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Connection failed due to exception, return to startup mode
            ShowStartupMode();

            while (Content?.XamlRoot == null)
            {
                if (_windowLifetimeCts.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(50);
            }

            var errorDialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("InvalidURLTitle") ?? "Invalid URL",
                Content = $"{LocalizationHelper.GetString("InvalidURLMessage") ?? "Failed to parse URL:"} {ex.Message}",
                CloseButtonText = LocalizationHelper.GetString("OK") ?? "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }

    private IDisposable RetainSession(SshConnectionSession session)
    {
        lock (_connectionOwnershipLock)
        {
            _sessionRetainCounts.TryGetValue(session, out var count);
            _sessionRetainCounts[session] = checked(count + 1);
        }

        return new SessionLease(this, session);
    }

    private void ReleaseSessionLease(SshConnectionSession session)
    {
        var disposeSession = false;
        lock (_connectionOwnershipLock)
        {
            if (!_sessionRetainCounts.TryGetValue(session, out var count) || count <= 0)
            {
                throw new SynchronizationLockException("The SSH session lease was not held.");
            }

            if (count == 1)
            {
                _sessionRetainCounts.Remove(session);
                if (!_ownedConnections.Any(existing => ReferenceEquals(existing.Session, session)))
                {
                    _connectionSessions.Remove(session);
                    disposeSession = true;
                }
            }
            else
            {
                _sessionRetainCounts[session] = count - 1;
            }
        }

        if (disposeSession)
        {
            session.Dispose();
        }
    }

    private async Task<(bool Accepted, string Username, string Password)> PromptForUrlCredentialsAsync(
        string hostname,
        string username)
    {
        var usernameBox = new TextBox
        {
            Text = username,
            PlaceholderText = LocalizationHelper.GetString("EnterUsernameLabel") ?? "Enter username",
            Height = 36,
            Width = 320,
            Visibility = string.IsNullOrWhiteSpace(username) ? Visibility.Visible : Visibility.Collapsed
        };
        var passwordBox = new PasswordBox
        {
            PlaceholderText = LocalizationHelper.GetString("EnterPasswordLabel") ?? "Enter password",
            Height = 36,
            Width = 320
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12, 12, 12, 8)
        };
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(username)
                ? hostname
                : $"{username}@{hostname}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        });
        if (string.IsNullOrWhiteSpace(username))
        {
            content.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("UsernameLabel") ?? "Username"
            });
            content.Children.Add(usernameBox);
        }
        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("PasswordLabel") ?? "Password",
            Margin = new Thickness(0, 8, 0, 0)
        });
        content.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = $"Connect to {hostname}",
            Content = content,
            PrimaryButtonText = LocalizationHelper.GetString("Connect") ?? "Connect",
            CloseButtonText = LocalizationHelper.GetString("Cancel") ?? "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return (false, string.Empty, string.Empty);
        }

        var enteredUsername = string.IsNullOrWhiteSpace(username)
            ? usernameBox.Text.Trim()
            : username;
        return string.IsNullOrWhiteSpace(enteredUsername)
            ? (false, string.Empty, string.Empty)
            : (true, enteredUsername, passwordBox.Password);
    }

    private void NavigationSplitView_PaneOpening(SplitView sender, object args)
    {
        ApplyNavigationPaneOcclusionToCurrentTab(paneOpen: true);
    }

    private void NavigationSplitView_PaneClosed(SplitView sender, object args)
    {
        ApplyNavigationPaneOcclusionToCurrentTab(paneOpen: false);
    }

    private void NavigationSplitView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavigationSplitView.IsPaneOpen)
        {
            ApplyNavigationPaneOcclusionToCurrentTab(paneOpen: true);
        }
    }

    private void ApplyNavigationPaneOcclusionToCurrentTab(bool? paneOpen = null)
    {
        var tabContent = TabContentArea.Children
            .OfType<SftpTabContent>()
            .SingleOrDefault(tab => tab.Visibility == Visibility.Visible);
        if (tabContent == null)
        {
            return;
        }

        var isOpen = paneOpen ?? NavigationSplitView.IsPaneOpen;
        if (!isOpen || NavigationSplitView.PanePlacement != SplitViewPanePlacement.Left)
        {
            tabContent.SetTerminalLeftOverlayBoundary(null);
            return;
        }

        try
        {
            if (Content is UIElement rootContent)
            {
                var splitViewOrigin = NavigationSplitView
                    .TransformToVisual(rootContent)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                tabContent.SetTerminalLeftOverlayBoundary(
                    splitViewOrigin.X + NavigationSplitView.OpenPaneLength);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // The visual can briefly be between tree transitions while switching tabs.
        }

        tabContent.SetTerminalLeftOverlayBoundary(NavigationSplitView.OpenPaneLength);
    }

    private void TogglePaneButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationSplitView.IsPaneOpen = !NavigationSplitView.IsPaneOpen;
        _isPaneOpenState = NavigationSplitView.IsPaneOpen;
        SaveNavigationPaneState();
    }

    private void ClosePaneButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationSplitView.IsPaneOpen = false;
        _isPaneOpenState = false;
        SaveNavigationPaneState();
    }

    private void LoadNavigationPaneState()
    {
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (localSettings.Values.TryGetValue("NavigationPaneOpen", out var value) && value is bool isOpen)
            {
                _isPaneOpenState = isOpen;
                NavigationSplitView.IsPaneOpen = isOpen;
            }
        }
        catch
        {
            // Не критично, если не удалось загрузить состояние
        }
    }

    private void SaveNavigationPaneState()
    {
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["NavigationPaneOpen"] = _isPaneOpenState;
        }
        catch
        {
            // Не критично, если не удалось сохранить состояние
        }
    }

    private void LoadConnectionsList()
    {
        ConnectionsListPanel.Children.Clear();

        List<SavedConnection> connections;
        List<ConnectionGroupSettings> groupSettings;
        try
        {
            connections = _connectionManager.GetAllConnections();
            groupSettings = _connectionManager.LoadGroups();
        }
        catch (Exception persistenceError)
        {
            Log.Error("Loading saved connections failed", persistenceError);
            ConnectionsListPanel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                Title = LocalizationHelper.GetString("SavedConnectionsUnavailableTitle") ??
                        "Saved connections are unavailable",
                Message = persistenceError.Message,
                IsOpen = true,
                IsClosable = false,
                Margin = new Thickness(12)
            });
            return;
        }
        var grouped = connections
            .GroupBy(
                c => string.IsNullOrWhiteSpace(c.Group) ? "" : c.Group.Trim(),
                StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => string.IsNullOrEmpty(group.Key) ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in grouped)
        {
            // Group header
            var groupKey = group.Key;
            var settings = groupSettings.FirstOrDefault(item => string.Equals(
                item.Name,
                groupKey,
                StringComparison.CurrentCultureIgnoreCase));
            var isCollapsible = !string.IsNullOrEmpty(groupKey);
            var isExpanded = !isCollapsible || settings?.IsExpanded != false;
            var header = new Grid
            {
                Margin = new Thickness(12, 8, 12, 4)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var groupIcon = new FontIcon
            {
                Glyph = settings?.Glyph ?? ConnectionAppearanceDefaults.GroupGlyph,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseColor(
                    settings?.Color ?? (string.IsNullOrEmpty(groupKey) ? "#69797E" : ConnectionAppearanceDefaults.DefaultColor)))
            };
            Grid.SetColumn(groupIcon, 0);
            header.Children.Add(groupIcon);

            var groupName = new TextBlock
            {
                Text = string.IsNullOrEmpty(groupKey)
                    ? LocalizationHelper.GetString("UngroupedConnections")
                    : groupKey,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(groupName, 1);
            header.Children.Add(groupName);

            var groupConnectionsPanel = new StackPanel { Spacing = 4 };
            groupConnectionsPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

            if (isCollapsible)
            {
                var collapseGlyph = new FontIcon
                {
                    Glyph = isExpanded ? "\uE70D" : "\uE76C",
                    FontSize = 12
                };
                var collapseButton = new Button
                {
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = collapseGlyph
                };
                var collapseButtonName = LocalizationHelper.GetString(
                    isExpanded ? "CollapseConnectionGroup" : "ExpandConnectionGroup");
                ToolTipService.SetToolTip(collapseButton, collapseButtonName);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    collapseButton,
                    collapseButtonName);
                collapseButton.Click += async (sender, args) =>
                {
                    isExpanded = !isExpanded;
                    groupConnectionsPanel.Visibility = isExpanded
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    collapseGlyph.Glyph = isExpanded ? "\uE70D" : "\uE76C";
                    collapseButtonName = LocalizationHelper.GetString(
                        isExpanded ? "CollapseConnectionGroup" : "ExpandConnectionGroup");
                    ToolTipService.SetToolTip(collapseButton, collapseButtonName);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                        collapseButton,
                        collapseButtonName);
                    try
                    {
                        _connectionManager.SetGroupExpandedState(groupKey, isExpanded);
                        _connectionsPanel?.LoadConnections();
                    }
                    catch (Exception persistenceError)
                    {
                        await ShowPersistenceWarningAsync(persistenceError);
                    }
                };
                Grid.SetColumn(collapseButton, 2);
                header.Children.Add(collapseButton);
            }

            ConnectionsListPanel.Children.Add(header);

            // Connections in group
            foreach (var connection in group.OrderByDescending(c => c.LastUsed))
            {
                var card = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    CornerRadius = new CornerRadius(6)
                };

                var panel = new StackPanel { Spacing = 2 };

                var nameText = new TextBlock
                {
                    Text = connection.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };

                var detailsText = new TextBlock
                {
                    Text = $"{connection.Username}@{connection.Hostname}:{connection.Port}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };

                panel.Children.Add(nameText);
                panel.Children.Add(detailsText);
                if (!string.IsNullOrWhiteSpace(connection.Notes))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = connection.Notes,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 2,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                var content = new Grid
                {
                    ColumnSpacing = 9,
                    Padding = new Thickness(12, 8, 12, 8)
                };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var connectionIcon = new FontIcon
                {
                    Glyph = string.IsNullOrEmpty(connection.Glyph)
                        ? ConnectionAppearanceDefaults.ConnectionGlyph
                        : connection.Glyph,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 18,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseColor(connection.Color)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(connectionIcon, 0);
                Grid.SetColumn(panel, 1);
                content.Children.Add(connectionIcon);
                content.Children.Add(panel);

                var cardContent = new Grid();
                cardContent.Children.Add(content);

                var protocolReveal = new ConnectionProtocolReveal();
                protocolReveal.SftpRequested += async (s, e) =>
                {
                    NavigationSplitView.IsPaneOpen = false;
                    await SwitchToConnection(connection);
                };
                protocolReveal.SshRequested += async (s, e) =>
                {
                    NavigationSplitView.IsPaneOpen = false;
                    await SwitchToConnection(connection, openTerminalMaximized: true);
                };
                cardContent.Children.Add(protocolReveal);
                card.Child = cardContent;

                groupConnectionsPanel.Children.Add(card);
            }

            ConnectionsListPanel.Children.Add(groupConnectionsPanel);
        }
    }

    private static Windows.UI.Color ParseColor(string? color)
    {
        var value = (string.IsNullOrWhiteSpace(color)
            ? ConnectionAppearanceDefaults.DefaultColor
            : color).TrimStart('#');
        if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            if (value.Length == 6)
                argb |= 0xFF000000;

            return Windows.UI.Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }

        return Colors.DodgerBlue;
    }

    private async Task SwitchToConnection(
        SavedConnection connection,
        bool openTerminalMaximized = false)
    {
        string connectionKey;
        try
        {
            connectionKey = $"{connection.Username}@{KnownHostStore.GetEndpointKey(connection.Hostname, connection.Port)}#{connection.AuthenticationRevision}";
        }
        catch (ArgumentException)
        {
            connectionKey = string.Empty;
        }

        ActiveConnection? existingConnection = null;
        if (!string.IsNullOrEmpty(connectionKey))
        {
            lock (_connectionOwnershipLock)
            {
                _activeConnections.TryGetValue(connectionKey, out existingConnection);
            }
        }

        if (existingConnection?.Client.IsConnected == true)
        {
            _currentConnection = existingConnection;
            _sftpClient = existingConnection.Client;
            _hostname = connection.Hostname;
            m_appWindow.Title = $"SFTP Explorer - {connection.Username}@{connection.Hostname}";

            if (openTerminalMaximized)
            {
                await AddNewTabAsync(openTerminalMaximized: true);
            }
            else
            {
                var existingTab = MainTabView.TabItems
                    .OfType<TabViewItem>()
                    .FirstOrDefault(tab =>
                        tab.Tag is SftpTabContent tabContent &&
                        _tabConnections.TryGetValue(tabContent, out var tabConnection) &&
                        ReferenceEquals(tabConnection, existingConnection));
                if (existingTab != null)
                {
                    MainTabView.SelectedItem = existingTab;
                    UpdateTabContent(existingTab);
                }
                else
                {
                    await AddNewTabAsync();
                }
            }

            return;
        }

        string? password;
        try
        {
            password = _connectionManager.GetPassword(connection.Id);
        }
        catch (Exception persistenceError)
        {
            await ShowPersistenceWarningAsync(persistenceError);
            return;
        }

        if (string.IsNullOrEmpty(password) &&
            (connection.AuthenticationMode == SftpAuthenticationMode.Password ||
             connection.PrivateKeyRequiresPassphrase))
        {
            await ShowPasswordDialog(connection, openTerminalMaximized);
        }
        else
        {
            var success = await ConnectAsync(
                connection.Hostname,
                connection.Port,
                connection.Username,
                password ?? "",
                openTerminalMaximized: openTerminalMaximized,
                authenticationMode: connection.AuthenticationMode,
                privateKeyPath: connection.PrivateKeyPath,
                authenticationRevision: connection.AuthenticationRevision);
            if (success && _sftpClient != null)
            {
                try
                {
                    _connectionManager.UpdateLastUsed(connection.Id);
                }
                catch (Exception persistenceError)
                {
                    await ShowPersistenceWarningAsync(persistenceError);
                }
            }
        }
    }

    private async void NewConnectionButtonNav_Click(object sender, RoutedEventArgs e)
    {
        NavigationSplitView.IsPaneOpen = false;
        await ShowConnectionDialog();
        LoadConnectionsList();
    }

    private MenuFlyout CreateTabContextMenu(TabViewItem tab)
    {
        var menuFlyout = new MenuFlyout();

        var closeTabItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TabContextMenuCloseTab"),
            Icon = new SymbolIcon(Symbol.Cancel),
            Tag = tab
        };
        closeTabItem.Click += CloseTab_Click;

        var closeOtherTabsItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TabContextMenuCloseOtherTabs"),
            Icon = new SymbolIcon(Symbol.ClosePane),
            Tag = tab
        };
        closeOtherTabsItem.Click += CloseOtherTabs_Click;

        var closeTabsToRightItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TabContextMenuCloseTabsToTheRight"),
            Icon = new FontIcon { Glyph = "\uE972" },
            Tag = tab
        };
        closeTabsToRightItem.Click += CloseTabsToRight_Click;

        menuFlyout.Items.Add(closeTabItem);
        menuFlyout.Items.Add(closeOtherTabsItem);
        menuFlyout.Items.Add(closeTabsToRightItem);
        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var duplicateTabItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TabContextMenuDuplicateTab"),
            Icon = new SymbolIcon(Symbol.Copy),
            Tag = tab
        };
        duplicateTabItem.Click += DuplicateTab_Click;
        menuFlyout.Items.Add(duplicateTabItem);

        return menuFlyout;
    }

    private TabViewItem? GetTabFromContextMenu(object sender)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is TabViewItem tab)
        {
            return tab;
        }
        return null;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender);
        if (tab != null)
        {
            CloseTab(tab);
        }
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender);
        if (tab == null) return;

        var tabsToClose = MainTabView.TabItems.Cast<TabViewItem>()
            .Where(t => t != tab)
            .ToList();

        foreach (var tabToClose in tabsToClose)
        {
            CloseTab(tabToClose);
        }
    }

    private void CloseTabsToRight_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender);
        if (tab == null) return;

        var tabIndex = MainTabView.TabItems.IndexOf(tab);
        if (tabIndex < 0) return;

        var tabsToClose = MainTabView.TabItems.Cast<TabViewItem>()
            .Skip(tabIndex + 1)
            .ToList();

        foreach (var tabToClose in tabsToClose)
        {
            CloseTab(tabToClose);
        }
    }

    private async void DuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender);
        if (tab?.Tag is not SftpTabContent originalContent ||
            !_tabConnections.TryGetValue(originalContent, out var sourceConnection))
        {
            return;
        }

        ActiveConnection? duplicateConnection = null;
        var committed = false;
        using var sessionLease = RetainSession(sourceConnection.Session);
        try
        {
            var client = await _sshClientFactory.ConnectSftpAsync(
                sourceConnection.Session,
                ConfirmHostKeyAsync,
                _windowLifetimeCts.Token);
            duplicateConnection = new ActiveConnection(client, sourceConnection.Session);
            _windowLifetimeCts.Token.ThrowIfCancellationRequested();
            RegisterConnection(duplicateConnection);
            _currentConnection = duplicateConnection;
            _sftpClient = client;
            await AddConnectedTabAsync(
                duplicateConnection,
                insertionIndex: MainTabView.TabItems.IndexOf(tab) + 1,
                initialPath: originalContent.CurrentPath);
            committed = true;
        }
        catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ex is HostKeyChangedException changedHostKey)
            {
                await ShowChangedHostKeyDialogAsync(changedHostKey);
            }
            else
            {
                await ShowConnectionErrorAsync(sourceConnection.Session.Hostname, ex);
            }
        }
        finally
        {
            if (!committed && duplicateConnection != null)
            {
                UnregisterAndDisposeConnection(duplicateConnection);
            }
        }
    }

    private void CloseTab(TabViewItem tab)
    {
        // Remove content from area if it's the current tab
        if (tab.Tag is SftpTabContent tabContent)
        {
            tabContent.DisposeTerminal();
            TabContentArea.Children.Remove(tabContent);
            ReleaseTabConnection(tabContent);
        }

        MainTabView.TabItems.Remove(tab);

        // Show startup panel if all tabs closed
        if (MainTabView.TabItems.Count == 0)
        {
            MainTabView.SelectionChanged -= MainTabView_SelectionChanged;
            ShowStartupMode();
            SaveNavigationPaneState();
        }
    }

    private sealed class SessionLease : IDisposable
    {
        private MainWindow? _owner;
        private readonly SshConnectionSession _session;

        public SessionLease(MainWindow owner, SshConnectionSession session)
        {
            _owner = owner;
            _session = session;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSessionLease(_session);
        }
    }

    private sealed class ActiveConnection : IDisposable
    {
        private int _disposed;

        public ActiveConnection(SftpClient client, SshConnectionSession session)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public SftpClient Client { get; }

        public SshConnectionSession Session { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (Client.IsConnected)
                {
                    Client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Disconnecting SFTP client failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    Client.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Disposing SFTP client failed: {ex.Message}");
                }
            }
        }
    }
}
