using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using SftpExplorerWinUI.Models;
using SftpExplorerWinUI.Services;
using SftpExplorerWinUI.Helpers;

namespace SftpExplorerWinUI.Controls;

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return string.IsNullOrWhiteSpace(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed partial class ConnectionsPanel : UserControl
{
    public sealed class ConnectionGroup : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public required string Key { get; init; }
        public required string Name { get; init; }
        public required string Glyph { get; init; }
        public required string Color { get; init; }
        public required List<SavedConnection> Connections { get; init; }
        public required bool IsCollapsible { get; init; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionsVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapseGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapseButtonName)));
            }
        }

        public Visibility CollapseButtonVisibility => IsCollapsible
            ? Visibility.Visible
            : Visibility.Collapsed;
        public Visibility ConnectionsVisibility => !IsCollapsible || IsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        public string CollapseGlyph => IsExpanded ? "\uE70D" : "\uE76C";
        public string CollapseButtonName => LocalizationHelper.GetString(
            IsExpanded ? "CollapseConnectionGroup" : "ExpandConnectionGroup");

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ConnectionManager? _connectionManager;
    private readonly SshClientFactory _sshClientFactory;
    private List<SavedConnection> _connections;
    private bool _isConnecting = false;
    private Exception? _pendingLoadError;

    public event EventHandler<SavedConnection>? ConnectionSelected;
    public event EventHandler<SavedConnection>? ConnectionTerminalRequested;
    public event EventHandler? NewConnectionRequested;
    public event EventHandler? GroupExpansionChanged;

    public ConnectionsPanel()
        : this(null, null)
    {
    }

    public ConnectionsPanel(
        ConnectionManager? connectionManager,
        SshClientFactory? sshClientFactory)
    {
        InitializeComponent();
        _sshClientFactory = sshClientFactory ?? new SshClientFactory();
        _connections = new List<SavedConnection>();
        try
        {
            _connectionManager = connectionManager ?? new ConnectionManager();
            LoadConnections();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to initialize the saved-connections panel.", ex);
            _pendingLoadError = ex;
            ConnectionsRepeater.ItemsSource = Array.Empty<ConnectionGroup>();
            EmptyState.Visibility = Visibility.Visible;
            SubtitleText.Text = LocalizationHelper.GetString("SavedConnectionsUnavailableSubtitle");
            Loaded += ConnectionsPanel_Loaded;
        }
    }

    private async void ConnectionsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ConnectionsPanel_Loaded;
        var error = _pendingLoadError;
        _pendingLoadError = null;
        if (error != null)
        {
            await ShowPersistenceErrorAsync(
                LocalizationHelper.GetString("SavedConnectionsLoadErrorMessage"),
                error);
        }
    }

    public void LoadConnections()
    {
        var connectionManager = _connectionManager
            ?? throw new InvalidOperationException("Saved-connections storage is unavailable.");
        _connections = connectionManager.LoadConnections();

        var groupSettings = connectionManager.LoadGroups();
        var groups = _connections
            .GroupBy(
                c => string.IsNullOrWhiteSpace(c.Group) ? "" : c.Group.Trim(),
                StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => string.IsNullOrEmpty(group.Key) ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ConnectionGroup
            {
                Key = group.Key,
                Name = string.IsNullOrEmpty(group.Key)
                    ? LocalizationHelper.GetString("UngroupedConnections")
                    : group.Key,
                Glyph = groupSettings.FirstOrDefault(settings => string.Equals(
                    settings.Name,
                    group.Key,
                    StringComparison.CurrentCultureIgnoreCase))?.Glyph
                    ?? ConnectionAppearanceDefaults.GroupGlyph,
                Color = groupSettings.FirstOrDefault(settings => string.Equals(
                    settings.Name,
                    group.Key,
                    StringComparison.CurrentCultureIgnoreCase))?.Color
                    ?? (string.IsNullOrEmpty(group.Key) ? "#69797E" : ConnectionAppearanceDefaults.DefaultColor),
                Connections = group.OrderByDescending(c => c.LastUsed).ToList(),
                IsCollapsible = !string.IsNullOrEmpty(group.Key),
                IsExpanded = GetGroupExpandedState(group.Key, groupSettings)
            })
            .ToList();

        ConnectionsRepeater.ItemsSource = groups;

        // Показываем EmptyState если нет подключений
        EmptyState.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SubtitleText.Text = _connections.Count == 0
            ? "Select a saved connection or create new"
            : $"{_connections.Count} saved connection(s)";
    }

    private void NewConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        NewConnectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ConnectionSftp_Requested(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedConnection connection })
        {
            RequestConnection(connection, openTerminalMaximized: false);
        }
    }

    private void ConnectionSsh_Requested(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedConnection connection })
        {
            RequestConnection(connection, openTerminalMaximized: true);
        }
    }

    private void RequestConnection(SavedConnection connection, bool openTerminalMaximized)
    {
        if (_isConnecting) return;

        _isConnecting = true;
        if (openTerminalMaximized)
        {
            ConnectionTerminalRequested?.Invoke(this, connection);
        }
        else
        {
            ConnectionSelected?.Invoke(this, connection);
        }

        // Сбросим флаг через 500мс, чтобы предотвратить двойной клик
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() => _isConnecting = false);
            });
        });
    }

    private void MoreOptions_Click(object sender, RoutedEventArgs e)
    {
        // Flyout откроется автоматически
        if (sender is Button button)
        {
            button.Flyout?.ShowAt(button);
        }
    }

    private async void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConnectionGroup group } || !group.IsCollapsible)
            return;

        group.IsExpanded = !group.IsExpanded;
        try
        {
            (_connectionManager ?? throw new InvalidOperationException("Saved-connections storage is unavailable."))
                .SetGroupExpandedState(group.Key, group.IsExpanded);
            GroupExpansionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            group.IsExpanded = !group.IsExpanded;
            await ShowPersistenceErrorAsync(
                LocalizationHelper.GetString("GroupStateSaveErrorMessage"),
                ex);
        }
    }

    private static bool GetGroupExpandedState(
        string groupKey,
        IReadOnlyList<ConnectionGroupSettings> groupSettings)
    {
        if (string.IsNullOrEmpty(groupKey))
            return true;

        return groupSettings.FirstOrDefault(settings => string.Equals(
            settings.Name,
            groupKey,
            StringComparison.CurrentCultureIgnoreCase))?.IsExpanded ?? true;
    }

    private async void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SavedConnection connection)
        {
            try
            {
                await ShowEditDialog(connection);
            }
            catch (Exception ex)
            {
                await ShowPersistenceErrorAsync(
                    LocalizationHelper.GetString("ConnectionSaveErrorMessage"),
                    ex);
            }
        }
    }

    private async void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SavedConnection connection)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Connection",
                Content = $"Are you sure you want to delete '{connection.Name}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    (_connectionManager ?? throw new InvalidOperationException("Saved-connections storage is unavailable."))
                        .DeleteConnection(connection.Id);
                    LoadConnections();
                }
                catch (Exception ex)
                {
                    await ShowPersistenceErrorAsync(
                        LocalizationHelper.GetString("ConnectionDeleteErrorMessage"),
                        ex);
                }
            }
        }
    }

    private async System.Threading.Tasks.Task ShowEditDialog(SavedConnection connection)
    {
        var connectionManager = _connectionManager
            ?? throw new InvalidOperationException("Saved-connections storage is unavailable.");
        var dialog = new SftpConnectionDialog(
            connectionManager.LoadGroups(),
            connection,
            _sshClientFactory)
        {
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            // Work on a copy so a failed save cannot mutate the object still
            // displayed by the panel and give the impression that it persisted.
            var updatedConnection = new SavedConnection
            {
                Id = connection.Id,
                Name = dialog.ConnectionName,
                Hostname = dialog.Hostname,
                Port = dialog.Port,
                Username = dialog.Username,
                AuthenticationMode = dialog.AuthenticationMode,
                AuthenticationRevision = connection.AuthenticationRevision,
                PrivateKeyPath = dialog.PrivateKeyPath,
                PrivateKeyRequiresPassphrase = dialog.AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                    ((connection.AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                      string.Equals(connection.PrivateKeyPath, dialog.PrivateKeyPath, StringComparison.OrdinalIgnoreCase) &&
                      string.IsNullOrEmpty(dialog.Secret))
                        ? connection.PrivateKeyRequiresPassphrase
                        : !string.IsNullOrEmpty(dialog.Secret)),
                Group = dialog.GroupName,
                Notes = dialog.Notes,
                Glyph = dialog.ConnectionGlyph,
                Color = dialog.ConnectionColor,
                EncryptedPassword = connection.EncryptedPassword,
                CreatedAt = connection.CreatedAt,
                LastUsed = connection.LastUsed
            };

            // Обновляем пароль только если введен новый
            string? newPassword = string.IsNullOrEmpty(dialog.Secret) ? null : dialog.Secret;

            connectionManager.AddOrUpdateConnection(updatedConnection, newPassword);
            connectionManager.AddOrUpdateGroup(new ConnectionGroupSettings
            {
                Name = dialog.GroupName,
                Glyph = dialog.GroupGlyph,
                Color = dialog.GroupColor
            });
            LoadConnections();
        }
    }

    private async System.Threading.Tasks.Task ShowPersistenceErrorAsync(string message, Exception error)
    {
        Log.Error(message, error);
        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("SavedConnectionsErrorTitle"),
            Content = $"{message}\n\n{error.Message}",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }
}
