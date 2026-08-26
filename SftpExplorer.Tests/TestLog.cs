namespace SftpExplorerWinUI;

// ConnectionManager is linked into this cross-platform test project without the
// WinUI application. Tests do not need file logging, only the production API shape.
public static class Log
{
    public static void Info(string message, string? caller = null)
    {
    }

    public static void Warning(string message, Exception? ex = null, string? caller = null)
    {
    }

    public static void Error(string message, Exception? ex = null, string? caller = null)
    {
    }
}
