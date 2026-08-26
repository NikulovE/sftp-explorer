using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using SftpExplorerWinUI.Controls;
using SftpExplorerWinUI.Helpers;
using SftpExplorerWinUI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI;

public sealed partial class SftpConnectionDialog : ContentDialog
{
    public sealed record InputState(
        string ConnectionName,
        string GroupName,
        string Hostname,
        int Port,
        string Username,
        SftpAuthenticationMode AuthenticationMode,
        string Secret,
        string? PrivateKeyPath,
        string Notes,
        string ConnectionGlyph,
        string ConnectionColor,
        string GroupGlyph,
        string GroupColor,
        bool SaveCredentials);

    public sealed record ConnectionRequestResult(
        bool Success,
        string? ErrorMessage = null,
        HostKeyChangedException? ChangedHostKey = null);

    public Func<InputState, Task<ConnectionRequestResult>>? ConnectionRequestedAsync { get; set; }
    public InputState? ConnectedInputState { get; private set; }

    public string Hostname => HostnameBox.Text;
    public int Port => (int)PortBox.Value;
    public string Username => UsernameBox.Text;
    public SftpAuthenticationMode AuthenticationMode => AuthenticationModeBox.SelectedIndex == 1
        ? SftpAuthenticationMode.PrivateKey
        : SftpAuthenticationMode.Password;
    public string Secret => AuthenticationMode == SftpAuthenticationMode.PrivateKey
        ? PrivateKeyPassphraseBox.Password
        : PasswordBox.Password;
    public string Password => Secret;
    public string? PrivateKeyPath => AuthenticationMode == SftpAuthenticationMode.PrivateKey
        ? SshConnectionSession.NormalizePrivateKeyPath(PrivateKeyPathBox.Text)
        : null;
    public bool SaveCredentials => SaveCredentialsCheckbox.IsChecked == true;
    public string GroupName => GroupBox.Text?.Trim() ?? "";
    public string Notes => NotesBox.Text.Trim();
    public string ConnectionGlyph => ConnectionAppearancePicker.Glyph;
    public string ConnectionColor => ConnectionAppearancePicker.Color;
    public string GroupGlyph => GroupAppearancePicker.Glyph;
    public string GroupColor => GroupAppearancePicker.Color;
    public string ConnectionName => string.IsNullOrWhiteSpace(ConnectionNameBox.Text)
        ? $"{Username}@{Hostname}"
        : ConnectionNameBox.Text;

    private readonly List<ConnectionGroupSettings> _groups;
    private readonly SshClientFactory _sshClientFactory;
    private readonly CancellationTokenSource _dialogLifetimeCts = new();
    private CancellationTokenSource? _connectionTestCts;
    private bool _isConnecting;
    private bool _isTestingConnection;
    private bool _allowClose;
    private bool _isClosed;
    private bool _isAwaitingHostKeyConfirmation;
    private int _dialogLifetimeDisposed;
    private string? _closeButtonText;
    private TaskCompletionSource<bool>? _hostKeyConfirmationCompletion;
    private CancellationTokenRegistration _hostKeyConfirmationRegistration;

    public InputState CaptureInputState() => new(
        ConnectionNameBox.Text,
        GroupName,
        Hostname,
        Port,
        Username,
        AuthenticationMode,
        Secret,
        PrivateKeyPath,
        Notes,
        ConnectionGlyph,
        ConnectionColor,
        GroupGlyph,
        GroupColor,
        SaveCredentials);

    public void RestoreInputState(InputState state)
    {
        ConnectionNameBox.Text = state.ConnectionName;
        GroupBox.Text = state.GroupName;
        HostnameBox.Text = state.Hostname;
        PortBox.Value = state.Port;
        UsernameBox.Text = state.Username;
        AuthenticationModeBox.SelectedIndex = state.AuthenticationMode == SftpAuthenticationMode.PrivateKey ? 1 : 0;
        PrivateKeyPathBox.Text = state.PrivateKeyPath ?? "";
        if (state.AuthenticationMode == SftpAuthenticationMode.PrivateKey)
            PrivateKeyPassphraseBox.Password = state.Secret;
        else
            PasswordBox.Password = state.Secret;
        NotesBox.Text = state.Notes;
        ConnectionAppearancePicker.Glyph = state.ConnectionGlyph;
        ConnectionAppearancePicker.Color = state.ConnectionColor;
        GroupAppearancePicker.Glyph = state.GroupGlyph;
        GroupAppearancePicker.Color = state.GroupColor;
        SaveCredentialsCheckbox.IsChecked = state.SaveCredentials;
    }

    public void ShowConnectionError(string message)
    {
        ConnectionTestInfoBar.Severity = InfoBarSeverity.Error;
        ConnectionTestInfoBar.Title = LocalizationHelper.GetString("ConnectionFailedTitle") ?? "Connection Failed";
        ConnectionTestInfoBar.Message = message;
        ConnectionTestInfoBar.IsOpen = true;
    }

    public SftpConnectionDialog() : this(Enumerable.Empty<ConnectionGroupSettings>())
    {
    }

    public SftpConnectionDialog(
        IEnumerable<ConnectionGroupSettings> groups,
        SavedConnection? connection = null,
        SshClientFactory? sshClientFactory = null)
    {
        InitializeComponent();
        _sshClientFactory = sshClientFactory ?? new SshClientFactory();
        Resources["ContentDialogMaxHeight"] = double.PositiveInfinity;
        _groups = groups.ToList();
        GroupBox.ItemsSource = _groups.Select(group => group.Name).ToList();
        ConnectionAppearancePicker.Header = LocalizationHelper.GetString("ConnectionAppearanceLabel");
        GroupAppearancePicker.Header = LocalizationHelper.GetString("GroupAppearanceLabel");
        GroupAppearancePicker.Glyph = ConnectionAppearanceDefaults.GroupGlyph;

        if (connection != null)
        {
            Title = LocalizationHelper.GetString("EditConnectionTitle");
            PrimaryButtonText = LocalizationHelper.GetString("Save");
            ConnectionNameBox.Text = connection.Name;
            GroupBox.Text = connection.Group;
            HostnameBox.Text = connection.Hostname;
            PortBox.Value = connection.Port;
            UsernameBox.Text = connection.Username;
            AuthenticationModeBox.SelectedIndex = connection.AuthenticationMode == SftpAuthenticationMode.PrivateKey ? 1 : 0;
            PrivateKeyPathBox.Text = connection.PrivateKeyPath ?? "";
            NotesBox.Text = connection.Notes;
            ConnectionAppearancePicker.Glyph = connection.Glyph;
            ConnectionAppearancePicker.Color = connection.Color;
            PasswordBox.Header = LocalizationHelper.GetString("EditPasswordLabel");
            PasswordBox.PlaceholderText = LocalizationHelper.GetString("EditPasswordPlaceholder");
            PrivateKeyPassphraseBox.Header = LocalizationHelper.GetString("EditPassphraseLabel");
            PrivateKeyPassphraseBox.PlaceholderText = LocalizationHelper.GetString("EditPassphrasePlaceholder");
            SaveCredentialsPanel.Visibility = Visibility.Collapsed;
            UpdateAuthenticationModeUi();
            ApplyExistingGroupAppearance();
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ConnectionRequestedAsync == null)
        {
            if (!HasValidConnectionFields())
            {
                args.Cancel = true;
            }

            return;
        }

        // Keep the form open while Save & Connect performs the SSH handshake.
        args.Cancel = true;
        if (!HasValidConnectionFields())
        {
            return;
        }

        var inputState = CaptureInputState();
        SetConnectingState(true);

        try
        {
            var result = await ConnectionRequestedAsync(inputState);
            if (result.Success)
            {
                ConnectedInputState = inputState;
                _allowClose = true;
                Hide();
                return;
            }

            if (result.ChangedHostKey != null)
            {
                ShowChangedHostKeyError(result.ChangedHostKey);
            }
            else
            {
                ShowConnectionError(
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? LocalizationHelper.GetString("ConnectionFailedMessage") ?? "Failed to connect."
                        : result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            ShowConnectionError(ex.Message);
        }
        finally
        {
            SetConnectingState(false);
            TryDisposeDialogLifetime();
        }
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_isAwaitingHostKeyConfirmation)
        {
            _allowClose = true;
            _isClosed = true;
            _dialogLifetimeCts.Cancel();
            CompleteHostKeyConfirmation(false);
            args.Cancel = false;
            TryDisposeDialogLifetime();
            return;
        }

        if (_isTestingConnection)
        {
            _isClosed = true;
            _dialogLifetimeCts.Cancel();
            _connectionTestCts?.Cancel();
            args.Cancel = false;
            TryDisposeDialogLifetime();
            return;
        }

        args.Cancel = _isConnecting && !_allowClose;
        _isClosed = !args.Cancel;
        if (!args.Cancel)
        {
            _dialogLifetimeCts.Cancel();
            TryDisposeDialogLifetime();
        }
    }

    private async void ContentDialog_SecondaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        // Test Connection never closes the editor.
        args.Cancel = true;
        if (!HasValidConnectionFields())
        {
            ConnectionTestInfoBar.Severity = InfoBarSeverity.Warning;
            ConnectionTestInfoBar.Title = LocalizationHelper.GetString("ConnectionTestMissingFields");
            ConnectionTestInfoBar.Message = "";
            ConnectionTestInfoBar.IsOpen = true;
            return;
        }

        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        ConnectionTestInfoBar.Severity = InfoBarSeverity.Informational;
        ConnectionTestInfoBar.Title = LocalizationHelper.GetString("TestingConnection");
        ConnectionTestInfoBar.Message = $"{Username}@{Hostname}:{Port}";
        ConnectionTestInfoBar.IsOpen = true;

        var testCts = CancellationTokenSource.CreateLinkedTokenSource(
            _dialogLifetimeCts.Token);
        testCts.CancelAfter(TimeSpan.FromSeconds(30));
        var previousTest = Interlocked.Exchange(ref _connectionTestCts, testCts);
        try
        {
            previousTest?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _isTestingConnection = true;

        try
        {
            using var session = _sshClientFactory.CreateSession(
                Hostname, Port, Username, AuthenticationMode, Secret, PrivateKeyPath);
            using var client = await _sshClientFactory.ConnectSftpAsync(
                session,
                ConfirmHostKeyAsync,
                testCts.Token);
            await Task.Run(client.Disconnect);

            if (_isClosed)
            {
                return;
            }

            ConnectionTestInfoBar.Severity = InfoBarSeverity.Success;
            ConnectionTestInfoBar.Title = LocalizationHelper.GetString("ConnectionTestSucceeded");
            ConnectionTestInfoBar.Message = $"{Username}@{Hostname}:{Port}";
        }
        catch (HostKeyChangedException changedHostKey)
        {
            if (!_isClosed)
            {
                ShowChangedHostKeyError(changedHostKey);
            }
        }
        catch (OperationCanceledException) when (testCts.IsCancellationRequested)
        {
            if (!_isClosed)
            {
                ConnectionTestInfoBar.Severity = InfoBarSeverity.Error;
                ConnectionTestInfoBar.Title = LocalizationHelper.GetString("ConnectionTestFailed");
                ConnectionTestInfoBar.Message =
                    LocalizationHelper.GetString("ConnectionTestTimedOut") ??
                    "The connection test was cancelled or timed out.";
            }
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ConnectionTestInfoBar.Severity = InfoBarSeverity.Error;
                ConnectionTestInfoBar.Title = LocalizationHelper.GetString("ConnectionTestFailed");
                ConnectionTestInfoBar.Message = ex.Message;
            }
        }
        finally
        {
            _isTestingConnection = false;
            Interlocked.CompareExchange(ref _connectionTestCts, null, testCts);
            testCts.Dispose();
            if (!_isClosed)
            {
                IsPrimaryButtonEnabled = true;
                IsSecondaryButtonEnabled = true;
            }
            TryDisposeDialogLifetime();
        }
    }

    private bool HasValidConnectionFields()
    {
        var keyMissing = AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                         string.IsNullOrWhiteSpace(PrivateKeyPath);
        var keyNotFound = AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                          !keyMissing && !File.Exists(PrivateKeyPath);
        var valid = !string.IsNullOrWhiteSpace(Hostname) &&
                    !string.IsNullOrWhiteSpace(Username) &&
                    Port is >= 1 and <= 65535 && !keyMissing && !keyNotFound;
        if (!valid)
        {
            ConnectionTestInfoBar.Severity = InfoBarSeverity.Warning;
            ConnectionTestInfoBar.Title = keyMissing
                ? LocalizationHelper.GetString("PrivateKeyRequired") ?? "Select a private key file"
                : keyNotFound
                    ? LocalizationHelper.GetString("PrivateKeyNotFound") ?? "The private key file was not found"
                    : LocalizationHelper.GetString("ConnectionTestMissingFields") ?? "Complete the required fields";
            ConnectionTestInfoBar.Message = keyNotFound ? PrivateKeyPath ?? "" : "";
            ConnectionTestInfoBar.IsOpen = true;
        }
        return valid;
    }

    private void AuthenticationModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAuthenticationModeUi();
        if (ConnectionTestInfoBar != null) ConnectionTestInfoBar.IsOpen = false;
    }

    private void UpdateAuthenticationModeUi()
    {
        if (PasswordAuthenticationPanel == null || PrivateKeyAuthenticationPanel == null) return;
        var key = AuthenticationMode == SftpAuthenticationMode.PrivateKey;
        PasswordAuthenticationPanel.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        PrivateKeyAuthenticationPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowsePrivateKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId)
            {
                SettingsIdentifier = "SftpPrivateKeyPicker",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                Title = LocalizationHelper.GetString("PrivateKeyPickerTitle") ?? "Select a private key"
            };
            picker.FileTypeFilter.Add("*");
            var result = await picker.PickSingleFileAsync();
            if (result != null && !string.IsNullOrWhiteSpace(result.Path))
            {
                PrivateKeyPathBox.Text = result.Path;
                ConnectionTestInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex) { ShowConnectionError(ex.Message); }
    }

    private void SetConnectingState(bool isConnecting)
    {
        _isConnecting = isConnecting;
        DialogScrollViewer.IsEnabled = !isConnecting;
        IsPrimaryButtonEnabled = !isConnecting;
        IsSecondaryButtonEnabled = !isConnecting;
        ConnectionProgressPanel.Visibility = isConnecting ? Visibility.Visible : Visibility.Collapsed;
        ConnectionProgressRing.IsActive = isConnecting;

        if (isConnecting)
        {
            _closeButtonText = CloseButtonText;
            CloseButtonText = string.Empty;
        }
        else if (_closeButtonText != null)
        {
            CloseButtonText = _closeButtonText;
            _closeButtonText = null;
        }
    }

    /// <summary>
    /// Displays first-use host-key confirmation inside this dialog. WinUI does
    /// not permit a second ContentDialog on the same XamlRoot while this editor
    /// is open.
    /// </summary>
    public Task<bool> ConfirmHostKeyAsync(
        HostKeyPrompt prompt,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (DispatcherQueue.HasThreadAccess)
        {
            BeginHostKeyConfirmation(prompt, cancellationToken, completion);
        }
        else if (!DispatcherQueue.TryEnqueue(() =>
                     BeginHostKeyConfirmation(prompt, cancellationToken, completion)))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private void BeginHostKeyConfirmation(
        HostKeyPrompt prompt,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion)
    {
        if (_isClosed || cancellationToken.IsCancellationRequested ||
            _hostKeyConfirmationCompletion != null)
        {
            completion.TrySetResult(false);
            return;
        }

        _hostKeyConfirmationCompletion = completion;
        _isAwaitingHostKeyConfirmation = true;

        var trustButton = new Button
        {
            Content = LocalizationHelper.GetString("TrustHostKeyButton") ?? "Trust and connect"
        };
        trustButton.Click += (_, _) => CompleteHostKeyConfirmation(true);

        ConnectionTestInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionTestInfoBar.Title =
            LocalizationHelper.GetString("UnknownHostKeyTitle") ?? "Unknown SSH host key";
        ConnectionTestInfoBar.Message = string.Format(
            LocalizationHelper.GetString("HostKeyFirstUseMessage"),
            prompt.Hostname,
            prompt.Port,
            prompt.Algorithm,
            prompt.DisplayFingerprint);
        ConnectionTestInfoBar.ActionButton = trustButton;
        ConnectionTestInfoBar.IsOpen = true;

        // Keep only the explicit trust action and Cancel available. The fields
        // cannot be edited while the fingerprint for the captured endpoint is
        // being considered.
        DialogScrollViewer.IsEnabled = true;
        SetConnectionFieldsEnabled(false);
        ConnectionTestInfoBar.IsEnabled = true;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        ConnectionProgressPanel.Visibility = Visibility.Collapsed;
        ConnectionProgressRing.IsActive = false;
        CloseButtonText = _closeButtonText ??
            LocalizationHelper.GetString("Cancel") ?? "Cancel";

        _hostKeyConfirmationRegistration = cancellationToken.Register(() =>
        {
            DispatcherQueue.TryEnqueue(() => CompleteHostKeyConfirmation(false));
        });
    }

    private void CompleteHostKeyConfirmation(bool trusted)
    {
        var completion = _hostKeyConfirmationCompletion;
        if (completion == null)
        {
            return;
        }

        _hostKeyConfirmationCompletion = null;
        _isAwaitingHostKeyConfirmation = false;
        _hostKeyConfirmationRegistration.Dispose();
        ConnectionTestInfoBar.ActionButton = null;
        SetConnectionFieldsEnabled(true);

        if (_isConnecting && !_isClosed)
        {
            DialogScrollViewer.IsEnabled = false;
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            ConnectionProgressPanel.Visibility = Visibility.Visible;
            ConnectionProgressRing.IsActive = true;
            CloseButtonText = string.Empty;
        }

        completion.TrySetResult(trusted);
    }

    private void SetConnectionFieldsEnabled(bool enabled)
    {
        ConnectionNameBox.IsEnabled = enabled;
        ConnectionAppearancePicker.IsEnabled = enabled;
        GroupBox.IsEnabled = enabled;
        GroupAppearancePicker.IsEnabled = enabled;
        HostnameBox.IsEnabled = enabled;
        PortBox.IsEnabled = enabled;
        UsernameBox.IsEnabled = enabled;
        AuthenticationModeBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        PrivateKeyPathBox.IsEnabled = enabled;
        BrowsePrivateKeyButton.IsEnabled = enabled;
        PrivateKeyPassphraseBox.IsEnabled = enabled;
        NotesBox.IsEnabled = enabled;
        SaveCredentialsCheckbox.IsEnabled = enabled;
        CredentialStorageInfoButton.IsEnabled = enabled;
    }

    private void TryDisposeDialogLifetime()
    {
        if (!_isClosed || _isConnecting || _isTestingConnection ||
            _isAwaitingHostKeyConfirmation ||
            Interlocked.Exchange(ref _dialogLifetimeDisposed, 1) != 0)
        {
            return;
        }

        _dialogLifetimeCts.Dispose();
    }

    private void ShowChangedHostKeyError(HostKeyChangedException exception)
    {
        var forgetButton = new Button
        {
            Content = LocalizationHelper.GetString("ForgetHostKeyButton") ?? "Forget saved host key"
        };
        forgetButton.Click += async (_, _) =>
        {
            forgetButton.IsEnabled = false;
            try
            {
                await _sshClientFactory.KnownHosts.RemoveAsync(
                    exception.Hostname,
                    exception.Port);
                ConnectionTestInfoBar.Severity = InfoBarSeverity.Warning;
                ConnectionTestInfoBar.Title =
                    LocalizationHelper.GetString("HostKeyRemovedTitle") ?? "Saved host key removed";
                ConnectionTestInfoBar.Message =
                    LocalizationHelper.GetString("HostKeyForgottenMessage");
                ConnectionTestInfoBar.ActionButton = null;
            }
            catch (Exception removeError)
            {
                ConnectionTestInfoBar.Severity = InfoBarSeverity.Error;
                ConnectionTestInfoBar.Title =
                    LocalizationHelper.GetString("HostKeyRemoveFailedTitle") ?? "Could not remove saved host key";
                ConnectionTestInfoBar.Message = removeError.Message;
                forgetButton.IsEnabled = true;
            }
        };

        ConnectionTestInfoBar.Severity = InfoBarSeverity.Error;
        ConnectionTestInfoBar.Title =
            LocalizationHelper.GetString("HostKeyChangedTitle") ?? "SSH host key changed — connection blocked";
        ConnectionTestInfoBar.Message = string.Format(
            LocalizationHelper.GetString("HostKeyChangedDetailsMessage"),
            exception.Hostname,
            exception.Port,
            exception.ExpectedAlgorithm,
            exception.ExpectedFingerprint,
            exception.ReceivedAlgorithm,
            exception.ReceivedFingerprint);
        ConnectionTestInfoBar.ActionButton = forgetButton;
        ConnectionTestInfoBar.IsOpen = true;
    }

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyExistingGroupAppearance(GroupBox.SelectedItem as string);
    }

    private void GroupBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyExistingGroupAppearance();
    }

    private void ApplyExistingGroupAppearance(string? groupName = null)
    {
        var group = _groups.FirstOrDefault(existing => string.Equals(
            existing.Name,
            groupName ?? GroupName,
            StringComparison.CurrentCultureIgnoreCase));
        if (group == null)
            return;

        GroupAppearancePicker.Glyph = group.Glyph;
        GroupAppearancePicker.Color = group.Color;
    }
}
