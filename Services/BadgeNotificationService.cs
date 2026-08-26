using Microsoft.Windows.BadgeNotifications;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// Service to manage badge notifications on the taskbar icon.
/// Uses Windows App SDK 1.7+ BadgeNotificationManager.
/// </summary>
public static class BadgeNotificationService
{
    private static int _activeTransferCount = 0;
    private static readonly object _lock = new object();

    /// <summary>
    /// Gets the current count of active transfers.
    /// </summary>
    public static int ActiveTransferCount => _activeTransferCount;

    /// <summary>
    /// Increments the active transfer count and updates the badge.
    /// </summary>
    public static void IncrementTransfer()
    {
        lock (_lock)
        {
            _activeTransferCount++;
            UpdateBadge();
        }
    }

    /// <summary>
    /// Decrements the active transfer count and updates the badge.
    /// </summary>
    public static void DecrementTransfer()
    {
        lock (_lock)
        {
            if (_activeTransferCount > 0)
            {
                _activeTransferCount--;
            }
            UpdateBadge();
        }
    }

    /// <summary>
    /// Sets the active transfer count directly and updates the badge.
    /// </summary>
    public static void SetTransferCount(int count)
    {
        lock (_lock)
        {
            _activeTransferCount = Math.Max(0, count);
            UpdateBadge();
        }
    }

    /// <summary>
    /// Clears the badge notification.
    /// </summary>
    public static void ClearBadge()
    {
        lock (_lock)
        {
            _activeTransferCount = 0;
            try
            {
                BadgeNotificationManager.Current.ClearBadge();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to clear badge notification", ex);
            }
        }
    }

    private static void UpdateBadge()
    {
        try
        {
            if (_activeTransferCount <= 0)
            {
                BadgeNotificationManager.Current.ClearBadge();
            }
            else if (_activeTransferCount <= 99)
            {
                // Show numeric badge for 1-99 transfers
                BadgeNotificationManager.Current.SetBadgeAsCount((uint)_activeTransferCount);
            }
            else
            {
                // For 100+ transfers, show activity glyph
                BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Activity);
            }
        }
        catch (Exception ex)
        {
            // Badge notifications may not be available in all contexts
            Log.Error("Failed to update badge notification", ex);
        }
    }
}
