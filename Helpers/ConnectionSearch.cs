using System;
using SftpExplorerWinUI.Models;

namespace SftpExplorerWinUI.Helpers;

public static class ConnectionSearch
{
    public static bool Matches(SavedConnection connection, string query)
    {
        query = query.Trim();
        return query.Length == 0 ||
            Contains(connection.Name, query) ||
            Contains(connection.Hostname, query) ||
            Contains(connection.Username, query) ||
            Contains(connection.Group, query) ||
            Contains(connection.Notes, query) ||
            connection.Port.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
}
